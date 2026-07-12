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
using QuantConnect.Securities.Future;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// Fork-only boundary sweep (fop-weeklies P3): for every weekly/EOM root in the registry,
    /// enumerate all 2024-2025 expiries through the P2 expiry-rule engine and validate the
    /// resolved underlying future against structural invariants that hold for every policy:
    /// the underlying future never expires before the option, the resolution never skips more
    /// than one contract of the root's cycle, and the round-trip through
    /// <see cref="UnderlyingMappingValidator"/> is consistent
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionUnderlyingBoundarySweepTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // reload the market hours database from disk: other fixtures mutate entries in
            // memory, which would shift holiday-adjusted expiries and future expirations
            QuantConnect.Securities.MarketHoursDatabase.Reset();
        }

        private static readonly int[] _quarterlyCycle = { 3, 6, 9, 12 };
        private static readonly int[] _evenMonthCycle = { 2, 4, 6, 8, 10, 12 };
        private static readonly int[] _monthlyCycle = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };

        public static IEnumerable<TestCaseData> WeeklyRootDefinitions()
        {
            return FutureOptionsRootRegistry.GetDefinitions()
                .Where(definition => definition.UnderlyingRuleId != "legacy")
                .Select(definition => new TestCaseData(definition)
                    .SetName($"BoundarySweep({definition.OptionTicker},{definition.Market})"));
        }

        [TestCaseSource(nameof(WeeklyRootDefinitions))]
        public void ResolutionNeverReturnsExpiredFutureAndNeverSkipsMoreThanOneContract(
            FutureOptionRootDefinition definition)
        {
            Assert.IsTrue(FutureOptionExpiryRuleResolver.TryResolve(definition, out var expiryRule),
                $"no expiry rule for {definition.OptionTicker}");

            var holidays = FuturesExpiryUtilityFunctions.GetExpirationHolidays(
                definition.Market, definition.FutureTicker);
            var expiries = expiryRule.EnumerateExpiries(
                new DateTime(2024, 1, 1), new DateTime(2025, 12, 31), holidays).ToList();
            Assert.IsNotEmpty(expiries, $"no expiries enumerated for {definition.OptionTicker}");

            var underlyingFuture = Symbol.Create(definition.FutureTicker, SecurityType.Future, definition.Market);
            var futureExpiryFunction = FuturesExpiryFunctions.FuturesExpiryFunction(underlyingFuture);
            var cycle = CycleFor(definition.UnderlyingRuleId);

            foreach (var optionExpiry in expiries)
            {
                var resolved = FuturesOptionsUnderlyingMapper.GetUnderlyingFutureFromFutureOption(
                    definition.OptionTicker, definition.Market, optionExpiry, optionExpiry);

                Assert.IsNotNull(resolved,
                    $"{definition.OptionTicker} expiring {optionExpiry:yyyy-MM-dd} resolved to null");
                Assert.AreEqual(definition.FutureTicker, resolved.ID.Symbol);

                // invariant 1: an option can never outlive its underlying future
                Assert.GreaterOrEqual(resolved.ID.Date.Date, optionExpiry.Date,
                    $"{definition.OptionTicker} expiring {optionExpiry:yyyy-MM-dd} resolved to an " +
                    $"already-expired future ({resolved.ID.Date:yyyy-MM-dd})");

                // invariant 2: the resolution is the front cycle contract still alive at the
                // option's expiry, or the one immediately after it - never further out
                var frontContractMonth = FrontCycleContractMonth(optionExpiry, cycle, futureExpiryFunction);
                var secondContractMonth = NextCycleMonth(frontContractMonth, cycle);
                var resolvedContractMonth = FuturesExpiryUtilityFunctions.GetFutureContractMonth(resolved);
                Assert.IsTrue(
                    resolvedContractMonth == frontContractMonth || resolvedContractMonth == secondContractMonth,
                    $"{definition.OptionTicker} expiring {optionExpiry:yyyy-MM-dd} resolved to contract month " +
                    $"{resolvedContractMonth:yyyy-MM}, expected {frontContractMonth:yyyy-MM} or {secondContractMonth:yyyy-MM}");

                // invariant 3: the validator round-trip agrees with the mapper
                var optionSymbol = Symbol.CreateOption(resolved, definition.OptionTicker, definition.Market,
                    OptionStyle.American, OptionRight.Call, 100m, optionExpiry);
                Assert.IsTrue(UnderlyingMappingValidator.TryValidate(optionSymbol, out var error), error);
            }
        }

        private static int[] CycleFor(string underlyingRuleId)
        {
            switch (underlyingRuleId)
            {
                case "NextQuarterly":
                case "NextQuarterlyTreasury":
                    return _quarterlyCycle;
                case "NearestActiveMonth":
                    return _evenMonthCycle;
                case "FrontMonthly":
                    return _monthlyCycle;
                default:
                    throw new NotSupportedException($"unexpected underlying rule id '{underlyingRuleId}'");
            }
        }

        /// <summary>
        /// The first contract month of the cycle whose FUTURE expires on or after the option -
        /// the closest the underlying could possibly be
        /// </summary>
        private static DateTime FrontCycleContractMonth(DateTime optionExpiry, int[] cycle,
            Func<DateTime, DateTime> futureExpiryFunction)
        {
            var month = new DateTime(optionExpiry.Year, optionExpiry.Month, 1);
            for (var i = 0; i < 24; i++)
            {
                if (Array.IndexOf(cycle, month.Month) >= 0
                    && futureExpiryFunction(month).Date >= optionExpiry.Date)
                {
                    return month;
                }

                month = month.AddMonths(1);
            }

            throw new InvalidOperationException(
                $"no cycle contract found with future expiry on or after {optionExpiry:yyyy-MM-dd}");
        }

        private static DateTime NextCycleMonth(DateTime contractMonth, int[] cycle)
        {
            var month = contractMonth.AddMonths(1);
            while (Array.IndexOf(cycle, month.Month) < 0)
            {
                month = month.AddMonths(1);
            }

            return month;
        }
    }
}
