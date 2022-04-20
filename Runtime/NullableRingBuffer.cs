/*******************************************************
 * Copyright (C) 2021 James Frowen <JamesFrowenDev@gmail.com>
 * 
 * This file is part of JamesFrowen ClientSidePrediction
 * 
 * The code below can not be copied and/or distributed without the express
 * permission of James Frowen
 *******************************************************/

namespace JamesFrowen.CSP
{
    /// <summary>
    /// A Ring buffer where each element can be set be set or not
    /// </summary>
    /// <remarks>
    /// This is useful when you need nullable struct types but dont want to require `where T is struct` on all types using this
    /// </remarks>
    public struct NullableRingBuffer<T>
    {
        readonly ISnapshotDisposer<T> _disposer;
        readonly int _size;
        readonly Valid[] _buffer;

        public NullableRingBuffer(int size) : this(size, null) { }
        public NullableRingBuffer(int size, ISnapshotDisposer<T> disposer)
        {
            _size = size;
            _buffer = new Valid[size];
            _disposer = disposer;
        }

        int IndexToBuffer(int index)
        {
            //negative
            if (index < 0)
                index += _size;
            return index % _size;
        }


        public T Get(int index)
        {
            Valid item = _buffer[IndexToBuffer(index)];
            if (item.HasValue)
                return item.Value;
            else
                return default;
        }

        /// <summary>
        /// Does the element at the index have a value?
        /// </summary>
        public bool IsValid(int index)
        {
            Valid item = _buffer[IndexToBuffer(index)];
            return item.HasValue;
        }

        public bool TryGet(int index, out T value)
        {
            Valid item = _buffer[IndexToBuffer(index)];
            if (item.HasValue)
            {
                value = item.Value;
                return true;
            }
            else
            {
                value = default;
                return false;
            }
        }

        public void Set(int index, T value)
        {
            if (_disposer != null)
            {
                // when we set a new value, we want to make sure old value is disposed correctly. 
                // this allows for pooled objects to be used in the state
                Valid oldItem = _buffer[IndexToBuffer(index)];
                if (oldItem.HasValue)
                {
                    _disposer.DisposeState(oldItem.Value);
                }
            }

            _buffer[IndexToBuffer(index)] = new Valid
            {
                Value = value,
                HasValue = true,
            };
        }

        public void Clear(int index)
        {
            _buffer[IndexToBuffer(index)] = default;
        }

        // we can't use nullable here or we will have to limit T to struct
        // T should probably be limited to struct anyway, but seems like a pain to put `where T : struct`  everywhere
        // todo should we just make T a struct??
        struct Valid
        {
            public T Value;
            public bool HasValue;
        }
    }
}
