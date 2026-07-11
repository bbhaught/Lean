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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption.Expiry
{
    /// <summary>
    /// Fork-only tests (fop-weeklies) for <see cref="DailyExpiryRule"/>
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class DailyExpiryRuleTests
    {
        private static readonly IReadOnlyCollection<DateTime> NoHolidays = new HashSet<DateTime>();

        [Test]
        public void ExactDateOnTradingDayIsReturned()
        {
            var rule = new DailyExpiryRule();

            var expiry = rule.GetExpiryDate(FutureOptionContractKey.FromDate(new DateTime(2025, 6, 18)), NoHolidays);

            Assert.AreEqual(new DateTime(2025, 6, 18), expiry);
        }

        [Test]
        public void MonthKeyThrows()
        {
            var rule = new DailyExpiryRule();

            Assert.Throws<ArgumentException>(
                () => rule.GetExpiryDate(FutureOptionContractKey.FromMonth(2025, 6), NoHolidays));
        }

        [Test]
        public void WeekendDateThrows()
        {
            var rule = new DailyExpiryRule();

            Assert.Throws<ArgumentException>(
                () => rule.GetExpiryDate(FutureOptionContractKey.FromDate(new DateTime(2025, 6, 21)), NoHolidays));
        }

        [Test]
        public void HolidayDateThrows()
        {
            var holidays = new HashSet<DateTime> { new DateTime(2025, 6, 19) };
            var rule = new DailyExpiryRule();

            Assert.Throws<ArgumentException>(
                () => rule.GetExpiryDate(FutureOptionContractKey.FromDate(new DateTime(2025, 6, 19)), holidays));
        }

        [Test]
        public void ExcludedWeekdayThrows()
        {
            var rule = new DailyExpiryRule(new[] { DayOfWeek.Friday });

            Assert.Throws<ArgumentException>(
                () => rule.GetExpiryDate(FutureOptionContractKey.FromDate(new DateTime(2025, 6, 20)), NoHolidays));
        }

        [Test]
        public void EnumerateExpiriesSkipsWeekendsHolidaysAndExcludedDays()
        {
            // Week of 2025-06-16 (Mon) to 2025-06-22 (Sun) with Juneteenth Thursday the 19th as a
            // holiday and Fridays excluded (e.g. covered by the weekly series instead)
            var holidays = new HashSet<DateTime> { new DateTime(2025, 6, 19) };
            var rule = new DailyExpiryRule(new[] { DayOfWeek.Friday });

            var expiries = rule.EnumerateExpiries(new DateTime(2025, 6, 16), new DateTime(2025, 6, 22), holidays).ToList();

            CollectionAssert.AreEqual(new[]
            {
                new DateTime(2025, 6, 16),
                new DateTime(2025, 6, 17),
                new DateTime(2025, 6, 18)
            }, expiries);
        }
    }
}
