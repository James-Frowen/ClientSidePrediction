using System;
using Mirage;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class TickDebugger : NetworkBehaviour
    {
        //public int ClientDelay;

        TickRunner tickRunner;
        ClientTickRunner ClientRunner => (ClientTickRunner)tickRunner;

        double latestClientTime;
        int clientTick;
        int serverTick;

        ExponentialMovingAverage diff = new ExponentialMovingAverage(10);

        private void Awake()
        {
            Identity.OnStartClient.AddListener(OnStartClient);
            Identity.OnStartServer.AddListener(OnStartServer);
        }
        private void Update()
        {
            tickRunner.OnUpdate();
        }
        void OnStartServer()
        {
            tickRunner = new TickRunner();
            tickRunner.onTick += ServerTick;
        }

        private void ServerTick(int tick)
        {
            serverTick = tick;
            ToClient_StateMessage(tick, latestClientTime);
        }

        void OnStartClient()
        {
            tickRunner = new ClientTickRunner(
                movingAverageCount: 50 * 5// 5 seconds
                );
            tickRunner.onTick += ClientTick;
            NetworkTime.PingInterval = 0;
        }

        private void ClientTick(int tick)
        {
            clientTick = tick;
            ToServer_InputMessage(tick, tickRunner.UnscaledTime);
        }

        [ClientRpc(channel = Channel.Unreliable)]
        public void ToClient_StateMessage(int tick, double clientTime)
        {
            tickRunner.OnMessage(tick, clientTime);
            serverTick = tick;
            diff.Add(clientTick - serverTick);
        }


        [ServerRpc(channel = Channel.Unreliable)]
        public void ToServer_InputMessage(int tick, double clientTime)
        {
            clientTick = tick;
            diff.Add(clientTick - serverTick);
            latestClientTime = Math.Max(latestClientTime, clientTime);
        }

        private void OnGUI()
        {
            int x = IsServer ? 100 : 400;
            using (new GUILayout.AreaScope(new Rect(x, 10, 250, 500), GUIContent.none))
            {
                GUI.enabled = false;
                if (IsServer)
                {
                    bool ahead = clientTick > serverTick;
                    string aheadText = ahead ? "Ahead" : "behind";
                    GUILayout.Label($"Client Tick {clientTick} {aheadText}");
                }
                else
                {
                    GUILayout.Label($"Client Tick {clientTick}");
                }
                GUILayout.Label($"Server Tick {serverTick}");
                GUILayout.Space(20);
                GUILayout.Label($"Diff {diff.Value:0.00}");
                if (IsServer)
                    GUILayout.Label($"target {0.0f:0.00}");
                if (IsClient)
                    GUILayout.Label($"target {ClientRunner.Debug_DelayInTicks:0.00}");


                if (IsClient)
                {
                    GUILayout.Space(20);
                    GUILayout.Label($"scale {ClientRunner.TimeScale:0.00}");
                    (float rtt, float jitter) = ClientRunner.Debug_RTT.GetAverageAndStandardDeviation();
                    GUILayout.Label($"RTT {rtt * 1000:0}");
                    GUILayout.Label($"Jitter {jitter * 1000:0}");
                    GUILayout.Label($"Target Delay {(rtt + jitter * 2) * 1000:0}");
                }
                GUI.enabled = true;
            }
        }
    }
}
