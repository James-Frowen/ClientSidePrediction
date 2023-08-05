using System.Linq;
using JamesFrowen.CSP.Alloc;
using Mirage.Serialization;
using NUnit.Framework;
using UnityEngine;

namespace JamesFrowen.DeltaSnapshot.Tests
{
    [TestFixture(typeof(DeltaSnapshot_IntDiffPack))]
    [TestFixture(typeof(DeltaSnapshot_ZeroCounts))]
    [TestFixture(typeof(DeltaSnapshot_ValueZeroCounts))]
    [TestFixture(typeof(DeltaSnapshot_FloatFocus))]
    [TestFixture(typeof(DeltaSnapshot_LossyFloats))]
    [TestFixture(typeof(DeltaSnapshot_LossyFloats_Easy))]
    public abstract unsafe class DeltaSnapshotTestBase<TDeltaSnapshot> where TDeltaSnapshot : IDeltaSnapshot, new()
    {
        protected SimpleAlloc _alloc;
        protected DeltaSnapshotWriter _deltaSnapshot;
        protected int* _from;
        protected int* _serverTo;
        protected int* _clientTo;
        protected const int MAX_INT_SIZE = 1000;

        protected NetworkWriter _writer;
        protected NetworkReader _reader;

        [SetUp]
        public void SetUp()
        {
            _alloc = new SimpleAlloc();
            _deltaSnapshot = new DeltaSnapshotWriter(_alloc, new TDeltaSnapshot());

            _from = (int*)_alloc.Allocate(MAX_INT_SIZE * 4);
            _serverTo = (int*)_alloc.Allocate(MAX_INT_SIZE * 4);
            _clientTo = (int*)_alloc.Allocate(MAX_INT_SIZE * 4);

            _writer = new NetworkWriter(1300, true);
            _reader = new NetworkReader();
        }

        [TearDown]
        public void TearDown()
        {
            _alloc.ReleaseAll();
            _alloc = null;

            _writer.Reset();
            _reader.Dispose();
        }

        protected void WriterToReader()
        {
            _reader.Reset(_writer.ToArraySegment());
        }

        protected void DeltaReadWrite(int intSize)
        {
            _deltaSnapshot.WriteDelta(_writer, intSize, _from, _serverTo);
            WriterToReader();
            _deltaSnapshot.ReadDelta(_reader, intSize, _from, _clientTo);
        }
    }

    public unsafe class DeltaSnapshotTest<TDeltaSnapshot> : DeltaSnapshotTestBase<TDeltaSnapshot> where TDeltaSnapshot : IDeltaSnapshot, new()
    {
        [Test]
        public void ReadWriteSameValues()
        {
            _serverTo[0] = 2;
            _serverTo[1] = 100;
            _serverTo[2] = -201;

            const int intSize = 3;

            DeltaReadWrite(intSize);

            for (var i = 0; i < intSize; i++)
            {
                if (_serverTo[i] != _clientTo[i])
                    Assert.Fail($"Values not equal at index {i}");
            }
        }
    }
    public unsafe class DeltaSnapshotTest_Bandwidth<TDeltaSnapshot> : DeltaSnapshotTestBase<TDeltaSnapshot> where TDeltaSnapshot : IDeltaSnapshot, new()
    {
        [Test]
        [TestCase(0.9f, 0.1f, 1)]
        [TestCase(0.5f, 0.1f, 60)]
        public void BandwidthTest(float changeChance, float changeSize, int runCount)
        {
            const int intSize = 200;
            var size = new int[runCount];
            for (var r = 0; r < runCount; r++)
            {
                for (var i = 0; i < intSize; i++)
                {
                    var value = 0.01f;// Random.value;
                    _from[i] = *(int*)&value;

                    var shouldChange = Random.value < changeChance;
                    if (shouldChange)
                    {
                        var change = (Random.value - 0.5f) * changeSize;
                        var to = value + change;
                        _serverTo[i] = *(int*)&to;
                    }
                    else
                    {
                        _serverTo[i] = _from[i];
                    }
                }
                //Diff[0]:-1679461

                DeltaReadWrite(intSize);

                for (var i = 0; i < intSize; i++)
                {
                    if (typeof(TDeltaSnapshot) == typeof(DeltaSnapshot_LossyFloats)
                     || typeof(TDeltaSnapshot) == typeof(DeltaSnapshot_LossyFloats_Easy))
                    {
                        var sF = *(float*)(_serverTo + i);
                        var cF = *(float*)(_clientTo + i);
                        var diff = Mathf.Abs(sF - cF);

                        if (diff > 0.001f)
                        {
                            Assert.Fail($"Values not close enough at index {i}. From{_from[i]:X8} Server:{sF:X8} Client:{cF:X8}");
                        }
                    }
                    else
                    {
                        if (_serverTo[i] != _clientTo[i])
                            Assert.Fail($"Values not equal at index {i}. From{_from[i]:X8} Server:{_serverTo[i]:X8} Client:{_clientTo[i]:X8}");
                    }
                }

                var segment = _writer.ToArraySegment();
                size[r] = segment.Count;

                _writer.Reset();
            }

            var avg = size.Average();
            var min = size.Min();
            var max = size.Max();
            Debug.Log($"{typeof(TDeltaSnapshot).Name} - Delta Write: Original Size:{intSize * 4} Delta:[avg:{avg:0.0} min:{min} max:{max}]");
        }
    }
    public unsafe class DeltaSnapshotTest_Quaternion<TDeltaSnapshot> : DeltaSnapshotTestBase<TDeltaSnapshot> where TDeltaSnapshot : IDeltaSnapshot, new()
    {
        [Test]
        public void QuaternionCompress_IntDelta()
        {
            var qFrom = Quaternion.Euler(0, 30, 0);
            var qServer = Quaternion.Euler(0, 32, 0);

            // just use delta
            *(Quaternion*)_from = qFrom;
            *(Quaternion*)_serverTo = qServer;
            const int intSize = 4;

            DeltaReadWrite(intSize);

            Debug.Log($"BitCount: {_writer.BitPosition}");

            var qClient = *(Quaternion*)_clientTo;

            // 0.1f is small enough not to care about in this test
            Assert.That(Quaternion.Angle(qServer, qClient), Is.LessThan(0.1f));
        }
        [Test]
        public void QuaternionCompress_Pack_IntDelta()
        {
            var qFrom = Quaternion.Euler(0, 30, 0);
            var qServer = Quaternion.Euler(0, 32, 0);

            // just use delta
            *_from = (int)QuaternionPacker.PackAsInt(qFrom);
            *_serverTo = (int)QuaternionPacker.PackAsInt(qServer);
            const int intSize = 1;

            DeltaReadWrite(intSize);

            Debug.Log($"BitCount: {_writer.BitPosition}");

            var qClient = QuaternionPacker.UnpackFromInt((uint)*_clientTo);

            // 0.1f is small enough not to care about in this test
            Assert.That(Quaternion.Angle(qServer, qClient), Is.LessThan(0.1f));
        }
    }
}
