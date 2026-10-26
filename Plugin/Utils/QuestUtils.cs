using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using DynamicMaps.Data;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using HarmonyLib;
using UnityEngine;

namespace DynamicMaps.Utils
{
    // NOTE: Most of this is adapted from work done for Prop's GTFO mod (https://github.com/dvize/GTFO)
    // this likely does not count as a "substantial portion" of the software
    // under MIT license (https://github.com/dvize/GTFO/blob/master/LICENSE.txt)
    public static class QuestUtils
    {
        // reflection
        /*private static FieldInfo _playerQuestControllerField = AccessTools.Field(typeof(Player), "_questController");
        private static PropertyInfo _questControllerQuestsProperty = AccessTools.Property(typeof(AbstractQuestControllerClass), "Quests");
        private static FieldInfo _questsListField = AccessTools.Field(_questControllerQuestsProperty.PropertyType, "List_1");

        private static MethodInfo _questsGetConditionalMethod = AccessTools.Method(_questControllerQuestsProperty.PropertyType, "GetConditional", new Type[] { typeof(string) });

        private static Type _questType = _questControllerQuestsProperty.PropertyType.BaseType.GetGenericArguments()[0];
        private static MethodInfo _questIsConditionDone = AccessTools.Method(_questType, "IsConditionDone");

        private static FieldInfo _conditionCounterTemplateField = AccessTools.Field(typeof(ConditionCounterCreator), "TemplateConditions");
        private static FieldInfo _templateConditionsConditionsField = AccessTools.Field(_conditionCounterTemplateField.FieldType, "Conditions");
        private static FieldInfo _conditionListField = AccessTools.Field(_templateConditionsConditionsField.FieldType, "List_0");*/
        //

        // TODO: move to config
        private const string _questCategory = "Quest";
        private const string _questImagePath = "Markers/quest.png";
        private static Vector2 _questPivot = new Vector2(0.5f, 0f);
        private static Color _questColor = Color.green;
        //

        public static List<TriggerWithId> TriggersWithIds;
        public static List<LootItem> QuestItems;

        /// <summary>Zone areas (map position and outline in meters) found by the last <see cref="GetMarkerDefsForPlayer"/>.</summary>
        public static readonly List<QuestZoneArea> ZoneAreas = new List<QuestZoneArea>();

        public struct QuestZoneArea
        {
            public Vector3 Position;
            public Vector2[] Outline;  // closed loop relative to Position, map meters
        }

        internal static void TryCaptureQuestData()
        {
            var gameWorld = Singleton<GameWorld>.Instance;

            if (TriggersWithIds == null)
            {
                TriggersWithIds = GameObject.FindObjectsOfType<TriggerWithId>().ToList();
            }

            if (QuestItems == null)
            {
                QuestItems = Traverse.Create(gameWorld)
                    .Field("LootItems")
                    .Field("_iteration")
                    .GetValue<List<LootItem>>()
                    .Where(i => i.Item.QuestItem).ToList();
            }
        }

        internal static void DiscardQuestData()
        {
            if (TriggersWithIds != null)
            {
                TriggersWithIds.Clear();
                TriggersWithIds = null;
            }

            if (QuestItems != null)
            {
                QuestItems.Clear();
                QuestItems = null;
            }
        }

        internal static IEnumerable<MapMarkerDef> GetMarkerDefsForPlayer(Player player)
        {
            if (TriggersWithIds == null || QuestItems == null)
            {
                Plugin.Log.LogWarning($"TriggersWithIds null: {TriggersWithIds == null} or QuestItems null: {QuestItems == null}");
                return null;
            }

            var markers = new List<MapMarkerDef>();
            ZoneAreas.Clear();

            var quests = GetIncompleteQuests(player);
            foreach (var quest in quests)
            {
                markers.AddRange(GetMarkerDefsForQuest(player, quest));
            }

            return markers;
        }

        internal static IEnumerable<MapMarkerDef> GetMarkerDefsForQuest(Player player, QuestDataClass quest)
        {
            var markers = new List<MapMarkerDef>();

            var conditions = GetIncompleteQuestConditions(player, quest);
            foreach (var condition in conditions)
            {
                var questName = quest.Template.NameLocaleKey.BSGLocalized();
                var conditionDescription = condition.id.BSGLocalized();

                var positions = GetPositionsForCondition(condition, questName, conditionDescription);
                foreach (var position in positions)
                {
                    var isDuplicate = false;

                    // check against previously created markers for duplicate position
                    foreach (var marker in markers)
                    {
                        if (MathUtils.ApproxEquals(marker.Position.x, position.x)
                         && MathUtils.ApproxEquals(marker.Position.y, position.y)
                         && MathUtils.ApproxEquals(marker.Position.z, position.z))
                        {
                            isDuplicate = true;
                            break;
                        }
                    }

                    if (isDuplicate)
                    {
                        continue;
                    }

                    markers.Add(CreateQuestMapMarkerDef(position, questName, conditionDescription));
                }
            }

            return markers;
        }

        private static IEnumerable<Vector3> GetPositionsForCondition(Condition condition, string questName,
                                                                    string conditionDescription)
        {
            switch (condition)
            {
                case ConditionZone zoneCondition:
                {
                    foreach (var position in GetPositionsForZoneId(zoneCondition.zoneId, questName, conditionDescription))
                    {
                        yield return position;
                    }
                    break;
                }
                case ConditionLaunchFlare flareCondition:
                {
                    foreach (var position in GetPositionsForZoneId(flareCondition.zoneID, questName, conditionDescription))
                    {
                        yield return position;
                    }
                    break;
                }
                case ConditionVisitPlace place:
                {
                    foreach (var position in GetPositionsForZoneId(place.target, questName, conditionDescription))
                    {
                        yield return position;
                    }
                    break;
                }
                case ConditionInZone zone:
                {
                    foreach (var zoneId in zone.zoneIds)
                    {
                        foreach (var position in GetPositionsForZoneId(zoneId, questName, conditionDescription))
                        {
                            yield return position;
                        }
                    }
                    break;
                }
                case ConditionFindItem findItemCondition:
                {
                    foreach (var position in GetPositionsForQuestItems(findItemCondition.target, questName, conditionDescription))
                    {
                        yield return position;
                    }
                    break;
                }
                case ConditionExitName exitCondition:
                {
                    var exfils = Singleton<GameWorld>.Instance.ExfiltrationController.ExfiltrationPoints;
                    var specifiedExit = exfils.FirstOrDefault(e => e.Settings.Name == exitCondition.exitName);

                    if (specifiedExit != null)
                    {
                        yield return MathUtils.ConvertToMapPosition(specifiedExit.transform);
                    }

                    break;
                }
                case ConditionCounterCreator conditionCreator:
                {
                    // this will recurse back into this method
                    foreach (var position in GetPositionsForConditionCreator(conditionCreator, questName, conditionDescription))
                    {
                        yield return position;
                    }
                    break;
                }
                default:
                {
                    break;
                }
            }
        }

        private static IEnumerable<Vector3> GetPositionsForConditionCreator(ConditionCounterCreator conditionCreator,
                                                                            string questName, string conditionDescription)
        {
            var counter = conditionCreator._templateConditions;
            foreach (var condition in counter.Conditions)
            {
                foreach (var position in GetPositionsForCondition(condition, questName, conditionDescription))
                {
                    yield return position;
                }
            }
        }

        private static IEnumerable<Vector3> GetPositionsForZoneId(string zoneId, string questName,
                                                                  string conditionDescription)
        {
            var zones = TriggersWithIds?.GetZoneTriggers(zoneId);
            foreach (var zone in zones)
            {
                TryAddZoneArea(zone);
                yield return MathUtils.ConvertToMapPosition(zone.transform.position);
            }
        }

        /// <summary>
        /// Records the horizontal outline of each collider of a zone trigger: box colliders as their real (rotated)
        /// rectangle, sphere colliders as a circle, anything else as its bounding rectangle.
        /// </summary>
        private static void TryAddZoneArea(TriggerWithId zone)
        {
            foreach (var collider in zone.GetComponentsInChildren<Collider>())
            {
                if (collider == null)
                {
                    continue;
                }

                var worldPoints = GetOutlineWorldPoints(collider);
                if (worldPoints == null)
                {
                    continue;
                }

                var mapPoints = worldPoints.Select(p => (Vector2)MathUtils.ConvertToMapPosition(p)).ToArray();
                var center = Vector2.zero;
                foreach (var point in mapPoints)
                {
                    center += point;
                }
                center /= mapPoints.Length;

                var outline = mapPoints.Select(p => p - center).ToArray();
                var position = new Vector3(center.x, center.y, collider.bounds.center.y);

                var isDuplicate = false;
                foreach (var existing in ZoneAreas)
                {
                    if (Vector2.Distance(existing.Position, position) < 0.5f && existing.Outline.Length == outline.Length
                     && Vector2.Distance(existing.Outline[0], outline[0]) < 0.5f)
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate)
                {
                    ZoneAreas.Add(new QuestZoneArea { Position = position, Outline = outline });
                }
            }
        }

        private static Vector3[] GetOutlineWorldPoints(Collider collider)
        {
            switch (collider)
            {
                case BoxCollider box:
                {
                    var half = box.size / 2f;
                    var corners = new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
                    return corners
                        .Select(c => box.transform.TransformPoint(box.center + new Vector3(c.x * half.x, 0f, c.y * half.z)))
                        .ToArray();
                }
                case SphereCollider sphere:
                {
                    var scale = sphere.transform.lossyScale;
                    var radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    var center = sphere.transform.TransformPoint(sphere.center);
                    const int segments = 64;
                    var points = new Vector3[segments];
                    for (var i = 0; i < segments; i++)
                    {
                        var angle = i * 2f * Mathf.PI / segments;
                        points[i] = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                    }
                    return points;
                }
                default:
                {
                    var bounds = collider.bounds;
                    if (bounds.size == Vector3.zero)
                    {
                        return null;
                    }

                    var min = bounds.min;
                    var max = bounds.max;
                    return new[]
                    {
                        new Vector3(min.x, bounds.center.y, min.z), new Vector3(max.x, bounds.center.y, min.z),
                        new Vector3(max.x, bounds.center.y, max.z), new Vector3(min.x, bounds.center.y, max.z)
                    };
                }
            }
        }

        private static IEnumerable<Vector3> GetPositionsForQuestItems(IEnumerable<string> questItemIds, string questName,
                                                                      string conditionDescription)
        {
            foreach (var questItemId in questItemIds)
            {
                var questItems = QuestItems?.Where(i => i.TemplateId == questItemId);
                foreach (var item in questItems)
                {
                    yield return MathUtils.ConvertToMapPosition(item.transform.position);
                }
            }
        }

        private static IEnumerable<Condition> GetIncompleteQuestConditions(Player player, QuestDataClass quest)
        {
            // TODO: Template.Conditions is a GClass reference
            if (quest?.Template?.Conditions == null)
            {
                Plugin.Log.LogError($"GetIncompleteQuestConditions: quest.Template.Conditions is null, skipping quest");
                yield break;
            }

            // TODO: conditions is a GClass reference
            if (!quest.Template.Conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var conditions) || conditions == null)
            {
                Plugin.Log.LogError($"Quest {quest.Template.NameLocaleKey.BSGLocalized()} doesn't have conditions marked AvailableForFinish, skipping it");
                yield break;
            }

            foreach (var condition in conditions)
            {
                if (condition == null)
                {
                    Plugin.Log.LogWarning($"Quest {quest.Template.NameLocaleKey.BSGLocalized()} has null condition, skipping it");
                    continue;
                }

                // filter out completed conditions
                if (IsConditionCompleted(player, quest, condition))
                {
                    continue;
                }

                yield return condition;
            }
        }

        private static IEnumerable<QuestDataClass> GetIncompleteQuests(Player player)
        {
            var questController = player.QuestController;
            if (questController == null)
            {
                Plugin.Log.LogError($"Not able to get quests for player: {player.Id}, questController is null");
                yield break;
            }

            var quests = questController.Quests;
            if (quests == null)
            {
                Plugin.Log.LogError($"Not able to get quests for player: {player.Id}, quests is null");
                yield break;
            }

            var questsList = quests.list_1;
            if (questsList == null)
            {
                Plugin.Log.LogError($"Not able to get quests for player: {player.Id}, questsList is null");
                yield break;
            }

            foreach (var quest in questsList)
            {
                if (quest?.Template?.Conditions == null)
                {
                    continue;
                }

                if (quest.Status != EQuestStatus.Started)
                {
                    continue;
                }

                yield return quest;
            }
        }

        private static bool IsConditionCompleted(Player player, QuestDataClass questData, Condition condition)
        {
            // CompletedConditions is inaccurate (it doesn't reset when some quests do on death)
            // and also does not contain optional objectives, need to recheck if something is in there
            if (condition.IsNecessary && !questData.CompletedConditions.Contains(condition.id))
            {
                return false;
            }

            var questController = player.QuestController;
            if (questController == null)
            {
                return false;
            }

            var quests = questController.Quests;
            if (quests == null)
            {
                return false;
            }

            var quest = quests.GetConditional(questData.Id);
            if (quest == null)
            {
                return false;
            }

            return quest.IsConditionDone(condition);
        }

        private static IEnumerable<TriggerWithId> GetZoneTriggers(this IEnumerable<TriggerWithId> triggerWithIds, string zoneId)
        {
            return triggerWithIds.Where(t => t.Id == zoneId);
        }

        private static MapMarkerDef CreateQuestMapMarkerDef(Vector3 position, string questName, string conditionDescription)
        {
            return new MapMarkerDef
            {
                Category = _questCategory,
                Color = _questColor,
                ImagePath = _questImagePath,
                Position = position,
                Pivot = _questPivot,
                Text = questName
            };
        }
    }
}
