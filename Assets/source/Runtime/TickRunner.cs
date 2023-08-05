using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mirage.Logging;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Assertions;

#if CLIENT_TICK_RUNNER_VERBOSE
using System.IO;
#endif

namespace JamesFrowen.CSP
{
    public delegate void OnTick(int tick);


    public class PredictionTime : IPredictionTime
    {
        private readonly TickRunner _runner;

        public PredictionTime(TickRunner runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public float FixedDeltaTime => _runner.FixedDeltaTime;
        public double UnscaledTime => _runner.UnscaledTime;
        public float FixedTime => Tick * FixedDeltaTime;
        public double Time => _runner.Time;
        public double DeltaTime => _runner.DeltaTime;

        public int Tick { get; set; }
        public bool IsResimulation { get; set; } = false;
        public UpdateMethod Method { get; set; } = UpdateMethod.None;
    }
    public class TickRunner
    {
        private static readonly ILogger logger = LogFactory.GetLogger<TickRunner>();
        private static readonly ProfilerMarker beforeAllTicksMarker = new ProfilerMarker(ProfilerCategory.Network, "JamesFrowen.CSP.TickRunner.BeforeAllTicks");
        private static readonly ProfilerMarker beforeTickMarker = new ProfilerMarker(ProfilerCategory.Network, "JamesFrowen.CSP.TickRunner.BeforeTick");
        private static readonly ProfilerMarker onTickMarker = new ProfilerMarker(ProfilerCategory.Network, "JamesFrowen.CSP.TickRunner.OnTick");
        private static readonly ProfilerMarker afterTickMarker = new ProfilerMarker(ProfilerCategory.Network, "JamesFrowen.CSP.TickRunner.AfterTick");
        private static readonly ProfilerMarker afterAllTicksMarker = new ProfilerMarker(ProfilerCategory.Network, "JamesFrowen.CSP.TickRunner.AfterAllTicks");

        public readonly float TickRate;

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

        /// <summary>
        /// Limit number of ticks per frame
        /// <para>
        /// This is to avoid running too long per frame. Higher number will catch up faster, but will cause longer frame times
        /// </para>
        /// </summary>
        public int MaxTickPerFrame = 10;

        protected int _tick;
        protected double _time;
        protected double _deltaTime;

        /// <summary>
        /// Used by client to keep up with server
        /// <para>always 1 on server</para>
        /// </summary>
        public float TimeScaleMultiple { get; protected set; } = 1;

        private readonly Stopwatch stopwatch;
        private double tickTimer;
        private double lastFrame;
        private bool _isRunning;

        /// <summary>
        /// keep track of last tick invoked on event, incase client jumps to line up with server
        /// </summary>
        protected int lastInvokedTick;


        /// <summary>
        /// Called once a frame, before any ticks
        /// </summary>
        public event Action BeforeAllTicks;

        /// <summary>
        /// Main tick update event, Called before <see cref="Tick"/>
        /// </summary>
        public event OnTick BeforeTick;
        /// <summary>
        /// Main tick update event
        /// </summary>
        public event OnTick OnTick;
        /// <summary>
        /// Late tick update event, Called after <see cref="Tick"/>
        /// </summary>
        public event OnTick AfterTick;

        /// <summary>
        /// Called every frame, after all ticks (even if there were no ticks run this frame)
        /// </summary>
        public event Action AfterAllTicks;

        public TickRunner(float tickRate = 50)
        {
            stopwatch = Stopwatch.StartNew();
            TickRate = tickRate;
        }

        public bool IsRunning
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _isRunning;
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

        public double Time
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _time;
        }
        public double DeltaTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _deltaTime;
        }


        public double UnscaledTime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => stopwatch.Elapsed.TotalSeconds;
        }

        public void SetRunning(bool running)
        {
            _isRunning = running;
        }

        private double GetCurrentTime()
        {
            return stopwatch.Elapsed.TotalSeconds;
        }

        public virtual void OnUpdate()
        {
            var now = GetCurrentTime();
            var startTick = _tick;
            var max = now + (MaxFrameTime / 1000f);
            var delta = now - lastFrame;
            lastFrame = now;

#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPaused)
                return;
#endif
            // store last frame above even if we are not running
            // so that when we start running again the delta will not be huge
            if (!_isRunning)
                return;

            if (BeforeAllTicks != null)
            {
                beforeAllTicksMarker.Begin();
                BeforeAllTicks.Invoke();
                beforeAllTicksMarker.End();
            }


            var timeDelta = delta * UnityEngine.Time.timeScale * TimeScaleMultiple;

            _time += timeDelta;
            _deltaTime = timeDelta;
            tickTimer += timeDelta;

            while (tickTimer > FixedDeltaTime)
            {
                tickTimer -= FixedDeltaTime;
                _tick++;

                // only invoke is tick is later, see lastInvokedTick
                // todo what if we jump back, do we not need to resimulate?
                if (_tick > lastInvokedTick)
                {
                    if (BeforeTick != null)
                    {
                        beforeTickMarker.Begin();
                        BeforeTick.Invoke(_tick);
                        beforeTickMarker.End();
                    }
                    if (OnTick != null)
                    {
                        onTickMarker.Begin();
                        OnTick.Invoke(_tick);
                        onTickMarker.End();
                    }
                    if (AfterTick != null)
                    {
                        afterTickMarker.Begin();
                        AfterTick.Invoke(_tick);
                        afterTickMarker.End();
                    }

                    lastInvokedTick = _tick;
                }

                // todo improve this. Maybe have a max for tickTimer incase we get too far ahead of it.
                //      eg if we are slow for a few frames, and get 200 ticks behind, we could maybe drop 120 frames and continue from there?
                //      would we need to tell client about this

                // todo should we reset tickTimer if we stop the while loop? otherwise next frame might also be long
                if (GetCurrentTime() > max)
                {
                    if (logger.WarnEnabled()) logger.LogWarning($"Took longer than {MaxFrameTime}ms to process frame. Processed {_tick - startTick} ticks in {(GetCurrentTime() - now) * 1000f}ms");
                    break;
                }

                if (_tick > startTick + MaxTickPerFrame)
                {
                    if (logger.WarnEnabled()) logger.LogWarning($"Reached max ticks per frame ({MaxTickPerFrame}). Time taken {(GetCurrentTime() - now) * 1000f:0.0}ms. tickTimer:{tickTimer * 1000f:0.0}ms");

                    // todo check if resetting this is bad
                    // in single player mode it should be fine as we will just continue as normal from new time
                    // in multiplayer it might cause syncing problems 
                    tickTimer = 0;
                    break;
                }
            }

            if (logger.LogEnabled()) logger.Log($"TickRunner (tick={_tick}): {_tick - startTick} ticks in {(GetCurrentTime() - now) * 1000f:0.0}ms\n" +
                $"  unscaledTime:{now:0.000}s time:{_time:0.000}s, deltaTime:{_deltaTime * 1000f:0.0}ms, tickTimer:{tickTimer * 1000f:0.0}ms");

            if (AfterAllTicks != null)
            {
                afterAllTicksMarker.Begin();
                AfterAllTicks.Invoke();
                afterAllTicksMarker.End();
            }
        }

        // have this virtual methods here, just so we have use 1 field for TickRunner.
        // we will only call this method on client so it should be a ClientTickRunner
        public virtual void OnMessage(int serverTick, double clientTime) => throw new NotSupportedException("OnMessage is not supported for default tick runner. See ClientTickRunner");
    }

    public class ClientTickRunner : TickRunner
    {
        private static readonly ILogger logger = LogFactory.GetLogger<ClientTickRunner>();

        // this number neeeds to be less than buffer size in order for resimulation to work correctly
        // this number will clamp RTT average to a max value, so it should recover faster after RTT is back to normal
        private const float MAX_RTT = 1.0f;
        // ring buffers are 64, so set 60 as max to be safe
        // todo make this a field, not const
        private const int MAX_TICK_DELAY = 60;

        private readonly MovingAverage _RTTAverage = new MovingAverage(100);


        // we want to be more aggresive with scale when behind
        // because whne we are behind the server wont get our inputs in time
        private readonly float _farBehindScale = 2f;
        private readonly float _behindScale = 1.05f;
        private readonly float _normalScale = 1f;
        private readonly float _aheadScale = 0.98f;
        private readonly float _farAheadScale = 0.80f;

        // IMPORTANT: values below are in ticks
        private readonly float _skipAheadThreshold = 10f;
        private readonly float _farBehindThreshold = 4f;
        private readonly float _behindThreshold = 1f;
        private readonly float _aheadThreshold = -2f;
        private readonly float _farAheadThreshold = -20f;

        private bool _intialized;
        private int _latestServerTick;

        /// <summary>
        /// Most recent RTT before taking average
        /// </summary>
        public float CurrentRTT;

        /// <summary>
        /// Guess for what current server tick is. We then compare this to the tick received to see if we need to speed up or slow down
        /// </summary>
        public float ServerGuess;
        /// <summary>
        /// Most recent server tick received
        /// </summary>
        public int ServerTick;

        /// <summary>
        /// Client tick runner will only update after it has received a message from server
        /// <para>When manually updating Receive/send from tickrunner you also need to check Intialized otherwise no messages will be received</para>
        /// </summary>
        public bool Intialized => _intialized;

        public (float rtt, float jitter) GetRTTAndJitter()
        {
            return _RTTAverage.GetAverageAndStandardDeviation();
        }

        public float GetDelayInTicks()
        {
            return Tick - ServerGuess;
        }

#if CLIENT_TICK_RUNNER_VERBOSE
        private StreamWriter _writer;
#endif



        /// <summary>
        /// Invoked at start AND if client gets too get away from server
        /// </summary>
        public event Action OnTickSkip;

        public ClientTickRunner(float tickRate = 50) : base(tickRate)
        {
            // 2 seconds worth of average
            _RTTAverage = new MovingAverage(Mathf.RoundToInt(2 * tickRate));

#if CLIENT_TICK_RUNNER_VERBOSE
            try
            {
                _writer = new StreamWriter(Path.Combine(Application.persistentDataPath, "ClientTickRunner.csv")) { AutoFlush = true };
            }
            catch (IOException e)
            {
                if (logger.WarnEnabled()) logger.LogWarning($"Fail to create ClientTickRunner.csv because: {e}");
                // clear ref just incase. It will stop Debug() from trying to write 
                _writer = null;
            }
            Debug("serverTick,serverGuess,localTick,delayInTicks,delayInSeconds,delayFromLag,delayFromJitter,diff,newRTT,intialized");
#endif
        }

        public void ResetTime(TimeInfo timeInfo)
        {
            // reset first, then add new value
            ResetTime();

            ServerTick = timeInfo.Tick;
            AddTimeToAverage(timeInfo.ClientTime);

            InitNew(ServerTick);
        }

        public void ResetTime()
        {
            _RTTAverage.Reset();
            _intialized = false;
        }

        public override void OnUpdate()
        {
            // only update client tick if server has sent first state
            if (_intialized)
                base.OnUpdate();
        }

        private bool CheckOrder(int serverTick)
        {
            if (serverTick <= _latestServerTick)
            {
                logger.LogError($"Received message out of order server:{_latestServerTick}, new:{serverTick}");
                return false;
            }
            _latestServerTick = serverTick;
            return true;
        }

        /// <summary>
        /// Updates <see cref="clientScaleTime"/> to keep <see cref="ClientTime"/> in line with <see cref="LatestServerTime"/>
        /// </summary>
        /// <param name="serverTime"></param>
        public override void OnMessage(int serverTick, double clientSendTime)
        {
            if (!CheckOrder(serverTick))
                return;

            ServerTick = serverTick;
            AddTimeToAverage(clientSendTime);
#if CLIENT_TICK_RUNNER_VERBOSE
            VerboseLog(serverTick, clientSendTime);
#endif

            // if first message set client time to server-diff
            // reset stuff if too far behind
            // todo check this is correct
            if (!_intialized)
            {
                InitNew(serverTick);
                return;
            }

            // guess what tick we have to be to reach serever in time
            ServerGuess = _tick - DelayInTicks();
            // how far was out guess off?
            var diff = serverTick - ServerGuess;

            // if diff is bad enough, skip ahead
            // todo do we need abs, do also want to skip back if we are very ahead?
            // todo will skipping behind cause negative effects? we dont want Tick event to be invoked for a tick twice
            if (Math.Abs(diff) > _skipAheadThreshold)
            {
                (var lag, var jitter) = _RTTAverage.GetAverageAndStandardDeviation();
                var currentTick = _tick;
                InitNew(serverTick);
                var newTick = _tick;
                logger.LogWarning($"Client fell behind, skipping ahead. localTick:(current={currentTick},newTick={newTick}) server:{serverTick:0.00} serverGuess:{ServerGuess} diff:{diff:0.00}. RTT[lag={lag},jitter={jitter}]");
                return;
            }

            // apply timescale to try get closer to server
            TimeScaleMultiple = GetTimeScale(diff);

            //todo add trace level
            if (logger.LogEnabled()) logger.Log($"st {serverTick:0.00} sg {ServerGuess:0.00} ct {_tick:0.00} diff {diff * 1000:0.0}, wanted:{diff * 1000:0.0}, scale:{TimeScaleMultiple}");
        }

        private float DelayInTicks()
        {
            (var lag, var jitter) = _RTTAverage.GetAverageAndStandardDeviation();

            // *2 so we have 2 stdDev worth of range
            var delayFromJitter = jitter * 2;
            var delayFromLag = lag;
            var delayInSeconds = delayFromLag + delayFromJitter;
            // +2 tick to make sure we are always ahead
            // we set time to be delay, and tick to be floor of that, so need +2
            var delayInTicks = (delayInSeconds * TickRate) + 2;


            if (delayInTicks > MAX_TICK_DELAY)
            {
                if (logger.WarnEnabled())
                    logger.LogWarning($"delay in ticks over max of {MAX_TICK_DELAY}, value:{delayInTicks:0.0} ticks");
                delayInTicks = MAX_TICK_DELAY;
            }

            return delayInTicks;
        }

        private void AddTimeToAverage(double clientSendTime)
        {
            // only add if client time was returned from server
            // it will be zero before client sends first input
            if (clientSendTime != 0)
            {
                var newRTT = UnscaledTime - clientSendTime;
                if (newRTT > MAX_RTT)
                {
                    if (logger.WarnEnabled())
                        logger.LogWarning($"return trip time is over max of {MAX_RTT}s, value:{newRTT * 1000:0.0}ms");
                    newRTT = MAX_RTT;
                }
                Assert.IsTrue(newRTT > 0);
                CurrentRTT = (float)newRTT;
            }
            else
            {
                // just add 150 ms as tick RTT
                CurrentRTT = 0.150f;
            }

            _RTTAverage.Add(CurrentRTT);
        }

        private void InitNew(int serverTick)
        {
            var newTick = serverTick + DelayInTicks();
            _time = (double)newTick / TickRate;
            _tick = Mathf.FloorToInt(newTick);

            // todo do we need to also set _time here?
            TimeScaleMultiple = _normalScale;
            _intialized = true;
            // todo do we need to invoke this at start as well as skip?
            OnTickSkip?.Invoke();
        }

        private float GetTimeScale(float diff)
        {
            // diff is server-client,
            // if positive then server is ahead, => we can run client faster to catch up
            // if negative then server is behind, => we need to run client slow to not run out of spanshots

            // we want diffVsGoal to be as close to 0 as possible

            // server ahead, speed up client
            if (diff > _farBehindThreshold) // too far behind  
                return _farBehindScale;
            if (diff > _behindThreshold) // slightly behind
                return _behindScale;

            // server behind, slow down client
            if (diff < _farAheadThreshold) // too far ahead
                return _farAheadScale;
            if (diff < _aheadThreshold) // slighty ahead
                return _aheadScale;

            // close enough
            return _normalScale;
        }


#if CLIENT_TICK_RUNNER_VERBOSE
        private void VerboseLog(int serverTick, double clientSendTime)
        {
            (var lag, var jitter) = _RTTAverage.GetAverageAndStandardDeviation();
            var delayFromJitter = jitter * 2;
            var delayFromLag = lag;
            var delayInSeconds = delayFromLag + delayFromJitter;

            var serverGuess = _tick - DelayInTicks();
            var diff = serverTick - serverGuess;

            var newRTT = UnscaledTime - clientSendTime;
            Debug($"{serverTick},{serverGuess},{_tick},{(float)DelayInTicks()},{delayInSeconds},{delayFromLag},{delayFromJitter},{diff},{newRTT},{intialized}");
        }

        private void Debug(string line)
        {
            if (_writer == null)
                return;

            _writer.WriteLine(line);
        }
#endif
    }
}
