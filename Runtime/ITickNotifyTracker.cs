/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

using System;
using Mirage.Logging;
using Mirage.SocketLayer;

namespace JamesFrowen.CSP
{
    public interface ITickNotifyTracker
    {
        int LastAckedTick { get; set; }
    }

    public class TickNotifyToken : INotifyCallBack
    {
        static Pool<TickNotifyToken> pool = new Pool<TickNotifyToken>((_, __) => new TickNotifyToken(), 0, 10, Helper.BufferSize, LogFactory.GetLogger<TickNotifyToken>());
        public static INotifyCallBack GetToken(ITickNotifyTracker tracker, int tick)
        {
            TickNotifyToken token = pool.Take();
            token.tracker = tracker;
            token.tick = tick;
            return token;
        }

        ITickNotifyTracker tracker;
        int tick;

        public void OnDelivered()
        {
            // take highest value of current ack and new ack
            tracker.LastAckedTick = Math.Max(tracker.LastAckedTick, tick);
            pool.Put(this);
        }

        public void OnLost()
        {
            pool.Put(this);
        }
    }
}
