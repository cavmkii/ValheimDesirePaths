using System;
using System.Collections.Generic;
using UnityEngine;

namespace DesirePaths
{
    /// <summary>
    /// Applies a wear stage to the ground using the game's own terrain operation, the same
    /// mechanism the hoe uses. The operation is routed by the game to whoever owns the
    /// zone's terrain data, so it is saved and synced like any hoe edit.
    ///
    /// Must run on a machine that has the terrain loaded, i.e. the client standing there.
    /// A dedicated server only generates zones transiently and has no heightmaps to edit.
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

        public static void Apply(Vector3 pos, WearStage stage, ShapeSettings shape)
        {
            if (stage == WearStage.Untouched)
                return;

            Heightmap hm = Heightmap.FindHeightmap(pos);
            if (hm == null)
            {
                Plugin.Debug($"No terrain loaded at {pos}; skipping {stage}.");
                return;
            }

            Surface surface = ReadSurface(pos);
            if (shape.ProtectCultivated && (surface == Surface.Cultivated || hm.IsCultivated(pos)))
            {
                Plugin.Debug($"Cultivated ground at {pos}; leaving it alone.");
                return;
            }

            var settings = new TerrainOp.Settings();

            bool smooth = shape.SmoothRadius > 0f && !BuildingsNearby(pos, shape);
            if (smooth)
            {
                settings.m_smooth = true;
                settings.m_smoothRadius = shape.SmoothRadius;
                settings.m_smoothPower = shape.SmoothPower;
            }

            // Never downgrade: a hand-paved road stays paved when it reaches the dirt stage.
            if (stage == WearStage.DirtPath && surface == Surface.Natural)
            {
                settings.m_paintCleared = true;
                settings.m_paintType = TerrainModifier.PaintType.Dirt;
                settings.m_paintRadius = shape.DirtPathRadius;
                settings.m_paintHeightCheck = false;
            }
            else if (stage == WearStage.StoneRoad && surface != Surface.Paved)
            {
                settings.m_paintCleared = true;
                settings.m_paintType = TerrainModifier.PaintType.Paved;
                settings.m_paintRadius = shape.StoneRoadRadius;
                settings.m_paintHeightCheck = false;
            }

            if (!settings.m_smooth && !settings.m_paintCleared)
            {
                Plugin.Debug($"Nothing to do for {stage} at {pos} (surface {surface}, smoothing {(smooth ? "on" : "blocked")}).");
                return;
            }

            Run(pos, settings);
            Plugin.Debug($"Applied {stage} at {pos} (smooth={settings.m_smooth}, paint={(settings.m_paintCleared ? settings.m_paintType.ToString() : "none")}).");
        }

        /// <summary>
        /// Spawns a throwaway TerrainOp. Its Awake finds the overlapping heightmaps, sends the
        /// operation to each zone's terrain compiler and destroys itself — exactly what happens
        /// when a hoe piece is placed, minus the placement effects.
        /// </summary>
        private static void Run(Vector3 pos, TerrainOp.Settings settings)
        {
            var go = new GameObject("DesirePaths_TerrainOp");
            go.SetActive(false);
            go.transform.position = pos;

            var op = go.AddComponent<TerrainOp>();
            op.m_settings = settings;
            op.m_onPlacedEffect = new EffectList();
            op.m_spawnOnPlaced = null;

            try
            {
                go.SetActive(true); // Awake runs here and applies the operation.
            }
            finally
            {
                if (go != null)
                    UnityEngine.Object.Destroy(go);
            }
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
                // The paint grid has been width^2 in some game versions and (width+1)^2 in others.
                int stride = Mathf.RoundToInt(Mathf.Sqrt(tc.m_paintMask.Length));
                if (stride <= 0 || stride * stride != tc.m_paintMask.Length)
                    return Surface.Natural;

                Vector3 rel = pos - hm.transform.position;
                int x = Mathf.Clamp(Mathf.FloorToInt(rel.x / hm.m_scale + hm.m_width / 2f), 0, stride - 1);
                int z = Mathf.Clamp(Mathf.FloorToInt(rel.z / hm.m_scale + hm.m_width / 2f), 0, stride - 1);
                int index = z * stride + x;
                if (index >= tc.m_modifiedPaint.Length || !tc.m_modifiedPaint[index])
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
