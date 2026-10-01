using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DesirePaths
{
    /// <summary>
    /// Step counts per grid cell for one world. Lives on whichever machine is authoritative:
    /// the server (or single-player/host), or a client whose server does not run the mod.
    /// </summary>
    internal sealed class WearStore
    {
        private const uint Magic = 0x48545044; // "DPTH"
        private const int FormatVersion = 2;

        private struct Cell
        {
            public float Steps;
            public WearStage Stage;
            public double LastStep; // world time in seconds
        }

        private readonly Dictionary<long, Cell> _cells = new Dictionary<long, Cell>();
        private readonly string _path;
        private bool _dirty;

        public string Path => _path;
        public int Count => _cells.Count;

        private WearStore(string path)
        {
            _path = path;
        }

        public static long CellKey(Vector3 pos, float cellSize)
        {
            int x = Mathf.FloorToInt(pos.x / cellSize);
            int z = Mathf.FloorToInt(pos.z / cellSize);
            return ((long)x << 32) | (uint)z;
        }

        /// <summary>
        /// Adds one step at <paramref name="pos"/>. Returns the stage the cell has just advanced to,
        /// or <see cref="WearStage.Untouched"/> if nothing changed. <paramref name="previous"/> is
        /// the stage before this step, so callers can apply every stage passed through.
        /// </summary>
        public WearStage RecordStep(Vector3 pos, double now, out float steps, out WearStage previous)
        {
            long key = CellKey(pos, PathConfig.CellSize.Value);
            _cells.TryGetValue(key, out Cell cell);

            cell.Steps = Decayed(cell, now) + 1f;
            cell.LastStep = now;
            steps = cell.Steps;
            previous = cell.Stage;

            WearStage reached = StageFor(cell.Steps);
            WearStage advancedTo = WearStage.Untouched;
            if (reached > cell.Stage)
            {
                cell.Stage = reached;
                advancedTo = reached;
            }

            _cells[key] = cell;
            _dirty = true;
            return advancedTo;
        }

        /// <summary>World-space centre of the cell containing <paramref name="pos"/> (y kept).</summary>
        public static Vector3 CellCentre(Vector3 pos)
        {
            float size = PathConfig.CellSize.Value;
            return new Vector3((Mathf.FloorToInt(pos.x / size) + 0.5f) * size, pos.y,
                               (Mathf.FloorToInt(pos.z / size) + 0.5f) * size);
        }

        /// <summary>Centres of the up to 8 neighbouring cells that have reached at least <paramref name="stage"/>.</summary>
        public List<Vector3> NeighboursAtLeast(Vector3 pos, WearStage stage)
        {
            float size = PathConfig.CellSize.Value;
            int cx = Mathf.FloorToInt(pos.x / size);
            int cz = Mathf.FloorToInt(pos.z / size);
            var result = new List<Vector3>();
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0)
                    continue;
                long key = ((long)(cx + dx) << 32) | (uint)(cz + dz);
                if (_cells.TryGetValue(key, out Cell c) && c.Stage >= stage)
                    result.Add(new Vector3((cx + dx + 0.5f) * size, pos.y, (cz + dz + 0.5f) * size));
            }
            return result;
        }

        /// <summary>Calls <paramref name="visit"/> with the centre of every cell at DirtPath or above.</summary>
        public void ForEachRoad(System.Action<Vector3, WearStage> visit)
        {
            float size = PathConfig.CellSize.Value;
            foreach (var kv in _cells)
            {
                if (kv.Value.Stage < WearStage.DirtPath)
                    continue;
                int x = (int)(kv.Key >> 32);
                int z = (int)(kv.Key & 0xffffffffL);
                visit(new Vector3((x + 0.5f) * size, 0f, (z + 0.5f) * size), kv.Value.Stage);
            }
        }

        private static float Decayed(Cell cell, double now)
        {
            float perDay = PathConfig.DecayPerDay.Value;
            if (perDay <= 0f || cell.Steps <= 0f || cell.LastStep <= 0.0 || now <= cell.LastStep)
                return cell.Steps;

            float dayLength = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0
                ? EnvMan.instance.m_dayLengthSec
                : 1800f;
            double days = (now - cell.LastStep) / dayLength;
            return Mathf.Max(0f, cell.Steps - (float)(days * perDay));
        }

        /// <summary>True if stage <paramref name="s"/> is switched on (its own step setting isn't 0).</summary>
        public static bool IsEnabled(int[] thresholds, int s) => thresholds[s] > 0;

        /// <summary>Highest enabled stage whose threshold <paramref name="steps"/> has met.</summary>
        private static WearStage StageFor(float steps)
        {
            int[] thresholds = PathConfig.Thresholds();
            WearStage stage = WearStage.Untouched;
            for (int s = 1; s < thresholds.Length; s++)
            {
                if (IsEnabled(thresholds, s) && steps >= thresholds[s])
                    stage = (WearStage)s;
            }
            return stage;
        }

        /// <summary>Version 1 had three stages: 1 smoothed, 2 dirt path, 3 stone road.</summary>
        private static WearStage MigrateV1(byte old)
        {
            switch (old)
            {
                case 1: return WearStage.Trampled;
                case 2: return WearStage.DirtPath;
                case 3: return WearStage.StoneRoad;
                default: return WearStage.Untouched;
            }
        }

        public static WearStore Load(string path)
        {
            var store = new WearStore(path);
            if (!File.Exists(path))
                return store;

            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadUInt32() != Magic)
                        throw new InvalidDataException("not a DesirePaths file");
                    int version = reader.ReadInt32();
                    if (version != 1 && version != FormatVersion)
                        throw new InvalidDataException($"unsupported format version {version}");

                    int count = reader.ReadInt32();
                    store._dirty = version != FormatVersion;
                    for (int i = 0; i < count; i++)
                    {
                        long key = reader.ReadInt64();
                        store._cells[key] = new Cell
                        {
                            Steps = reader.ReadSingle(),
                            Stage = version == 1 ? MigrateV1(reader.ReadByte()) : (WearStage)reader.ReadByte(),
                            LastStep = reader.ReadDouble(),
                        };
                    }
                }
            }
            catch (Exception e)
            {
                // Keep the unreadable file around rather than overwriting it on the next save.
                string backup = path + ".corrupt";
                Plugin.Log.LogError($"Could not read {path} ({e.Message}); moved it to {backup} and starting fresh.");
                try
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(path, backup);
                }
                catch (Exception moveError)
                {
                    Plugin.Log.LogError($"Could not move {path}: {moveError.Message}");
                }
                store._cells.Clear();
            }

            return store;
        }

        public void Save(double now)
        {
            if (!_dirty)
                return;

            Prune(now);

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path));
                string tmp = _path + ".tmp";
                using (var writer = new BinaryWriter(File.Create(tmp)))
                {
                    writer.Write(Magic);
                    writer.Write(FormatVersion);
                    writer.Write(_cells.Count);
                    foreach (var kv in _cells)
                    {
                        writer.Write(kv.Key);
                        writer.Write(kv.Value.Steps);
                        writer.Write((byte)kv.Value.Stage);
                        writer.Write(kv.Value.LastStep);
                    }
                }
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
                _dirty = false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Failed to save {_path}: {e.Message}");
            }
        }

        /// <summary>Drops cells that have decayed to nothing and never reached a stage.</summary>
        private void Prune(double now)
        {
            if (now <= 0.0)
                return;

            List<long> dead = null;
            foreach (var kv in _cells)
            {
                if (kv.Value.Stage == WearStage.Untouched && Decayed(kv.Value, now) < 0.5f)
                    (dead ?? (dead = new List<long>())).Add(kv.Key);
            }
            if (dead == null)
                return;
            foreach (long key in dead)
                _cells.Remove(key);
        }
    }
}
