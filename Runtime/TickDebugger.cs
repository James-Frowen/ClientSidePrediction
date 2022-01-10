using Mirage;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class TickDebugger : NetworkBehaviour
    {
        TickRunner tickRunner;

        int clientTick;
        int serverTick;

        ExponentialMovingAverage diff = new ExponentialMovingAverage(10);

        private void Awake()
        {
            Identity.OnStartClient.AddListener(OnStartClient);
            Identity.OnStartServer.AddListener(OnStartServer);
        }
        void OnStartServer()
        {
            tickRunner = ServerObjectManager.GetComponent<TickRunner>();
            tickRunner.onTick += ServerTick;
        }

        private void ServerTick(int tick)
        {
            serverTick = tick;
            RpcStateMessage(tick);
        }

        [ClientRpc(channel = Channel.Unreliable)]
        public void RpcStateMessage(int tick)
        {
            tickRunner.OnMessage(tick);
            serverTick = tick;
            diff.Add(clientTick - serverTick);
        }

        void OnStartClient()
        {
            tickRunner = ClientObjectManager.GetComponent<TickRunner>();
            tickRunner.InitTime(this);
            tickRunner.onClientTick += ClientTick;
            NetworkTime.PingInterval = 0;
        }

        private void ClientTick(int tick)
        {
            clientTick = tick;
            RpcInputMessage(tick);
        }

        [ServerRpc(channel = Channel.Unreliable)]
        public void RpcInputMessage(int tick)
        {
            clientTick = tick;
            diff.Add(clientTick - serverTick);
        }

        private void OnGUI()
        {
            int x = IsServer ? 100 : 400;
            using (new GUILayout.AreaScope(new Rect(x, 10, 250, 500), GUIContent.none))
            {
                GUI.enabled = false;
                GUILayout.Label($"Client Tick {clientTick}");
                GUILayout.Label($"Server Tick {serverTick}");
                GUILayout.Space(20);
                GUILayout.Label($"Diff {diff.Value:0.00}");
                if (IsServer)
                    GUILayout.Label($"target {tickRunner.ClientDelay:0.00}");
                if (IsClient)
                    GUILayout.Label($"target {tickRunner.interpolationTime.TargetDelayTicks:0.00}");


                if (IsClient)
                {
                    GUILayout.Space(20);
                    GUILayout.Label($"scale {tickRunner.interpolationTime.TimeScale:0.00}");
                    GUILayout.Label($"RTT {NetworkTime.Rtt * 1000:0}");
                    GUILayout.Label($"Target Delay {(NetworkTime.Rtt + tickRunner.interpolationTime.ClientDelaySeconds) * 1000:0}");
                }
                GUI.enabled = true;
            }
        }
    }
}
