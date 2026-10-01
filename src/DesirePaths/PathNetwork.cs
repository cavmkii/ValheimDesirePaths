using System.Collections.Generic;
using UnityEngine;

namespace DesirePaths
{
    /// <summary>
    /// Who keeps the step counts:
    ///  - Single player / hosting: this machine (it is the server).
    ///  - Client on a server running the mod: the server. Clients report steps, the server
    ///    counts them for everyone and tells the stepping client when to reshape the ground.
    ///  - Client on a server without the mod: this client keeps its own counts.
    /// </summary>
    internal static class PathNetwork
    {
        public const int ProtocolVersion = 3;

        private const string RpcHello = "DesirePaths_Hello";
        private const string RpcHelloAck = "DesirePaths_HelloAck";
        private const string RpcStep = "DesirePaths_Step";
        private const string RpcApply = "DesirePaths_Apply";
        private const string RpcRoads = "DesirePaths_Roads";

        /// <summary>How long a client waits for the server to answer before counting locally.</summary>
        private const float HandshakeTimeout = 10f;

        /// <summary>Steps reported further than this from the reporting player are ignored.</summary>
        private const float MaxReportDistance = 40f;

        public enum Mode
        {
            Inactive,
            Authoritative, // this machine is the server, or single player
            AwaitingServer,
            ServerCounts,
            LocalCounts,   // server does not have the mod
        }

        public static Mode Current { get; private set; } = Mode.Inactive;

        /// <summary>Cell size and cooldown the server uses, so client de-duplication matches it.</summary>
        public static float ServerCellSize { get; private set; }
        public static float ServerCooldown { get; private set; }

        private static ZRoutedRpc _registeredOn;
        private static float _helloSentAt;

        /// <summary>Server side: peers that completed the handshake with a matching protocol.</summary>
        private static readonly HashSet<long> CompatiblePeers = new HashSet<long>();

        /// <summary>Call every frame. Tracks session start/end and registers RPCs.</summary>
        public static void Tick()
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                if (Current != Mode.Inactive)
                {
                    Plugin.Debug("Session ended.");
                    MapRoads.Clear();
                }
                Current = Mode.Inactive;
                _registeredOn = null;
                return;
            }

            if (_registeredOn != ZRoutedRpc.instance)
            {
                Register(ZRoutedRpc.instance);
                _registeredOn = ZRoutedRpc.instance;
                CompatiblePeers.Clear();
                Current = Mode.Inactive;
                ServerCellSize = 0f;
                ServerCooldown = 0f;
            }

            if (ZNet.instance.IsServer())
            {
                Current = Mode.Authoritative;
                return;
            }

            if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected || Player.m_localPlayer == null)
                return;

            switch (Current)
            {
                case Mode.Inactive:
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcHello, ProtocolVersion);
                    _helloSentAt = Time.time;
                    Current = Mode.AwaitingServer;
                    break;

                case Mode.AwaitingServer:
                    if (Time.time - _helloSentAt > HandshakeTimeout)
                    {
                        Current = Mode.LocalCounts;
                        Plugin.Log.LogInfo("Server did not answer; it probably does not run DesirePaths. Counting your own steps locally.");
                    }
                    break;
            }
        }

        private static void Register(ZRoutedRpc rpc)
        {
            rpc.Register<int>(RpcHello, OnHello);
            rpc.Register<ZPackage>(RpcHelloAck, OnHelloAck);
            rpc.Register<Vector3>(RpcStep, OnStep);
            rpc.Register<ZPackage>(RpcApply, OnApply);
            rpc.Register<ZPackage>(RpcRoads, OnRoads);
        }

        /// <summary>Called by the step tracker when the local player wears a cell.</summary>
        public static void ReportStep(Vector3 pos)
        {
            switch (Current)
            {
                case Mode.Authoritative:
                case Mode.LocalCounts:
                    CountStep(pos, applyLocally: true, sender: 0L);
                    break;

                case Mode.ServerCounts:
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RpcStep, pos);
                    break;

                // Inactive / AwaitingServer: drop the step rather than guess who should count it.
            }
        }

        private static void CountStep(Vector3 pos, bool applyLocally, long sender)
        {
            WearStore store = Plugin.Store;
            if (store == null)
                return;

            double now = ZNet.instance.GetTimeSeconds();
            WearStage advanced = store.RecordStep(pos, now, out float steps, out WearStage previous);

            if (PathConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo($"Step at {pos:F1} from {(applyLocally ? "local player" : sender.ToString())}: {steps:F1} steps");

            if (advanced == WearStage.Untouched)
                return;

            Plugin.Debug($"Cell at {pos:F1} reached {advanced} after {steps:F0} steps.");

            if (advanced >= WearStage.DirtPath)
            {
                MapRoads.Set(pos, advanced);
                BroadcastRoad(pos, advanced);
            }

            // Apply every enabled stage passed through, in order, so a cell that jumps several
            // stages at once (e.g. after a config change) still gets the in-between looks.
            int[] thresholds = PathConfig.Thresholds();
            ShapeSettings shape = ShapeSettings.FromConfig();
            for (var stage = previous + 1; stage <= advanced; stage++)
            {
                if (!WearStore.IsEnabled(thresholds, (int)stage))
                    continue;

                if (applyLocally)
                {
                    if (TerrainShaper.Apply(pos, stage, shape))
                        Plugin.AnnounceStage(stage);
                }
                else
                {
                    var pkg = new ZPackage();
                    pkg.Write(pos);
                    pkg.Write((int)stage);
                    shape.Write(pkg);
                    ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcApply, pkg);
                }
            }
        }

        // ---- server side ----

        private static void OnHello(long sender, int clientVersion)
        {
            if (!ZNet.instance.IsServer())
                return;

            if (clientVersion == ProtocolVersion)
                CompatiblePeers.Add(sender);
            else
                Plugin.Log.LogWarning($"Peer {sender} runs DesirePaths protocol {clientVersion}, this server runs {ProtocolVersion}. Its steps will be ignored.");

            var pkg = new ZPackage();
            pkg.Write(ProtocolVersion);
            pkg.Write(PathConfig.CellSize.Value);
            pkg.Write(PathConfig.SameCellCooldown.Value);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcHelloAck, pkg);

            if (clientVersion == ProtocolVersion)
                SendAllRoads(sender);
        }

        /// <summary>Sends every known road cell to a newly joined client for its map.</summary>
        private static void SendAllRoads(long peer)
        {
            WearStore store = Plugin.Store;
            if (store == null)
                return;

            var pkg = new ZPackage();
            int count = 0;
            var body = new ZPackage();
            store.ForEachRoad((pos, stage) =>
            {
                body.Write(pos.x);
                body.Write(pos.z);
                body.Write((int)stage);
                count++;
            });
            pkg.Write(count);
            pkg.Write(body);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcRoads, pkg);
            Plugin.Debug($"Sent {count} road cell(s) to {peer}.");
        }

        /// <summary>Tells every client running the mod about a cell that just became a path or road.</summary>
        private static void BroadcastRoad(Vector3 pos, WearStage stage)
        {
            if (!ZNet.instance.IsServer() || CompatiblePeers.Count == 0)
                return;

            var body = new ZPackage();
            body.Write(pos.x);
            body.Write(pos.z);
            body.Write((int)stage);
            foreach (long peer in CompatiblePeers)
            {
                var pkg = new ZPackage();
                pkg.Write(1);
                pkg.Write(body);
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, RpcRoads, pkg);
            }
        }

        private static void OnStep(long sender, Vector3 pos)
        {
            if (!ZNet.instance.IsServer() || !PathConfig.Enabled.Value || !CompatiblePeers.Contains(sender))
                return;

            // Only accept steps near where the reporting player actually is.
            ZNetPeer peer = ZNet.instance.GetPeer(sender);
            if (peer == null)
                return;
            if (Utils.DistanceXZ(peer.m_refPos, pos) > MaxReportDistance)
            {
                Plugin.Debug($"Ignoring step from {sender} at {pos:F1}: too far from the player.");
                return;
            }

            CountStep(pos, applyLocally: false, sender: sender);
        }

        // ---- client side ----

        private static void OnHelloAck(long sender, ZPackage pkg)
        {
            if (ZNet.instance.IsServer() || sender != ZRoutedRpc.instance.GetServerPeerID())
                return;

            int serverVersion = pkg.ReadInt();
            if (serverVersion != ProtocolVersion)
            {
                Plugin.Log.LogWarning($"Server runs DesirePaths protocol {serverVersion}, this client runs {ProtocolVersion}. Update the mod on both; counting locally until then.");
                Current = Mode.LocalCounts;
                return;
            }

            ServerCellSize = pkg.ReadSingle();
            ServerCooldown = pkg.ReadSingle();

            if (Current == Mode.LocalCounts)
                Plugin.Log.LogInfo("Server answered late; switching to server-side step counts.");
            MapRoads.Clear(); // the server sends its own road list next
            Current = Mode.ServerCounts;
            Plugin.Debug($"Server counts steps (cell {ServerCellSize} m, cooldown {ServerCooldown} s).");
        }

        private static void OnRoads(long sender, ZPackage pkg)
        {
            if (ZNet.instance.IsServer() || sender != ZRoutedRpc.instance.GetServerPeerID())
                return;

            int count = pkg.ReadInt();
            ZPackage body = pkg.ReadPackage();
            for (int i = 0; i < count; i++)
            {
                float x = body.ReadSingle();
                float z = body.ReadSingle();
                var stage = (WearStage)body.ReadInt();
                MapRoads.Set(new Vector3(x, 0f, z), stage);
            }
            Plugin.Debug($"Received {count} road cell(s) from the server.");
        }

        private static void OnApply(long sender, ZPackage pkg)
        {
            if (ZNet.instance.IsServer() || sender != ZRoutedRpc.instance.GetServerPeerID())
                return;

            Vector3 pos = pkg.ReadVector3();
            var stage = (WearStage)pkg.ReadInt();
            ShapeSettings shape = ShapeSettings.Read(pkg);

            if (TerrainShaper.Apply(pos, stage, shape))
                Plugin.AnnounceStage(stage);
        }
    }
}
