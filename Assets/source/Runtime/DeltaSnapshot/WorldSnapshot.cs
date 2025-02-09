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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JamesFrowen.CSP.Alloc;
using Mirage;
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.DeltaSnapshot
{
    public class WorldSnapshot
    {
        private static readonly ILogger logger = LogFactory.GetLogger<WorldSnapshot>();

        private readonly IAllocator _allocator;

        private readonly List<IdentitySnapshot> _snapshots = new List<IdentitySnapshot>();
        private readonly Dictionary<uint, IdentitySnapshot> _lookup = new Dictionary<uint, IdentitySnapshot>();
        private readonly int _tickBufferSize;

        public IReadOnlyDictionary<uint, IdentitySnapshot> LookUp => _lookup;
        public IReadOnlyList<IdentitySnapshot> Snapshots => _snapshots;

        public WorldSnapshot(IAllocator allocator, int tickBufferSize)
        {
            _allocator = allocator;
            _tickBufferSize = tickBufferSize;
        }

        public IdentitySnapshot CreateAndAdd(NetworkIdentity identity, ISnapshotBehaviour[] behaviours, IAllocator allocator = null)
        {
            var snap = new IdentitySnapshot(identity, behaviours, _tickBufferSize, allocator);
            Debug.Assert(snap.Identity.NetId != 0);

            _snapshots.Add(snap);
            _lookup.Add(snap.Identity.NetId, snap);

            return snap;
        }

        public void Remove(NetworkIdentity identity, bool release)
        {
            Debug.Assert(identity.NetId != 0);

            if (!_lookup.TryGetValue(identity.NetId, out var snap))
            {
                // todo remove warning, CreateAndAdd is not called when GO has no snapshotbehaviours, but remove still is 
                if (logger.WarnEnabled()) logger.LogWarning($"trying to remove {identity.NetId} but it was missing in Lookup");
                return;
            }

            if (release)
            {
                snap.Release(_allocator);
            }

            _lookup.Remove(identity.NetId);
            _snapshots.Remove(snap);
        }

        /// <summary>
        /// Copies state from previous tick to 
        /// </summary>
        /// <param name="previousTick"></param>
        /// <param name="nextTick"></param>
        public unsafe void CopyFromPreviousTick(int tick)
        {
            // nextTick is the snapshot we are preparing to use,
            // it is "buffer size" ticks old so needs to be synced with previous one first

            var count = _snapshots.Count;
            for (var i = 0; i < count; i++)
            {
                var snap = _snapshots[i];
                snap.CopySnapshot(tick - 1, tick);
                snap.SetActivePtr(tick);
            }
        }

        public void SetActivePtr(int tick)
        {
            var count = _snapshots.Count;
            for (var i = 0; i < count; i++)
            {
                var snap = _snapshots[i];
                snap.SetActivePtr(tick);
            }
        }
    }

    /// <summary>
    /// An allocation for a single NetworkIdentity for a single tick
    /// </summary>
    public unsafe class IdentitySnapshot : IHasAllocatedPointer, ISnapshotManager
    {
        public readonly NetworkIdentity Identity;
        public readonly ISnapshotBehaviour[] Snapshots;
        public readonly int TickBufferSize;
        public readonly int IntSizePerTick;
        private void* _ptr;

        void* IHasAllocatedPointer.Ptr
        {
            get => _ptr;
            set => _ptr = value;
        }
        public int* IntPtr => (int*)_ptr;
        public string name => $"Group for {Identity.name} ({Identity.NetId})";

        /// <summary>
        /// Creates a group and sets up <paramref name="snapshots"/> with pointer offset and manager reference
        /// </summary>
        /// <param name="manager"></param>
        /// <param name="identity"></param>
        /// <param name="snapshots"></param>
        /// <param name="allocator">Leaeve as null to now allocate right away. Call <see cref="Allocate(IAllocator)"/> to allocate later</param>
        /// <returns></returns>
        public IdentitySnapshot(NetworkIdentity identity, ISnapshotBehaviour[] snapshots, int tickBufferSize, IAllocator allocator = null)
        {
            Identity = identity;
            Snapshots = snapshots;
            TickBufferSize = tickBufferSize;

            var intSize = calculateIntSize(snapshots);

            IntSizePerTick = intSize;

            if (allocator != null)
                Allocate(allocator);
        }

        private int calculateIntSize(ISnapshotBehaviour[] snapshots)
        {
            // +1 for netid
            var intSize = IdentitySnapshot.Header.INT_SIZE;
            foreach (var behaviour in snapshots)
            {
                // set offset of this behaviour to be the size of all previous
                behaviour.PtrIntOffset = intSize;
                behaviour.SnapshotManager = this;

                intSize += behaviour.AllocationSizeInts;
            }

            return intSize;
        }

        public unsafe void Allocate(IAllocator allocator)
        {
            if (Identity.NetId == 0)
                throw new ArgumentException($"Identity must be spawned before Allocating for group");
            if (_ptr != null)
                throw new InvalidOperationException($"Group already been allocated");

            allocator.Allocate(this, IntSizePerTick * TickBufferSize * 4);
            SetHeader();
        }

        /// <summary>
        /// Call this after allocation PTR to set up the header
        /// </summary>
        private void SetHeader()
        {
            if (Identity.NetId == 0)
                throw new InvalidOperationException($"NetId zero for {Identity.name}");

            // we have to set this for all ticks, because we dont know what tick we start on
            for (var tick = 0; tick < TickBufferSize; tick++)
            {
                var offset = tick * IntSizePerTick;
                var hPtr = (Header*)(IntPtr + offset);
                hPtr->NetId = Identity.NetId;

                // todo does header need prefab hash so client can spawn it if missing?
                //Debug.Assert(Identity.IsPrefab);
                //hPtr->PrefabHash = Identity.PrefabHash;
            }
        }

        public unsafe void Release(IAllocator allocator)
        {
            allocator.Release(this);
        }

        /// <summary>
        /// Copies snapshot from 1 tick to another
        /// </summary>
        public unsafe void CopySnapshot(int from, int to)
        {
            var fromPtr = GetStateAtTick(from);
            var toPtr = GetStateAtTick(to);
            UnsafeHelper.Copy(fromPtr, toPtr, IntSizePerTick);
        }

        public void SetActivePtr(int tick)
        {
            var offset = OffsetForTick(tick);
            for (var i = 0; i < Snapshots.Length; i++)
            {
                Snapshots[i].Ptr = IntPtr + offset + Snapshots[i].PtrIntOffset;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int OffsetForTick(int tick)
        {
            var index = tick % TickBufferSize;
            return index * IntSizePerTick;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe int* GetStateAtTick(int tick)
        {
            var ptr = IntPtr + OffsetForTick(tick);
            return ptr;
        }

        public unsafe void* GetStateAtTick(ISnapshotBehaviour behaviour, int tick)
        {
            Debug.Assert(Array.IndexOf(Snapshots, behaviour) != -1);

            var ptr = GetStateAtTick(tick) + behaviour.PtrIntOffset;
            return ptr;
        }


        [StructLayout(LayoutKind.Explicit, Size = INT_SIZE)]
        public struct Header
        {
            public const int INT_SIZE = 1;

            [FieldOffset(0)] public uint NetId;
        }
    }

    public static unsafe class UnsafeHelper
    {
        public static void Copy(void* from, void* to, int intCount)
        {
            Copy((int*)from, (int*)to, intCount);
        }
        public static void Copy(int* from, int* to, int intCount)
        {
            for (var j = 0; j < intCount; j++)
            {
                to[j] = from[j];
            }
        }
        public static bool CopyAndCheckChanged(int* from, int* to, int intCount)
        {
            var anyChanged = false;
            for (var j = 0; j < intCount; j++)
            {
                if (to[j] != from[j])
                    anyChanged = true;

                to[j] = from[j];
            }
            return anyChanged;
        }
    }
}
