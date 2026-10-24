using System.Collections.Generic;
using System.Reflection;
using DynamicMaps.Config;
using DynamicMaps.DynamicMarkers;
using EFT;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace DynamicMaps.Patches
{
    // Shows the corpse reveal radius as a perk row in the Perception skill tooltip. The row is an extra BuffPanel built
    // from the tooltip's own template, so it looks like the vanilla perks: the description of a buff comes from its id,
    // so that lookup answers for our buff instead.
    internal static class PerceptionSkillUiPatches
    {
        private static readonly FieldInfo UiField = AccessTools.Field(typeof(UIElement), "UI");

        // Switching: a normal row, and the gold ELITE tag and background once the skill is elite
        private class CorpseRadiusBuff : SkillManager.Buff
        {
            internal readonly Skill Skill;

            internal CorpseRadiusBuff(Skill skill)
            {
                Skill = skill;
                Id = EBuffId.PerceptionLootDot;
                BuffType = SkillManager.EBuffType.Switching;
            }

            internal string Description()
            {
                var radius = Mathf.RoundToInt(CorpseMarkerProvider.GetRevealRadiusAtLevel(Skill.Level, Skill.IsEliteLevel));
                return $"Bodies you killed show on the map once you are within <color=#54c1ff>{radius} m</color> of them";
            }
        }

        // Shown at every level so the elite multiplier can be seen before it is unlocked
        private class EliteOtherCorpsesBuff : SkillManager.Buff
        {
            internal readonly Skill Skill;

            internal EliteOtherCorpsesBuff(Skill skill)
            {
                Skill = skill;
                Id = EBuffId.PerceptionmEliteNoIdea;
                BuffType = SkillManager.EBuffType.Switching;
            }

            internal static string Description(bool elite)
            {
                var radius = Settings.OtherCorpseCloseRadius.Value;
                var eliteRadius = Mathf.Max(radius, Settings.EliteOtherCorpseRadius.Value);
                return elite
                    ? $"Bodies you did not kill show on the map within <color=#54c1ff>{eliteRadius:0.#} m</color>, without looting them"
                    : $"Bodies you did not kill show on the map within <color=#54c1ff>{radius:0.#} m</color> without looting them, <color=#54c1ff>{eliteRadius:0.#} m</color> at elite";
            }
        }

        private static bool OtherRowEnabled => (Settings.OtherCorpsesReveal.Value == OtherCorpseReveal.Close || Settings.OtherCorpsesReveal.Value == OtherCorpseReveal.LootedOrClose) && Settings.OtherCorpseCloseRadius.Value > 0f;

        private static bool RowEnabled => Settings.OnlyShowNearbyCorpses.Value && Settings.CorpseRevealRadius.Value > 0f;

        internal class SkillTooltipShowPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(SkillTooltip).GetMethod(nameof(SkillTooltip.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

            [PatchPostfix]
            private static void Postfix(SkillTooltip __instance, Skill skill)
            {
                if (skill == null || skill.Id != ESkillId.Perception)
                {
                    return;
                }

                var buffs = new List<SkillManager.Buff>();
                if (RowEnabled)
                {
                    buffs.Add(new CorpseRadiusBuff(skill));
                }

                if (OtherRowEnabled)
                {
                    buffs.Add(new EliteOtherCorpsesBuff(skill));
                }

                if (buffs.Count == 0)
                {
                    return;
                }

                var ui = (UIParent)UiField.GetValue(__instance);
                ui.AddViewList(buffs, __instance._buffPanelTemplate, __instance._buffContainer,
                    (buff, panel) =>
                    {
                        panel.Show(skill, buff);
                        panel.UpdateBuff();
                        panel.UpdateVisibility();
                    });
            }
        }

        internal class BuffDescriptionPatch : ModulePatch
        {
            protected override MethodBase GetTargetMethod() => typeof(BuffIcon).GetMethod(nameof(BuffIcon.GetBuffDescription), BindingFlags.Static | BindingFlags.Public);

            [PatchPrefix]
            private static bool Prefix(SkillManager.Buff buff, ref string __result)
            {
                if (buff is CorpseRadiusBuff radius)
                {
                    __result = radius.Description();
                    return false;
                }

                if (buff is EliteOtherCorpsesBuff other)
                {
                    __result = EliteOtherCorpsesBuff.Description(other.Skill.IsEliteLevel);
                    return false;
                }

                return true;
            }
        }
    }
}
