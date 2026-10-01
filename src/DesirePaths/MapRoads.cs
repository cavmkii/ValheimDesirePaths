using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DesirePaths
{
    /// <summary>
    /// Draws worn paths on the minimap and the large map as dotted lines.
    ///
    /// The dots are UI elements laid over the map image, not pixels in the map texture, so they
    /// stay crisp at any zoom. Road cells are grouped into coarser buckets (2 m, 4 m, 8 m ...);
    /// each frame the bucket size is chosen so neighbouring dots are about DotSpacing pixels apart
    /// on screen, and one dot is drawn per bucket at the average position of its cells. Zoomed in
    /// you get a dense trail; zoomed out the dots merge so the line stays readable.
    ///
    /// Dots are only drawn where the map has been explored.
    /// </summary>
    internal static class MapRoads
    {
        private const int MinLevel = 1;  // 2 m buckets
        private const int MaxLevel = 10; // 1024 m buckets

        private struct Bucket
        {
            public float SumX, SumZ;
            public int Count;
            public WearStage Stage;
        }

        /// <summary>Road cells at 1 m resolution -> highest stage, so re-sends don't double count.</summary>
        private static readonly Dictionary<long, WearStage> Cells = new Dictionary<long, WearStage>();

        /// <summary>Per level: bucket key -> aggregate of the cells inside it.</summary>
        private static readonly Dictionary<long, Bucket>[] Levels = CreateLevels();

        private static readonly Overlay Small = new Overlay("small");
        private static readonly Overlay Large = new Overlay("large");

        private static Sprite _dot;

        private static Dictionary<long, Bucket>[] CreateLevels()
        {
            var levels = new Dictionary<long, Bucket>[MaxLevel + 1];
            for (int i = 0; i < levels.Length; i++)
                levels[i] = new Dictionary<long, Bucket>();
            return levels;
        }

        public static void Clear()
        {
            Cells.Clear();
            foreach (var level in Levels)
                level.Clear();
            Small.Hide();
            Large.Hide();
        }

        /// <summary>Records a cell that has reached <paramref name="stage"/>. Below DirtPath is ignored.</summary>
        public static void Set(Vector3 pos, WearStage stage)
        {
            if (stage < WearStage.DirtPath)
                return;

            long key = WearStore.CellKey(pos, 1f);
            bool isNew = !Cells.TryGetValue(key, out WearStage old);
            if (!isNew && old >= stage)
                return;
            Cells[key] = stage;

            for (int level = MinLevel; level <= MaxLevel; level++)
            {
                long bucketKey = WearStore.CellKey(pos, 1 << level);
                Levels[level].TryGetValue(bucketKey, out Bucket b);
                if (isNew)
                {
                    b.SumX += pos.x;
                    b.SumZ += pos.z;
                    b.Count++;
                }
                if (stage > b.Stage)
                    b.Stage = stage;
                Levels[level][bucketKey] = b;
            }
        }

        public static void Tick()
        {
            Minimap map = Minimap.instance;
            if (map == null || !PathConfig.ShowOnMap.Value || Cells.Count == 0)
            {
                Small.Hide();
                Large.Hide();
                return;
            }

            if (_dot == null)
                _dot = CreateDotSprite();

            Small.Draw(map, map.m_mapImageSmall);
            Large.Draw(map, map.m_mapImageLarge);
        }

        private static Sprite CreateDotSprite()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float r = size / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                float a = Mathf.Clamp01(r - d); // 1 px soft edge
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static bool IsExplored(Minimap map, float wx, float wz)
        {
            if (!PathConfig.MapRespectFog.Value)
                return true;
            int size = map.m_textureSize;
            int x = Mathf.FloorToInt(wx / map.m_pixelSize + size / 2f);
            int y = Mathf.FloorToInt(wz / map.m_pixelSize + size / 2f);
            if (x < 0 || y < 0 || x >= size || y >= size)
                return false;
            int i = y * size + x;
            return (map.m_explored != null && i < map.m_explored.Length && map.m_explored[i])
                || (map.m_exploredOthers != null && i < map.m_exploredOthers.Length && map.m_exploredOthers[i]);
        }

        /// <summary>A pool of dot images parented to one of the map images.</summary>
        private sealed class Overlay
        {
            private readonly string _name;
            private RectTransform _root;
            private RawImage _owner;
            private readonly List<Image> _dots = new List<Image>();
            private int _used;

            public Overlay(string name)
            {
                _name = name;
            }

            public void Hide()
            {
                if (_root != null)
                    _root.gameObject.SetActive(false);
            }

            public void Draw(Minimap map, RawImage image)
            {
                if (image == null || !image.isActiveAndEnabled)
                {
                    Hide();
                    return;
                }

                EnsureRoot(image);
                _root.gameObject.SetActive(true);

                Rect rect = image.rectTransform.rect;
                Rect uv = image.uvRect;
                float worldSize = map.m_textureSize * map.m_pixelSize; // metres covered by uv 0..1
                if (uv.width <= 0f || uv.height <= 0f || worldSize <= 0f)
                    return;

                float pxPerMetre = rect.width / (uv.width * worldSize);
                float wantMetres = PathConfig.MapDotSpacing.Value / Mathf.Max(pxPerMetre, 1e-6f);
                int level = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(Mathf.Max(wantMetres, 1f), 2f)), MinLevel, MaxLevel);

                float dotSize = PathConfig.MapDotSize.Value;
                Color dirt = PathConfig.DirtPathMapColor.Value;
                Color road = PathConfig.StoneRoadMapColor.Value;

                _used = 0;
                foreach (Bucket b in Levels[level].Values)
                {
                    float wx = b.SumX / b.Count;
                    float wz = b.SumZ / b.Count;

                    float u = wx / worldSize + 0.5f;
                    float v = wz / worldSize + 0.5f;
                    float lx = (u - uv.xMin) / uv.width;
                    float ly = (v - uv.yMin) / uv.height;
                    if (lx < 0f || lx > 1f || ly < 0f || ly > 1f)
                        continue;
                    if (!IsExplored(map, wx, wz))
                        continue;

                    Image dot = Next();
                    dot.color = b.Stage >= WearStage.StoneRoad ? road : dirt;
                    RectTransform rt = dot.rectTransform;
                    float s = b.Stage >= WearStage.StoneRoad ? dotSize * 1.25f : dotSize;
                    rt.sizeDelta = new Vector2(s, s);
                    rt.anchoredPosition = new Vector2((lx - 0.5f) * rect.width, (ly - 0.5f) * rect.height);
                }

                for (int i = _used; i < _dots.Count; i++)
                {
                    if (_dots[i].gameObject.activeSelf)
                        _dots[i].gameObject.SetActive(false);
                }
            }

            private void EnsureRoot(RawImage image)
            {
                if (_root != null && _owner == image)
                    return;

                if (_root != null)
                    Object.Destroy(_root.gameObject);
                _dots.Clear();

                var go = new GameObject("DesirePaths_" + _name, typeof(RectTransform));
                _root = (RectTransform)go.transform;
                _root.SetParent(image.rectTransform, false);
                _root.anchorMin = Vector2.zero;
                _root.anchorMax = Vector2.one;
                _root.offsetMin = Vector2.zero;
                _root.offsetMax = Vector2.zero;
                _root.pivot = new Vector2(0.5f, 0.5f);
                _owner = image;
            }

            private Image Next()
            {
                Image dot;
                if (_used < _dots.Count)
                {
                    dot = _dots[_used];
                }
                else
                {
                    var go = new GameObject("dot", typeof(RectTransform));
                    go.transform.SetParent(_root, false);
                    dot = go.AddComponent<Image>();
                    dot.sprite = _dot;
                    dot.raycastTarget = false;
                    RectTransform rt = dot.rectTransform;
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    _dots.Add(dot);
                }
                if (!dot.gameObject.activeSelf)
                    dot.gameObject.SetActive(true);
                _used++;
                return dot;
            }
        }
    }
}
