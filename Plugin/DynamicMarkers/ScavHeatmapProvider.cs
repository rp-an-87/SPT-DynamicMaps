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
    /// Positions are sampled every few seconds, snapped to a grid and shown as soft blobs; heat only rises on a
    /// sample and fades out slowly afterwards, so killing a scav doesn't make the heat vanish instantly.
    /// Per-floor visibility follows the map layers, like markers.
    /// </summary>
    public class ScavHeatmapProvider : IDynamicMarkerProvider
    {
        // grid x, grid y, map level (floor)
        private readonly Dictionary<(int, int, int), ScavHeatCell> _cells = new Dictionary<(int, int, int), ScavHeatCell>();
        private readonly Dictionary<(int, int, int), (int count, float heightSum)> _samples = new Dictionary<(int, int, int), (int count, float heightSum)>();

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

        private MapView _map;
        private ScavHeatmapTicker _ticker;
        private bool _isShown;
        private float _nextSampleTime;
        private float _lastUpdateTime;

        public void OnShowInRaid(MapView map)
        {
            _map = map;
            _isShown = true;
            EnsureTicker();

            _lastUpdateTime = Time.unscaledTime;
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
            ClearCells();
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

            var deltaTime = Mathf.Max(0f, now - _lastUpdateTime);
            _lastUpdateTime = now;

            var fadeSeconds = Mathf.Max(1f, Settings.ScavHeatmapFadeSeconds.Value);
            var expired = new List<(int, int, int)>();
            foreach (var entry in _cells)
            {
                var cell = entry.Value;
                // heat jumps up to the sampled value but only decays linearly, over the configured fadeout time
                cell.Heat = Mathf.Max(cell.Target, cell.Heat - deltaTime / fadeSeconds);
                cell.ApplyVisual();

                if (cell.Target <= 0f && cell.Heat <= 0.01f)
                {
                    expired.Add(entry.Key);
                }
            }

            foreach (var key in expired)
            {
                RemoveCell(key);
            }
        }

        private void Sample()
        {
            _nextSampleTime = Time.unscaledTime + Mathf.Max(1f, Settings.ScavHeatmapUpdateSeconds.Value);

            // nothing is wanted right now: let existing cells fade out
            foreach (var cell in _cells.Values)
            {
                cell.Target = 0f;
            }

            var gameWorld = Singleton<GameWorld>.Instance;
            var mainPlayer = GameUtils.GetMainPlayer();
            if (gameWorld == null || mainPlayer == null || _map == null || !GameUtils.IsInRaid())
            {
                return;
            }

            // same rule as the other intel-gated markers, except that 0 means "any intel level"
            if ((GameUtils.GetIntelLevel() ?? 0) < Settings.ScavHeatmapIntelLevel.Value)
            {
                return;
            }

            var cellSize = Settings.ScavHeatmapCellSize.Value;
            var range = Settings.ScavHeatmapRange.Value;
            var origin = mainPlayer.Position;

            _samples.Clear();
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
                var level = _map.GetLevelAt(mapPosition) ?? int.MinValue;
                var key = (Mathf.FloorToInt(mapPosition.x / cellSize), Mathf.FloorToInt(mapPosition.y / cellSize), level);

                _samples.TryGetValue(key, out var sample);
                _samples[key] = (sample.count + 1, sample.heightSum + mapPosition.z);
            }

            var scavsForFullHeat = Mathf.Max(1, Settings.ScavHeatmapScavsForFullHeat.Value);
            foreach (var entry in _samples)
            {
                var key = entry.Key;
                var averageHeight = entry.Value.heightSum / entry.Value.count;

                if (!_cells.TryGetValue(key, out var cell))
                {
                    var center = new Vector3((key.Item1 + 0.5f) * cellSize, (key.Item2 + 0.5f) * cellSize, averageHeight);
                    cell = ScavHeatCell.Create(_map.MapMarkerContainer, center, cellSize);
                    _cells[key] = cell;
                    _map.AddLayerBound(cell);  // applies the current floor status immediately
                }

                cell.SetCellSize(cellSize);
                cell.Target = Mathf.Clamp01(entry.Value.count / (float)scavsForFullHeat);
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

        private void RemoveCell((int, int, int) key)
        {
            if (!_cells.TryGetValue(key, out var cell))
            {
                return;
            }

            _cells.Remove(key);
            if (cell == null)
            {
                return;
            }

            _map?.RemoveLayerBound(cell);
            cell.gameObject.SetActive(false);  // destroy is not guaranteed to be immediate
            Object.Destroy(cell.gameObject);
        }

        private void ClearCells()
        {
            foreach (var key in new List<(int, int, int)>(_cells.Keys))
            {
                RemoveCell(key);
            }
        }

        private void Teardown()
        {
            _isShown = false;
            ClearCells();

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
