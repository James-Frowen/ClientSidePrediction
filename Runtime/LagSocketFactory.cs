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
        public override IEndPoint GetBindEndPoint() => inner.GetBindEndPoint();
        public override IEndPoint GetConnectEndPoint(string address = null, ushort? port = null) => inner.GetConnectEndPoint(address, port);
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
    }


    public class LagSocket : ISocket
    {
        readonly ISocket inner;
        readonly Pool<ByteBuffer> pool = new Pool<ByteBuffer>(ByteBuffer.CreateNew, 1300, 10, 1000, LogFactory.GetLogger<LagSocket>());
        readonly byte[] receiveBuffer = new byte[1300];
        readonly LagSettings settings;
        readonly Random random = new Random();

        // these offsets done need to be settings because they just move the sin wave left/right from the origin
        // these will make sure that the sign waves dont overlap and have similar values to each other
        readonly double dropSinOffset;
        readonly double latencySinOffset;
        readonly double jitterSinOffset;

        // hard copies to endpoints because the one inner gives us may be changed and re-used
        readonly Dictionary<IEndPoint, IEndPoint> endPoints = new Dictionary<IEndPoint, IEndPoint>();
        readonly List<Message> messages = new List<Message>();

        readonly object __locker = new object();
        volatile bool closed = false;


        public LagSocket(ISocket inner, LagSettings settings)
        {
            this.inner = inner;
            this.settings = settings;
            // how much dropChance Sin way will be offset
            dropSinOffset = random.NextDouble() * 2 * Math.PI;
            latencySinOffset = random.NextDouble() * 2 * Math.PI;
            jitterSinOffset = random.NextDouble() * 2 * Math.PI;
        }

        public void Send(IEndPoint endPoint, byte[] packet, int length) => inner.Send(endPoint, packet, length);

        public void Bind(IEndPoint endPoint)
        {
            inner.Bind(endPoint);
            StartReceiveThread();
        }
        public void Connect(IEndPoint endPoint)
        {
            inner.Connect(endPoint);
            StartReceiveThread();
        }

        public void Close()
        {
            inner.Close();
            closed = true;
        }


        void StartReceiveThread()
        {
            new Thread(ReceiveLoop).Start();
        }

        void ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    while (!closed && inner.Poll())
                    {
                        ProcessInnerMessage();
                    }

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
        void ProcessInnerMessage()
        {
            int length = inner.Receive(receiveBuffer, out IEndPoint endPoint);
            if (Drop() || length <= 0)
            {
                return;
            }

            if (!endPoints.TryGetValue(endPoint, out IEndPoint endPointCopy))
            {
                endPointCopy = endPoint.CreateCopy();
                endPoints[endPoint] = endPointCopy;
            }

            // we have to lock for `pool.Take` and `messages.Add`
            lock (__locker)
            {
                ByteBuffer buffer = pool.Take();
                Buffer.BlockCopy(receiveBuffer, 0, buffer.array, 0, length);
                double receiveTime = Now() + Lag();
                var item = new Message(receiveTime, length, buffer, endPointCopy);
                messages.Add(item);
            }
        }


        double Now()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        bool Drop()
        {
            // note 0.1 is high, but good example numbers :)
            // chance = 0.1, sin(t) = 0.8 => 8% chance to be dropped this time
            // chance = 0.1, sin(t) = 0.1 => 1% chance to be dropped this time
            // chance = 0.1, sin(t) = -1 => 0% chance to be dropped this time

            double rand = random.NextDouble();

            double angle = Now() * settings.DropSinFrequency + dropSinOffset;
            double chance = settings.DropChance * Math.Sin(angle);
            return rand < chance;
        }
        double Lag()
        {
            // add 2 sin wave together so there will be chances between ticks and between seconds

            double angle1 = Now() * settings.LatencySinFrequency + latencySinOffset;
            double angle2 = Now() * settings.JitterSinFrequency + jitterSinOffset;
            float sin1 = Math.Sign(angle1) * settings.LatencySinAmplitude;
            float sin2 = Math.Sign(angle2) * settings.JitterSinAmplitude;

            return settings.Latency + sin1 + sin2;
        }

        public bool Poll()
        {
            return AnyMessages();
        }
        bool AnyMessages()
        {
            int count = messages.Count;
            double now = Now();
            for (int i = 0; i < count; i++)
            {
                if (messages[i].Time < now)
                {
                    return true;
                }
            }

            return false;
        }

        public int Receive(byte[] buffer, out IEndPoint endPoint)
        {
            double earliest = double.MaxValue;
            int index = 0;
            int count = messages.Count;
            for (int i = 0; i < count; i++)
            {
                double time = messages[i].Time;
                if (time < earliest)
                {
                    earliest = time;
                    index = i;
                }
            }

            // copy message values
            Message message = messages[index];
            int length = message.Length;
            Buffer.BlockCopy(message.Buffer.array, 0, buffer, 0, length);
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

        struct Message
        {
            public readonly double Time;
            public readonly int Length;
            public readonly ByteBuffer Buffer;
            public readonly IEndPoint EndPoint;

            public Message(double time, int length, ByteBuffer buffer, IEndPoint endPoint)
            {
                Time = time;
                Length = length;
                Buffer = buffer;
                EndPoint = endPoint;
            }
        }
    }

    class LagSocketGUI
    {
        //public Rect offset = new Rect(10, 10, 400, 800);
        //public Color background;
        GUIStyle style;
        Texture2D tex;
        GUIStyle boldText;

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

        readonly Dictionary<string, float> delayText = new Dictionary<string, float>();
        void sliderWithTextBox(string label, ref float value, float min, float max, bool logSlider = false)
        {
            using (new GUILayout.HorizontalScope())
            {
                GUILayout.Label(label);
                slider(ref value, min, max, logSlider);
                delayTextField(label, ref value);
            }

            value = Mathf.Clamp(value, min, max);
        }

        void delayTextField(string label, ref float value)
        {
            if (!delayText.ContainsKey(label)) delayText[label] = 0;

            string inText = value.ToString();
            string outText = GUILayout.TextField(inText);
            // if changed, save new time
            if (inText != outText)
            {
                // changed time
                delayText[label] = Time.time;
            }


            // if been 2 second since edit then apply value
            if (Time.time < delayText[label] + 2)
            {
                if (float.TryParse(outText, out float fValue))
                {

                    value = fValue;
                }
            }
        }

        static void slider(ref float value, float min, float max, bool logSlider)
        {
            if (logSlider)
            {
                float inValue = (float)Math.Log10(value);
                float inMin = (float)Math.Log10(min != 0 ? min : 1 / 10_000f);
                float inMax = (float)Math.Log10(max);

                float outValue = GUILayout.HorizontalSlider(inValue, inMin, inMax);
                value = (float)Math.Pow(10, outValue);
            }
            else
            {
                value = GUILayout.HorizontalSlider(value, min, max);
            }
        }
    }
}
