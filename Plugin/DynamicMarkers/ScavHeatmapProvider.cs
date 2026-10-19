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
    /// Every regular scav gets a soft heat blob centered on it, refreshed every few seconds. Blobs fade in when a scav
    /// first shows up, glide along as it moves, and when it dies or leaves they stay where they were and fade out, so
    /// killing a scav doesn't make the heat vanish instantly. Overlapping blobs stack into hotter areas.
    /// Per-floor visibility follows the map layers, like markers.
    ///
    /// Performance: scav positions are read every few seconds, visuals update ~10 times a second and only when a value
    /// actually changed (changing a UI graphic forces a canvas rebuild), and nothing runs while the map is hidden.
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

        private const float _visualInterval = 0.1f;   // seconds between visual updates
        private const float _glideSpeed = 2.5f;       // how quickly a blob glides to a newly sampled position
        private const float _minMove = 0.25f;         // meters; smaller position changes are not applied
        private const float _minHeatChange = 0.004f;  // smaller heat changes are not redrawn

        private readonly Dictionary<string, ScavHeatCell> _blobs = new Dictionary<string, ScavHeatCell>();
        private readonly HashSet<string> _seen = new HashSet<string>();
        private readonly List<string> _expired = new List<string>();
        private readonly List<string> _keyBuffer = new List<string>();

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

        /// <summary>Called every frame by the ticker while the map is shown; does real work only a few times a second.</summary>
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
            foreach (var entry in _blobs)
            {
                var blob = entry.Value;

                // heat builds up linearly while the scav is tracked (fade-in) and decays linearly once it's gone (fadeout)
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

                if (blob.Target > 0f && (blob.TargetPosition - blob.Position).sqrMagnitude > _minMove * _minMove)
                {
                    blob.Position = Vector3.Lerp(blob.Position, blob.TargetPosition, glide);
                }

                var endOfFade = (heat <= 0f || heat >= 1f) && !Mathf.Approximately(blob.AppliedHeat, heat);
                if (forceRedraw || endOfFade || Mathf.Abs(heat - blob.AppliedHeat) > _minHeatChange)
                {
                    blob.ApplyVisual();
                }

                if (blob.Target <= 0f && blob.Heat <= 0.01f)
                {
                    _expired.Add(entry.Key);
                }
            }

            foreach (var key in _expired)
            {
                RemoveBlob(key);
            }
        }

        private void Sample()
        {
            _nextSampleTime = Time.unscaledTime + Mathf.Max(1f, Settings.ScavHeatmapUpdateSeconds.Value);

            // anything not seen below stops being tracked, and fades out where it was last seen
            _seen.Clear();

            var gameWorld = Singleton<GameWorld>.Instance;
            var mainPlayer = GameUtils.GetMainPlayer();
            var canTrack = gameWorld != null && mainPlayer != null && _map != null && GameUtils.IsInRaid()
                           // same rule as the other intel-gated markers, except that 0 means "any intel level"
                           && (GameUtils.GetIntelLevel() ?? 0) >= Settings.ScavHeatmapIntelLevel.Value;

            if (canTrack)
            {
                var radius = Settings.ScavHeatmapRadius.Value;
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
                    var id = player.ProfileId;
                    _seen.Add(id);

                    if (!_blobs.TryGetValue(id, out var blob))
                    {
                        blob = ScavHeatCell.Create(_map.MapMarkerContainer, mapPosition, radius);
                        _blobs[id] = blob;
                        _map.AddLayerBound(blob);  // applies the current floor status immediately
                    }

                    if (!Mathf.Approximately(blob.Radius, radius))
                    {
                        blob.SetRadius(radius);
                    }

                    blob.TargetPosition = mapPosition;
                    blob.Target = 1f;
                    _map.RefreshLayerBound(blob);  // the scav may have changed floor
                }
            }

            foreach (var entry in _blobs)
            {
                if (!_seen.Contains(entry.Key))
                {
                    entry.Value.Target = 0f;
                }
            }
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

        private void RemoveBlob(string key)
        {
            if (!_blobs.TryGetValue(key, out var blob))
            {
                return;
            }

            _blobs.Remove(key);
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
            _keyBuffer.Clear();
            _keyBuffer.AddRange(_blobs.Keys);
            foreach (var key in _keyBuffer)
            {
                RemoveBlob(key);
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
