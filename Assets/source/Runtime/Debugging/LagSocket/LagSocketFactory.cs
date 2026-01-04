/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Mirage.Logging;
using Mirage.SocketLayer;
using UnityEngine;
using Random = System.Random;

namespace JamesFrowen.Mirage.DebugScripts
{
    public class LagSocketFactory : SocketFactory
    {
        public SocketFactory inner;
        public LagSettings settings;

        public override int MaxPacketSize => inner.MaxPacketSize;
        public override ISocket CreateClientSocket() => new LagSocket(inner.CreateClientSocket(), settings);
        public override ISocket CreateServerSocket() => new LagSocket(inner.CreateClientSocket(), settings);
        public override IBindEndPoint GetBindEndPoint() => inner.GetBindEndPoint();
        public override IConnectEndPoint GetConnectEndPoint(string address = null, ushort? port = null) => inner.GetConnectEndPoint(address, port);
        public override bool IsSupported => inner.IsSupported;
    }

    [System.Serializable]
    public class LagSettings
    {
        /// <summary>
        /// Drop chance is multiplied by sin wave, this will mean message are more likely to be dropped a the peaks
        /// </summary>
        public float DropChance = 0.01f;
        /// <summary>
        /// Sin frequency
        /// <para>Higher numbers = more peaks</para>
        /// <para>Lower numbers = peaks last longer</para>
        /// </summary>
        public float DropSinFrequency = 0.5f;

        /// <summary>
        /// base latency
        /// </summary>
        public float Latency = 100 / 1000f;
        /// <summary>
        /// Sin Amplitude added to latency
        /// </summary>
        public float LatencySinAmplitude = 30 / 1000f;
        /// <summary>
        /// Sin Frequency added to latency
        /// </summary>
        public float LatencySinFrequency = 2f;

        /// <summary>
        /// Smaller changes in latency
        /// </summary>
        public float JitterSinAmplitude = 5 / 1000f;

        /// <summary>
        /// Sin Frequency added to latency
        /// </summary>
        public float JitterSinFrequency = 40f;

        public float ZagAmplitude = 5 / 1000f;
        public float ZagFrequency = 40 / 1000f;
    }


    public class LagSocket : ISocket
    {
        private readonly ISocket inner;
        private readonly Pool<ByteBuffer> pool = new Pool<ByteBuffer>(ByteBuffer.CreateNew, 1300, 10, 1000, LogFactory.GetLogger<LagSocket>());
        private readonly byte[] receiveBuffer = new byte[1300];
        private readonly LagSettings settings;
        private readonly Random random = new Random();

        // these offsets done need to be settings because they just move the sin wave left/right from the origin
        // these will make sure that the sign waves dont overlap and have similar values to each other
        private readonly double dropSinOffset;
        private readonly double latencySinOffset;
        private readonly double jitterSinOffset;
        private readonly double zagOffset;

        // hard copies to endpoints because the one inner gives us may be changed and re-used
        private readonly Dictionary<IConnectionHandle, IConnectionHandle> endPoints = new Dictionary<IConnectionHandle, IConnectionHandle>();
        private readonly List<Message> messages = new List<Message>();
        private readonly object __locker = new object();
        private volatile bool closed = false;


        public LagSocket(ISocket inner, LagSettings settings)
        {
            this.inner = inner;
            this.settings = settings;
            // how much dropChance Sin way will be offset
            dropSinOffset = random.NextDouble() * 2 * Math.PI;
            latencySinOffset = random.NextDouble() * 2 * Math.PI;
            jitterSinOffset = random.NextDouble() * 2 * Math.PI;
            zagOffset = random.NextDouble();
        }

        public void Send(IConnectionHandle endPoint, ReadOnlySpan<byte> span) => inner.Send(endPoint, span);

        public void Bind(IBindEndPoint endPoint)
        {
            inner.Bind(endPoint);
            StartReceiveThread();
        }
        public IConnectionHandle Connect(IConnectEndPoint endPoint)
        {
            var c = inner.Connect(endPoint);
            StartReceiveThread();
            return c;
        }

        public void Close()
        {
            inner.Close();
            closed = true;
        }

        private void StartReceiveThread()
        {
            new Thread(ReceiveLoop).Start();
        }

        private void ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    inner.Tick();

                    while (!closed && inner.Poll())
                    {
                        var length = inner.Receive(receiveBuffer, out var endPoint);
                        ProcessInnerMessage(endPoint, receiveBuffer.AsSpan(0, length), false);
                    }

                    inner.Flush();

                    // stop thread is closed
                    if (closed) { return; }

                    Thread.Sleep(1);
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }


        public void Tick() { }
        public void Flush() { }
        public void SetTickEvents(int maxPacketSize, OnData _, OnDisconnect __)
        {
            inner.SetTickEvents(maxPacketSize, OnDataSideThread, OnDisconnectSideThread);
        }

        private void OnDataSideThread(IConnectionHandle handle, ReadOnlySpan<byte> data)
        {
            ProcessInnerMessage(handle, data, false);
        }

        private void OnDisconnectSideThread(IConnectionHandle handle, ReadOnlySpan<byte> data, string reason)
        {
            ProcessInnerMessage(handle, data, true);
        }

        private void ProcessInnerMessage(IConnectionHandle endPoint, ReadOnlySpan<byte> span, bool isDisconnect)
        {
            if (span.Length <= 0)
                return;
            if (!isDisconnect && Drop())
                return;

            if (!endPoints.TryGetValue(endPoint, out var endPointCopy))
            {
                endPointCopy = endPoint.CreateCopy();
                endPoints[endPoint] = endPointCopy;
            }

            // we have to lock for `pool.Take` and `messages.Add`
            lock (__locker)
            {
                var buffer = pool.Take();
                span.CopyTo(buffer.array);
                // make sure disconnect happens right away
                var receiveTime = isDisconnect ? 0 : Now() + Lag();
                var item = new Message(receiveTime, span.Length, buffer, endPointCopy);
                messages.Add(item);
            }
        }

        private double Now()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private bool Drop()
        {
            // note 0.1 is high, but good example numbers :)
            // chance = 0.1, sin(t) = 0.8 => 8% chance to be dropped this time
            // chance = 0.1, sin(t) = 0.1 => 1% chance to be dropped this time
            // chance = 0.1, sin(t) = -1 => 0% chance to be dropped this time

            var rand = random.NextDouble();

            var angle = (Now() * settings.DropSinFrequency) + dropSinOffset;
            var chance = settings.DropChance * Math.Sin(angle);
            return rand < chance;
        }

        private double Lag()
        {
            // add 2 sin wave together so there will be chances between ticks and between seconds

            var now = Now();
            var angle1 = (now * settings.LatencySinFrequency) + latencySinOffset;
            var angle2 = (now * settings.JitterSinFrequency) + jitterSinOffset;
            var sin1 = Math.Sin(angle1);
            var sin2 = Math.Sin(angle2);

            var sin1A = sin1 * settings.LatencySinAmplitude;
            var sin2A = sin2 * settings.JitterSinAmplitude;

            var zagT = (now * settings.ZagFrequency) + zagOffset;
            var zag = zagT % 1;
            var zagA = zag * settings.ZagAmplitude;

            return settings.Latency + sin1A + sin2A + zagA;
        }

        public bool Poll()
        {
            return AnyMessages();
        }

        private bool AnyMessages()
        {
            var count = messages.Count;
            var now = Now();
            for (var i = 0; i < count; i++)
            {
                if (messages[i].Time < now)
                {
                    return true;
                }
            }

            return false;
        }

        public int Receive(Span<byte> outSpan, out IConnectionHandle endPoint)
        {
            var earliest = double.MaxValue;
            var index = 0;
            var count = messages.Count;
            for (var i = 0; i < count; i++)
            {
                var time = messages[i].Time;
                if (time < earliest)
                {
                    earliest = time;
                    index = i;
                }
            }

            // copy message values
            var message = messages[index];
            var length = message.Length;
            message.Buffer.array.AsSpan(0, length).CopyTo(outSpan);
            endPoint = message.EndPoint;

            // remove message
            // need lock for Release and RemoveAt
            lock (__locker)
            {
                message.Buffer.Release();
                messages.RemoveAt(index);
            }

            return length;
        }

        private struct Message
        {
            public readonly double Time;
            public readonly int Length;
            public readonly ByteBuffer Buffer;
            public readonly IConnectionHandle EndPoint;

            public Message(double time, int length, ByteBuffer buffer, IConnectionHandle endPoint)
            {
                Time = time;
                Length = length;
                Buffer = buffer;
                EndPoint = endPoint;
            }
        }
    }

    internal class LagSocketGUI
    {
        private readonly GUIStyle style;
        private readonly Texture2D tex;
        private readonly GUIStyle boldText;

        public LagSocketGUI()
        {
            style = new GUIStyle();
            tex = new Texture2D(1, 1);
            style.normal.background = tex;

            boldText = new GUIStyle(GUI.skin.label);
            boldText.fontStyle = FontStyle.Bold;
        }
        ~LagSocketGUI()
        {
            UnityEngine.Object.DestroyImmediate(tex);
        }

        public void OnGUI(Rect rect, Color background, LagSettings settings)
        {
            tex.SetPixel(0, 0, background);
            tex.Apply();

            using (new GUILayout.AreaScope(rect, GUIContent.none, style))
            {
                GUILayout.Label("Drop", boldText);
                sliderWithTextBox("chance", ref settings.DropChance, 0, 0.1f);
                sliderWithTextBox("frequency", ref settings.DropSinFrequency, 0, 10, true);
                GUILayout.Space(10);

                GUILayout.Label("Lag", boldText);
                sliderWithTextBox("base", ref settings.Latency, 0, 0.5f, true);
                sliderWithTextBox("amplitude 1", ref settings.LatencySinAmplitude, 0, 0.4f, true);
                sliderWithTextBox("frequency 1", ref settings.LatencySinFrequency, 0.01f, 20f, true);

                sliderWithTextBox("amplitude 2", ref settings.JitterSinAmplitude, 0, 0.1f, true);
                sliderWithTextBox("frequency 2", ref settings.JitterSinFrequency, 10f, 200f, true);
            }
        }

        private readonly Dictionary<string, float> delayText = new Dictionary<string, float>();

        private void sliderWithTextBox(string label, ref float value, float min, float max, bool logSlider = false)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(label);
                slider(ref value, min, max, logSlider);
                delayTextField(label, ref value);
            }

            value = Mathf.Clamp(value, min, max);
        }

        private void delayTextField(string label, ref float value)
        {
            if (!delayText.ContainsKey(label)) delayText[label] = 0;

            var inText = value.ToString();
            var outText = GUILayout.TextField(inText);
            // if changed, save new time
            if (inText != outText)
            {
                // changed time
                delayText[label] = Time.time;
            }


            // if been 2 second since edit then apply value
            if (Time.time < delayText[label] + 2)
            {
                // only set value if parse was successful
                if (float.TryParse(outText, out var fValue))
                {
                    value = fValue;
                }
            }
        }

        private static void slider(ref float value, float min, float max, bool logSlider)
        {
            if (logSlider)
            {
                var inValue = (float)Math.Log10(value);
                var inMin = (float)Math.Log10(min != 0 ? min : 1 / 10_000f);
                var inMax = (float)Math.Log10(max);

                var outValue = GUILayout.HorizontalSlider(inValue, inMin, inMax);
                value = (float)Math.Pow(10, outValue);
            }
            else
            {
                value = GUILayout.HorizontalSlider(value, min, max);
            }
        }
    }
}
