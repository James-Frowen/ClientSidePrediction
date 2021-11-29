using System.Diagnostics;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class TickRunner : MonoBehaviour
    {
        public int TickRate = 50;

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

        private void Update()
        {
            double now = stopwatch.Elapsed.TotalSeconds;
            double delta = now - lastFrame;
            lastFrame = now;

            tickTimer += delta;
            while (tickTimer > TickInterval)
            {
                tickTimer -= TickInterval;
                tick++;

                onTick?.Invoke(tick);
            }
        }
    }
}
