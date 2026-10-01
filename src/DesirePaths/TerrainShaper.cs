using System;
using System.Collections.Generic;
using UnityEngine;

namespace DesirePaths
{
    /// <summary>
    /// Applies a wear stage by editing the zone's terrain data directly: height deltas for
    /// smoothing and the paint mask for dirt/paving. This mirrors how World Edit Commands edits
    /// terrain in current Valheim. Spawning a TerrainOp does not work for custom settings, because
    /// the game now sends terrain operations by prefab hash and looks the settings up in ZNetScene.
    ///
    /// Must run on a machine that has the terrain loaded, i.e. the client standing there.
    /// A dedicated server only generates zones transiently and has no heightmaps to edit.
    /// Saving claims ownership of the zone's terrain data so the change is synced to everyone.
    /// </summary>
    internal static class TerrainShaper
    {
        private enum Surface
        {
            Natural,
            Dirt,
            Paved,
            Cultivated,
        }

        private static readonly List<Piece> PieceBuffer = new List<Piece>();
        private static readonly List<Heightmap> HeightmapBuffer = new List<Heightmap>();

        /// <summary>
        /// Applies a stage at <paramref name="pos"/>. Returns true only if terrain was changed.
        /// Every outcome is logged so failures are visible without debug logging.
        /// </summary>
        public static bool Apply(Vector3 pos, WearStage stage, ShapeSettings shape, List<Vector3> links)
        {
            if (stage == WearStage.Untouched)
                return false;

            Surface surface = ReadSurface(pos);
            // Only painted data is trusted; natural ground is never "cultivated".
            if (shape.ProtectCultivated && surface == Surface.Cultivated)
            {
                Plugin.Log.LogInfo($"{stage} at {pos:F1} skipped: cultivated ground.");
                return false;
            }

            bool buildingsNearby = BuildingsNearby(pos, shape);
            // Smooth at the first stage, when it becomes a path, and when it becomes a road.
            bool smoothStage = stage == WearStage.Trampled || stage == WearStage.DirtPath || stage == WearStage.StoneRoad;
            bool smooth = smoothStage && shape.SmoothRadius > 0f && !buildingsNearby;

            // Never downgrade: hand-paved ground is left as is, and dirt (e.g. a hoe path) isn't
            // repainted by the dirt stages.
            PaintStyle style = StyleFor(stage);
            bool paint = style.Kind != PaintKind.None
                && surface != Surface.Paved
                && !(surface == Surface.Dirt && stage <= WearStage.DirtPath);
            float paintRadius = stage == WearStage.StoneRoad ? shape.StoneRoadRadius : shape.DirtPathRadius;

            if (!smooth && !paint)
            {
                Plugin.Log.LogInfo($"{stage} at {pos:F1} skipped: nothing to do (surface {surface}, buildings nearby {buildingsNearby}).");
                return false;
            }

            float radius = Mathf.Max(smooth ? shape.SmoothRadius : 0f, paint ? paintRadius : 0f);
            // The paint is a stroke from this cell's centre to each neighbouring cell already at
            // this stage, so cells reaching a stage join into a continuous trail instead of a
            // row of separate circles.
            float reach = 0f;
            if (links != null)
                foreach (Vector3 l in links)
                    reach = Mathf.Max(reach, Utils.DistanceXZ(pos, l));

            HeightmapBuffer.Clear();
            Heightmap.FindHeightmap(pos, radius + reach + 1f, HeightmapBuffer);

            var compilers = new List<TerrainComp>();
            foreach (Heightmap hm in HeightmapBuffer)
            {
                if (hm == null)
                    continue;
                TerrainComp tc = hm.GetAndCreateTerrainCompiler();
                if (tc != null && tc.m_hmap != null)
                    compilers.Add(tc);
            }

            // One average for the whole brush, in world height. Computing it per zone made a
            // vertex on a zone seam move by different amounts in each zone and tore the seam.
            bool haveAverage = false;
            float average = 0f;
            if (smooth)
                haveAverage = WorldAverageHeight(compilers, pos, shape.SmoothRadius, out average);

            int heightNodes = 0, paintNodes = 0, zones = 0;
            foreach (TerrainComp tc in compilers)
            {
                int h = haveAverage ? SmoothHeights(tc, pos, shape.SmoothRadius, shape.SmoothPower, average) : 0;
                int p = paint ? PaintNodes(tc, pos, links, paintRadius, style) : 0;
                if (h + p == 0)
                    continue;

                Save(tc);
                heightNodes += h;
                paintNodes += p;
                zones++;
            }
            HeightmapBuffer.Clear();

            if (paint)
                ClutterSystem.instance?.ResetGrass(pos, paintRadius + 0.5f);

            Plugin.Log.LogInfo($"{stage} at {pos:F1}: smoothed {heightNodes} height nodes, painted {paintNodes} paint nodes{(paint ? " (" + style.Kind + ")" : "")} in {zones} zone(s), linked to {links?.Count ?? 0} neighbour(s); surface was {surface}, buildings nearby {buildingsNearby}.");
            return zones > 0;
        }

        private static Vector3 NodeToWorld(Heightmap hm, int x, int z)
        {
            Vector3 v = hm.transform.position;
            v.x += (x - hm.m_width / 2) * hm.m_scale;
            v.z += (z - hm.m_width / 2) * hm.m_scale;
            return v;
        }

        /// <summary>Average world height of every height node within the radius, across all zones.</summary>
        private static bool WorldAverageHeight(List<TerrainComp> compilers, Vector3 center, float radius, out float average)
        {
            double sum = 0;
            int count = 0;
            foreach (TerrainComp tc in compilers)
            {
                Heightmap hm = tc.m_hmap;
                int max = tc.m_width + 1;
                IList<float> heights = hm.m_heights;
                if (heights == null || heights.Count < max * max)
                    continue;
                float baseY = hm.transform.position.y;
                for (int z = 0; z < max; z++)
                for (int x = 0; x < max; x++)
                {
                    if (Utils.DistanceXZ(center, NodeToWorld(hm, x, z)) > radius)
                        continue;
                    sum += baseY + heights[z * max + x];
                    count++;
                }
            }
            average = count > 0 ? (float)(sum / count) : 0f;
            return count > 0;
        }

        /// <summary>
        /// Pulls heights inside the radius toward <paramref name="average"/> (a world height),
        /// strongest at the centre. The change depends only on world position and height, so a
        /// vertex shared by two zones moves the same in both. Returns the number of nodes changed.
        /// </summary>
        private static int SmoothHeights(TerrainComp tc, Vector3 center, float radius, float power, float average)
        {
            Heightmap hm = tc.m_hmap;
            int max = tc.m_width + 1;
            IList<float> heights = hm.m_heights;
            if (tc.m_levelDelta == null || tc.m_levelDelta.Length != max * max
                || heights == null || heights.Count < max * max)
                return 0;

            float baseY = hm.transform.position.y;
            int changed = 0;
            for (int z = 0; z < max; z++)
            for (int x = 0; x < max; x++)
            {
                float d = Utils.DistanceXZ(center, NodeToWorld(hm, x, z)) / radius;
                if (d > 1f)
                    continue;
                int i = z * max + x;
                // Half-strength at the centre, fading to nothing at the edge; higher power = softer edge.
                float weight = 0.5f * Mathf.Pow(1f - d, power / 3f);
                float delta = weight * (average - (baseY + heights[i]));
                tc.m_levelDelta[i] += delta + tc.m_smoothDelta[i];
                tc.m_smoothDelta[i] = 0f;
                tc.m_modifiedHeight[i] = tc.m_levelDelta[i] != 0f;
                changed++;
            }
            return changed;
        }

        private enum PaintKind
        {
            None,
            DirtPatches,   // partial red, patchy
            DirtFull,      // red, like the hoe's path
            PavingPatches, // partial blue over dirt, patchy
            PavingFull,    // blue, like the hoe's paved road
        }

        private struct PaintStyle
        {
            public PaintKind Kind;
            public float Coverage; // share of the area that shows wear (patch styles)
            public float Amount;   // channel strength where it shows (patch styles)
        }

        /// <summary>
        /// Patch stages: Coverage is the share of ground that shows wear, Amount the paint strength
        /// there. Strong dirt (roughly above half) is what makes the game drop grass, so Worn uses
        /// full-strength patches: patchy grass with bare ground between, short of a hoed path.
        /// </summary>
        private static PaintStyle StyleFor(WearStage stage)
        {
            PaintStyle Patches(PaintKind kind, float coverage, float amount) =>
                new PaintStyle { Kind = kind, Coverage = coverage, Amount = amount };

            switch (stage)
            {
                case WearStage.Trampled: return Patches(PaintKind.DirtPatches, 0.5f, 0.7f);
                case WearStage.Worn: return Patches(PaintKind.DirtPatches, 0.8f, 1f);
                case WearStage.DirtPath: return new PaintStyle { Kind = PaintKind.DirtFull };
                case WearStage.Gravel: return Patches(PaintKind.PavingPatches, 0.6f, 0.6f);
                case WearStage.StoneRoad: return new PaintStyle { Kind = PaintKind.PavingFull };
                default: return new PaintStyle { Kind = PaintKind.None };
            }
        }

        /// <summary>
        /// 0..1 noise used to make early wear patchy instead of a uniform smear. Perlin gives
        /// blobs a couple of metres across; a little per-node jitter breaks up their edges.
        /// </summary>
        private static float Patchiness(Vector3 world)
        {
            float n = Mathf.PerlinNoise(world.x * 0.45f + 1000f, world.z * 0.45f + 1000f);
            float jitter = Mathf.PerlinNoise(world.x * 2.3f + 500f, world.z * 2.3f + 500f);
            return Mathf.Clamp01(n * 0.8f + jitter * 0.2f);
        }

        /// <summary>
        /// Paints one stage. Paint channels: red = dirt, green = cultivated (never touched here),
        /// blue = paving; partial values blend the textures, which is how the patchy stages work.
        /// Channels only ever increase toward the stage's look, so overlapping brushes from
        /// neighbouring cells never undo each other. Returns the number of nodes changed.
        /// </summary>
        private static int PaintNodes(TerrainComp tc, Vector3 center, List<Vector3> links, float radius, PaintStyle style)
        {
            Heightmap hm = tc.m_hmap;
            int max = tc.m_width + 1;
            if (tc.m_paintMask == null || tc.m_paintMask.Length != max * max)
            {
                Plugin.Log.LogWarning($"Unexpected paint grid size {tc.m_paintMask?.Length ?? 0} (expected {max * max}); not painting.");
                return 0;
            }

            bool ashlands = WorldGenerator.IsAshlands(center.x, center.z);
            int changed = 0;
            for (int z = 0; z < max; z++)
            for (int x = 0; x < max; x++)
            {
                Vector3 world = NodeToWorld(hm, x, z);
                float d = StrokeDistance(world, center, links) / radius;
                if (d > 1f)
                    continue;
                int i = z * max + x;

                Color source = tc.m_modifiedPaint[i] ? tc.m_paintMask[i] : Heightmap.m_paintMaskNothing;
                // Ashlands stores lava in alpha and treats unmodified ground as alpha 0.
                if (ashlands && !tc.m_modifiedPaint[i])
                    source.a = 0f;

                // Full strength over the inner 70%, soft edge outside it.
                float edge = Mathf.Clamp01((1f - d) / 0.3f);
                Color result = source;

                switch (style.Kind)
                {
                    case PaintKind.DirtPatches:
                    case PaintKind.PavingPatches:
                    {
                        // Wear is likelier near the middle of the trail.
                        float coverage = style.Coverage * (1f - 0.5f * d);
                        float show = Mathf.Clamp01((coverage - Patchiness(world)) * 5f);
                        float amount = show * edge * style.Amount;
                        if (amount < 0.02f)
                            continue; // leave untouched ground untouched
                        if (style.Kind == PaintKind.DirtPatches)
                            result.r = Mathf.Max(source.r, amount);
                        else
                            result.b = Mathf.Max(source.b, amount);
                        break;
                    }
                    case PaintKind.DirtFull:
                        result.r = Mathf.Lerp(source.r, 1f, edge);
                        break;
                    case PaintKind.PavingFull:
                        result.r = Mathf.Lerp(source.r, 0f, edge);
                        result.b = Mathf.Lerp(source.b, 1f, edge);
                        break;
                }
                result.a = source.a; // keep lava/biome data untouched

                if (tc.m_modifiedPaint[i] && result == source)
                    continue;
                tc.m_paintMask[i] = result;
                tc.m_modifiedPaint[i] = true;
                changed++;
            }
            return changed;
        }

        /// <summary>Horizontal distance from <paramref name="p"/> to the stroke: the centre point plus a segment to each link.</summary>
        private static float StrokeDistance(Vector3 p, Vector3 center, List<Vector3> links)
        {
            float best = Utils.DistanceXZ(p, center);
            if (links == null)
                return best;

            var a = new Vector2(center.x, center.z);
            var q = new Vector2(p.x, p.z);
            foreach (Vector3 l in links)
            {
                var b = new Vector2(l.x, l.z);
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 0f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / len2) : 0f;
                best = Mathf.Min(best, Vector2.Distance(q, a + ab * t));
            }
            return best;
        }

        private static void Save(TerrainComp tc)
        {
            tc.GetComponent<ZNetView>()?.ClaimOwnership();
            tc.m_operations++;
            tc.m_lastOpPoint = Vector3.zero;
            tc.m_lastOpRadius = 0f;
            tc.Save();
            tc.m_hmap.Poke();
        }

        private static bool BuildingsNearby(Vector3 pos, ShapeSettings shape)
        {
            if (shape.BuildingClearance < 0f)
                return false;

            PieceBuffer.Clear();
            Piece.GetAllPiecesInRadius(pos, shape.SmoothRadius + shape.BuildingClearance, PieceBuffer);
            bool any = PieceBuffer.Count > 0;
            PieceBuffer.Clear();
            return any;
        }

        /// <summary>
        /// Reads the painted surface at a point from the zone's terrain compiler. Paint colours are
        /// red = dirt, green = cultivated, blue = paved. Best effort: if the layout of the paint data
        /// is not what we expect, report natural ground and let the operation proceed.
        /// </summary>
        private static Surface ReadSurface(Vector3 pos)
        {
            try
            {
                TerrainComp tc = TerrainComp.FindTerrainCompiler(pos);
                if (tc == null || tc.m_paintMask == null || tc.m_modifiedPaint == null || tc.m_hmap == null)
                    return Surface.Natural;

                Heightmap hm = tc.m_hmap;
                int max = tc.m_width + 1;
                if (tc.m_paintMask.Length != max * max)
                    return Surface.Natural;

                Vector3 rel = pos - hm.transform.position;
                int x = Mathf.Clamp(Mathf.RoundToInt(rel.x / hm.m_scale) + hm.m_width / 2, 0, max - 1);
                int z = Mathf.Clamp(Mathf.RoundToInt(rel.z / hm.m_scale) + hm.m_width / 2, 0, max - 1);
                int index = z * max + x;
                if (!tc.m_modifiedPaint[index])
                    return Surface.Natural;

                Color c = tc.m_paintMask[index];
                if (c.g > 0.5f && c.g >= c.r && c.g >= c.b) return Surface.Cultivated;
                // Only fully dirt or fully paved ground counts; the mod's own partial stages
                // (worn patches, gravel) must not block the stages after them.
                if (c.b > 0.9f) return Surface.Paved;
                if (c.r > 0.95f) return Surface.Dirt;
                return Surface.Natural;
            }
            catch (Exception e)
            {
                Plugin.Debug($"Could not read paint at {pos}: {e.Message}");
                return Surface.Natural;
            }
        }
    }
}
