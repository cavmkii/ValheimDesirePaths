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

        private static readonly Color Dirt = Color.red;
        private static readonly Color Paved = Color.blue;

        private static readonly List<Piece> PieceBuffer = new List<Piece>();
        private static readonly List<Heightmap> HeightmapBuffer = new List<Heightmap>();

        /// <summary>
        /// Applies a stage at <paramref name="pos"/>. Returns true only if terrain was changed.
        /// Every outcome is logged so failures are visible without debug logging.
        /// </summary>
        public static bool Apply(Vector3 pos, WearStage stage, ShapeSettings shape)
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
            bool smooth = shape.SmoothRadius > 0f && !buildingsNearby;

            // Never downgrade: a hand-paved road stays paved when it reaches the dirt stage.
            bool paint = false;
            Color color = default;
            float paintRadius = 0f;
            if (stage == WearStage.DirtPath && surface == Surface.Natural)
            {
                paint = true;
                color = Dirt;
                paintRadius = shape.DirtPathRadius;
            }
            else if (stage == WearStage.StoneRoad && surface != Surface.Paved)
            {
                paint = true;
                color = Paved;
                paintRadius = shape.StoneRoadRadius;
            }

            if (!smooth && !paint)
            {
                Plugin.Log.LogInfo($"{stage} at {pos:F1} skipped: nothing to do (surface {surface}, buildings nearby {buildingsNearby}).");
                return false;
            }

            float radius = Mathf.Max(smooth ? shape.SmoothRadius : 0f, paint ? paintRadius : 0f);
            HeightmapBuffer.Clear();
            Heightmap.FindHeightmap(pos, radius + 1f, HeightmapBuffer);

            int heightNodes = 0, paintNodes = 0, zones = 0;
            foreach (Heightmap hm in HeightmapBuffer)
            {
                if (hm == null)
                    continue;
                TerrainComp tc = hm.GetAndCreateTerrainCompiler();
                if (tc == null || tc.m_hmap == null)
                    continue;

                int h = smooth ? SmoothHeights(tc, pos, shape.SmoothRadius, shape.SmoothPower) : 0;
                int p = paint ? PaintNodes(tc, pos, paintRadius, color) : 0;
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

            Plugin.Log.LogInfo($"{stage} at {pos:F1}: smoothed {heightNodes} height nodes, painted {paintNodes} paint nodes{(paint ? " " + (color == Dirt ? "dirt" : "paved") : "")} in {zones} zone(s); surface was {surface}, buildings nearby {buildingsNearby}.");
            return zones > 0;
        }

        private static Vector3 NodeToWorld(Heightmap hm, int x, int z)
        {
            Vector3 v = hm.transform.position;
            v.x += (x - hm.m_width / 2) * hm.m_scale;
            v.z += (z - hm.m_width / 2) * hm.m_scale;
            return v;
        }

        /// <summary>
        /// Pulls heights inside the radius toward their local average, strongest at the centre.
        /// Returns the number of nodes changed.
        /// </summary>
        private static int SmoothHeights(TerrainComp tc, Vector3 center, float radius, float power)
        {
            Heightmap hm = tc.m_hmap;
            int max = tc.m_width + 1;
            if (tc.m_levelDelta == null || tc.m_levelDelta.Length != max * max || hm.m_heights == null)
                return 0;

            // Average height of the area being smoothed.
            float sum = 0f;
            int count = 0;
            for (int z = 0; z < max; z++)
            for (int x = 0; x < max; x++)
            {
                if (Utils.DistanceXZ(center, NodeToWorld(hm, x, z)) > radius)
                    continue;
                sum += hm.m_heights[z * max + x];
                count++;
            }
            if (count == 0)
                return 0;
            float average = sum / count;

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
                float delta = weight * (average - hm.m_heights[i]);
                tc.m_levelDelta[i] += delta + tc.m_smoothDelta[i];
                tc.m_smoothDelta[i] = 0f;
                tc.m_modifiedHeight[i] = tc.m_levelDelta[i] != 0f;
                changed++;
            }
            return changed;
        }

        /// <summary>Blends the paint mask toward <paramref name="color"/>. Returns nodes changed.</summary>
        private static int PaintNodes(TerrainComp tc, Vector3 center, float radius, Color color)
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
                float d = Utils.DistanceXZ(center, NodeToWorld(hm, x, z)) / radius;
                if (d > 1f)
                    continue;
                int i = z * max + x;

                Color source = tc.m_modifiedPaint[i] ? tc.m_paintMask[i] : Heightmap.m_paintMaskNothing;
                // Ashlands stores lava in alpha and treats unmodified ground as alpha 0.
                if (ashlands && !tc.m_modifiedPaint[i])
                    source.a = 0f;

                Color target = color;
                target.a = source.a; // keep lava/biome data untouched

                // Full strength over the inner 70%, soft edge outside it.
                float strength = Mathf.Clamp01((1f - d) / 0.3f);
                tc.m_paintMask[i] = Color.Lerp(source, target, strength);
                tc.m_modifiedPaint[i] = true;
                changed++;
            }
            return changed;
        }

        private static void Save(TerrainComp tc)
        {
            tc.GetComponent<ZNetView>()?.ClaimOwnership();
            tc.m_operations++;
            tc.m_lastOpPoint = Vector3.zero;
            tc.m_lastOpRadius = 0f;
            tc.Save();
            tc.m_hmap.Poke(false);
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
                if (c.b > 0.5f) return Surface.Paved;
                if (c.r > 0.5f) return Surface.Dirt;
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
