using DynamicMaps.Config;
using DynamicMaps.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace DynamicMaps.UI.Components
{
    /// <summary>
    /// The area of a quest zone objective: a thin solid outline plus an optional faint fill, in the zone's real shape
    /// (a box zone is a rotated rectangle, a sphere zone a circle). Drawn as one mesh in map meters; the line width is
    /// kept constant in screen pixels while zooming. Takes part in per-floor layer status through ILayerBound, like
    /// markers do. Zone shapes are convex, so the fill is a simple fan.
    /// </summary>
    public class QuestZoneRing : MaskableGraphic, ILayerBound
    {
        private const float _maxMiterFactor = 3f;

        private LayerStatus _status = LayerStatus.FullReveal;
        private Vector2[] _outline;  // closed loop, relative to Position, map meters
        private bool _onFloor = true;
        private float _lastScale = -1f;

        public RectTransform RectTransform => gameObject.transform as RectTransform;

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

        public static QuestZoneRing Create(GameObject parent, Vector3 position, Vector2[] outline)
        {
            var go = UIUtils.CreateUIGameObject(parent, "QuestZoneRing");
            var ring = go.AddComponent<QuestZoneRing>();

            ring.raycastTarget = false;  // never block marker hover/clicks
            ring.color = Color.white;
            ring._outline = outline;
            ring.RectTransform.sizeDelta = Vector2.zero;
            ring.Position = position;
            go.transform.SetAsFirstSibling();  // draw underneath markers

            ring.ApplyVisual();
            return ring;
        }

        public void HandleNewLayerStatus(LayerStatus status)
        {
            _status = status;
            ApplyVisual();
        }

        public void ApplyVisual()
        {
            // only the selected floor shows the area, like quest markers
            _onFloor = _status == LayerStatus.OnTop || _status == LayerStatus.FullReveal;
            enabled = _onFloor;
            SetVerticesDirty();
        }

        private void Update()
        {
            // the line width is in screen pixels, so rebuild when the map zoom changes
            var scale = Mathf.Abs(transform.lossyScale.x);
            if (scale > 0f && !Mathf.Approximately(scale, _lastScale))
            {
                _lastScale = scale;
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_outline == null || _outline.Length < 3 || !_onFloor)
            {
                return;
            }

            var baseColor = Settings.QuestZoneAreaColor.Value;
            var lineColor = baseColor;
            lineColor.a = Mathf.Clamp01(Settings.QuestZoneAreaOpacity.Value);
            var fillColor = baseColor;
            fillColor.a = Mathf.Clamp01(Settings.QuestZoneAreaFillOpacity.Value);

            var scale = _lastScale > 0f ? _lastScale : Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
            var half = Mathf.Max(0.5f, Settings.QuestZoneAreaLineThickness.Value) / scale / 2f;

            var count = _outline.Length;
            var outer = new Vector2[count];
            var inner = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                var previous = _outline[(i + count - 1) % count];
                var current = _outline[i];
                var next = _outline[(i + 1) % count];

                var normalIn = Normal(current - previous);
                var normalOut = Normal(next - current);
                var miter = normalIn + normalOut;
                var miterLength = miter.magnitude;

                // mitre join, so corners meet without overlap; limited so sharp corners can't spike
                Vector2 offset;
                if (miterLength < 0.0001f)
                {
                    offset = normalOut * half;
                }
                else
                {
                    miter /= miterLength;
                    var factor = Mathf.Min(1f / Mathf.Max(0.0001f, Vector2.Dot(miter, normalOut)), _maxMiterFactor);
                    offset = miter * (half * factor);
                }

                outer[i] = current + offset;
                inner[i] = current - offset;
            }

            // fill: fan over the inner edge, so it never overlaps the line
            if (fillColor.a > 0.004f)
            {
                var center = vh.currentVertCount;
                vh.AddVert(Vector2.zero, fillColor, Vector2.zero);
                for (var i = 0; i < count; i++)
                {
                    vh.AddVert(inner[i], fillColor, Vector2.zero);
                }
                for (var i = 0; i < count; i++)
                {
                    vh.AddTriangle(center, center + 1 + i, center + 1 + (i + 1) % count);
                }
            }

            // line: a quad strip between the outer and inner edge
            var lineStart = vh.currentVertCount;
            for (var i = 0; i < count; i++)
            {
                vh.AddVert(outer[i], lineColor, Vector2.zero);
                vh.AddVert(inner[i], lineColor, Vector2.zero);
            }
            for (var i = 0; i < count; i++)
            {
                var a = lineStart + i * 2;
                var b = lineStart + (i + 1) % count * 2;
                vh.AddTriangle(a, a + 1, b + 1);
                vh.AddTriangle(a, b + 1, b);
            }
        }

        private static Vector2 Normal(Vector2 direction)
        {
            direction.Normalize();
            return new Vector2(-direction.y, direction.x);
        }
    }
}
