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
    /// shrinks with your hideout intel level (never below a minimum). Blobs glide as the scavs move. A scav that dies or
    /// leaves range becomes a stationary ghost at its last position: it keeps counting at full strength for a (jittered)
    /// hold time, then falls to nothing, so the heat doesn't tell you when you killed the last one. Ghosts take part in
    /// merging like live scavs. Overlapping blobs stack into hotter areas. Per-floor visibility follows the map layers.
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

        // Rogues (exUsec) and Raiders (pmcBot) are shown only when enabled, and only from a configurable intel level
        private static readonly HashSet<WildSpawnType> _rogueRaiderRoles = new HashSet<WildSpawnType>
        {
            WildSpawnType.exUsec,
            WildSpawnType.pmcBot,
        };

        // Redraw throttles. 0 = every frame. Raise them if the map ever gets expensive with lots of scavs.
        private const float _visualInterval = 0f;    // seconds between visual updates
        private const float _minMove = 0f;           // meters; smaller position changes are not applied
        private const float _minHeatChange = 0f;     // smaller heat changes are not redrawn

        private const float _glideSpeed = 4f;        // how quickly a blob glides to a newly sampled position
        private const float _minRadius = 25f;        // the radius never goes below this, whatever the intel level
        private float _mergeDistance;                // the current map's pack merge distance
        private bool _flat;                          // the current map shows heat on every floor, stacked
        private const float _extentPadding = 25f;    // a merged blob covers its scavs' spread plus this much
        private const float _maxOffsetOfRadius = 0.5f;  // the offset never moves the blob more than this part of its radius

        private class Cluster
        {
            public readonly List<Vector3> Members = new List<Vector3>();
            public Vector3 Sum;       // weighted by member weight
            public float Weight;      // alive scav = 1, ghost = its remaining strength
            public int Level;
            public float Extent;
            public Vector3 Centroid => Sum / Weight;
        }

        /// <summary>A scav that died or left range: stays at its last position, full strength for Hold, then falls.</summary>
        private class Ghost
        {
            public string Id;
            public Vector3 Position;
            public int Level;
            public float DeathTime;
            public float Hold;
        }

        private readonly List<ScavHeatCell> _blobs = new List<ScavHeatCell>();
        private readonly List<(Vector3 position, int level, float weight)> _scavs = new List<(Vector3 position, int level, float weight)>();
        private readonly Dictionary<string, (Vector3 position, int level)> _known = new Dictionary<string, (Vector3 position, int level)>();
        private readonly Dictionary<string, (Vector3 position, int level)> _current = new Dictionary<string, (Vector3 position, int level)>();
        private readonly List<Ghost> _ghosts = new List<Ghost>();
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

            // _lastVisualTime is deliberately not reset: while the map was closed nothing ran, and the first update
            // after reopening applies all the fadeout time that passed in one step
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
            _known.Clear();
            _ghosts.Clear();
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
            var glide = 1f - Mathf.Exp(-_glideSpeed * deltaTime);
            var weightSmoothing = 1f - Mathf.Exp(-3f * deltaTime);

            // redraw everything when a visual setting changed (opacity, color, overlap count)
            var settingsHash = Settings.ScavHeatmapMaxOpacity.Value
                               + Settings.ScavHeatmapScavsForFullHeat.Value * 3.1f
                               + Settings.ScavHeatmapColor.Value.r * 7.3f
                               + Settings.ScavHeatmapColor.Value.g * 11.7f
                               + Settings.ScavHeatmapColor.Value.b * 13.1f
                               + (_flat ? 17.9f : 0f);
            var forceRedraw = !Mathf.Approximately(settingsHash, _lastSettingsHash);
            _lastSettingsHash = settingsHash;

            _expired.Clear();
            foreach (var blob in _blobs)
            {
                // heat is full while scavs are tracked and decays linearly once they're gone (fadeout)
                var heat = blob.Target > blob.Heat
                    ? blob.Target
                    : Mathf.Max(blob.Target, blob.Heat - deltaTime / fadeOutSeconds);

                blob.Heat = heat;  // always keep the real value; only the redraw below is throttled

                // the weight already follows the ghosts' hold and fall; this only smooths the steps between samples
                if (blob.Weight >= blob.DisplayWeight)
                {
                    blob.DisplayWeight = blob.Weight;
                }
                else
                {
                    blob.DisplayWeight = Mathf.Lerp(blob.DisplayWeight, blob.Weight, weightSmoothing);
                }

                if (!Mathf.Approximately(blob.Radius, blob.TargetRadius))
                {
                    blob.SetRadius(Mathf.Abs(blob.Radius - blob.TargetRadius) < 0.1f
                        ? blob.TargetRadius
                        : Mathf.Lerp(blob.Radius, blob.TargetRadius, glide));
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
            _current.Clear();

            var gameWorld = Singleton<GameWorld>.Instance;
            var mainPlayer = GameUtils.GetMainPlayer();
            var intelLevel = GameUtils.GetIntelLevel() ?? 0;
            var mapSettings = GetMapSettings(gameWorld);
            var canTrack = gameWorld != null && mainPlayer != null && _map != null && GameUtils.IsInRaid()
                           // same rule as the other intel-gated markers, except that 0 means "any intel level"
                           && intelLevel >= Settings.ScavHeatmapIntelLevel.Value
                           && (mapSettings == null || mapSettings.Enabled.Value);

            _flat = Settings.IsScavHeatmapFlat(mapSettings);
            _mergeDistance = mapSettings != null ? mapSettings.MergeDistance.Value : 75f;  // unknown map: half the fallback radius

            if (canTrack)
            {
                var range = Settings.ScavHeatmapRange.Value;
                var origin = mainPlayer.Position;
                var includeRogueRaiders = Settings.ScavHeatmapIncludeRogueRaiders.Value
                                          && intelLevel >= Settings.ScavHeatmapRogueRaiderIntelLevel.Value;

                foreach (var player in gameWorld.AllAlivePlayersList)
                {
                    if (player == null || player.IsYourPlayer || player.IsHeadlessClient() || player.IsBTRShooter()
                        || !IsTracked(player, includeRogueRaiders) || player.HasCorpse())
                    {
                        continue;
                    }

                    if (range > 0f && Vector3.Distance(player.Position, origin) > range)
                    {
                        continue;
                    }

                    var mapPosition = MathUtils.ConvertToMapPosition(player.Position);  // x, y = ground plane, z = height
                    // flat map: one shared "floor" so scavs above and below each other merge and no floor hides the heat
                    var level = _flat ? 0 : _map.GetLevelAt(mapPosition) ?? int.MinValue;
                    _scavs.Add((mapPosition, level, 1f));
                    _current[player.ProfileId] = (mapPosition, level);
                }

                UpdateGhosts();
                BuildClusters();
            }
            else
            {
                _known.Clear();
                _ghosts.Clear();
            }

            ApplyClusters(mapSettings, intelLevel);
        }

        /// <summary>Per-map settings for the current location, or null if the map is unknown.</summary>
        private static Settings.ScavHeatmapMapSettings GetMapSettings(GameWorld gameWorld)
        {
            var locationId = gameWorld?.LocationId?.ToLowerInvariant();
            return locationId != null && Settings.ScavHeatmapMaps.TryGetValue(locationId, out var mapSettings)
                ? mapSettings
                : null;
        }

        /// <summary>
        /// Scavs seen at the last sample but not now (dead or out of range) become ghosts at their last position.
        /// Ghosts are added to the scav list with their remaining strength, after the live scavs.
        /// </summary>
        private void UpdateGhosts()
        {
            var now = Time.unscaledTime;
            var holdSetting = Mathf.Max(0f, Settings.ScavHeatmapHoldSeconds.Value);

            foreach (var entry in _known)
            {
                if (_current.ContainsKey(entry.Key))
                {
                    continue;
                }

                _ghosts.Add(new Ghost
                {
                    Id = entry.Key,
                    Position = entry.Value.position,
                    Level = entry.Value.level,
                    DeathTime = now,
                    // jittered, so the moment the heat starts to fall doesn't give away when the last kill was
                    Hold = holdSetting * Random.Range(0.8f, 1.2f),
                });
            }

            _known.Clear();
            foreach (var entry in _current)
            {
                _known[entry.Key] = entry.Value;
            }

            var fall = Mathf.Max(1f, Settings.ScavHeatmapFadeSeconds.Value);
            for (var i = _ghosts.Count - 1; i >= 0; i--)
            {
                var ghost = _ghosts[i];
                var age = now - ghost.DeathTime;
                if (_current.ContainsKey(ghost.Id) || age >= ghost.Hold + fall)
                {
                    _ghosts.RemoveAt(i);  // back among the living, or fully faded
                    continue;
                }

                var weight = age <= ghost.Hold ? 1f : 1f - (age - ghost.Hold) / fall;
                if (weight > 0.01f)
                {
                    _scavs.Add((ghost.Position, ghost.Level, weight));
                }
            }
        }

        /// <summary>Merges scavs on the same floor that are within the merge distance into packs.</summary>
        private void BuildClusters()
        {
            var mergeDistance = _mergeDistance;

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
                target.Sum += scav.position * scav.weight;
                target.Weight += scav.weight;
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
        private void ApplyClusters(Settings.ScavHeatmapMapSettings mapSettings, int intelLevel)
        {
            _tracked.Clear();
            foreach (var blob in _blobs)
            {
                if (blob.Target > 0f)
                {
                    _tracked.Add(blob);
                }
            }

            var maxMatchDistance = Mathf.Max(60f, _mergeDistance + 20f);
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
            var radius = GetIntelRadius(mapSettings, intelLevel);

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
                blob.Flat = _flat;
                blob.Weight = cluster.Weight;
                blob.DisplayWeight = blob.Weight;
                _blobs.Add(blob);
                _map.AddLayerBound(blob);  // applies the current floor status immediately
                ApplyCluster(blob, cluster, radius);
                blob.SetRadius(blob.TargetRadius);  // new blobs start at their size, existing ones glide
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
            blob.TargetRadius = blobRadius;  // the blob glides to it (a ghost expiring would otherwise pop the size)

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
            blob.Flat = _flat;
            blob.Weight = cluster.Weight;
            blob.TargetPosition = centroid + new Vector3(offset.x, offset.y, 0f);
            _map.RefreshLayerBound(blob);  // the scavs may have changed floor
        }

        /// <summary>Radius shrinks with the hideout intel level, but never below the minimum.</summary>
        private static float GetIntelRadius(Settings.ScavHeatmapMapSettings mapSettings, int intelLevel)
        {
            // unknown map: fall back to a middle-of-the-road radius
            var baseRadius = mapSettings?.Radius.Value ?? 150f;
            var reduction = mapSettings?.Reduction.Value ?? 20f;
            return Mathf.Max(_minRadius, baseRadius - intelLevel * reduction);
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

        private static bool IsTracked(Player player, bool includeRogueRaiders)
        {
            if (!player.IsScav())
            {
                return false;
            }

            var role = player.Profile.Info.Settings.Role;
            return _scavRoles.Contains(role) || (includeRogueRaiders && _rogueRaiderRoles.Contains(role));
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
            _known.Clear();
            _ghosts.Clear();
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
