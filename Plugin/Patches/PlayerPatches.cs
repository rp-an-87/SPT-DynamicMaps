using System;
using System.Collections.Generic;
using System.Reflection;
using SPT.Reflection.Patching;
using DynamicMaps.Config;
using DynamicMaps.Utils;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace DynamicMaps.Patches
{
    internal class PlayerOnDeadPatch : ModulePatch
    {
        internal static event Action<Player> OnDead;

        /// <summary>When each dead player died (Time.unscaledTime), by profile id. Filled even while the map has never been shown.</summary>
        internal static Dictionary<string, float> DeathTimes { get; } = [];

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(Player), nameof(Player.OnDead));
        }

        [PatchPostfix]
        public static void PatchPostfix(Player __instance)
        {
            // only needed by the reveal delay, which is off by default
            if (Settings.RevealKilledCorpsesAfterDelay.Value)
            {
                DeathTimes[__instance.ProfileId] = Time.unscaledTime;
            }

            OnDead?.Invoke(__instance);
        }
    }

    internal class PlayerInteractCorpsePatch : ModulePatch
    {
        /// <summary>Profile id of the dead player whose corpse the main player just opened.</summary>
        internal static event Action<string> OnCorpseOpened;

        /// <summary>Profile ids of corpses the main player opened this raid. Filled even while the map has never been shown.</summary>
        internal static HashSet<string> LootedProfileIds { get; } = [];

        protected override MethodBase GetTargetMethod()
        {
            // the offline main player is a LocalPlayer, which doesn't override Player.Interact (ClientPlayer, used online, does)
            return AccessTools.Method(typeof(Player), nameof(Player.Interact));
        }

        [PatchPostfix]
        public static void PatchPostfix(Player __instance, IItemOwner loot)
        {
            try
            {
                // bots loot corpses too, only the main player's looting counts
                if (__instance != GameUtils.GetMainPlayer())
                {
                    return;
                }

                if (loot is not CorpseItemController corpseController)
                {
                    if (loot?.RootItem is InventoryEquipment)
                    {
                        Plugin.Log.LogInfo($"Interact with an equipment owner that is not a corpse controller: {loot.GetType().Name}");
                    }

                    return;
                }

                var killedProfileId = corpseController.KilledProfileID;
                if (string.IsNullOrEmpty(killedProfileId))
                {
                    return;
                }

                LootedProfileIds.Add(killedProfileId);
                Plugin.Log.LogInfo($"Opened corpse {killedProfileId}");
                OnCorpseOpened?.Invoke(killedProfileId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Caught error while running corpse interact patch");
                Plugin.Log.LogError($"{e.Message}");
                Plugin.Log.LogError($"{e.StackTrace}");
            }
        }
    }

    internal class PlayerInventoryThrowItemPatch : ModulePatch
    {
        internal static event Action<int, Item> OnThrowItem;
        internal static Dictionary<int, Item> ThrownItems = [];

        private bool _hasRegisteredEvents;

        protected override MethodBase GetTargetMethod()
        {
            if (!_hasRegisteredEvents)
            {
                GameWorldOnDestroyPatch.OnRaidEnd += OnRaidEnd;
                GameWorldDestroyLootPatch.OnDestroyLoot += OnDestroyLoot;
                _hasRegisteredEvents = true;
            }

            return AccessTools.Method(typeof(Player.PlayerInventoryController),
                                      nameof(Player.PlayerInventoryController.ThrowItem));
        }

        [PatchPostfix]
        public static void PatchPostfix(Player.PlayerInventoryController __instance, Item item)
        {
            // only look at main player's dropped items
            var player = __instance.Player;
            if (player == null || player != GameUtils.GetMainPlayer())
            {
                return;
            }

            var itemNetId = item.Id.GetHashCode();
            ThrownItems[itemNetId] = item;
            OnThrowItem?.Invoke(itemNetId, item);
        }

        internal static void OnDestroyLoot(LootItem lootItem)
        {
            if (lootItem == null || lootItem.Item == null)
            {
                return;
            }

            ThrownItems.Remove(lootItem.GetNetId());
        }

        internal static void OnRaidEnd()
        {
            ThrownItems.Clear();
        }
    }
}
