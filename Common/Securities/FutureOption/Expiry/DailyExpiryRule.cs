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
    /// Expiry rule for daily futures-option series (e.g. 2025 CME daily treasury options): a
    /// contract expires every trading day, optionally excluding configured weekdays on which the
    /// product lists no daily contract (e.g. days covered by the weekly series instead).
    /// Daily contracts are identified by exact-date keys; there is no meaningful mapping from a
    /// bare contract month to a single daily expiry
    /// </summary>
    public class DailyExpiryRule : IFutureOptionExpiryRule
    {
        private readonly HashSet<DayOfWeek> _excludedDays;

        /// <summary>
        /// Creates a new daily expiry rule
        /// </summary>
        /// <param name="excludedDays">Weekdays on which no daily contract is listed, in addition
        /// to weekends which are always excluded; null or empty excludes only weekends and holidays</param>
        public DailyExpiryRule(IEnumerable<DayOfWeek> excludedDays = null)
        {
            _excludedDays = excludedDays != null ? new HashSet<DayOfWeek>(excludedDays) : new HashSet<DayOfWeek>();
        }

        /// <summary>
        /// Gets the expiry date of the exact-date contract key. Throws when the key has no exact
        /// date or names a non-trading or excluded day: daily contracts only exist on trading days
        /// </summary>
        public DateTime GetExpiryDate(FutureOptionContractKey contractKey, IReadOnlyCollection<DateTime> holidays)
        {
            FutureOptionExpiryRuleUtilities.ValidateHolidays(holidays);

            if (!contractKey.ExactDate.HasValue)
            {
                throw new ArgumentException(
                    "DailyExpiryRule.GetExpiryDate(): daily series require an exact-date contract key; " +
                    "a contract month does not identify a single daily expiry");
            }

            var date = contractKey.ExactDate.Value;
            if (!IsListedDay(date, holidays))
            {
                throw new ArgumentException(
                    $"DailyExpiryRule.GetExpiryDate(): {date:yyyy-MM-dd} is not a listed trading day for this daily series");
            }

            return date;
        }

        /// <summary>
        /// Enumerates every listed trading day within [start, end] inclusive, ascending
        /// </summary>
        public IEnumerable<DateTime> EnumerateExpiries(DateTime rangeStart, DateTime rangeEnd, IReadOnlyCollection<DateTime> holidays)
        {
            FutureOptionExpiryRuleUtilities.ValidateHolidays(holidays);

            for (var date = rangeStart.Date; date <= rangeEnd.Date; date = date.AddDays(1))
            {
                if (IsListedDay(date, holidays))
                {
                    yield return date;
                }
            }
        }

        private bool IsListedDay(DateTime date, IReadOnlyCollection<DateTime> holidays)
        {
            return FutureOptionExpiryRuleUtilities.IsBusinessDay(date, holidays) && !_excludedDays.Contains(date.DayOfWeek);
        }
    }
}
