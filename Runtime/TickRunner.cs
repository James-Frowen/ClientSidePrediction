using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mirage;
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.CSP
{
    public class TickRunner : IPredictionTime
    {
        public delegate void OnTick(int tick);
        static readonly ILogger logger = LogFactory.GetLogger<TickRunner>();

        public float TickRate = 50;

        protected int _tick;

        /// <summary>
        /// Used by client to keep up with server
        /// <para>always 1 on server</para>
        /// </summary>
        public float TimeScale { get; protected set; } = 1;

        readonly Stopwatch stopwatch;
        double tickTimer;
        double lastFrame;
        /// <summary>
        /// keep track of last tick invoked on event, incase client jumps to line up with server
        /// </summary>
        int lastInvokedTick;

        /// <summary>
        /// Make tick update event, Called before <see cref="onTick"/>
        /// </summary>
        public event OnTick onPreTick;
        /// <summary>
        /// Make tick update event
        /// </summary>
        public event OnTick onTick;
        /// <summary>
        /// Late tick update event, Called after <see cref="onTick"/>
        /// </summary>
        public event OnTick onPostTick;

        public TickRunner()
        {
            stopwatch = Stopwatch.StartNew();
        }

        public float FixedDeltaTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => 1f / TickRate;
        }

        public int Tick
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _tick;
        }

        public double UnscaledTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => stopwatch.Elapsed.TotalSeconds;
        }

        bool IPredictionTime.IsResimulation => false;
        float IPredictionTime.FixedTime => Tick * FixedDeltaTime;

        public void OnUpdate()
        {
            double now = stopwatch.Elapsed.TotalSeconds;
            double delta = now - lastFrame;
            lastFrame = now;
#if DEBUG
            if (delta > FixedDeltaTime * 100)
            {
                // if more than 100 frames behind then skip
                // this is is only to stop editor breaking if using breakpoints
                if (logger.LogEnabled()) logger.LogError($"Time Delta was {delta}, skipping tick Updates");
                return;
            }
#endif

            tickTimer += delta * TimeScale;
            while (tickTimer > FixedDeltaTime)
            {
                tickTimer -= FixedDeltaTime;
                _tick++;

                // only invoke is tick is later, see lastInvokedTick
                // todo what if we jump back, do we not need to resimulate?
                if (_tick > lastInvokedTick)
                {
                    onPreTick?.Invoke(_tick);
                    onTick?.Invoke(_tick);
                    onPostTick?.Invoke(_tick);
                    lastInvokedTick = _tick;
                }
            }
        }

        // have this virtual methods here, just so we have use 1 field for TickRunner.
        // we will only call this method on client so it should be a ClientTickRunner
        public virtual void OnMessage(int serverTick) => throw new NotSupportedException("OnMessage is not supported for default tick runner. See ClientTickRunner");
    }

    public class ClientTickRunner : TickRunner
    {
        static readonly ILogger logger = LogFactory.GetLogger<ClientTickRunner>();

        readonly NetworkTime networkTime;

        readonly ExponentialMovingAverage diffAvg;

        readonly float fastScale = 1.01f;
        readonly float normalScale = 1f;
        readonly float slowScale = 0.99f;

        readonly float positiveThreshold;
        readonly float negativeThreshold;
        readonly float skipAheadThreshold;

        public float ClientDelay = 2;

        public double TargetDelayTicks => (networkTime.Rtt * TickRate) + ClientDelay;

        bool intialized;
        int latestServerTick;

        public float ClientDelaySeconds => ClientDelay * FixedDeltaTime;

        /// <summary>
        /// Invoked at start AND if client gets too get away from server
        /// </summary>
        public event Action OnTickSkip;

        /// <param name="diffThreshold">how far off client time can be before changing its speed, Good value is half SyncInterval</param>
        /// <param name="timeScaleModifier">how much to speed up/slow down by is behind/ahead</param>
        /// <param name="skipThreshold">skip ahead to server tick if this far behind</param>
        /// <param name="movingAverageCount">how many ticks used in average, increase or decrease with framerate</param>
        public ClientTickRunner(NetworkTime networkTime, float diffThreshold = 0.5f, float timeScaleModifier = 0.01f, float skipThreshold = 10f, int movingAverageCount = 30)
        {
            this.networkTime = networkTime;

            // IMPORTANT: most of these values are in tick NOT seconds, so careful when using them

            // if client is off by 0.5 then speed up/slow down
            positiveThreshold = diffThreshold;
            negativeThreshold = -positiveThreshold;

            // skip ahead if client fall behind by this many ticks
            skipAheadThreshold = skipThreshold;

            // speed up/slow down up by 0.01 if after/behind
            // we never want to be behind so catch up faster
            fastScale = normalScale + (timeScaleModifier * 5);
            slowScale = normalScale - timeScaleModifier;

            diffAvg = new ExponentialMovingAverage(movingAverageCount);
        }

        /// <summary>
        /// Updates <see cref="clientScaleTime"/> to keep <see cref="ClientTime"/> in line with <see cref="LatestServerTime"/>
        /// </summary>
        /// <param name="serverTime"></param>
        public override void OnMessage(int serverTick)
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
            double serverGuess = _tick - TargetDelayTicks;
            // how wrong were we
            double diff = serverTick - serverGuess;

            // if diff is bad enough, skip ahead
            // todo do we need abs, do also want to skip back if we are very ahead?
            // todo will skipping behind cause negative effects? we dont want Tick event to be invoked for a tick twice
            if (Math.Abs(diff) > skipAheadThreshold)
            {
                logger.LogWarning($"Client fell behind, skipping ahead. server:{serverTick:0.00} serverGuess:{serverGuess} diff:{diff:0.00}");
                InitNew(serverTick);
                return;
            }

            // average the diff
            diffAvg.Add(diff);

            // apply scale to correct guess
            AdjustClientTimeScale((float)diffAvg.Value);

            //todo add trace level
            if (logger.LogEnabled()) logger.Log($"st {serverTick:0.00} sg {serverGuess:0.00} ct {_tick:0.00} diff {diff * 1000:0.0}, wanted:{diffAvg.Value * 1000:0.0}, scale:{TimeScale}");
        }

        private void InitNew(int serverTick)
        {
            _tick = Mathf.CeilToInt(serverTick + (float)TargetDelayTicks);
            TimeScale = normalScale;
            diffAvg.Reset();
            intialized = true;
            // todo do we need to invoke this at start as well as skip?
            OnTickSkip?.Invoke();
        }

        private void AdjustClientTimeScale(float diff)
        {
            // diff is server-client,
            // if positive then server is ahead, => we can run client faster to catch up
            // if negative then server is behind, => we need to run client slow to not run out of spanshots

            // we want diffVsGoal to be as close to 0 as possible

            // server ahead, speed up client
            if (diff > positiveThreshold)
                TimeScale = fastScale;
            // server behind, slow down client
            else if (diff < negativeThreshold)
                TimeScale = slowScale;
            // close enough
            else
                TimeScale = normalScale;
        }
    }
}
