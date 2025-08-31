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
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using UnityEngine;

namespace JamesFrowen.CSP.VisualEvents
{
    public class MissingNetworkMessageException : System.Exception
    {
        public MissingNetworkMessageException(Type type) : base($"No [NetworkMessage] found on type:{type}") { }
    }
    public static class VisualEventTypeChecker<TData>
    {
        private static bool HasChecked;
        public static void Validate()
        {
            if (!HasChecked)
            {
                CheckAndThrow();
                HasChecked = true;
            }
        }
        public static void CheckAndThrow()
        {
            var id = MessagePacker.GetId<TData>();
            if (!MessagePacker.MessageTypes.ContainsKey(id))
                throw new MissingNetworkMessageException(typeof(TData));
        }
    }
    public static class VisualEventTypeChecker<TBehaviour, TData>
            where TBehaviour : NetworkBehaviour
    {
        private static bool HasRegisteredBehaviour;

        public static void Validate()
        {
            VisualEventTypeChecker<TData>.Validate();

            if (!HasRegisteredBehaviour)
            {
                MessagePacker.RegisterMessage<BehaviourAndData<TBehaviour, TData>>();
                HasRegisteredBehaviour = true;
            }
        }
    }
    public static class VisualEventExtensions
    {
        [WeaverSerializeCollection]
        public static void WriteBehaviourVisualEvent<TBehaviour, TData>(this NetworkWriter writer, BehaviourAndData<TBehaviour, TData> msg)
            where TBehaviour : NetworkBehaviour
        {
            writer.WriteNetworkBehaviour(msg.Behaviour);
            writer.Write(msg.Data);

        }

        [WeaverSerializeCollection]
        public static BehaviourAndData<TBehaviour, TData> ReadBehaviourVisualEvent<TBehaviour, TData>(this NetworkReader reader)
            where TBehaviour : NetworkBehaviour
        {
            var behaviour = reader.ReadNetworkBehaviour<TBehaviour>();
            var data = reader.Read<TData>();
            return new BehaviourAndData<TBehaviour, TData>
            {
                Behaviour = behaviour,
                Data = data,
            };
        }


        /// <summary>Queue an event that has a single handler</summary>
        public static void QueueEventStatic<TData>(this IVisualEventBehaviour behaviour, TData data)
        {
            behaviour.VisualEventManager.QueueEvent(data);
        }

        /// <summary>Queue an event that has handlers per behaviour</summary>
        public static void QueueEventBehaviour<TBehaviour, TData>(this TBehaviour behaviour, TData data)
            where TBehaviour : NetworkBehaviour, IVisualEventBehaviour
        {
            VisualEventTypeChecker<TBehaviour, TData>.Validate();

            var wrapper = new BehaviourAndData<TBehaviour, TData>
            {
                Behaviour = behaviour,
                Data = data,
            };
            behaviour.VisualEventManager.QueueEvent(wrapper);
        }

        public static void RegisterEventStatic<TData>(this IVisualEventBehaviour behaviour, Action<TData> callback)
        {
            behaviour.VisualEventManager.Register(callback, errorIfExists: true);
        }

        public static void RegisterEventBehaviour<TBehaviour, TData>(this IVisualEventBehaviour behaviour, Action<TBehaviour, TData> callback) where TBehaviour : NetworkBehaviour
        {
            VisualEventTypeChecker<TBehaviour, TData>.Validate();
            var wrapperCallback = new Action<BehaviourAndData<TBehaviour, TData>>(behaviourAndData => callback.Invoke(behaviourAndData.Behaviour, behaviourAndData.Data));
            behaviour.VisualEventManager.Register(wrapperCallback, errorIfExists: false);
        }
    }

    /// <summary>Behaviour that can receive VisualEventManager</summary>
    public interface IVisualEventBehaviour // TODO assign VisualEventManager when Behaviour is spawned
    {
        VisualEventManager VisualEventManager { get; protected internal set; }
    }

    public class VisualEventManager
    {
        public static readonly ILogger logger = LogFactory.GetLogger<VisualEventManager>();

        private readonly NetworkWorld _world;
        private readonly Dictionary<int, IVisualEventHandler> _handlers = new Dictionary<int, IVisualEventHandler>();
        private readonly List<Action<NetworkWriter>> _queuedWrite = new List<Action<NetworkWriter>>();

        internal void SendMessages(List<INetworkPlayer> players, uint tick) // TODO call this in server loop
        {
            if (_queuedWrite.Count == 0)
                return;

            using var writer = NetworkWriterPool.GetWriter();
            var wrapper = new VisualEventMessage { tick = tick };
            foreach (var action in _queuedWrite)
                action.Invoke(writer);
            _queuedWrite.Clear();

            wrapper.payload = writer.ToArraySegment();

            // TODO should this be unreliable?
            NetworkServer.SendToMany(players, wrapper);
        }
        internal void HandleMessage(VisualEventMessage wrapper) // TODO register this handler on client
        {
            using var reader = NetworkReaderPool.GetReader(wrapper.payload, _world);

            while (reader.CanReadBytes(4)) // we have 4 bytes for size and id
            {
                var startPosition = reader.BitPosition;
                var payloadSize = reader.ReadUInt16(); // safety check so we can skip if we get error
                var id = MessagePacker.UnpackId(reader);
                try
                {
                    var handler = GetHandler(id);
                    handler.Handle(wrapper.tick, reader);
                }
                catch (Exception ex)
                {
                    if (logger.ErrorEnabled()) logger.LogError($"Error handling VisualEvent message: {ex}");
                }

                var expectedEndPos = startPosition + payloadSize;
                if (reader.BitPosition != expectedEndPos)
                {
                    var read = reader.BitPosition - startPosition;
                    if (logger.WarnEnabled()) logger.LogWarning($"Reader did not read the expected number of bits. read:{read}bits expected:{payloadSize}bits");
                    reader.MoveBitPosition(expectedEndPos);
                }
            }
        }
        internal void VisualUpdate(int latestTick) // TODO call on client once a tick
        {
            foreach (var handler in _handlers.Values)
                handler.VisualUpdate(latestTick);
        }

        private VisualEventHandler<T> GetHandler<T>()
        {
            var id = MessagePacker.GetId<T>();
            return (VisualEventHandler<T>)GetHandler(id);
        }
        private IVisualEventHandler GetHandler(int id)
        {
            if (!_handlers.TryGetValue(id, out var handler))
            {
                CreateHandler(id);
                _handlers.Add(id, handler);
            }
            return handler;
        }
        private static IVisualEventHandler CreateHandler(int id)
        {
            var type = MessagePacker.MessageTypes[id];
            var handlerType = typeof(VisualEventHandler<>);
            var generic = handlerType.MakeGenericType(type);
            var instance = Activator.CreateInstance(generic);
            return (IVisualEventHandler)instance;
        }

        public void QueueEvent<T>(T e)
        {
            VisualEventTypeChecker<T>.Validate();
            _queuedWrite.Add(writer => WriteEvent(writer, e));

            static void WriteEvent(NetworkWriter writer, T e)
            {
                var startPosition = writer.BitPosition;
                writer.WriteUInt16(0); // placeholder
                MessagePacker.Pack(e, writer);
                var endPosition = writer.BitPosition;
                var length = checked((uint)(endPosition - startPosition));
                writer.WriteAtPosition(length, 16, startPosition);
            }
        }

        public void Register<T>(Action<T> callback, bool errorIfExists)
        {
            VisualEventTypeChecker<T>.Validate();

            var handler = GetHandler<T>();
            if (handler.Callback != null)
            {
                handler.Callback = callback;
                if (logger.LogEnabled()) logger.Log($"Registering event for {typeof(T)}");
            }
            else
            {
                if (errorIfExists)
                {
                    if (logger.ErrorEnabled()) logger.LogError($"Callback already registered for {typeof(T)}");
                }
                else
                {
                    // this is existed behaviour events, each should register in awake, meaning it could be called multiple times
                    if (logger.LogEnabled()) logger.Log($"Callback already registered for {typeof(T)}");
                }
            }
        }

        public interface IVisualEventHandler
        {
            void Handle(uint tick, PooledNetworkReader reader);
            void VisualUpdate(int latestTick);
        }

        public class VisualEventHandler<T> : IVisualEventHandler
        {
            private readonly Queue<VisualEvent> _queue = new();
            public Action<T> Callback;

            public void Handle(uint tick, PooledNetworkReader reader)
            {
                var msg = reader.Read<T>();
                _queue.Enqueue(new VisualEvent(tick, msg));
            }

            public void VisualUpdate(int latestTick) // TODO call on client once a tick
            {
                while (_queue.TryPeek(out var item) && item.Tick <= latestTick)
                {
                    InvokeEvent(item.EventData);
                    _queue.Dequeue();
                }
            }

            private void InvokeEvent(T eventData)
            {
                if (Callback != null)
                    Callback.Invoke(eventData);
                else
                    Debug.LogError($"Received {typeof(T)} but callback was not registered");
            }

            /// <summary>
            /// Used for one off events, should be shown on or after <see cref="Tick"/>
            /// </summary>
            private struct VisualEvent
            {
                public uint Tick;
                public T EventData;

                public VisualEvent(uint tick, T eventData)
                {
                    Tick = tick;
                    EventData = eventData;
                }
            }
        }
    }

    [NetworkMessage]
    [WeaverWriteAsGeneric]
    public struct BehaviourAndData<TBehaviour, TData>
    where TBehaviour : NetworkBehaviour
    {
        public TBehaviour Behaviour;
        public TData Data;
    }

    [NetworkMessage]
    public struct VisualEventMessage
    {
        public uint tick;
        public ArraySegment<byte> payload;
    }
}
