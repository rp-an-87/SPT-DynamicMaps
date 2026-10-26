using System.Collections.Generic;
using DynamicMaps.Config;
using DynamicMaps.Data;
using DynamicMaps.DynamicMarkers;
using DynamicMaps.UI.Components;
using DynamicMaps.Utils;
using UnityEngine;

namespace DynamicMaps
{
    public class QuestMarkerProvider : IDynamicMarkerProvider
    {
        private List<MapMarker> _questMarkers = [];
        private List<QuestZoneRing> _zoneRings = [];
        private MapView _map;

        public void OnShowInRaid(MapView map)
        {
            if (GameUtils.IsScavRaid())
            {
                return;
            }

            AddQuestObjectiveMarkers(map);
        }

        public void OnHideInRaid(MapView map)
        {
            // TODO: don't just be lazy and try to update markers
            TryRemoveMarkers();
        }

        public void OnMapChanged(MapView map, MapDef mapDef)
        {
            if (!GameUtils.IsInRaid())
            {
                return;
            }

            TryRemoveMarkers();
            AddQuestObjectiveMarkers(map);
        }

        public void OnRaidEnd(MapView map)
        {
            QuestUtils.DiscardQuestData();
            TryRemoveMarkers();
        }

        public void OnDisable(MapView map)
        {
            TryRemoveMarkers();
        }

        private void AddQuestObjectiveMarkers(MapView map)
        {
            QuestUtils.TryCaptureQuestData();

            var player = GameUtils.GetMainPlayer();

            var markerDefs = QuestUtils.GetMarkerDefsForPlayer(player);
            foreach (var markerDef in markerDefs)
            {
                var marker = map.AddMapMarker(markerDef);
                _questMarkers.Add(marker);
            }

            AddZoneRings(map);
        }

        private void AddZoneRings(MapView map)
        {
            if (!Settings.ShowQuestZoneAreas.Value)
            {
                return;
            }

            _map = map;
            foreach (var area in QuestUtils.ZoneAreas)
            {
                var ring = QuestZoneRing.Create(map.MapMarkerContainer, area.Position, area.Outline);
                _zoneRings.Add(ring);
                map.AddLayerBound(ring);  // applies the current floor status immediately
            }
        }

        private void TryRemoveMarkers()
        {
            foreach (var ring in _zoneRings)
            {
                if (ring == null)
                {
                    continue;
                }

                _map?.RemoveLayerBound(ring);
                ring.gameObject.SetActive(false);  // destroy is not guaranteed to be immediate
                Object.Destroy(ring.gameObject);
            }
            _zoneRings.Clear();

            foreach (var marker in _questMarkers)
            {
                marker.ContainingMapView.RemoveMapMarker(marker);
            }
            _questMarkers.Clear();
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
}
