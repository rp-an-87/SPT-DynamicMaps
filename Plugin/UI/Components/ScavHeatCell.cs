using DynamicMaps.Config;
using DynamicMaps.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace DynamicMaps.UI.Components
{
    /// <summary>
    /// One heat blob of the scav heatmap, centered on a scav: a soft round sprite sized in map meters (so it scales
    /// with the map, unlike markers). Takes part in per-floor layer status through ILayerBound, like markers do.
    /// Overlapping blobs stack, so groups of scavs show up as hotter areas.
    /// </summary>
    public class ScavHeatCell : MonoBehaviour, ILayerBound
    {
        private const int _spriteSize = 64;
        private static readonly Color _coolColor = new Color(1f, 0.85f, 0.2f);
        private static Sprite _blobSprite;

        public Image Image { get; private set; }
        public RectTransform RectTransform => gameObject.transform as RectTransform;

        /// <summary>1 while a scav is being tracked, 0 once it died or left; heat moves towards this value over time.</summary>
        public float Target { get; set; }
        public float Heat { get; set; }

        /// <summary>The heat that was last drawn; used to skip redraws (every redraw rebuilds the UI canvas).</summary>
        public float AppliedHeat { get; private set; } = -1f;

        public float Radius { get; private set; }

        /// <summary>Where the tracked scav is; the blob glides towards it between samples.</summary>
        public Vector3 TargetPosition { get; set; }

        private float _levelAlpha = 1f;
        private Vector3 _position;
        public Vector3 Position
        {
            get { return _position; }
            set
            {
                _position = value;
                RectTransform.anchoredPosition = value;  // vector3 to vector2 discards z (height)
            }
        }

        public static ScavHeatCell Create(GameObject parent, Vector3 position, float radiusMeters)
        {
            var go = UIUtils.CreateUIGameObject(parent, "ScavHeatCell");
            var cell = go.AddComponent<ScavHeatCell>();

            cell.Image = go.AddComponent<Image>();
            cell.Image.raycastTarget = false;  // never block marker hover/clicks
            cell.Image.sprite = GetBlobSprite();
            cell.Image.type = Image.Type.Simple;
            cell.Image.color = Color.clear;

            cell.SetRadius(radiusMeters);
            cell.Position = position;
            cell.TargetPosition = position;
            go.transform.SetAsFirstSibling();  // draw underneath markers

            return cell;
        }

        public void SetRadius(float radiusMeters)
        {
            Radius = radiusMeters;
            RectTransform.sizeDelta = Vector2.one * (radiusMeters * 2f);
        }

        public void HandleNewLayerStatus(LayerStatus status)
        {
            // only the selected floor shows heat, like scav markers
            _levelAlpha = status == LayerStatus.OnTop || status == LayerStatus.FullReveal ? 1f : 0f;
            ApplyVisual();
        }

        public void ApplyVisual()
        {
            var heat = Mathf.Clamp01(Heat);
            AppliedHeat = heat;
            var color = Color.Lerp(_coolColor, Settings.ScavHeatmapColor.Value, heat);
            color.a = heat * GetPerBlobOpacity() * _levelAlpha;

            Image.color = color;
            Image.enabled = color.a > 0.004f;
        }

        /// <summary>
        /// Opacity of a single fully hot blob, chosen so that "scavs for full heat" overlapping blobs reach the
        /// configured max opacity (stacked alpha: 1 - (1 - a)^n).
        /// </summary>
        private static float GetPerBlobOpacity()
        {
            var maxOpacity = Mathf.Clamp(Settings.ScavHeatmapMaxOpacity.Value, 0.05f, 0.99f);
            var scavsForFullHeat = Mathf.Max(1, Settings.ScavHeatmapScavsForFullHeat.Value);
            return 1f - Mathf.Pow(1f - maxOpacity, 1f / scavsForFullHeat);
        }

        private static Sprite GetBlobSprite()
        {
            if (_blobSprite != null)
            {
                return _blobSprite;
            }

            var texture = new Texture2D(_spriteSize, _spriteSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var half = (_spriteSize - 1) / 2f;
            for (var y = 0; y < _spriteSize; y++)
            {
                for (var x = 0; x < _spriteSize; x++)
                {
                    var distance = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                    var falloff = Mathf.Clamp01(1f - distance);
                    var alpha = falloff * falloff * (3f - 2f * falloff);  // smoothstep, soft edge
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            texture.Apply();
            _blobSprite = Sprite.Create(texture, new Rect(0, 0, _spriteSize, _spriteSize), new Vector2(0.5f, 0.5f));
            return _blobSprite;
        }
    }
}
