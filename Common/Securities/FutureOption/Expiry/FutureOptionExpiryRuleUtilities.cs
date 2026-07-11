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

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Shared date helpers for the fop-weeklies fork expiry rules
    /// </summary>
    internal static class FutureOptionExpiryRuleUtilities
    {
        /// <summary>
        /// True when the given date is neither a weekend day nor contained in the holiday set
        /// </summary>
        internal static bool IsBusinessDay(DateTime date, IReadOnlyCollection<DateTime> holidays)
        {
            return date.DayOfWeek != DayOfWeek.Saturday
                && date.DayOfWeek != DayOfWeek.Sunday
                && !holidays.Contains(date.Date);
        }

        /// <summary>
        /// Returns the given date if it is a business day, otherwise the closest prior business day.
        /// This is the CME holiday adjustment for weekly and end-of-month futures options: contracts
        /// scheduled to expire on an exchange holiday (e.g. Good Friday) expire on the immediately
        /// preceding business day
        /// </summary>
        internal static DateTime AdjustToPriorBusinessDay(DateTime date, IReadOnlyCollection<DateTime> holidays)
        {
            date = date.Date;
            while (!IsBusinessDay(date, holidays))
            {
                date = date.AddDays(-1);
            }
            return date;
        }

        /// <summary>
        /// Subtracts the given number of business days from the date. The input date is assumed
        /// to already be a business day when <paramref name="businessDays"/> is zero
        /// </summary>
        internal static DateTime SubtractBusinessDays(DateTime date, int businessDays, IReadOnlyCollection<DateTime> holidays)
        {
            date = date.Date;
            for (var i = 0; i < businessDays; i++)
            {
                do
                {
                    date = date.AddDays(-1);
                }
                while (!IsBusinessDay(date, holidays));
            }
            return date;
        }

        /// <summary>
        /// Attempts to compute the Nth occurrence of the given weekday within the calendar month.
        /// Returns false when the month has no Nth occurrence (week 5 in most months)
        /// </summary>
        internal static bool TryGetNthWeekdayOfMonth(int year, int month, int weekOfMonth, DayOfWeek dayOfWeek, out DateTime date)
        {
            var firstOfMonth = new DateTime(year, month, 1);
            var offsetToFirstOccurrence = ((int)dayOfWeek - (int)firstOfMonth.DayOfWeek + 7) % 7;
            var day = 1 + offsetToFirstOccurrence + (weekOfMonth - 1) * 7;

            if (day > DateTime.DaysInMonth(year, month))
            {
                date = default;
                return false;
            }

            date = new DateTime(year, month, day);
            return true;
        }

        /// <summary>
        /// Throws when the holiday collection is null
        /// </summary>
        internal static void ValidateHolidays(IReadOnlyCollection<DateTime> holidays)
        {
            ArgumentNullException.ThrowIfNull(holidays);
        }
    }
}
