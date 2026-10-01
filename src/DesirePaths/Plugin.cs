using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace DesirePaths
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "cavmkii.DesirePaths";
        public const string Name = "Desire Paths";
        public const string Version = "0.3.1";

        private const float AutosaveInterval = 120f;

        internal static ManualLogSource Log;
        internal static WearStore Store { get; private set; }

        private readonly StepTracker _tracker = new StepTracker();
        private float _nextAutosave;
        private double _lastWorldTime;

        private void Awake()
        {
            Log = Logger;
            PathConfig.Bind(Config);
            Log.LogInfo($"{Name} {Version} loaded.");
        }

        private void Update()
        {
            PathNetwork.Tick();
            UpdateStore();

            if (PathNetwork.Current == PathNetwork.Mode.Inactive)
            {
                _tracker.Reset();
                return;
            }

            if (PathConfig.Enabled.Value)
                _tracker.Tick();

            MapRoads.Tick();
        }

        /// <summary>Loads, swaps, autosaves and unloads the step store as sessions come and go.</summary>
        private void UpdateStore()
        {
            if (ZNet.instance != null)
                _lastWorldTime = ZNet.instance.GetTimeSeconds();

            string wanted = WantedStorePath();
            if (Store != null && Store.Path != wanted)
            {
                Store.Save(_lastWorldTime);
                Debug($"Saved and closed {Store.Path}.");
                Store = null;
            }

            if (Store == null && wanted != null)
            {
                Store = WearStore.Load(wanted);
                MapRoads.Clear();
                Store.ForEachRoad(MapRoads.Set);
                Log.LogInfo($"Using step data {wanted} ({Store.Count} cells).");
                _nextAutosave = Time.time + AutosaveInterval;
            }

            if (Store != null && Time.time >= _nextAutosave)
            {
                Store.Save(_lastWorldTime);
                _nextAutosave = Time.time + AutosaveInterval;
            }
        }

        private static string WantedStorePath()
        {
            string prefix;
            switch (PathNetwork.Current)
            {
                case PathNetwork.Mode.Authoritative: prefix = "world"; break;
                case PathNetwork.Mode.LocalCounts: prefix = "local"; break;
                default: return null;
            }

            World world = ZNet.m_world ?? WorldGenerator.instance?.m_world;
            if (world == null)
                return null;

            string file = $"{prefix}_{Sanitize(world.m_name)}_{Sanitize(world.m_seedName)}.dat";
            return Path.Combine(Path.Combine(Paths.ConfigPath, "DesirePaths"), file);
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "unknown";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        private void OnApplicationQuit() => SaveNow();

        private void OnDestroy() => SaveNow();

        private void SaveNow()
        {
            Store?.Save(_lastWorldTime);
        }

        internal static void AnnounceStage(WearStage stage)
        {
            if (!PathConfig.ShowStageMessages.Value || MessageHud.instance == null || stage == WearStage.Untouched)
                return;

            string text;
            switch (stage)
            {
                case WearStage.Trampled: text = "The grass here is getting trampled"; break;
                case WearStage.Worn: text = "A trail is wearing in"; break;
                case WearStage.DirtPath: text = "A path has worn into the ground"; break;
                case WearStage.Gravel: text = "Stones are showing through the path"; break;
                case WearStage.StoneRoad: text = "This path has become a road"; break;
                default: return;
            }
            MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, text);
        }

        internal static void Debug(string message)
        {
            if (PathConfig.VerboseLogging != null && PathConfig.VerboseLogging.Value)
                Log.LogInfo(message);
        }
    }
}
