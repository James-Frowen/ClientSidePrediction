using System;
using System.Diagnostics;
using Mirage;
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class TickRunner : MonoBehaviour, IPredictionTime
    {
        static readonly ILogger logger = LogFactory.GetLogger<TickRunner>();

        public int TickRate = 50;
        public float ClientDelay = 2;

        public float TickInterval => 1f / TickRate;
        double tickTimer;
        double lastFrame;
        int tick;

        Stopwatch stopwatch;

        float IPredictionTime.FixedDeltaTime => TickInterval;

        public delegate void OnTick(int tick);
        public event OnTick onTick;
        public event OnTick onClientTick
        {
            add => interpolationTime.onTick += value;
            remove => interpolationTime.onTick -= value;
        }
        internal InterpolationTime interpolationTime;

        private void Awake()
        {
            stopwatch = Stopwatch.StartNew();
        }

        private void OnValidate()
        {
            if (interpolationTime != null)
                interpolationTime.ClientDelayTicks = ClientDelay;
        }

        public void InitTime(NetworkBehaviour behaviour)
        {
            interpolationTime = new InterpolationTime(this, behaviour.NetworkTime, tickDelay: ClientDelay);
        }

        /// <summary>
        /// Call this when client receives messagr from server
        /// </summary>
        public void OnMessage(int receivedTick)
        {
            interpolationTime.OnMessage(receivedTick);
        }

        private void Update()
        {
            double now = stopwatch.Elapsed.TotalSeconds;
            double delta = now - lastFrame;
            lastFrame = now;

            if (interpolationTime != null)
                interpolationTime.OnUpdate(now);
#if DEBUG
            if (delta > TickInterval * 100)
            {
                // if more than 100 frames behind then skip
                // this is is only to stop editor breaking if using breakpoints
                if (logger.LogEnabled()) logger.LogError($"Time Delta was {delta}, skipping tick Updates");
                return;
            }
#endif

            tickTimer += delta;
            while (tickTimer > TickInterval)
            {
                tickTimer -= TickInterval;
                tick++;

                onTick?.Invoke(tick);
            }
        }
    }

    public class InterpolationTime
    {
        static readonly ILogger logger = LogFactory.GetLogger<InterpolationTime>();

        readonly NetworkTime networkTime;
        readonly TickRunner tickRunner;

        readonly ExponentialMovingAverage diffAvg;

        readonly float fastScale = 1.01f;
        readonly float normalScale = 1f;
        readonly float slowScale = 0.99f;

        readonly float positiveThreshold;
        readonly float negativeThreshold;
        readonly float skipAheadThreshold;

        float clientDelay;

        public double TargetDelayTicks => (networkTime.Rtt * tickRunner.TickRate) + clientDelay;

        bool intialized;
        //float clientTick;
        float clientScaleTime;
        int latestServerTick;

        double previous;
        double tickTimer;
        int tick;
        public event TickRunner.OnTick onTick;

        public float TimeScale => clientScaleTime;
        public float ClientDelaySeconds => clientDelay * tickRunner.TickInterval;
        public float ClientDelayTicks
        {
            get => clientDelay;
            set => clientDelay = value;
        }

        /// <param name="diffThreshold">how far off client time can be before changing its speed, Good value is half SyncInterval</param>
        /// <param name="timeScaleModifier">how much to speed up/slow down by is behind/ahead</param>
        /// <param name="skipThreshold">skip ahead to server tick if this far behind</param>
        /// <param name="tickDelay">How many ticks to be behind the server, This should be high enough to handle dropped packets and small changes in lantacny</param>
        /// <param name="movingAverageCount">how many ticks used in average, increase or decrease with framerate</param>
        public InterpolationTime(TickRunner tickRunner, NetworkTime networkTime, float diffThreshold = 0.5f, float timeScaleModifier = 0.01f, float skipThreshold = 5f, float tickDelay = 2, int movingAverageCount = 30)
        {
            this.tickRunner = tickRunner;
            this.networkTime = networkTime;

            // IMPORTANT: most of these values are in tick NOT seconds, so careful when using them

            // if client is off by 0.5 then speed up/slow down
            positiveThreshold = diffThreshold;
            negativeThreshold = -positiveThreshold;

            // skip ahead if client fall behind by this many ticks
            skipAheadThreshold = skipThreshold;

            // speed up/slow down up by 0.01 if after/behind
            fastScale = normalScale + timeScaleModifier;
            slowScale = normalScale - timeScaleModifier;

            clientDelay = tickDelay;

            diffAvg = new ExponentialMovingAverage(movingAverageCount);

            // start at normal time scale
            clientScaleTime = normalScale;
        }

        public void OnUpdate(double now)
        {
            double delta = (now - previous);
            previous = now;

            tickTimer += delta * clientScaleTime;
            while (tickTimer > tickRunner.TickInterval)
            {
                tickTimer -= tickRunner.TickInterval;
                tick++;

                onTick?.Invoke(tick);
            }
        }


        /// <summary>
        /// Updates <see cref="clientScaleTime"/> to keep <see cref="ClientTime"/> in line with <see cref="LatestServerTime"/>
        /// </summary>
        /// <param name="serverTime"></param>
        public void OnMessage(int serverTick)
        {
#if DEBUG
            if (serverTick <= latestServerTick)
            {
                logger.LogError($"Received message out of order server:{latestServerTick}, new:{serverTick}");
                return;
            }
            latestServerTick = serverTick;
#endif

            // if first message set client time to server-diff
            // reset stuff if too far behind
            // todo check this is correct
            if (!intialized)
            {
                InitNew(serverTick);
                return;
            }

            // if OWD=10,delay=2 =>  then server-tick=2*owd+delay => 22
            // guess what we think server tick was
            // tick = (seconds*ticks/second) + ticks, units checkout :)
            double serverGuess = tick - TargetDelayTicks;
            // how wrong were we
            double diff = serverTick - serverGuess;

            // if diff is bad enough, skip ahead
            // todo do we need abs, do also want to skip back if we are very ahead?
            // todo will skipping behind cause negative effects? we dont want Tick event to be invoked for a tick twice
            if (Math.Abs(diff) > skipAheadThreshold)
            {
                logger.LogWarning($"Client fell behind, skipping ahead. server:{serverTick:0.00} client:{tick} diff:{diff:0.00}");
                InitNew(serverTick);
                return;
            }

            // average the diff
            diffAvg.Add(diff);

            // apply scale to correct guess
            AdjustClientTimeScale((float)diffAvg.Value);

            //todo add trace level
            if (logger.LogEnabled()) logger.Log($"st {serverTick:0.00} sg {serverGuess:0.00} ct {tick:0.00} diff {diff * 1000:0.0}, wanted:{diffAvg.Value * 1000:0.0}, scale:{clientScaleTime}");
        }

        private void InitNew(int serverTick)
        {
            tick = Mathf.CeilToInt(serverTick + (float)TargetDelayTicks);
            clientScaleTime = normalScale;
            diffAvg.Reset();
            intialized = true;
        }

        private void AdjustClientTimeScale(float diff)
        {
            // diff is server-client,
            // if positive then server is ahead, => we can run client faster to catch up
            // if negative then server is behind, => we need to run client slow to not run out of spanshots

            // we want diffVsGoal to be as close to 0 as possible

            // server ahead, speed up client
            if (diff > positiveThreshold)
                clientScaleTime = fastScale;
            // server behind, slow down client
            else if (diff < negativeThreshold)
                clientScaleTime = slowScale;
            // close enough
            else
                clientScaleTime = normalScale;
        }
    }
}
