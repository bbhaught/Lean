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

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Identifies a single futures-option contract within an expiry series: the contract year and
    /// month, optionally the week of the month (for weekly series, week N = Nth occurrence of the
    /// series weekday in the calendar month) and optionally an exact date (for daily series).
    /// Part of the fop-weeklies fork expiry-rule engine
    /// </summary>
    public readonly struct FutureOptionContractKey : IEquatable<FutureOptionContractKey>
    {
        /// <summary>
        /// The contract year
        /// </summary>
        public int Year { get; }

        /// <summary>
        /// The contract month (1-12)
        /// </summary>
        public int Month { get; }

        /// <summary>
        /// The week of the month (1-5) for weekly series, null otherwise.
        /// Week N is the Nth occurrence of the series weekday within the calendar month
        /// </summary>
        public int? WeekOfMonth { get; }

        /// <summary>
        /// The exact expiry date for daily series, null otherwise. Always a bare date (no time)
        /// </summary>
        public DateTime? ExactDate { get; }

        /// <summary>
        /// Creates a new contract key
        /// </summary>
        /// <param name="year">Contract year</param>
        /// <param name="month">Contract month (1-12)</param>
        /// <param name="weekOfMonth">Optional week of the month (1-5) for weekly series</param>
        /// <param name="exactDate">Optional exact date for daily series; must lie in the given
        /// year and month and is truncated to its date component</param>
        public FutureOptionContractKey(int year, int month, int? weekOfMonth = null, DateTime? exactDate = null)
        {
            if (year < 1900 || year > 2200)
            {
                throw new ArgumentOutOfRangeException(nameof(year), year,
                    "FutureOptionContractKey: year must be between 1900 and 2200");
            }
            if (month < 1 || month > 12)
            {
                throw new ArgumentOutOfRangeException(nameof(month), month,
                    "FutureOptionContractKey: month must be between 1 and 12");
            }
            if (weekOfMonth.HasValue && (weekOfMonth.Value < 1 || weekOfMonth.Value > 5))
            {
                throw new ArgumentOutOfRangeException(nameof(weekOfMonth), weekOfMonth.Value,
                    "FutureOptionContractKey: week of month must be between 1 and 5");
            }
            if (weekOfMonth.HasValue && exactDate.HasValue)
            {
                throw new ArgumentException(
                    "FutureOptionContractKey: week of month and exact date are mutually exclusive");
            }
            if (exactDate.HasValue && (exactDate.Value.Year != year || exactDate.Value.Month != month))
            {
                throw new ArgumentException(
                    $"FutureOptionContractKey: exact date {exactDate.Value:yyyy-MM-dd} does not lie in contract month {year}-{month:00}");
            }

            Year = year;
            Month = month;
            WeekOfMonth = weekOfMonth;
            ExactDate = exactDate?.Date;
        }

        /// <summary>
        /// Creates a month-level contract key (monthly, quarterly and end-of-month series)
        /// </summary>
        public static FutureOptionContractKey FromMonth(int year, int month)
        {
            return new FutureOptionContractKey(year, month);
        }

        /// <summary>
        /// Creates a weekly contract key. Week N is the Nth occurrence of the series weekday
        /// within the calendar month
        /// </summary>
        public static FutureOptionContractKey FromWeek(int year, int month, int weekOfMonth)
        {
            return new FutureOptionContractKey(year, month, weekOfMonth);
        }

        /// <summary>
        /// Creates an exact-date contract key (daily series)
        /// </summary>
        public static FutureOptionContractKey FromDate(DateTime date)
        {
            return new FutureOptionContractKey(date.Year, date.Month, null, date.Date);
        }

        /// <summary>
        /// Determines whether the specified contract key is equal to this instance
        /// </summary>
        public bool Equals(FutureOptionContractKey other)
        {
            return Year == other.Year
                && Month == other.Month
                && WeekOfMonth == other.WeekOfMonth
                && ExactDate == other.ExactDate;
        }

        /// <summary>
        /// Determines whether the specified object is equal to this instance
        /// </summary>
        public override bool Equals(object obj)
        {
            return obj is FutureOptionContractKey other && Equals(other);
        }

        /// <summary>
        /// Serves as a hash function for this contract key
        /// </summary>
        public override int GetHashCode()
        {
            return HashCode.Combine(Year, Month, WeekOfMonth, ExactDate);
        }

        /// <summary>
        /// Equality operator
        /// </summary>
        public static bool operator ==(FutureOptionContractKey left, FutureOptionContractKey right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// Inequality operator
        /// </summary>
        public static bool operator !=(FutureOptionContractKey left, FutureOptionContractKey right)
        {
            return !left.Equals(right);
        }

        /// <summary>
        /// Returns a string representation of this contract key
        /// </summary>
        public override string ToString()
        {
            if (ExactDate.HasValue)
            {
                return $"{ExactDate.Value:yyyy-MM-dd}";
            }
            if (WeekOfMonth.HasValue)
            {
                return $"{Year}-{Month:00}W{WeekOfMonth.Value}";
            }
            return $"{Year}-{Month:00}";
        }
    }
}
