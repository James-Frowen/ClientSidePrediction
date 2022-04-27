using UnityEngine;

namespace JamesFrowen.CSP
{
    public abstract class TickDebuggerOutput : MonoBehaviour
    {
        public bool IsServer { get; set; }
        public bool IsClient { get; set; }

        public int ClientTick { get; set; }
        public int ServerTick { get; set; }
        public double Diff { get; set; }

        public float ClientDelayInTicks { get; set; }
        public float ClientTimeScale { get; set; }
        public float ClientRTT { get; set; }
        public float ClientJitter { get; set; }
    }
}
