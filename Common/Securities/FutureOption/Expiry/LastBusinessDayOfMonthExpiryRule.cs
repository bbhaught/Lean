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
    /// Expiry rule for end-of-month futures-option series (e.g. ES EOM root EW): the contract
    /// expires on the last business day of the calendar month, optionally offset by a number of
    /// business days before it. Exchange holidays are excluded from the business-day count, so a
    /// month ending in a holiday (e.g. Good Friday 2024-03-29) naturally expires the prior
    /// business day
    /// </summary>
    public class LastBusinessDayOfMonthExpiryRule : IFutureOptionExpiryRule
    {
        private readonly int _businessDayOffset;

        /// <summary>
        /// Creates a new rule with the given business-day offset
        /// </summary>
        /// <param name="businessDayOffset">Number of business days before the last business day of
        /// the month the series expires on; zero (default) expires on the last business day itself</param>
        public LastBusinessDayOfMonthExpiryRule(int businessDayOffset = 0)
        {
            if (businessDayOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(businessDayOffset), businessDayOffset,
                    "LastBusinessDayOfMonthExpiryRule: business day offset must not be negative");
            }

            _businessDayOffset = businessDayOffset;
        }

        /// <summary>
        /// Gets the expiry date: the last business day of the key's contract month minus the
        /// configured business-day offset
        /// </summary>
        public DateTime GetExpiryDate(FutureOptionContractKey contractKey, IReadOnlyCollection<DateTime> holidays)
        {
            FutureOptionExpiryRuleUtilities.ValidateHolidays(holidays);

            var lastCalendarDay = new DateTime(contractKey.Year, contractKey.Month,
                DateTime.DaysInMonth(contractKey.Year, contractKey.Month));
            var lastBusinessDay = FutureOptionExpiryRuleUtilities.AdjustToPriorBusinessDay(lastCalendarDay, holidays);

            return FutureOptionExpiryRuleUtilities.SubtractBusinessDays(lastBusinessDay, _businessDayOffset, holidays);
        }

        /// <summary>
        /// Enumerates all expiries of this series within [start, end] inclusive, ascending
        /// </summary>
        public IEnumerable<DateTime> EnumerateExpiries(DateTime rangeStart, DateTime rangeEnd, IReadOnlyCollection<DateTime> holidays)
        {
            FutureOptionExpiryRuleUtilities.ValidateHolidays(holidays);

            var month = new DateTime(rangeStart.Year, rangeStart.Month, 1);
            var lastMonth = new DateTime(rangeEnd.Year, rangeEnd.Month, 1);

            while (month <= lastMonth)
            {
                var expiry = GetExpiryDate(FutureOptionContractKey.FromMonth(month.Year, month.Month), holidays);
                if (expiry >= rangeStart.Date && expiry <= rangeEnd.Date)
                {
                    yield return expiry;
                }

                month = month.AddMonths(1);
            }
        }
    }
}
