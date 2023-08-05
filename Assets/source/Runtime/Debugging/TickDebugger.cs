using System;
using Mirage;

namespace JamesFrowen.CSP.Debugging
{
    public class TickDebugger : NetworkBehaviour
    {
        private TickRunner tickRunner;

        private ClientTickRunner ClientRunner => (ClientTickRunner)tickRunner;

        private double latestClientTime;
        private int clientTick;
        private int serverTick;
        private readonly ExponentialMovingAverage diff = new ExponentialMovingAverage(10);
        private TickDebuggerOutput gui;

        private void Awake()
        {
            Identity.OnStartClient.AddListener(OnStartClient);
            Identity.OnStartServer.AddListener(OnStartServer);
            gui = GetComponent<TickDebuggerOutput>();
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
                gui.ClientTimeScale = ClientRunner.TimeScaleMultiple;

                var (rtt, jitter) = ClientRunner.GetRTTAndJitter();

                gui.ClientDelayInTicks = ClientRunner.GetDelayInTicks();
                gui.ClientRTT = rtt;
                gui.ClientJitter = jitter;
            }
        }

        private void OnStartServer()
        {
            tickRunner = new TickRunner();
            tickRunner.OnTick += ServerTick;
        }

        private void ServerTick(int tick)
        {
            serverTick = tick;
            ToClient_StateMessage(tick, latestClientTime);
        }

        private void OnStartClient()
        {
            tickRunner = new ClientTickRunner();
            tickRunner.OnTick += ClientTick;
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
