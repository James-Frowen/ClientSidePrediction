using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Mirage.Logging;
using UnityEngine;
using UnityEngine.Assertions;

namespace JamesFrowen.CSP
{
    public class TickRunner : IPredictionTime
    {
        public delegate void OnTick(int tick);
        static readonly ILogger logger = LogFactory.GetLogger<TickRunner>();

        public float TickRate = 50;

        /// <summary>
        /// Max milliseconds per frame to process. Wont start new Ticks if current frame is over this limit.
        /// <para>
        /// This can avoid freezes if ticks start to take a long time.
        /// </para>
        /// <para>
        /// The runner will try to run <see cref="TickRate"/> per second, but if they take longer than 1 second then each frame will get longer and longer.
        /// This limit will stops extra ticks in that frame from being processed, allowing other parts of the applications (eg message processing).
        /// <para>
        /// Any stopped ticks will run next frame instead
        /// </para>
        /// </para>
        /// </summary>
        public float MaxFrameTime = 200;

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

        double GetCurrentTime()
        {
            return stopwatch.Elapsed.TotalSeconds;
        }

        public virtual void OnUpdate()
        {
            double now = GetCurrentTime();
            int startTick = _tick;
            double max = now + (MaxFrameTime / 1000f);
            double delta = now - lastFrame;
            lastFrame = now;

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

                if (GetCurrentTime() > max)
                {
                    if (logger.WarnEnabled()) logger.LogWarning($"Took longer than {MaxFrameTime}ms to process frame. Processed {_tick - startTick} ticks in {(GetCurrentTime() - now) * 1000f}ms");
                    break;
                }
            }
        }

        // have this virtual methods here, just so we have use 1 field for TickRunner.
        // we will only call this method on client so it should be a ClientTickRunner
        public virtual void OnMessage(int serverTick, double clientTime) => throw new NotSupportedException("OnMessage is not supported for default tick runner. See ClientTickRunner");
    }

    public class ClientTickRunner : TickRunner
    {

        static readonly ILogger logger = LogFactory.GetLogger<ClientTickRunner>();

        readonly SimpleMovingAverage _RTTAverage;

        readonly float fastScale = 1.01f;
        readonly float normalScale = 1f;
        readonly float slowScale = 0.99f;

        readonly float positiveThreshold;
        readonly float negativeThreshold;
        readonly float skipAheadThreshold;
        readonly float skipBackThreshold;

        public float ClientDelay = 2;

        bool intialized;
        int latestServerTick;

        //public float ClientDelaySeconds => ClientDelay * FixedDeltaTime;

#if DEBUG
        public float Debug_DelayInTicks { get; private set; }
        public SimpleMovingAverage Debug_RTT => _RTTAverage;
#endif

        /// <summary>
        /// Invoked at start AND if client gets too get away from server
        /// </summary>
        public event Action OnTickSkip;

        /// <param name="diffThreshold">how far off client time can be before changing its speed, Good value is half SyncInterval</param>
        /// <param name="timeScaleModifier">how much to speed up/slow down by is behind/ahead</param>
        /// <param name="skipThreshold">skip ahead to server tick if this far behind</param>
        /// <param name="movingAverageCount">how many ticks used in average, increase or decrease with framerate</param>
        public ClientTickRunner(float diffThreshold = 0.5f, float timeScaleModifier = 0.01f, float skipThreshold = 10f, int movingAverageCount = 100)
        {
            // IMPORTANT: most of these values are in tick NOT seconds, so careful when using them

            // if client is off by 0.5 then speed up/slow down
            positiveThreshold = diffThreshold;
            negativeThreshold = -positiveThreshold;

            // skip ahead if client fall behind by this many ticks
            skipAheadThreshold = skipThreshold;
            skipBackThreshold = skipThreshold * 5;

            // speed up/slow down up by 0.01 if after/behind
            // we never want to be behind so catch up faster
            fastScale = normalScale + (timeScaleModifier * 5);
            slowScale = normalScale - timeScaleModifier;

            _RTTAverage = new SimpleMovingAverage(movingAverageCount);
        }

        /// <summary>
        /// Updates <see cref="clientScaleTime"/> to keep <see cref="ClientTime"/> in line with <see cref="LatestServerTime"/>
        /// </summary>
        /// <param name="serverTime"></param>
        public override void OnMessage(int serverTick, double clientSendTime)
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
#if DEBUG
                Debug("serverTick,serverGuess,localTick,delayInTicks,delayInSeconds,delayFromLag,delayFromJitter,diff,");
#endif
                InitNew(serverTick, clientSendTime);
                return;
            }

            AddTimeToAverage(clientSendTime);

            (float lag, float jitter) = _RTTAverage.GetAverageAndStandardDeviation();

            //public double TargetDelayTicks => (networkTime.Rtt * TickRate) + ClientDelay;
            // todo use jitter for delay
            float delayFromJitter = ClientDelay * jitter;
            float delayFromLag = lag;
            float delayInSeconds = delayFromLag + delayFromJitter;
            // +1 tick to make sure we are always ahead
            float delayInTicks = (delayInSeconds * TickRate) + 1;
#if DEBUG
            Debug_DelayInTicks = delayInTicks;
#endif

            // if OWD=10,delay=2 =>  then server-tick=2*owd+delay => 22
            // guess what we think server tick was
            // tick = (seconds*ticks/second) + ticks, units checkout :)
            float serverGuess = _tick - delayInTicks;
            // how wrong were we
            float diff = serverTick - serverGuess;

#if DEBUG
            Debug($"{serverTick},{serverGuess},{_tick},{delayInTicks},{delayInSeconds},{delayFromLag},{delayFromJitter},{diff},");
#endif


            // if diff is bad enough, skip ahead
            // todo do we need abs, do also want to skip back if we are very ahead?
            // todo will skipping behind cause negative effects? we dont want Tick event to be invoked for a tick twice
            if (Math.Abs(diff) > skipAheadThreshold)
            {
                logger.LogWarning($"Client fell behind, skipping ahead. server:{serverTick:0.00} serverGuess:{serverGuess} diff:{diff:0.00}");
                InitNew(serverTick, clientSendTime);
                return;
            }

            // apply scale to correct guess
            AdjustClientTimeScale(diff);

            //todo add trace level
            if (logger.LogEnabled()) logger.Log($"st {serverTick:0.00} sg {serverGuess:0.00} ct {_tick:0.00} diff {diff * 1000:0.0}, wanted:{diff * 1000:0.0}, scale:{TimeScale}");
        }

        private void AddTimeToAverage(double clientSendTime)
        {
            // only add if client time was returned from server
            // it will be zero before client sends first input
            if (clientSendTime != 0)
            {
                double newRTT = UnscaledTime - clientSendTime;
                Assert.IsTrue(newRTT > 0);
                _RTTAverage.Add((float)newRTT);
            }
        }

        private void InitNew(int serverTick, double clientSendTime)
        {
            AddTimeToAverage(clientSendTime);
            (float lag, float jitter) = _RTTAverage.GetAverageAndStandardDeviation();

            _tick = Mathf.CeilToInt(serverTick + (lag * TickRate) + 2 /*+2 ticks because we dont want to be behind server guess*/);
            TimeScale = normalScale;
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

#if DEBUG
        static StreamWriter _writer = new StreamWriter(Path.Combine(Application.persistentDataPath, "ClientTickRunner.log")) { AutoFlush = true };
        void Debug(string line)
        {
            _writer.WriteLine(line);
        }
#endif
    }
}
