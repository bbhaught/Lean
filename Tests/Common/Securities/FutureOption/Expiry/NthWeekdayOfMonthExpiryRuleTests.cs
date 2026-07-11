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
    /// Fork-only tests (fop-weeklies) for <see cref="NthWeekdayOfMonthExpiryRule"/>
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class NthWeekdayOfMonthExpiryRuleTests
    {
        private static readonly IReadOnlyCollection<DateTime> NoHolidays = new HashSet<DateTime>();

        [TestCase(2025, 6, 1, "2025-06-06")]
        [TestCase(2025, 6, 2, "2025-06-13")]
        [TestCase(2025, 6, 3, "2025-06-20")]
        [TestCase(2025, 6, 4, "2025-06-27")]
        [TestCase(2025, 8, 5, "2025-08-29")]
        [TestCase(2026, 1, 1, "2026-01-02", Description = "January 2026 starts on a Thursday: week-1 Friday is the 2nd")]
        public void ComputesNthFridayOfMonth(int year, int month, int week, string expected)
        {
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday, week);

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromMonth(year, month), NoHolidays);

            Assert.AreEqual(DateTime.Parse(expected, CultureInfo.InvariantCulture), expiry);
        }

        [Test]
        public void HolidayRollsBackToPriorBusinessDay()
        {
            // Good Friday 2026-04-03 is the first Friday of April 2026. This date is missing from
            // the LEAN MHDB cme/cbot wildcard holiday entries (only comex GC / nymex CL specific
            // entries carry it), so it is exercised here with an explicit holiday list
            var holidays = new HashSet<DateTime> { new DateTime(2026, 4, 3) };
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday, 1);

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2026, 4), holidays);

            Assert.AreEqual(new DateTime(2026, 4, 2), expiry);
        }

        [Test]
        public void ConsecutiveHolidaysWalkBackMultipleDays()
        {
            // Friday and Thursday both holidays: expiry moves to Wednesday
            var holidays = new HashSet<DateTime> { new DateTime(2026, 4, 3), new DateTime(2026, 4, 2) };
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday, 1);

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2026, 4), holidays);

            Assert.AreEqual(new DateTime(2026, 4, 1), expiry);
        }

        [Test]
        public void MondayHolidayRollsBackOverTheWeekend()
        {
            // Documents the current prior-business-day behavior for Monday series per design A
            // section 2.1. Whether CME rolls holiday-Monday weeklies backward over the weekend or
            // forward to Tuesday is unverified; P5 validation against listed contracts will confirm
            var holidays = new HashSet<DateTime> { new DateTime(2025, 9, 1) };
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Monday, 1);

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2025, 9), holidays);

            Assert.AreEqual(new DateTime(2025, 8, 29), expiry);
        }

        [Test]
        public void MissingFifthWeekThrows()
        {
            // April 2025 has only four Mondays
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Monday, 5);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2025, 4), NoHolidays));
        }

        [Test]
        public void ContractKeyWeekOverridesWhenRuleHasNoWeek()
        {
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday);

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromWeek(2025, 6, 2), NoHolidays);

            Assert.AreEqual(new DateTime(2025, 6, 13), expiry);
        }

        [Test]
        public void NoWeekAnywhereThrows()
        {
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday);

            Assert.Throws<ArgumentException>(
                () => rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2025, 6), NoHolidays));
        }

        [Test]
        public void ExactDateKeyThrows()
        {
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday, 1);

            Assert.Throws<ArgumentException>(
                () => rule.GetExpiryDate(FutureOptionContractKey.FromDate(new DateTime(2025, 6, 6)), NoHolidays));
        }

        [Test]
        public void EnumerateExpiriesFixedWeekSkipsMonthsWithoutTheWeek()
        {
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday, 5);

            var expiries = rule.EnumerateExpiries(new DateTime(2025, 1, 1), new DateTime(2025, 12, 31), NoHolidays).ToList();

            // 2025 months with five Fridays: January, May, August, October
            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2025, 1, 31),
                new DateTime(2025, 5, 30),
                new DateTime(2025, 8, 29),
                new DateTime(2025, 10, 31)
            }, expiries);
        }

        [Test]
        public void EnumerateExpiriesAllWeeksReturnsEveryOccurrenceInRange()
        {
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday);

            var expiries = rule.EnumerateExpiries(new DateTime(2025, 6, 1), new DateTime(2025, 7, 31), NoHolidays).ToList();

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2025, 6, 6),
                new DateTime(2025, 6, 13),
                new DateTime(2025, 6, 20),
                new DateTime(2025, 6, 27),
                new DateTime(2025, 7, 4),
                new DateTime(2025, 7, 11),
                new DateTime(2025, 7, 18),
                new DateTime(2025, 7, 25)
            }, expiries);
        }

        [Test]
        public void EnumerateExpiriesAppliesHolidayAdjustmentAndRangeFilter()
        {
            var holidays = new HashSet<DateTime> { new DateTime(2025, 7, 4) };
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday, 1);

            var expiries = rule.EnumerateExpiries(new DateTime(2025, 6, 15), new DateTime(2025, 7, 15), holidays).ToList();

            // June week-1 Friday (2025-06-06) is before the range start; July's rolls to Thursday 3rd
            CollectionAssert.AreEqual(new[] { new DateTime(2025, 7, 3) }, expiries);
        }

        [Test]
        public void EnumerateExpiriesIncludesNextMonthContractAdjustedIntoRange()
        {
            // A week-1 Wednesday on New Year's Day rolls back into December of the previous year
            var holidays = new HashSet<DateTime> { new DateTime(2025, 1, 1) };
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Wednesday, 1);

            var expiries = rule.EnumerateExpiries(new DateTime(2024, 12, 1), new DateTime(2024, 12, 31), holidays).ToList();

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2024, 12, 4),
                new DateTime(2024, 12, 31)
            }, expiries);
        }

        [TestCase(0)]
        [TestCase(6)]
        public void InvalidConstructorWeekThrows(int week)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday, week));
        }

        [Test]
        public void NullHolidaysThrows()
        {
            var rule = new NthWeekdayOfMonthExpiryRule(DayOfWeek.Friday, 1);

            Assert.Throws<ArgumentNullException>(
                () => rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2025, 6), null));
        }
    }
}
