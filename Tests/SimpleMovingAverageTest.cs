using JamesFrowen.CSP;
using NUnit.Framework;

namespace Tests
{
    public class SimpleMovingAverageTest
    {
        [Test]
        public void AverageIsZeroWhenEmpty()
        {
            var movingAvg = new SimpleMovingAverage(10);
            Assert.That(movingAvg.GetAverage(), Is.Zero);
        }

        [Test]
        public void CalculatesAverage()
        {
            var movingAvg = new SimpleMovingAverage(10);
            movingAvg.Add(1);
            Assert.That(movingAvg.GetAverage(), Is.EqualTo(1));

            movingAvg.Add(1);
            Assert.That(movingAvg.GetAverage(), Is.EqualTo(1));

            movingAvg.Add(3);
            movingAvg.Add(3);
            Assert.That(movingAvg.GetAverage(), Is.EqualTo(2));
        }

        [Test]
        public void StdDevIsZeroWhenCountLessThan2()
        {
            var movingAvg = new SimpleMovingAverage(10);
            Assert.That(movingAvg.GetStandardDeviation(), Is.Zero);
            movingAvg.Add(1);
            Assert.That(movingAvg.GetStandardDeviation(), Is.Zero);
        }

        [Test]
        public void CalculatesStdDev()
        {
            var movingAvg = new SimpleMovingAverage(10);
            const float expected = 0.5f;
            movingAvg.Add(1);
            movingAvg.Add(1.5f);
            movingAvg.Add(2);
            Assert.That(movingAvg.GetStandardDeviation(), Is.EqualTo(expected));

            movingAvg.Add(1);
            movingAvg.Add(2);
            Assert.That(movingAvg.GetStandardDeviation(), Is.EqualTo(expected));
        }

        [Test]
        public void CalculatesStdDevAndAvg()
        {
            var movingAvg = new SimpleMovingAverage(10);
            const float expectedAvg = 1.5f;
            const float expectedStdDev = 0.5f;
            float average;
            float stdDev;

            movingAvg.Add(1);
            movingAvg.Add(1.5f);
            movingAvg.Add(2);
            (average, stdDev) = movingAvg.GetAverageAndStandardDeviation();
            Assert.That(average, Is.EqualTo(expectedAvg));
            Assert.That(stdDev, Is.EqualTo(expectedStdDev));

            movingAvg.Add(1);
            movingAvg.Add(2);
            (average, stdDev) = movingAvg.GetAverageAndStandardDeviation();
            Assert.That(average, Is.EqualTo(expectedAvg));
            Assert.That(stdDev, Is.EqualTo(expectedStdDev));
        }
    }
}
