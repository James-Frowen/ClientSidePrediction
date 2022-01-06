using System.Collections;
using System.Diagnostics;
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class TickRunner : MonoBehaviour
    {
        static readonly ILogger logger = LogFactory.GetLogger<TickRunner>();

        public int TickRate = 50;
        public float TimeScale = 1f;

        public float TickInterval => 1f / TickRate;
        double tickTimer;
        double lastFrame;
        int tick;

        Stopwatch stopwatch;


        public delegate void OnTick(int tick);
        public event OnTick onTick;

        private void Awake()
        {
            stopwatch = Stopwatch.StartNew();
        }

        public bool slowMode { get; private set; }
        IEnumerator current;
        int clientDelay = 0;
        public int fakeDelay { get; private set; } = 0;
        bool auto = false;
        bool autoServer = false;
        float rate = 1;
        private void OnGUI()
        {
            slowMode = GUILayout.Toggle(slowMode, GUIContent.none);

            clientDelay = int.Parse(GUILayout.TextField(clientDelay.ToString()));
            fakeDelay = int.Parse(GUILayout.TextField(fakeDelay.ToString()));
            if (GUILayout.Button("Next"))
            {
                ManualNext();
            }
            auto = GUILayout.Toggle(auto, GUIContent.none);
            autoServer = GUILayout.Toggle(autoServer, GUIContent.none);
            rate = float.Parse(GUILayout.TextField(rate.ToString("0.00")));
        }

        private void ManualNext()
        {
            if (current == null || !current.MoveNext())
            {
                current = ManualUpdate();
            }
        }

        //internal PredictionBehaviour.ServerController Server { get; set; }

        IEnumerator ManualUpdate()
        {
            tick++;
            onTick?.Invoke(tick - clientDelay);
            if (logger.LogEnabled()) logger.Log($"Client tick {tick - clientDelay}");

            throw new System.NotImplementedException();
            //IEnumerator serverTick = Server.Tick(tick);

            //while (serverTick.MoveNext())
            //{
            //    if (!autoServer)
            //        yield return null;
            //}
            //  if (logger.LogEnabled())   logger.Log($"Server tick {tick}");
        }

        float previous = 0;

        private void Update()
        {
            if (slowMode)
            {
                if (auto && Time.time > previous + 1f / rate)
                {
                    previous = Time.time;
                    ManualNext();
                }

            }
            else
            {
                double now = stopwatch.Elapsed.TotalSeconds;
                double delta = now - lastFrame;
                lastFrame = now;

#if DEBUG
                if (delta > TickInterval * 100)
                {
                    // if more than 100 frames behind then skip
                    // this is is only to stop editor breaking if using breakpoints
                    if (logger.LogEnabled()) logger.LogError($"Time Delta was {delta}, skipping tick Updates");
                    return;
                }
#endif

                tickTimer += delta * TimeScale;
                while (tickTimer > TickInterval)
                {
                    tickTimer -= TickInterval;
                    tick++;

                    onTick?.Invoke(tick);
                }
            }
        }
    }
}
