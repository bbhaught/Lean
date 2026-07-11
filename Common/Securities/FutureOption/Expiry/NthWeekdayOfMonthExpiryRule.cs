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

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Expiry rule for weekly futures-option series that expire on the Nth occurrence of a fixed
    /// weekday within the calendar month (CME weekly convention), e.g. ES Friday weeklies EW1-EW4,
    /// Monday weeklies E1A-E5A, crude LO1-LO5, gold OG1-OG5, treasury ZN1-ZN5/WY1-WY5.
    /// When the scheduled weekday is an exchange holiday (e.g. Good Friday), the contract expires
    /// on the immediately preceding business day.
    /// Note: forward (next-business-day) holiday adjustment for Monday series is not modeled; the
    /// P5 calendar validation against actually-listed contracts will confirm per-product behavior
    /// </summary>
    public class NthWeekdayOfMonthExpiryRule : IFutureOptionExpiryRule
    {
        private readonly DayOfWeek _dayOfWeek;
        private readonly int? _weekOfMonth;

        /// <summary>
        /// Creates a new rule for the given weekday and optional fixed week of the month
        /// </summary>
        /// <param name="dayOfWeek">The weekday the series expires on</param>
        /// <param name="weekOfMonth">The fixed week of the month (1-5) of this series root, or null
        /// when the week is provided per contract via <see cref="FutureOptionContractKey.WeekOfMonth"/></param>
        public NthWeekdayOfMonthExpiryRule(DayOfWeek dayOfWeek, int? weekOfMonth = null)
        {
            if (weekOfMonth.HasValue && (weekOfMonth.Value < 1 || weekOfMonth.Value > 5))
            {
                throw new ArgumentOutOfRangeException(nameof(weekOfMonth), weekOfMonth.Value,
                    "NthWeekdayOfMonthExpiryRule: week of month must be between 1 and 5");
            }

            _dayOfWeek = dayOfWeek;
            _weekOfMonth = weekOfMonth;
        }

        /// <summary>
        /// Gets the expiry date: the Nth occurrence of the rule weekday in the key's contract
        /// month, adjusted to the prior business day when it falls on a holiday
        /// </summary>
        public DateTime GetExpiryDate(FutureOptionContractKey contractKey, IReadOnlyCollection<DateTime> holidays)
        {
            FutureOptionExpiryRuleUtilities.ValidateHolidays(holidays);

            if (contractKey.ExactDate.HasValue)
            {
                throw new ArgumentException(
                    "NthWeekdayOfMonthExpiryRule.GetExpiryDate(): exact-date contract keys are not " +
                    "supported by weekly rules, use a week-of-month key");
            }

            var weekOfMonth = contractKey.WeekOfMonth ?? _weekOfMonth;
            if (!weekOfMonth.HasValue)
            {
                throw new ArgumentException(
                    "NthWeekdayOfMonthExpiryRule.GetExpiryDate(): no week of month available, neither " +
                    "the rule nor the contract key specifies one");
            }

            if (!FutureOptionExpiryRuleUtilities.TryGetNthWeekdayOfMonth(
                contractKey.Year, contractKey.Month, weekOfMonth.Value, _dayOfWeek, out var date))
            {
                throw new ArgumentOutOfRangeException(nameof(contractKey), contractKey,
                    $"NthWeekdayOfMonthExpiryRule.GetExpiryDate(): {contractKey.Year}-{contractKey.Month:00} " +
                    $"has no occurrence {weekOfMonth.Value} of {_dayOfWeek}");
            }

            return FutureOptionExpiryRuleUtilities.AdjustToPriorBusinessDay(date, holidays);
        }

        /// <summary>
        /// Enumerates all expiries of this series within [start, end] inclusive, ascending.
        /// Months whose Nth weekday does not exist (week 5 in most months) are skipped
        /// </summary>
        public IEnumerable<DateTime> EnumerateExpiries(DateTime rangeStart, DateTime rangeEnd, IReadOnlyCollection<DateTime> holidays)
        {
            FutureOptionExpiryRuleUtilities.ValidateHolidays(holidays);

            // iterate one month past the end: a week-1 contract of the following month can adjust
            // backwards into the requested range (e.g. a weekday-1 holiday on the 1st of the month)
            var month = new DateTime(rangeStart.Year, rangeStart.Month, 1);
            var lastMonth = new DateTime(rangeEnd.Year, rangeEnd.Month, 1).AddMonths(1);

            while (month <= lastMonth)
            {
                var firstWeek = _weekOfMonth ?? 1;
                var lastWeek = _weekOfMonth ?? 5;
                for (var week = firstWeek; week <= lastWeek; week++)
                {
                    if (!FutureOptionExpiryRuleUtilities.TryGetNthWeekdayOfMonth(
                        month.Year, month.Month, week, _dayOfWeek, out var date))
                    {
                        break;
                    }

                    var adjusted = FutureOptionExpiryRuleUtilities.AdjustToPriorBusinessDay(date, holidays);
                    if (adjusted >= rangeStart.Date && adjusted <= rangeEnd.Date)
                    {
                        yield return adjusted;
                    }
                }

                month = month.AddMonths(1);
            }
        }
    }
}
