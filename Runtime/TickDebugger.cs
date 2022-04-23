using System;
using Mirage;

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

        TickDebuggerGui gui;

        private void Awake()
        {
            Identity.OnStartClient.AddListener(OnStartClient);
            Identity.OnStartServer.AddListener(OnStartServer);
            gui = GetComponent<TickDebuggerGui>();
        }
        private void Update()
        {
            tickRunner.OnUpdate();

            gui.IsServer = IsServer;
            gui.IsClient = IsClient;

            gui.ClientTick = clientTick;
            gui.ServerTick = serverTick;
            gui.Diff = diff.Var;

            if (IsClient)
            {
                gui.ClientDelayInTicks = ClientRunner.Debug_DelayInTicks;
                gui.ClientTimeScale = ClientRunner.TimeScale;
                (float average, float stdDev) = ClientRunner.Debug_RTT.GetAverageAndStandardDeviation();
                gui.ClientRTT = average;
                gui.ClientJitter = stdDev;
            }
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
    }
}
