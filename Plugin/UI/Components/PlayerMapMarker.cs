using DynamicMaps.Config;
using DynamicMaps.DynamicMarkers;
using DynamicMaps.Utils;
using EFT;
using UnityEngine;

namespace DynamicMaps.UI.Components
{
    public class PlayerMapMarker : MapMarker
    {
        private static float _maxCallbackTime = 0.5f;  // how often to call callback in seconds
        private static Vector2 _pivot = new Vector2(0.5f, 0.5f);

        public IPlayer Player { get; private set; }

        private float _callbackTime = _maxCallbackTime;  // make sure to start with a callback
        private float _crowd;  // 0 = no corpses around, 1 = full effect; eased

        public static PlayerMapMarker Create(IPlayer player, GameObject parent, string imagePath, Color color, string category,
                                             Vector2 size, float degreesRotation, float scale)
        {
            var name = $"{player.Profile.GetCorrectedNickname()}";
            var marker = Create<PlayerMapMarker>(parent, name, category, imagePath, color,
                                                 MathUtils.ConvertToMapPosition(player.Position),
                                                 size, _pivot, degreesRotation, scale);
            marker.IsDynamic = true;
            marker.Player = player;

            return marker;
        }

        public PlayerMapMarker()
        {
            ImageAlphaLayerStatus[LayerStatus.Hidden] = 0.25f;
            ImageAlphaLayerStatus[LayerStatus.Underneath] = 0.25f;
            ImageAlphaLayerStatus[LayerStatus.OnTop] = 1f;
            ImageAlphaLayerStatus[LayerStatus.FullReveal] = 1f;

            LabelAlphaLayerStatus[LayerStatus.Hidden] = 0.0f;
            LabelAlphaLayerStatus[LayerStatus.Underneath] = 0.0f;
            LabelAlphaLayerStatus[LayerStatus.OnTop] = 0.0f;
            LabelAlphaLayerStatus[LayerStatus.FullReveal] = 1.00f;
        }

        // Markers added later (corpses) are drawn above this one, so move back on top.
        private void KeepOnTop()
        {
            if (Settings.PlayerMarkerAlwaysOnTop.Value && transform.GetSiblingIndex() != transform.parent.childCount - 1)
            {
                transform.SetAsLastSibling();
            }
        }

        // Near corpses the marker gets smaller so the pile stays readable around it. The effect
        // grows linearly with the (distance-weighted) number of corpses around the player, up to the full-effect
        // count, and is eased over time so nothing jumps.
        private void UpdateCrowdEffect()
        {
            var weight = CorpseMarkerProvider.Current?.WeightedCorpseCountNear(Player.Position, Settings.PlayerMarkerCorpseRadius.Value) ?? 0f;
            var target = Mathf.Clamp01(weight / Mathf.Max(0.01f, Settings.PlayerMarkerFullEffectCount.Value));

            _crowd = Mathf.Lerp(_crowd, target, 1f - Mathf.Exp(-Settings.PlayerMarkerEaseSpeed.Value * Time.unscaledDeltaTime));

            Image.rectTransform.localScale = Vector3.one * Mathf.Lerp(1f, Settings.PlayerMarkerMinScale.Value, _crowd);
        }

        private void LateUpdate()
        {
            if (Player?.Transform?.Original == null)
            {
                return;
            }

            // throttle callback, since that leads to a layer search which might be expensive
            _callbackTime += Time.deltaTime;
            var callback = _callbackTime >= _maxCallbackTime;
            if (callback)
            {
                _callbackTime = 0f;
            }

            MoveAndRotate(MathUtils.ConvertToMapPosition(Player.Position), -Player.Rotation.x, callback);

            if (callback)
            {
                KeepOnTop();
            }

            UpdateCrowdEffect();
        }
    }
}
