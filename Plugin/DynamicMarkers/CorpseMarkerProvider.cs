using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using DynamicMaps.Config;
using DynamicMaps.Data;
using DynamicMaps.Patches;
using DynamicMaps.UI.Components;
using DynamicMaps.Utils;
using EFT;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DynamicMaps.DynamicMarkers
{
    public class CorpseMarkerProvider : IDynamicMarkerProvider
    {
        private const string _skullImagePath = "Markers/skull.png";

        // TODO: move to config
        private const string _friendlyCorpseCategory = "Friendly Corpse";
        private const string _friendlyCorpseImagePath = _skullImagePath;
        private static Color _friendlyCorpseColor = Color.Lerp(Color.blue, Color.white, 0.5f);

        private const string _killedCorpseCategory = "Killed Corpse";
        private const string _killedCorpseImagePath = _skullImagePath;

        private const string _killedBossCorpseCategory = "Killed Boss Corpse";
        private const string _killedBossCorpseImagePath = _skullImagePath;

        private const string _bossCorpseCategory = "Boss Corpse";
        private const string _bossCorpseImagePath = _skullImagePath;

        private const string _friendlyKilledCorpseCategory = "Friendly Killed Corpse";
        private const string _friendlyKilledCorpseImagePath = _skullImagePath;
        private static Color _friendlyKilledCorpseColor = Color.Lerp(Color.Lerp(Color.blue, Color.white, 0.5f), Color.red, 0.5f);

        private const string _friendlyKilledBossCorpseCategory = "Friendly Killed Boss Corpse";
        private const string _friendlyKilledBossCorpseImagePath = _skullImagePath;

        private const string _otherCorpseCategory = "Other Corpse";
        private const string _otherCorpseImagePath = _skullImagePath;
        //

        private bool _showFriendlyCorpses = true;
        public bool ShowFriendlyCorpses
        {
            get
            {
                return _showFriendlyCorpses;
            }

            set
            {
                HandleSetBoolOption(ref _showFriendlyCorpses, value);
            }
        }

        private bool _showKilledCorpses = true;
        public bool ShowKilledCorpses
        {
            get
            {
                return _showKilledCorpses;
            }

            set
            {
                HandleSetBoolOption(ref _showKilledCorpses, value);
            }
        }

        private bool _showFriendlyKilledCorpses = true;
        public bool ShowFriendlyKilledCorpses
        {
            get
            {
                return _showFriendlyKilledCorpses;
            }

            set
            {
                HandleSetBoolOption(ref _showFriendlyKilledCorpses, value);
            }
        }

        private bool _showBossCorpses = false;
        public bool ShowBossCorpses
        {
            get
            {
                return _showBossCorpses;
            }

            set
            {
                HandleSetBoolOption(ref _showBossCorpses, value);
            }
        }

        private bool _showOtherCorpses = false;
        public bool ShowOtherCorpses
        {
            get
            {
                return _showOtherCorpses;
            }

            set
            {
                HandleSetBoolOption(ref _showOtherCorpses, value);
            }
        }

        private const float _checkIntervalSeconds = 0.5f;

        private MapView _lastMapView;
        private Dictionary<Player, MapMarker> _corpseMarkers = [];
        private CorpseRevealTicker _ticker;
        private float _nextCheckTime;

        // corpses waiting for their marker, with their time of death
        private readonly Dictionary<Player, float> _pending = [];
        private readonly HashSet<Player> _pendingOthers = [];  // other corpses waiting for the close-up reveal
        private readonly HashSet<Player> _revealed = [];

        public void OnShowInRaid(MapView map)
        {
            _lastMapView = map;
            EnsureTicker();

            TryAddMarkers();
            CheckPending();

            // both stay subscribed while the map is hidden: deaths need their timestamp and looting happens with the map closed
            GameWorldUnregisterPlayerPatch.OnUnregisterPlayer -= OnUnregisterPlayer;
            GameWorldUnregisterPlayerPatch.OnUnregisterPlayer += OnUnregisterPlayer;
            PlayerInteractCorpsePatch.OnCorpseOpened -= OnCorpseOpened;
            PlayerInteractCorpsePatch.OnCorpseOpened += OnCorpseOpened;
        }

        public void OnHideInRaid(MapView map)
        {
            // keep subscriptions, see OnShowInRaid
        }

        public void OnRaidEnd(MapView map)
        {
            GameWorldUnregisterPlayerPatch.OnUnregisterPlayer -= OnUnregisterPlayer;
            PlayerInteractCorpsePatch.OnCorpseOpened -= OnCorpseOpened;

            _lastMapView = map;
            TryRemoveMarkers();
            RemoveTicker();

            _pending.Clear();
            _pendingOthers.Clear();
            _revealed.Clear();
        }

        public void OnMapChanged(MapView map, MapDef mapDef)
        {
            _lastMapView = map;
            EnsureTicker();

            foreach (var corpse in _corpseMarkers.Keys.ToList())
            {
                TryRemoveMarker(corpse);
                TryAddMarker(corpse);
            }
        }

        public void OnDisable(MapView map)
        {
            GameWorldUnregisterPlayerPatch.OnUnregisterPlayer -= OnUnregisterPlayer;
            PlayerInteractCorpsePatch.OnCorpseOpened -= OnCorpseOpened;
            TryRemoveMarkers();
            RemoveTicker();
        }

        /// <summary>Called by the ticker while the map object is active.</summary>
        internal void Update()
        {
            if (Time.unscaledTime < _nextCheckTime)
            {
                return;
            }

            _nextCheckTime = Time.unscaledTime + _checkIntervalSeconds;
            CheckPending();
        }

        private void OnCorpseOpened(string killedProfileId)
        {
            if (!GameUtils.IsInRaid())
            {
                return;
            }

            foreach (var player in Singleton<GameWorld>.Instance.AllPlayersEverExisted)
            {
                if (player.ProfileId != killedProfileId || !player.HasCorpse())
                {
                    continue;
                }

                // opening a corpse reveals it, whatever the near/looted rules say
                _pending.Remove(player);
                _pendingOthers.Remove(player);
                if (_corpseMarkers.TryGetValue(player, out var marker))
                {
                    ApplyLootedState(player, marker);
                }
                else
                {
                    TryAddMarker(player);
                }
            }
        }

        private void ApplyLootedState(Player player, MapMarker marker)
        {
            marker.AlphaScale = PlayerInteractCorpsePatch.LootedProfileIds.Contains(player.ProfileId) ? Settings.LootedCorpseAlpha.Value : 1f;
        }

        // distance within which a corpse killed by the main player's group gets its marker
        private static float GetRevealRadius(Player mainPlayer)
        {
            var perception = mainPlayer.Skills?.Perception;
            return GetRevealRadiusAtLevel(perception?.Level ?? 0, perception?.IsEliteLevel ?? false);
        }

        internal static float GetRevealRadiusAtLevel(int perceptionLevel, bool elite)
        {
            var radius = Settings.CorpseRevealRadius.Value;
            if (radius <= 0f)
            {
                return 0f;
            }

            radius += perceptionLevel * Settings.CorpseRevealRadiusPerPerceptionLevel.Value;
            if (elite)
            {
                radius += Settings.CorpseRevealRadiusEliteBonus.Value;
            }

            return radius;
        }

        // distance within which a corpse not killed by the main player's group gets its marker
        private static float GetOtherCorpseRadius(Player mainPlayer)
        {
            var mode = Settings.OtherCorpsesReveal.Value;
            if (mode != OtherCorpseReveal.Close && mode != OtherCorpseReveal.LootedOrClose)
            {
                return 0f;
            }

            var radius = Settings.OtherCorpseCloseRadius.Value;
            if (mainPlayer.Skills?.Perception?.IsEliteLevel ?? false)
            {
                radius = Mathf.Max(radius, Settings.EliteOtherCorpseRadius.Value);
            }

            return radius;
        }

        private void CheckPending()
        {
            if ((_pending.Count == 0 && _pendingOthers.Count == 0) || !GameUtils.IsInRaid())
            {
                return;
            }

            var mainPlayer = GameUtils.GetMainPlayer();
            if (mainPlayer == null)
            {
                return;
            }

            var delayEnabled = Settings.RevealKilledCorpsesAfterDelay.Value;
            var now = Time.unscaledTime;
            var delay = Settings.CorpseRevealDelaySeconds.Value;
            var radius = GetRevealRadius(mainPlayer);
            var mainPosition = ((IPlayer)mainPlayer).Position;

            foreach (var entry in _pending.ToList())
            {
                var player = entry.Key;
                if (player == null)
                {
                    _pending.Remove(player);
                    continue;
                }

                var delayPassed = delayEnabled && now - entry.Value >= delay;
                if (!delayPassed && Vector3.Distance(mainPosition, ((IPlayer)player).Position) > radius)
                {
                    continue;
                }

                _pending.Remove(player);
                _revealed.Add(player);
                TryAddMarker(player);
            }

            if (_pendingOthers.Count == 0)
            {
                return;
            }

            var closeRadius = GetOtherCorpseRadius(mainPlayer);
            foreach (var player in _pendingOthers.ToList())
            {
                if (player == null)
                {
                    _pendingOthers.Remove(player);
                    continue;
                }

                if (closeRadius <= 0f || Vector3.Distance(mainPosition, ((IPlayer)player).Position) > closeRadius)
                {
                    continue;
                }

                _pendingOthers.Remove(player);
                _revealed.Add(player);
                TryAddMarker(player);
            }
        }

        private void EnsureTicker()
        {
            if (_ticker != null || _lastMapView == null)
            {
                return;
            }

            _ticker = _lastMapView.gameObject.AddComponent<CorpseRevealTicker>();
            _ticker.Provider = this;
        }

        private void RemoveTicker()
        {
            if (_ticker == null)
            {
                return;
            }

            Object.Destroy(_ticker);
            _ticker = null;
        }

        private void OnUnregisterPlayer(IPlayer iPlayer)
        {
            if (iPlayer is not Player)
            {
                return;
            }

            var player = iPlayer as Player;
            if (player.HasCorpse())
            {
                TryAddMarker(player);
            }
        }

        private void TryRemoveMarkers()
        {
            foreach (var corpse in _corpseMarkers.Keys.ToList())
            {
                TryRemoveMarker(corpse);
            }

            _corpseMarkers.Clear();
        }

        private void TryAddMarkers()
        {
            if (!GameUtils.IsInRaid())
            {
                return;
            }

            // add all players that have spawned already in raid
            var gameWorld = Singleton<GameWorld>.Instance;
            foreach (var player in gameWorld.AllPlayersEverExisted)
            {
                if (!player.HasCorpse() || _corpseMarkers.ContainsKey(player))
                {
                    continue;
                }

                TryAddMarker(player);
            }
        }

        public void RefreshMarkers()
        {
            if (!GameUtils.IsInRaid()) return;

            foreach (var marker in _corpseMarkers.ToArray())
            {
                TryRemoveMarker(marker.Key);
            }

            var corpses = Singleton<GameWorld>.Instance.AllPlayersEverExisted
                .Where(p => p.HasCorpse());
            
            foreach (var corpse in corpses)
            {
                TryAddMarker(corpse);
            }
        }
        
        private void TryAddMarker(Player player)
        {
            if (_lastMapView is null || _corpseMarkers.ContainsKey(player))
            {
                return;
            }

            var intelLevel = GameUtils.GetIntelLevel();
            
            if (Settings.ShowCorpseIntelLevel.Value > intelLevel) return;
            
            // set category and color
            var category = _otherCorpseCategory;
            var imagePath = _otherCorpseImagePath;
            var color = Settings.KilledOtherColor.Value;
            
            if (player.IsGroupedWithMainPlayer())
            {
                category = _friendlyCorpseCategory;
                imagePath = _friendlyCorpseImagePath;
                color = _friendlyCorpseColor;
            }
            else if (player.IsTrackedBoss() && player.DidMainPlayerKill())
            {
                category = _killedBossCorpseCategory;
                imagePath = _killedBossCorpseImagePath;
                color = Settings.KilledBossColor.Value;
            }
            else if (player.DidMainPlayerKill())
            {
                category = _killedCorpseCategory;
                imagePath = _killedCorpseImagePath;
                color = Settings.KilledCorpseColor.Value;
            }
            else if (player.IsTrackedBoss() && player.DidTeammateKill())
            {
                category = _friendlyKilledBossCorpseCategory;
                imagePath = _friendlyKilledBossCorpseImagePath;
                color = Settings.KilledBossColor.Value;
            }
            else if (player.DidTeammateKill())
            {
                category = _friendlyKilledCorpseCategory;
                imagePath = _friendlyKilledCorpseImagePath;
                color = _friendlyKilledCorpseColor;
            }
            else if (player.IsTrackedBoss())
            {
                category = _bossCorpseCategory;
                imagePath = _bossCorpseImagePath;
                color = Settings.KilledBossColor.Value;
            }

            if (!ShouldShowCategory(category))
            {
                return;
            }

            // the map shouldn't confirm a kill or give away other corpses: unless the main player opened it,
            // a corpse is held back until it is near (or the delay passed, see CheckPending), and other corpses
            // until opened. Friendly corpses are never held back.
            var isOpened = PlayerInteractCorpsePatch.LootedProfileIds.Contains(player.ProfileId);
            if (!_revealed.Contains(player) && category != _friendlyCorpseCategory)
            {
                var isOwnKill = category == _killedCorpseCategory || category == _killedBossCorpseCategory
                    || category == _friendlyKilledCorpseCategory || category == _friendlyKilledBossCorpseCategory;

                if (isOwnKill)
                {
                    // opening a corpse of your own kill reveals it, wherever it was
                    if (Settings.OnlyShowNearbyCorpses.Value && !isOpened)
                    {
                        if (!PlayerOnDeadPatch.DeathTimes.TryGetValue(player.ProfileId, out var deathTime))
                        {
                            deathTime = Time.unscaledTime;
                        }

                        _pending.TryAdd(player, deathTime);
                        return;
                    }
                }
                else
                {
                    var mode = Settings.OtherCorpsesReveal.Value;
                    var byLoot = mode == OtherCorpseReveal.Looted || mode == OtherCorpseReveal.LootedOrClose;
                    var byClose = mode == OtherCorpseReveal.Close || mode == OtherCorpseReveal.LootedOrClose;

                    if (mode != OtherCorpseReveal.Always && !(byLoot && isOpened))
                    {
                        if (byClose && Settings.OtherCorpseCloseRadius.Value > 0f)
                        {
                            _pendingOthers.Add(player);
                        }

                        return;
                    }
                }
            }

            var markerDef = new MapMarkerDef
            {
                Category = category,
                ImagePath = imagePath,
                Text = player.Profile.GetCorrectedNickname(),
                Color = color,
                Position = MathUtils.ConvertToMapPosition(((IPlayer)player).Position)
            };

            // try adding marker
            var marker = _lastMapView.AddMapMarker(markerDef);
            _corpseMarkers[player] = marker;
            ApplyLootedState(player, marker);
        }

        private void RemoveDisabledMarkers()
        {
            foreach (var corpse in _corpseMarkers.Keys.ToList())
            {
                var marker = _corpseMarkers[corpse];
                if (!ShouldShowCategory(marker.Category))
                {
                    TryRemoveMarker(corpse);
                }
            }
        }

        private void TryRemoveMarker(Player player)
        {
            if (!_corpseMarkers.ContainsKey(player))
            {
                return;
            }

            _corpseMarkers[player].ContainingMapView.RemoveMapMarker(_corpseMarkers[player]);
            _corpseMarkers.Remove(player);
        }

        private bool ShouldShowCategory(string category)
        {
            switch (category)
            {
                case _friendlyCorpseCategory:
                    return _showFriendlyCorpses;
                case _killedCorpseCategory:
                case _killedBossCorpseCategory:
                    return _showKilledCorpses;
                case _friendlyKilledCorpseCategory:
                case _friendlyKilledBossCorpseCategory:
                    return _showFriendlyKilledCorpses;
                case _bossCorpseCategory:
                    return _showBossCorpses;
                case _otherCorpseCategory:
                    return _showOtherCorpses;
                default:
                    return false;
            }
        }

        private void HandleSetBoolOption(ref bool boolOption, bool value)
        {
            if (value == boolOption)
            {
                return;
            }

            boolOption = value;

            if (boolOption)
            {
                TryAddMarkers();
            }
            else
            {
                RemoveDisabledMarkers();
            }
        }

        public void OnShowOutOfRaid(MapView map)
        {
            // do nothing
        }

        public void OnHideOutOfRaid(MapView map)
        {
            // do nothing
        }
    }

    /// <summary>Gives the provider a per-frame tick, since marker providers are plain objects.</summary>
    internal class CorpseRevealTicker : MonoBehaviour
    {
        public CorpseMarkerProvider Provider { get; set; }

        private void Update()
        {
            Provider?.Update();
        }
    }
}
