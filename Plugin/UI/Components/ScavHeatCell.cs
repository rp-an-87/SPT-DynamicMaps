using DynamicMaps.Config;
using DynamicMaps.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace DynamicMaps.UI.Components
{
    /// <summary>
    /// One cell of the scav heatmap: a soft round blob sized in map meters (so it scales with the map, unlike markers).
    /// Takes part in per-floor layer status through ILayerBound, the same way markers do.
    /// </summary>
    public class ScavHeatCell : MonoBehaviour, ILayerBound
    {
        private const int _spriteSize = 64;
        private static readonly Color _coolColor = new Color(1f, 0.85f, 0.2f);
        private static Sprite _blobSprite;

        // blobs are drawn larger than the cell so neighbouring cells blend into each other
        public const float BlobScale = 1.7f;

        public Image Image { get; private set; }
        public RectTransform RectTransform => gameObject.transform as RectTransform;

        public float Target { get; set; }
        public float Heat { get; set; }

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

        public static ScavHeatCell Create(GameObject parent, Vector3 position, float cellSizeMeters)
        {
            var go = UIUtils.CreateUIGameObject(parent, "ScavHeatCell");
            var cell = go.AddComponent<ScavHeatCell>();

            cell.Image = go.AddComponent<Image>();
            cell.Image.raycastTarget = false;  // never block marker hover/clicks
            cell.Image.sprite = GetBlobSprite();
            cell.Image.type = Image.Type.Simple;
            cell.Image.color = Color.clear;

            cell.SetCellSize(cellSizeMeters);
            cell.Position = position;
            go.transform.SetAsFirstSibling();  // draw underneath markers

            return cell;
        }

        public void SetCellSize(float cellSizeMeters)
        {
            RectTransform.sizeDelta = Vector2.one * (cellSizeMeters * BlobScale);
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
            var alpha = heat * Settings.ScavHeatmapMaxOpacity.Value * _levelAlpha;
            var color = Color.Lerp(_coolColor, Settings.ScavHeatmapColor.Value, heat);
            color.a = alpha;

            Image.color = color;
            Image.enabled = alpha > 0.004f;
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
