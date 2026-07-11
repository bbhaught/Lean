/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using NUnit.Framework;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption.Expiry
{
    /// <summary>
    /// Fork-only tests (fop-weeklies) for <see cref="FutureOptionContractKey"/>
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionContractKeyTests
    {
        [Test]
        public void FromMonthCreatesMonthKey()
        {
            var key = FutureOptionContractKey.FromMonth(2025, 6);

            Assert.AreEqual(2025, key.Year);
            Assert.AreEqual(6, key.Month);
            Assert.IsNull(key.WeekOfMonth);
            Assert.IsNull(key.ExactDate);
        }

        [Test]
        public void FromWeekCreatesWeekKey()
        {
            var key = FutureOptionContractKey.FromWeek(2025, 6, 3);

            Assert.AreEqual(2025, key.Year);
            Assert.AreEqual(6, key.Month);
            Assert.AreEqual(3, key.WeekOfMonth);
            Assert.IsNull(key.ExactDate);
        }

        [Test]
        public void FromDateCreatesExactDateKeyTruncatedToDate()
        {
            var key = FutureOptionContractKey.FromDate(new DateTime(2025, 6, 18, 14, 30, 0));

            Assert.AreEqual(2025, key.Year);
            Assert.AreEqual(6, key.Month);
            Assert.IsNull(key.WeekOfMonth);
            Assert.AreEqual(new DateTime(2025, 6, 18), key.ExactDate);
        }

        [TestCase(1899, 6)]
        [TestCase(2201, 6)]
        [TestCase(2025, 0)]
        [TestCase(2025, 13)]
        public void InvalidYearOrMonthThrows(int year, int month)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FutureOptionContractKey.FromMonth(year, month));
        }

        [TestCase(0)]
        [TestCase(6)]
        public void InvalidWeekOfMonthThrows(int weekOfMonth)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FutureOptionContractKey.FromWeek(2025, 6, weekOfMonth));
        }

        [Test]
        public void WeekOfMonthAndExactDateAreMutuallyExclusive()
        {
            Assert.Throws<ArgumentException>(
                () => new FutureOptionContractKey(2025, 6, 3, new DateTime(2025, 6, 18)));
        }

        [Test]
        public void ExactDateOutsideContractMonthThrows()
        {
            Assert.Throws<ArgumentException>(
                () => new FutureOptionContractKey(2025, 6, null, new DateTime(2025, 7, 1)));
        }

        [Test]
        public void EqualityIsValueBased()
        {
            var left = FutureOptionContractKey.FromWeek(2025, 6, 3);
            var right = FutureOptionContractKey.FromWeek(2025, 6, 3);
            var other = FutureOptionContractKey.FromWeek(2025, 6, 4);

            Assert.IsTrue(left == right);
            Assert.IsTrue(left != other);
            Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
        }
    }
}
