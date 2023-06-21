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
using System.Runtime.InteropServices;
using Mirage.Logging;
using UnityEngine;

namespace JamesFrowen.CSP.Alloc
{
    public sealed unsafe class SimpleAlloc : IAllocator
    {
        private static readonly ILogger logger = LogFactory.GetLogger<SimpleAlloc>();

        ~SimpleAlloc() => ReleaseAll();

        private readonly Dictionary<IHasAllocatedPointer, Allocation> _allocations = new Dictionary<IHasAllocatedPointer, Allocation>();

        public void Allocate(IHasAllocatedPointer owner, int byteCount)
        {
            if (byteCount % 4 != 0)
                if (logger.WarnEnabled()) logger.LogWarning($"Alloc size was not a mutliple of 4");

            var intPtr = Marshal.AllocHGlobal(byteCount);
            AllocHelper.ZeroMemory(intPtr, byteCount);
            var ptr = intPtr.ToPointer();

            var allocation = new Allocation(ptr, byteCount);

            _allocations.Add(owner, allocation);
            if (logger.LogEnabled()) logger.Log($"Alloc ptr:{(ulong)ptr:X}, size={byteCount} owner:{owner.name}");
            owner.Ptr = allocation.ptr;

#if DEBUG
            ValidateZero(owner, byteCount);
#endif
        }
        public void* Allocate(int byteCount)
        {
            var noOwner = new NoOwner();
            Allocate(noOwner, byteCount);
            return noOwner.Ptr;
        }

        private static void ValidateZero(IHasAllocatedPointer owner, int byteCount)
        {
#if DEBUG
            var intSize = byteCount / 4;
            var intPtr = (int*)owner.Ptr;
            for (var i = 0; i < intSize; i++)
            {
                if (intPtr[i] != 0)
                    throw new InvalidOperationException("Failed to fully zero pointer");
            }
#endif
        }

        public void Release(IHasAllocatedPointer owner)
        {
            var ptr = owner.Ptr;
            ReleasePtr(ptr);

            var removed = _allocations.Remove(owner);
            if (!removed)
            {
                if (logger.WarnEnabled()) logger.LogWarning($"Failed to remove from allocations {(ulong)ptr:X} owner:{owner.name}");
            }

            owner.Ptr = null;
        }

        private static void ReleasePtr(void* ptr)
        {
            if (logger.LogEnabled()) logger.Log($"Release ptr:{(ulong)ptr:X}");

            Marshal.FreeHGlobal(new IntPtr(ptr));
        }

        public void ReleaseAll()
        {
            foreach (var kvp in _allocations)
            {
                var owner = kvp.Key;
                var alloc = kvp.Value;

                ReleasePtr(alloc.ptr);
                owner.Ptr = null;
            }
            _allocations.Clear();
        }

        public void Dispose()
        {
            ReleaseAll();
        }

        private sealed class NoOwner : IHasAllocatedPointer
        {
            public string name => "NoOwner";
            public void* Ptr { get; set; }
        }
    }
    public unsafe struct Allocation
    {
        public readonly void* ptr;
        public readonly int size;

        public Allocation(void* ptr, int size)
        {
            this.ptr = ptr;
            this.size = size;
        }
    }
}
