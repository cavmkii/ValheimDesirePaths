using System.Collections.Generic;
using UnityEngine;

namespace DesirePaths
{
    /// <summary>
    /// Watches the local player and reports a step each time they walk into a new grid cell
    /// on bare terrain. Building floors, water, boats, dungeons and mid-air don't count.
    /// </summary>
    internal sealed class StepTracker
    {
        private long _lastCell = long.MinValue;
        private readonly Dictionary<long, float> _recent = new Dictionary<long, float>();
        private float _nextPrune;

        public void Reset()
        {
            _lastCell = long.MinValue;
            _recent.Clear();
        }

        public void Tick()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                Reset();
                return;
            }

            float cellSize = PathNetwork.ServerCellSize > 0f ? PathNetwork.ServerCellSize : PathConfig.CellSize.Value;
            float cooldown = PathNetwork.ServerCellSize > 0f ? PathNetwork.ServerCooldown : PathConfig.SameCellCooldown.Value;

            Vector3 pos = player.transform.position;
            long cell = WearStore.CellKey(pos, cellSize);
            if (cell == _lastCell)
                return;
            _lastCell = cell;

            if (!IsWalkingOnTerrain(player))
                return;

            float now = Time.time;
            if (_recent.TryGetValue(cell, out float last) && now - last < cooldown)
                return;
            _recent[cell] = now;
            PruneRecent(now, cooldown);

            if (WorldGenerator.instance != null)
            {
                Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pos);
                if ((PathConfig.ExcludedBiomes.Value & biome) != 0)
                    return;
            }

            PathNetwork.ReportStep(pos);
        }

        private static bool IsWalkingOnTerrain(Player player)
        {
            if (player.IsDead() || player.IsTeleporting() || player.InInterior())
                return false;
            if (!player.IsOnGround() || player.InWater() || player.IsSwimming() || player.IsAttached())
                return false;

            // Standing on the terrain mesh, not on a floor, rock, or a tree root.
            Collider ground = player.GetLastGroundCollider();
            return ground != null && ground.GetComponentInParent<Heightmap>() != null;
        }

        private void PruneRecent(float now, float cooldown)
        {
            if (now < _nextPrune)
                return;
            _nextPrune = now + 30f;

            var stale = new List<long>();
            foreach (var kv in _recent)
            {
                if (now - kv.Value >= cooldown)
                    stale.Add(kv.Key);
            }
            foreach (long key in stale)
                _recent.Remove(key);
        }
    }
}
