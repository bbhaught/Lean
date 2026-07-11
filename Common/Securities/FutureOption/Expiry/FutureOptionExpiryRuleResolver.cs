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
using System.Collections.Concurrent;

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Maps the <see cref="FutureOptionRootDefinition.ExpiryRuleId"/> of a registry root
    /// definition to a parameterized <see cref="IFutureOptionExpiryRule"/> instance.
    /// Part of the fop-weeklies fork expiry-rule engine
    /// </summary>
    public static class FutureOptionExpiryRuleResolver
    {
        /// <summary>
        /// Rule id of roots whose expiry is computed by the hardcoded legacy table in
        /// <see cref="FuturesOptionsExpiryFunctions"/>. Not resolvable to a rule instance
        /// </summary>
        public const string Legacy = "legacy";

        /// <summary>
        /// Rule id of roots whose expiry rule was never implemented upstream and which
        /// historically resolved through the silent fallback to the underlying future's expiry.
        /// Kept as an explicit registry-declared rule so their behavior is unchanged; handled
        /// directly by <see cref="FuturesOptionsExpiryFunctions"/>, not resolvable to a rule instance
        /// </summary>
        public const string UnderlyingFuture = "underlying_future";

        /// <summary>
        /// Rule id of weekly series expiring on the Nth occurrence of a weekday in the month
        /// </summary>
        public const string NthWeekday = "nth_weekday";

        /// <summary>
        /// Rule id of end-of-month series expiring on the last business day of the month
        /// </summary>
        public const string LastBusinessDay = "last_business_day";

        /// <summary>
        /// Rule id of daily series expiring every trading day
        /// </summary>
        public const string Daily = "daily";

        private static readonly ConcurrentDictionary<string, IFutureOptionExpiryRule> _rules = new();

        /// <summary>
        /// Attempts to resolve the given root definition to an expiry rule instance.
        /// Returns false for the <see cref="Legacy"/> and <see cref="UnderlyingFuture"/> rule ids,
        /// which are handled directly by <see cref="FuturesOptionsExpiryFunctions"/>.
        /// Throws for unknown rule ids or definitions missing required parameters: a misconfigured
        /// registry must fail loudly, never fall back to a plausible-looking wrong expiry
        /// </summary>
        /// <param name="definition">The registry root definition to resolve</param>
        /// <param name="rule">The resolved rule instance, or null when not resolvable</param>
        /// <returns>True when a rule instance was resolved</returns>
        public static bool TryResolve(FutureOptionRootDefinition definition, out IFutureOptionExpiryRule rule)
        {
            ArgumentNullException.ThrowIfNull(definition);

            switch (definition.ExpiryRuleId)
            {
                case null:
                case "":
                case Legacy:
                case UnderlyingFuture:
                    rule = null;
                    return false;

                case NthWeekday:
                    if (!definition.ExpiryDayOfWeek.HasValue)
                    {
                        throw new ArgumentException(
                            "FutureOptionExpiryRuleResolver.TryResolve(): root " +
                            $"'{definition.OptionTicker}' (market '{definition.Market}') uses expiry rule " +
                            $"'{NthWeekday}' but defines no expiryDayOfWeek");
                    }
                    rule = _rules.GetOrAdd(
                        $"{NthWeekday}:{definition.ExpiryDayOfWeek.Value}:{definition.WeekOfMonth}",
                        _ => new NthWeekdayOfMonthExpiryRule(definition.ExpiryDayOfWeek.Value, definition.WeekOfMonth));
                    return true;

                case LastBusinessDay:
                    rule = _rules.GetOrAdd(LastBusinessDay, _ => new LastBusinessDayOfMonthExpiryRule());
                    return true;

                case Daily:
                    rule = _rules.GetOrAdd(Daily, _ => new DailyExpiryRule());
                    return true;

                default:
                    throw new NotSupportedException(
                        "FutureOptionExpiryRuleResolver.TryResolve(): unknown expiry rule id " +
                        $"'{definition.ExpiryRuleId}' for future option root '{definition.OptionTicker}' " +
                        $"(market '{definition.Market}')");
            }
        }
    }
}
