using System.Collections.Generic;
using Comfort.Common;
using DynamicMaps.Config;
using DynamicMaps.Data;
using DynamicMaps.UI.Components;
using DynamicMaps.Utils;
using EFT;
using UnityEngine;

namespace DynamicMaps.DynamicMarkers
{
    /// <summary>
    /// Fork addition: shows scav activity as a heatmap instead of exact markers.
    ///
    /// Scavs close to each other (same floor, within the merge distance) are merged into one blob at their group center.
    /// Each blob is shifted by a stable random offset, so the brightest spot doesn't point at a scav, and the blob radius
    /// shrinks with your hideout intel level (never below a minimum). Blobs fade in when scavs first show up, glide as they
    /// move, and when the scavs die or leave they stay where they were and fade out, so killing a scav doesn't make the
    /// heat vanish instantly. Overlapping blobs stack into hotter areas. Per-floor visibility follows the map layers.
    ///
    /// Performance: nothing runs while the map is hidden. While it is shown, blobs update every frame (the game already
    /// draws live scav markers on the same canvas, so this is not a meaningful extra cost).
    /// </summary>
    public class ScavHeatmapProvider : IDynamicMarkerProvider
    {
        // "scav" in this mod means the whole Savage side (raiders, rogues, cultists, boss followers...); the heatmap
        // only counts the actual scav roles
        private static readonly HashSet<WildSpawnType> _scavRoles = new HashSet<WildSpawnType>
        {
            WildSpawnType.assault,
            WildSpawnType.assaultGroup,
            WildSpawnType.cursedAssault,
            WildSpawnType.crazyAssaultEvent,
            WildSpawnType.marksman,
        };

        // Redraw throttles. 0 = every frame. Raise them if the map ever gets expensive with lots of scavs.
        private const float _visualInterval = 0f;    // seconds between visual updates
        private const float _minMove = 0f;           // meters; smaller position changes are not applied
        private const float _minHeatChange = 0f;     // smaller heat changes are not redrawn

        private const float _glideSpeed = 4f;        // how quickly a blob glides to a newly sampled position
        private const float _minRadius = 50f;        // the radius never goes below this, whatever the intel level
        private const float _extentPadding = 25f;    // a merged blob covers its scavs' spread plus this much
        private const float _maxOffsetOfRadius = 0.5f;  // the offset never moves the blob more than this part of its radius

        private class Cluster
        {
            public readonly List<Vector3> Members = new List<Vector3>();
            public Vector3 Sum;
            public int Level;
            public float Extent;
            public Vector3 Centroid => Sum / Members.Count;
        }

        private readonly List<ScavHeatCell> _blobs = new List<ScavHeatCell>();
        private readonly List<(Vector3 position, int level)> _scavs = new List<(Vector3 position, int level)>();
        private readonly List<Cluster> _clusters = new List<Cluster>();
        private readonly List<ScavHeatCell> _tracked = new List<ScavHeatCell>();
        private readonly List<(float distance, int cluster, int blob)> _pairs = new List<(float distance, int cluster, int blob)>();
        private readonly List<ScavHeatCell> _expired = new List<ScavHeatCell>();

        private MapView _map;
        private ScavHeatmapTicker _ticker;
        private bool _isShown;
        private float _nextSampleTime;
        private float _lastVisualTime;
        private float _lastSettingsHash;

        public void OnShowInRaid(MapView map)
        {
            _map = map;
            _isShown = true;
            EnsureTicker();

            _lastVisualTime = Time.unscaledTime;
            Sample();
        }

        public void OnHideInRaid(MapView map)
        {
            _isShown = false;
        }

        public void OnRaidEnd(MapView map)
        {
            Teardown();
        }

        public void OnMapChanged(MapView map, MapDef mapDef)
        {
            ClearBlobs();
            _map = map;
            if (_isShown)
            {
                EnsureTicker();
                Sample();
            }
        }

        public void OnDisable(MapView map)
        {
            Teardown();
        }

        public void OnShowOutOfRaid(MapView map) { }

        public void OnHideOutOfRaid(MapView map) { }

        /// <summary>Called every frame by the ticker while the map is shown.</summary>
        internal void Update()
        {
            if (!_isShown || _map == null)
            {
                return;
            }

            var now = Time.unscaledTime;
            if (now >= _nextSampleTime)
            {
                Sample();
            }

            var deltaTime = now - _lastVisualTime;
            if (deltaTime < _visualInterval)
            {
                return;
            }

            _lastVisualTime = now;
            UpdateBlobs(deltaTime);
        }

        private void UpdateBlobs(float deltaTime)
        {
            var fadeOutSeconds = Mathf.Max(1f, Settings.ScavHeatmapFadeSeconds.Value);
            var fadeInSeconds = Settings.ScavHeatmapFadeInSeconds.Value;
            var glide = 1f - Mathf.Exp(-_glideSpeed * deltaTime);

            // redraw everything when a visual setting changed (opacity, color, overlap count)
            var settingsHash = Settings.ScavHeatmapMaxOpacity.Value
                               + Settings.ScavHeatmapScavsForFullHeat.Value * 3.1f
                               + Settings.ScavHeatmapColor.Value.r * 7.3f
                               + Settings.ScavHeatmapColor.Value.g * 11.7f
                               + Settings.ScavHeatmapColor.Value.b * 13.1f;
            var forceRedraw = !Mathf.Approximately(settingsHash, _lastSettingsHash);
            _lastSettingsHash = settingsHash;

            _expired.Clear();
            foreach (var blob in _blobs)
            {
                // heat builds up linearly while scavs are tracked (fade-in) and decays linearly once they're gone (fadeout)
                var heat = blob.Heat;
                if (blob.Target > heat)
                {
                    heat = fadeInSeconds <= 0.01f
                        ? blob.Target
                        : Mathf.Min(blob.Target, heat + deltaTime / fadeInSeconds);
                }
                else
                {
                    heat = Mathf.Max(blob.Target, heat - deltaTime / fadeOutSeconds);
                }

                blob.Heat = heat;  // always keep the real value; only the redraw below is throttled

                // the pack size fades too: a scav dying in a pack lowers the heat gradually instead of popping
                if (!Mathf.Approximately(blob.DisplayWeight, blob.Weight))
                {
                    var up = blob.Weight > blob.DisplayWeight;
                    var rate = up
                        ? (fadeInSeconds <= 0.01f ? 1000f : 1f / fadeInSeconds)
                        : 1f / fadeOutSeconds;
                    blob.DisplayWeight = Mathf.MoveTowards(blob.DisplayWeight, blob.Weight, deltaTime * rate);
                }

                if (blob.Target > 0f && (blob.TargetPosition - blob.Position).sqrMagnitude > _minMove * _minMove)
                {
                    blob.Position = Vector3.Lerp(blob.Position, blob.TargetPosition, glide);
                }

                var endOfFade = (heat <= 0f || heat >= 1f) && !Mathf.Approximately(blob.AppliedHeat, heat);
                if (forceRedraw
                    || endOfFade
                    || Mathf.Abs(heat - blob.AppliedHeat) > _minHeatChange
                    || !Mathf.Approximately(blob.AppliedWeight, blob.DisplayWeight))
                {
                    blob.ApplyVisual();
                }

                if (blob.Target <= 0f && blob.Heat <= 0.01f)
                {
                    _expired.Add(blob);
                }
            }

            foreach (var blob in _expired)
            {
                RemoveBlob(blob);
            }
        }

        private void Sample()
        {
            _nextSampleTime = Time.unscaledTime + Mathf.Max(0.25f, Settings.ScavHeatmapUpdateSeconds.Value);

            _scavs.Clear();
            _clusters.Clear();

            var gameWorld = Singleton<GameWorld>.Instance;
            var mainPlayer = GameUtils.GetMainPlayer();
            var canTrack = gameWorld != null && mainPlayer != null && _map != null && GameUtils.IsInRaid()
                           // same rule as the other intel-gated markers, except that 0 means "any intel level"
                           && (GameUtils.GetIntelLevel() ?? 0) >= Settings.ScavHeatmapIntelLevel.Value;

            if (canTrack)
            {
                var range = Settings.ScavHeatmapRange.Value;
                var origin = mainPlayer.Position;

                foreach (var player in gameWorld.AllAlivePlayersList)
                {
                    if (player == null || player.IsYourPlayer || player.IsHeadlessClient() || player.IsBTRShooter()
                        || !IsRegularScav(player) || player.HasCorpse())
                    {
                        continue;
                    }

                    if (range > 0f && Vector3.Distance(player.Position, origin) > range)
                    {
                        continue;
                    }

                    var mapPosition = MathUtils.ConvertToMapPosition(player.Position);  // x, y = ground plane, z = height
                    _scavs.Add((mapPosition, _map.GetLevelAt(mapPosition) ?? int.MinValue));
                }

                BuildClusters();
            }

            ApplyClusters();
        }

        /// <summary>Merges scavs on the same floor that are within the merge distance into packs.</summary>
        private void BuildClusters()
        {
            var mergeDistance = Settings.ScavHeatmapMergeDistance.Value;

            foreach (var scav in _scavs)
            {
                Cluster target = null;
                if (mergeDistance > 0f)
                {
                    foreach (var cluster in _clusters)
                    {
                        if (cluster.Level == scav.level && Distance2D(cluster.Centroid, scav.position) <= mergeDistance)
                        {
                            target = cluster;
                            break;
                        }
                    }
                }

                if (target == null)
                {
                    target = new Cluster { Level = scav.level };
                    _clusters.Add(target);
                }

                target.Members.Add(scav.position);
                target.Sum += scav.position;
            }

            foreach (var cluster in _clusters)
            {
                var centroid = cluster.Centroid;
                foreach (var member in cluster.Members)
                {
                    cluster.Extent = Mathf.Max(cluster.Extent, Distance2D(centroid, member));
                }
            }
        }

        /// <summary>
        /// Hands the packs to blobs. Packs are matched to the closest blob that is still tracking scavs, so a blob keeps
        /// its fade state and its offset as its scavs move; new packs get a new blob, blobs without a pack start fading out.
        /// </summary>
        private void ApplyClusters()
        {
            _tracked.Clear();
            foreach (var blob in _blobs)
            {
                if (blob.Target > 0f)
                {
                    _tracked.Add(blob);
                }
            }

            var maxMatchDistance = Mathf.Max(60f, Settings.ScavHeatmapMergeDistance.Value + 20f);
            _pairs.Clear();
            for (var c = 0; c < _clusters.Count; c++)
            {
                for (var b = 0; b < _tracked.Count; b++)
                {
                    if (_tracked[b].Level != _clusters[c].Level)
                    {
                        continue;
                    }

                    var distance = Distance2D(_clusters[c].Centroid, _tracked[b].Centroid);
                    if (distance <= maxMatchDistance)
                    {
                        _pairs.Add((distance, c, b));
                    }
                }
            }

            _pairs.Sort((a, b) => a.distance.CompareTo(b.distance));

            var clusterUsed = new bool[_clusters.Count];
            var blobUsed = new bool[_tracked.Count];
            var radius = GetIntelRadius();

            foreach (var pair in _pairs)
            {
                if (clusterUsed[pair.cluster] || blobUsed[pair.blob])
                {
                    continue;
                }

                clusterUsed[pair.cluster] = true;
                blobUsed[pair.blob] = true;
                ApplyCluster(_tracked[pair.blob], _clusters[pair.cluster], radius);
            }

            for (var c = 0; c < _clusters.Count; c++)
            {
                if (clusterUsed[c])
                {
                    continue;
                }

                var cluster = _clusters[c];
                var blob = ScavHeatCell.Create(_map.MapMarkerContainer, cluster.Centroid, radius);
                blob.Offset = CreateStableOffset();
                blob.Level = cluster.Level;
                blob.Weight = cluster.Members.Count;
                blob.DisplayWeight = blob.Weight;
                _blobs.Add(blob);
                _map.AddLayerBound(blob);  // applies the current floor status immediately
                ApplyCluster(blob, cluster, radius);
            }

            for (var b = 0; b < _tracked.Count; b++)
            {
                if (!blobUsed[b])
                {
                    _tracked[b].Target = 0f;  // its scavs died or left: fades out where it is
                }
            }
        }

        private void ApplyCluster(ScavHeatCell blob, Cluster cluster, float radius)
        {
            var centroid = cluster.Centroid;
            var blobRadius = Mathf.Max(radius, cluster.Extent + _extentPadding);
            if (!Mathf.Approximately(blob.Radius, blobRadius))
            {
                blob.SetRadius(blobRadius);
            }

            // the fuzz is a constant distance, but never so large that the scavs end up outside their blob
            var offset = blob.Offset * Settings.ScavHeatmapFuzz.Value;
            var maxOffset = blobRadius * _maxOffsetOfRadius;
            if (offset.magnitude > maxOffset)
            {
                offset = offset.normalized * maxOffset;
            }

            blob.Target = 1f;
            blob.Centroid = centroid;
            blob.Level = cluster.Level;
            blob.Weight = cluster.Members.Count;
            blob.TargetPosition = centroid + new Vector3(offset.x, offset.y, 0f);
            _map.RefreshLayerBound(blob);  // the scavs may have changed floor
        }

        /// <summary>Radius shrinks with the hideout intel level, but never below the minimum.</summary>
        private static float GetIntelRadius()
        {
            var intelLevel = GameUtils.GetIntelLevel() ?? 0;
            var radius = Settings.ScavHeatmapRadius.Value - intelLevel * Settings.ScavHeatmapRadiusReduction.Value;
            return Mathf.Max(_minRadius, radius);
        }

        /// <summary>A random direction and a 0.5-1 fraction of the fuzz distance, chosen once per blob and never re-rolled.</summary>
        private static Vector2 CreateStableOffset()
        {
            var direction = Random.insideUnitCircle.normalized;
            if (direction == Vector2.zero)
            {
                direction = Vector2.right;
            }

            return direction * Random.Range(0.5f, 1f);
        }

        private static float Distance2D(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static bool IsRegularScav(Player player)
        {
            return player.IsScav() && _scavRoles.Contains(player.Profile.Info.Settings.Role);
        }

        private void EnsureTicker()
        {
            if (_ticker != null || _map == null)
            {
                return;
            }

            _ticker = _map.gameObject.AddComponent<ScavHeatmapTicker>();
            _ticker.Provider = this;
        }

        private void RemoveBlob(ScavHeatCell blob)
        {
            _blobs.Remove(blob);
            if (blob == null)
            {
                return;
            }

            _map?.RemoveLayerBound(blob);
            blob.gameObject.SetActive(false);  // destroy is not guaranteed to be immediate
            Object.Destroy(blob.gameObject);
        }

        private void ClearBlobs()
        {
            foreach (var blob in _blobs.ToArray())
            {
                RemoveBlob(blob);
            }
        }

        private void Teardown()
        {
            _isShown = false;
            ClearBlobs();

            if (_ticker != null)
            {
                Object.Destroy(_ticker);
                _ticker = null;
            }
        }
    }

    /// <summary>Gives the provider a per-frame tick, since marker providers are plain objects.</summary>
    internal class ScavHeatmapTicker : MonoBehaviour
    {
        public ScavHeatmapProvider Provider { get; set; }

        private void Update()
        {
            Provider?.Update();
        }
    }
}
