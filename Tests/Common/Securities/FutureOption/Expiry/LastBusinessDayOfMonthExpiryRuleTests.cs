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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption.Expiry
{
    /// <summary>
    /// Fork-only tests (fop-weeklies) for <see cref="LastBusinessDayOfMonthExpiryRule"/>
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class LastBusinessDayOfMonthExpiryRuleTests
    {
        private static readonly IReadOnlyCollection<DateTime> NoHolidays = new HashSet<DateTime>();

        [TestCase(2025, 12, "2025-12-31", Description = "month ends on a Wednesday")]
        [TestCase(2025, 5, "2025-05-30", Description = "month ends on a Saturday, prior Friday")]
        [TestCase(2024, 3, "2024-03-29", Description = "month ends on a Sunday, prior Friday (no holidays passed)")]
        [TestCase(2026, 2, "2026-02-27", Description = "month ends on a Saturday, prior Friday")]
        public void ComputesLastBusinessDayOfMonth(int year, int month, string expected)
        {
            var rule = new LastBusinessDayOfMonthExpiryRule();

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromMonth(year, month), NoHolidays);

            Assert.AreEqual(DateTime.Parse(expected, CultureInfo.InvariantCulture), expiry);
        }

        [Test]
        public void HolidayAtMonthEndRollsBack()
        {
            // Good Friday 2024-03-29: March 2024 ends Sunday 31st, Friday 29th is a holiday,
            // so the end-of-month expiry lands on Thursday the 28th
            var holidays = new HashSet<DateTime> { new DateTime(2024, 3, 29) };
            var rule = new LastBusinessDayOfMonthExpiryRule();

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2024, 3), holidays);

            Assert.AreEqual(new DateTime(2024, 3, 28), expiry);
        }

        [Test]
        public void BusinessDayOffsetCountsBusinessDaysOnly()
        {
            // December 2025: last business day 31st (Wed); offset 2 skips 30th/29th to Monday 29th...
            // offset counts business days: 31 -> 30 -> 29, so offset 2 = Monday the 29th
            var rule = new LastBusinessDayOfMonthExpiryRule(2);

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2025, 12), NoHolidays);

            Assert.AreEqual(new DateTime(2025, 12, 29), expiry);
        }

        [Test]
        public void BusinessDayOffsetSkipsWeekendsAndHolidays()
        {
            // December 2025 with the 30th as a holiday: 31 -> (30 skipped) -> 29 for offset 1
            var holidays = new HashSet<DateTime> { new DateTime(2025, 12, 30) };
            var rule = new LastBusinessDayOfMonthExpiryRule(1);

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2025, 12), holidays);

            Assert.AreEqual(new DateTime(2025, 12, 29), expiry);
        }

        [Test]
        public void NegativeOffsetThrows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LastBusinessDayOfMonthExpiryRule(-1));
        }

        [Test]
        public void EnumerateExpiriesReturnsOnePerMonthInRange()
        {
            var holidays = new HashSet<DateTime> { new DateTime(2024, 3, 29) };
            var rule = new LastBusinessDayOfMonthExpiryRule();

            var expiries = rule.EnumerateExpiries(new DateTime(2024, 1, 1), new DateTime(2024, 4, 30), holidays).ToList();

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 1, 31),
                new DateTime(2024, 2, 29),
                new DateTime(2024, 3, 28),
                new DateTime(2024, 4, 30)
            }, expiries);
        }
    }
}
