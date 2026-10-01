using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DesirePaths
{
    /// <summary>
    /// Draws worn paths on the minimap and the large map in old topographic map style:
    /// dirt paths use the intermittent-stream symbol (a dash followed by three dots), stone roads
    /// a heavier long dash.
    ///
    /// Road cells are grouped into square buckets (4 m, 8 m, ... 1024 m). Each frame the bucket
    /// size is picked so neighbouring buckets are about NodeSpacingPx apart on screen. Occupied
    /// neighbouring buckets are joined into a graph, the graph is split into chains between
    /// junctions and ends, each chain is smoothed, and the line pattern is laid along it by
    /// on-screen arc length so it flows continuously. Everything is UI drawn over the map
    /// image, like pins, so it stays crisp at any zoom. Nothing is drawn in unexplored areas.
    /// </summary>
    internal static class MapRoads
    {
        private const int MinLevel = 2;  // 4 m buckets
        private const int MaxLevel = 10; // 1024 m buckets
        private const float NodeSpacingPx = 12f;

        private struct Bucket
        {
            public float SumX, SumZ;
            public int Count;
            public WearStage Stage;
            public Vector2 Centre => new Vector2(SumX / Count, SumZ / Count);
        }

        private sealed class Chain
        {
            public readonly List<Vector2> World = new List<Vector2>();
            public bool Road;
        }

        private sealed class LevelData
        {
            public readonly Dictionary<long, Bucket> Buckets = new Dictionary<long, Bucket>();
            public List<Chain> Chains;          // null when out of date
            public readonly List<Bucket> Isolated = new List<Bucket>();
        }

        /// <summary>Road cells at 1 m resolution -> highest stage, so re-sends don't double count.</summary>
        private static readonly Dictionary<long, WearStage> Cells = new Dictionary<long, WearStage>();
        private static readonly LevelData[] Levels = CreateLevels();

        private static readonly Overlay Small = new Overlay("small");
        private static readonly Overlay Large = new Overlay("large");

        private static Sprite _dot;

        private static LevelData[] CreateLevels()
        {
            var levels = new LevelData[MaxLevel + 1];
            for (int i = 0; i < levels.Length; i++)
                levels[i] = new LevelData();
            return levels;
        }

        public static void Clear()
        {
            Cells.Clear();
            foreach (LevelData level in Levels)
            {
                level.Buckets.Clear();
                level.Chains = null;
                level.Isolated.Clear();
            }
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
                LevelData data = Levels[level];
                long bucketKey = WearStore.CellKey(pos, 1 << level);
                data.Buckets.TryGetValue(bucketKey, out Bucket b);
                if (isNew)
                {
                    b.SumX += pos.x;
                    b.SumZ += pos.z;
                    b.Count++;
                }
                if (stage > b.Stage)
                    b.Stage = stage;
                data.Buckets[bucketKey] = b;
                data.Chains = null;
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

        // ---- graph building ----

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;
        private static int KeyX(long key) => (int)(key >> 32);
        private static int KeyZ(long key) => (int)(key & 0xffffffffL);

        private static void BuildChains(LevelData data)
        {
            var buckets = data.Buckets;
            var roadAdj = new Dictionary<long, List<long>>();
            var pathAdj = new Dictionary<long, List<long>>();
            data.Isolated.Clear();

            void Link(Dictionary<long, List<long>> adj, long a, long b)
            {
                if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<long>();
                if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<long>();
                la.Add(b);
                lb.Add(a);
            }

            // Forward half of the 8-neighbourhood, so each pair is considered once.
            int[,] dirs = { { 1, 0 }, { 0, 1 }, { 1, 1 }, { 1, -1 } };
            foreach (var kv in buckets)
            {
                int x = KeyX(kv.Key), z = KeyZ(kv.Key);
                for (int d = 0; d < 4; d++)
                {
                    int dx = dirs[d, 0], dz = dirs[d, 1];
                    long n = Key(x + dx, z + dz);
                    if (!buckets.TryGetValue(n, out Bucket other))
                        continue;
                    // Skip a diagonal when an orthogonal step already connects the pair;
                    // otherwise every staircase becomes a row of triangles.
                    if (dx != 0 && dz != 0 && (buckets.ContainsKey(Key(x + dx, z)) || buckets.ContainsKey(Key(x, z + dz))))
                        continue;
                    // In a filled 2x2 block, drop the bottom and left edges so a wide trail
                    // reads as one line instead of a ladder.
                    if (dx == 1 && dz == 0 && buckets.ContainsKey(Key(x, z + 1)) && buckets.ContainsKey(Key(x + 1, z + 1)))
                        continue;
                    if (dx == 0 && dz == 1 && buckets.ContainsKey(Key(x + 1, z)) && buckets.ContainsKey(Key(x + 1, z + 1)))
                        continue;

                    bool road = kv.Value.Stage >= WearStage.StoneRoad && other.Stage >= WearStage.StoneRoad;
                    Link(road ? roadAdj : pathAdj, kv.Key, n);
                }
            }

            var chains = new List<Chain>();
            WalkChains(roadAdj, buckets, true, chains);
            WalkChains(pathAdj, buckets, false, chains);

            // A bucket counts as isolated only if no chain touches it.
            var touched = new HashSet<long>(roadAdj.Keys);
            touched.UnionWith(pathAdj.Keys);
            data.Isolated.Clear();
            foreach (var kv in buckets)
            {
                if (!touched.Contains(kv.Key))
                    data.Isolated.Add(kv.Value);
            }

            data.Chains = chains;
        }

        /// <summary>Splits a graph into chains running between nodes that aren't simple pass-throughs.</summary>
        private static void WalkChains(Dictionary<long, List<long>> adj, Dictionary<long, Bucket> buckets, bool road, List<Chain> chains)
        {
            var used = new HashSet<(long, long)>();
            bool Take(long a, long b)
            {
                var e = a < b ? (a, b) : (b, a);
                return used.Add(e);
            }

            Chain Walk(long start, long next)
            {
                var chain = new Chain { Road = road };
                chain.World.Add(buckets[start].Centre);
                long prev = start, cur = next;
                while (true)
                {
                    chain.World.Add(buckets[cur].Centre);
                    List<long> n = adj[cur];
                    if (n.Count != 2)
                        break;
                    long following = n[0] == prev ? n[1] : n[0];
                    if (!Take(cur, following))
                        break;
                    prev = cur;
                    cur = following;
                }
                return chain;
            }

            // Chains from ends and junctions first.
            foreach (var kv in adj)
            {
                if (kv.Value.Count == 2)
                    continue;
                foreach (long n in kv.Value)
                {
                    if (Take(kv.Key, n))
                        chains.Add(Walk(kv.Key, n));
                }
            }
            // What's left are closed loops.
            foreach (var kv in adj)
            {
                foreach (long n in kv.Value)
                {
                    if (Take(kv.Key, n))
                        chains.Add(Walk(kv.Key, n));
                }
            }
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
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d)));
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static bool IsExplored(Minimap map, Vector2 world)
        {
            if (!PathConfig.MapRespectFog.Value)
                return true;
            int size = map.m_textureSize;
            int x = Mathf.FloorToInt(world.x / map.m_pixelSize + size / 2f);
            int y = Mathf.FloorToInt(world.y / map.m_pixelSize + size / 2f);
            if (x < 0 || y < 0 || x >= size || y >= size)
                return false;
            int i = y * size + x;
            return (map.m_explored != null && i < map.m_explored.Length && map.m_explored[i])
                || (map.m_exploredOthers != null && i < map.m_exploredOthers.Length && map.m_exploredOthers[i]);
        }

        // ---- drawing ----

        /// <summary>One element of a line pattern, in multiples of the line width.</summary>
        private struct Mark
        {
            public bool Dot;
            public bool Gap;
            public float Length;
        }

        // Intermittent stream: long dash, then three dots.
        private static readonly Mark[] PathPattern =
        {
            new Mark { Length = 6f }, new Mark { Gap = true, Length = 1.6f },
            new Mark { Dot = true }, new Mark { Gap = true, Length = 1.6f },
            new Mark { Dot = true }, new Mark { Gap = true, Length = 1.6f },
            new Mark { Dot = true }, new Mark { Gap = true, Length = 1.6f },
        };

        // Road: heavier long dashes with short gaps.
        private static readonly Mark[] RoadPattern =
        {
            new Mark { Length = 7f }, new Mark { Gap = true, Length = 2f },
        };

        /// <summary>The marks for one map image, pooled.</summary>
        private sealed class Overlay
        {
            private readonly string _name;
            private RectTransform _root;
            private RawImage _owner;
            private readonly List<Image> _pool = new List<Image>();
            private int _used;

            // Per-draw state.
            private Minimap _map;
            private Rect _rect, _uv;
            private float _worldSize;
            private readonly List<Vector2> _pts = new List<Vector2>();
            private readonly List<Vector2> _smooth = new List<Vector2>();
            private readonly List<float> _cum = new List<float>();

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

                _map = map;
                _rect = image.rectTransform.rect;
                _uv = image.uvRect;
                _worldSize = map.m_textureSize * map.m_pixelSize;
                if (_uv.width <= 0f || _uv.height <= 0f || _worldSize <= 0f || _rect.width <= 0f)
                    return;

                float pxPerMetre = _rect.width / (_uv.width * _worldSize);
                float wantMetres = NodeSpacingPx / Mathf.Max(pxPerMetre, 1e-6f);
                int level = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(Mathf.Max(wantMetres, 1f), 2f)), MinLevel, MaxLevel);
                LevelData data = Levels[level];
                if (data.Chains == null)
                    BuildChains(data);

                float width = PathConfig.MapLineWidth.Value;
                Color pathColor = PathConfig.DirtPathMapColor.Value;
                Color roadColor = PathConfig.StoneRoadMapColor.Value;

                _used = 0;
                foreach (Chain chain in data.Chains)
                {
                    if (chain.Road)
                        DrawChain(chain, RoadPattern, width * 1.5f, roadColor);
                    else
                        DrawChain(chain, PathPattern, width, pathColor);
                }
                foreach (Bucket b in data.Isolated)
                {
                    Vector2 world = b.Centre;
                    Vector2 local = ToLocal(world);
                    if (!Inside(local) || !IsExplored(_map, world))
                        continue;
                    bool road = b.Stage >= WearStage.StoneRoad;
                    PlaceDot(local, (road ? 1.5f : 1f) * width * 1.4f, road ? roadColor : pathColor);
                }

                for (int i = _used; i < _pool.Count; i++)
                {
                    if (_pool[i].gameObject.activeSelf)
                        _pool[i].gameObject.SetActive(false);
                }
            }

            private Vector2 ToLocal(Vector2 world)
            {
                float u = world.x / _worldSize + 0.5f;
                float v = world.y / _worldSize + 0.5f;
                return new Vector2(((u - _uv.xMin) / _uv.width - 0.5f) * _rect.width,
                                   ((v - _uv.yMin) / _uv.height - 0.5f) * _rect.height);
            }

            private Vector2 ToWorld(Vector2 local)
            {
                float u = (local.x / _rect.width + 0.5f) * _uv.width + _uv.xMin;
                float v = (local.y / _rect.height + 0.5f) * _uv.height + _uv.yMin;
                return new Vector2((u - 0.5f) * _worldSize, (v - 0.5f) * _worldSize);
            }

            private bool Inside(Vector2 local) =>
                Mathf.Abs(local.x) <= _rect.width * 0.5f && Mathf.Abs(local.y) <= _rect.height * 0.5f;

            private void DrawChain(Chain chain, Mark[] pattern, float width, Color color)
            {
                // Screen-space points, smoothed once (Chaikin) so bucket steps read as curves.
                _pts.Clear();
                foreach (Vector2 w in chain.World)
                    _pts.Add(ToLocal(w));

                _smooth.Clear();
                if (_pts.Count < 3)
                {
                    _smooth.AddRange(_pts);
                }
                else
                {
                    _smooth.Add(_pts[0]);
                    for (int i = 0; i < _pts.Count - 1; i++)
                    {
                        _smooth.Add(Vector2.Lerp(_pts[i], _pts[i + 1], 0.25f));
                        _smooth.Add(Vector2.Lerp(_pts[i], _pts[i + 1], 0.75f));
                    }
                    _smooth.Add(_pts[_pts.Count - 1]);
                }

                _cum.Clear();
                _cum.Add(0f);
                for (int i = 1; i < _smooth.Count; i++)
                    _cum.Add(_cum[i - 1] + Vector2.Distance(_smooth[i - 1], _smooth[i]));
                float total = _cum[_cum.Count - 1];
                if (total <= 0f)
                    return;

                // Quick reject: chain entirely off this map image.
                float half = Mathf.Max(_rect.width, _rect.height);
                bool any = false;
                foreach (Vector2 p in _smooth)
                {
                    if (Mathf.Abs(p.x) < half && Mathf.Abs(p.y) < half) { any = true; break; }
                }
                if (!any)
                    return;

                float s = 0f;
                int m = 0;
                while (s < total)
                {
                    Mark mark = pattern[m];
                    m = (m + 1) % pattern.Length;
                    if (mark.Gap)
                    {
                        s += mark.Length * width;
                    }
                    else if (mark.Dot)
                    {
                        float at = s + width * 0.5f;
                        if (at <= total)
                        {
                            Vector2 p = PointAt(at, out _);
                            if (Inside(p) && IsExplored(_map, ToWorld(p)))
                                PlaceDot(p, width * 1.15f, color);
                        }
                        s += width;
                    }
                    else
                    {
                        float end = Mathf.Min(s + mark.Length * width, total);
                        DrawDash(s, end, width, color);
                        s = end;
                    }
                }
            }

            /// <summary>Point at arc length <paramref name="s"/>; also returns its segment index.</summary>
            private Vector2 PointAt(float s, out int seg)
            {
                seg = 0;
                while (seg < _cum.Count - 2 && _cum[seg + 1] < s)
                    seg++;
                float len = _cum[seg + 1] - _cum[seg];
                float t = len > 0f ? (s - _cum[seg]) / len : 0f;
                return Vector2.Lerp(_smooth[seg], _smooth[seg + 1], Mathf.Clamp01(t));
            }

            /// <summary>Draws the stretch [a, b] of the polyline, one rectangle per segment it spans.</summary>
            private void DrawDash(float a, float b, float width, Color color)
            {
                Vector2 p = PointAt(a, out int seg);
                while (a < b)
                {
                    float segEnd = Mathf.Min(_cum[seg + 1], b);
                    Vector2 q = PointAt(segEnd, out _);
                    Vector2 mid = (p + q) * 0.5f;
                    if ((q - p).sqrMagnitude > 0.01f && Inside(mid) && IsExplored(_map, ToWorld(mid)))
                        PlaceRect(p, q, width, color);
                    a = segEnd;
                    p = q;
                    if (seg >= _cum.Count - 2)
                        break;
                    seg++;
                }
            }

            private void PlaceRect(Vector2 p, Vector2 q, float width, Color color)
            {
                Image img = Next(null);
                img.color = color;
                RectTransform rt = img.rectTransform;
                Vector2 d = q - p;
                // Slight overlap hides hairline gaps where a dash bends across segments.
                rt.sizeDelta = new Vector2(d.magnitude + width * 0.3f, width);
                rt.anchoredPosition = (p + q) * 0.5f;
                rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            }

            private void PlaceDot(Vector2 p, float size, Color color)
            {
                Image img = Next(_dot);
                img.color = color;
                RectTransform rt = img.rectTransform;
                rt.sizeDelta = new Vector2(size, size);
                rt.anchoredPosition = p;
                rt.localRotation = Quaternion.identity;
            }

            private void EnsureRoot(RawImage image)
            {
                if (_root != null && _owner == image)
                    return;

                if (_root != null)
                    Object.Destroy(_root.gameObject);
                _pool.Clear();

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

            private Image Next(Sprite sprite)
            {
                Image img;
                if (_used < _pool.Count)
                {
                    img = _pool[_used];
                }
                else
                {
                    var go = new GameObject("mark", typeof(RectTransform));
                    go.transform.SetParent(_root, false);
                    img = go.AddComponent<Image>();
                    img.raycastTarget = false;
                    RectTransform rt = img.rectTransform;
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    _pool.Add(img);
                }
                if (img.sprite != sprite)
                    img.sprite = sprite;
                if (!img.gameObject.activeSelf)
                    img.gameObject.SetActive(true);
                _used++;
                return img;
            }
        }
    }
}
