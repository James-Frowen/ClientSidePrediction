using UnityEngine;

namespace JamesFrowen.CSP
{
    public class TickDebuggerGui : MonoBehaviour
    {
        public bool IsServer;
        public bool IsClient;

        public int ClientTick;
        public int ServerTick;
        public double Diff;

        public float ClientDelayInTicks;
        public float ClientTimeScale;
        public float ClientRTT;
        public float ClientJitter;

        private void OnGUI()
        {
            int x = IsServer ? 100 : 400;
            using (new GUILayout.AreaScope(new Rect(x, 10, 250, 500), GUIContent.none))
            {
                GUI.enabled = false;
                if (IsServer)
                {
                    bool ahead = ClientTick > ServerTick;
                    string aheadText = ahead ? "Ahead" : "behind";
                    GUILayout.Label($"Client Tick {ClientTick} {aheadText}");
                }
                else
                {
                    GUILayout.Label($"Client Tick {ClientTick}");
                }
                GUILayout.Label($"Server Tick {ServerTick}");
                GUILayout.Space(20);
                GUILayout.Label($"Diff {Diff:0.00}");
                if (IsServer)
                    GUILayout.Label($"target {0.0f:0.00}");
                if (IsClient)
                    GUILayout.Label($"target {ClientDelayInTicks:0.00}");


                if (IsClient)
                {
                    GUILayout.Space(20);
                    GUILayout.Label($"scale {ClientTimeScale:0.00}");
                    GUILayout.Label($"RTT {ClientRTT * 1000:0}");
                    GUILayout.Label($"Jitter {ClientJitter * 1000:0}");
                    GUILayout.Label($"Target Delay {(ClientRTT + ClientJitter * 2) * 1000:0}");
                }
                GUI.enabled = true;
            }
        }
    }
}
