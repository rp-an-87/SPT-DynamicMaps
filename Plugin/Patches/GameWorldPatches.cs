using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPT.Reflection.Patching;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;

namespace DynamicMaps.Patches
{
    internal class GameStartedPatch : ModulePatch
    {
        public static List<LootableContainer> HiddenStashes { get; } = [];

        /// <summary>When the raid started (Time.unscaledTime, you got control), or -1 before. Scavs that spawned while loading count from here.</summary>
        public static float StartTime { get; set; } = -1f;
        
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted));
        }

        [PatchPostfix]
        public static void PatchPostfix(GameWorld __instance)
        {
            StartTime = UnityEngine.Time.unscaledTime;

            // Credit to RaiRai for finding the hidden stash names
            var caches = LocationScene.GetAllObjects<LootableContainer>()
                .Where( x => 
                    x.name.StartsWith("scontainer_wood_CAP")  || 
                    x.name.StartsWith("scontainer_Blue_Barrel_Base_Cap"))
                .ToList();


            foreach (var cache in caches)
            {
                HiddenStashes.Add(cache);
            }
        }
    }
    
    internal class GameWorldOnDestroyPatch : ModulePatch
    {
        internal static event Action OnRaidEnd;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnDestroy));
        }

        [PatchPrefix]
        public static void PatchPrefix()
        {
            try
            {
                OnRaidEnd?.Invoke();
                GameStartedPatch.HiddenStashes.Clear();
                GameStartedPatch.StartTime = -1f;
                PlayerOnDeadPatch.DeathTimes.Clear();
                PlayerOnDeadPatch.HeatmapDeaths.Clear();
                GameWorldRegisterPlayerPatch.SpawnTimes.Clear();
                PlayerInteractCorpsePatch.LootedProfileIds.Clear();
            }
            catch(Exception e)
            {
                Plugin.Log.LogError($"Caught error while doing end of raid calculations");
                Plugin.Log.LogError($"{e.Message}");
                Plugin.Log.LogError($"{e.StackTrace}");
            }
        }
    }

    internal class GameWorldRegisterPlayerPatch : ModulePatch
    {
        /// <summary>When each player was registered in the raid (Time.unscaledTime), by profile id. For the scav heatmap fade-in, filled while the map is closed.</summary>
        internal static Dictionary<string, float> SpawnTimes { get; } = [];

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.RegisterPlayer));
        }

        [PatchPostfix]
        public static void PatchPostfix(IPlayer iPlayer)
        {
            if (iPlayer != null && !SpawnTimes.ContainsKey(iPlayer.ProfileId))
            {
                SpawnTimes[iPlayer.ProfileId] = UnityEngine.Time.unscaledTime;
            }
        }
    }

    internal class GameWorldUnregisterPlayerPatch : ModulePatch
    {
        internal static event Action<IPlayer> OnUnregisterPlayer;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.UnregisterPlayer));
        }

        [PatchPostfix]
        public static void PatchPostfix(IPlayer iPlayer)
        {
            OnUnregisterPlayer?.Invoke(iPlayer);
        }
    }

    internal class LootItemInitPatch : ModulePatch
    {
        internal static event Action<LootItem> OnRegisterLoot;

        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(LootItem), nameof(LootItem.Init));
            //return typeof(GameWorld).GetMethod("RegisterLoot").MakeGenericMethod(typeof(LootItem));
        }

        [PatchPostfix]
        public static void PatchPostfix(LootItem __instance, Item item)
        {
            if (item == null) return;
            OnRegisterLoot?.Invoke(__instance); 
        }
    }

    internal class GameWorldDestroyLootPatch : ModulePatch
    {
        internal static event Action<LootItem> OnDestroyLoot;

        protected override MethodBase GetTargetMethod()
        {
            return typeof(GameWorld).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "DestroyLoot" && m.GetParameters().FirstOrDefault(p => p.Name == "loot") != null);
        }

        [PatchPrefix]
        public static void PatchPrefix(Object loot)
        {
            try
            {
                if (loot is LootItem lootItem)
                {
                    OnDestroyLoot?.Invoke(lootItem);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Caught error while running DestroyLoot patch");
                Plugin.Log.LogError($"{e.Message}");
                Plugin.Log.LogError($"{e.StackTrace}");
            }
        }
    }
}
