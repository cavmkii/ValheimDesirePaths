using System.Collections.Generic;
using UnityEngine;

namespace DesirePaths
{
    /// <summary>
    /// Draws worn paths onto the map. The minimap and the large map share one texture
    /// (Minimap.m_mapTexture), generated from world data at load, so terrain edits never show on
    /// it by themselves. Each map pixel covers several metres, so a path shows as a trail of
    /// tinted pixels: any pixel containing a dirt path or stone road cell is recoloured, and its
    /// forest shading is cleared so the trail stays visible through woods.
    ///
    /// The texture is rebuilt by the game on load (and can be rebuilt later), which discards our
    /// pixels. A cheap periodic check notices that and repaints everything.
    /// </summary>
    internal static class MapRoads
    {
        private struct Road
        {
            public Vector2 Pos;
            public WearStage Stage;
        }

        private const float RepaintCheckInterval = 3f;

        private static readonly Dictionary<long, Road> Roads = new Dictionary<long, Road>();
        private static readonly List<Road> Pending = new List<Road>();

        /// <summary>Map pixel index -> highest stage drawn there.</summary>
        private static readonly Dictionary<int, WearStage> Painted = new Dictionary<int, WearStage>();

        private static Texture2D _paintedTexture;
        private static bool _repaintAll;
        private static float _nextCheck;

        public static void Clear()
        {
            Roads.Clear();
            Pending.Clear();
            Painted.Clear();
            _paintedTexture = null;
        }

        public static void RequestRepaint() => _repaintAll = true;

        /// <summary>Records a cell that has reached <paramref name="stage"/>. Below DirtPath is ignored.</summary>
        public static void Set(Vector3 pos, WearStage stage)
        {
            if (stage < WearStage.DirtPath)
                return;

            long key = WearStore.CellKey(pos, 1f);
            if (Roads.TryGetValue(key, out Road existing) && existing.Stage >= stage)
                return;

            var road = new Road { Pos = new Vector2(pos.x, pos.z), Stage = stage };
            Roads[key] = road;
            Pending.Add(road);
        }

        public static void Tick()
        {
            Minimap map = Minimap.instance;
            if (!PathConfig.ShowOnMap.Value || map == null || map.m_mapTexture == null)
                return;

            Texture2D tex = map.m_mapTexture;

            // A new texture object, or our pixels no longer present, means the map was regenerated.
            if (tex != _paintedTexture)
                _repaintAll = true;
            else if (Time.time >= _nextCheck)
            {
                _nextCheck = Time.time + RepaintCheckInterval;
                if (!SamplePixelIntact(tex))
                    _repaintAll = true;
            }

            if (_repaintAll)
            {
                _repaintAll = false;
                Painted.Clear();
                Pending.Clear();
                Pending.AddRange(Roads.Values);
            }

            if (Pending.Count == 0)
                return;

            Texture2D forest = map.m_forestMaskTexture;
            bool forestChanged = false;
            int drawn = 0;
            foreach (Road road in Pending)
            {
                if (!ToPixel(map, tex, road.Pos, out int x, out int y))
                    continue;
                int index = y * tex.width + x;
                if (Painted.TryGetValue(index, out WearStage already) && already >= road.Stage)
                    continue;

                tex.SetPixel(x, y, ColorFor(road.Stage));
                Painted[index] = road.Stage;
                drawn++;

                if (forest != null && x < forest.width && y < forest.height)
                {
                    Color f = forest.GetPixel(x, y);
                    if (f.r > 0f)
                    {
                        f.r = 0f;
                        forest.SetPixel(x, y, f);
                        forestChanged = true;
                    }
                }
            }
            Pending.Clear();

            if (drawn > 0)
                tex.Apply();
            if (forestChanged)
                forest.Apply();

            _paintedTexture = tex;
            _nextCheck = Time.time + RepaintCheckInterval;
            if (drawn > 0)
                Plugin.Debug($"Drew {drawn} road pixel(s) on the map ({Painted.Count} total).");
        }

        private static Color ColorFor(WearStage stage) =>
            stage >= WearStage.StoneRoad ? PathConfig.StoneRoadMapColor.Value : PathConfig.DirtPathMapColor.Value;

        /// <summary>Same world-to-pixel mapping the game uses for the map texture.</summary>
        private static bool ToPixel(Minimap map, Texture2D tex, Vector2 world, out int x, out int y)
        {
            int half = map.m_textureSize / 2;
            float mx = world.x / map.m_pixelSize + half;
            float my = world.y / map.m_pixelSize + half;
            x = Mathf.RoundToInt(mx / map.m_textureSize * tex.width);
            y = Mathf.RoundToInt(my / map.m_textureSize * tex.height);
            return x >= 0 && y >= 0 && x < tex.width && y < tex.height;
        }

        /// <summary>Checks one pixel we painted still has our colour.</summary>
        private static bool SamplePixelIntact(Texture2D tex)
        {
            foreach (var kv in Painted)
            {
                int x = kv.Key % tex.width;
                int y = kv.Key / tex.width;
                Color want = ColorFor(kv.Value);
                Color have = tex.GetPixel(x, y);
                return Mathf.Abs(want.r - have.r) < 0.02f
                    && Mathf.Abs(want.g - have.g) < 0.02f
                    && Mathf.Abs(want.b - have.b) < 0.02f;
            }
            return true; // nothing painted yet
        }
    }
}
