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
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Securities;
using QuantConnect.Securities.FutureOption;
using QuantConnect.Securities.Option;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// fop-weeklies fork P4: proves that OptionFilterUniverse.WeeklysOnly()/StandardsOnly() classify
    /// future option contracts through the registry-driven FutureOptionSymbol.IsStandard (P1),
    /// separating weekly/end-of-month roots from standard monthly roots
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionFilterUniverseTests
    {
        private static readonly Symbol _esMarch2020 = Symbol.CreateFuture(
            QuantConnect.Securities.Futures.Indices.SP500EMini, Market.CME, new DateTime(2020, 3, 20));

        private static Symbol CreateContract(string root, DateTime expiry, decimal strike = 3200m)
        {
            return Symbol.CreateOption(_esMarch2020, root, Market.CME, OptionStyle.American,
                OptionRight.Call, strike, expiry);
        }

        private static readonly Symbol _standardContract = CreateContract("ES", new DateTime(2020, 3, 20));
        private static readonly Symbol _weeklyContract = CreateContract("EW2", new DateTime(2020, 1, 10));
        private static readonly Symbol _mondayWeeklyContract = CreateContract("E1A", new DateTime(2020, 1, 6));
        private static readonly Symbol _endOfMonthContract = CreateContract("EW", new DateTime(2020, 1, 31));
        private static readonly Symbol _unknownRootContract = CreateContract("ZZTOP", new DateTime(2020, 2, 21));

        private static OptionFilterUniverse CreateFilterUniverse(params Symbol[] symbols)
        {
            var canonical = symbols[0].Canonical;
            var config = new SubscriptionDataConfig(typeof(TradeBar), canonical, Resolution.Minute,
                TimeZones.NewYork, TimeZones.NewYork, true, true, true);

            var option = new QuantConnect.Securities.FutureOption.FutureOption(
                canonical,
                MarketHoursDatabase.FromDataFolder().GetExchangeHours(config),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties(SymbolProperties.GetDefault(Currencies.USD)),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache(),
                underlying: null);

            var data = symbols.Select(symbol => new OptionUniverse { Symbol = symbol }).ToList();
            var underlying = new Tick { Value = 3230m, Time = new DateTime(2020, 1, 3) };

            return new OptionFilterUniverse(option, data, underlying);
        }

        private static List<Symbol> AllTestSymbols()
        {
            return new List<Symbol>
            {
                _standardContract, _weeklyContract, _mondayWeeklyContract, _endOfMonthContract, _unknownRootContract
            };
        }

        [Test]
        public void WeeklysOnlySelectsWeeklyAndEndOfMonthRoots()
        {
            var universe = CreateFilterUniverse(AllTestSymbols().ToArray());

            // ApplyTypesFilter is what Option.SetFilter invokes after the user filter function runs
            List<Symbol> filtered = universe.WeeklysOnly().ApplyTypesFilter();

            // weekly (EW2, E1A) and end-of-month (EW) roots are non-standard; the standard root and
            // the registry-unknown root (legacy behavior: unknown = standard) are excluded
            CollectionAssert.AreEquivalent(
                new[] { _weeklyContract, _mondayWeeklyContract, _endOfMonthContract },
                filtered);
        }

        [Test]
        public void StandardsOnlySelectsStandardAndUnknownRoots()
        {
            var universe = CreateFilterUniverse(AllTestSymbols().ToArray());

            List<Symbol> filtered = universe.StandardsOnly().ApplyTypesFilter();

            CollectionAssert.AreEquivalent(new[] { _standardContract, _unknownRootContract }, filtered);
        }

        [Test]
        public void WeeklysOnlyIsEmptyWhenOnlyStandardContractsExist()
        {
            var universe = CreateFilterUniverse(_standardContract, _unknownRootContract);

            List<Symbol> filtered = universe.WeeklysOnly().ApplyTypesFilter();
            Assert.IsEmpty(filtered);
        }

        [Test]
        public void RootsSharingAnExpirationDateClassifyIndependently()
        {
            // the ES quarterly and the EW3 week-3 weekly both expire on the third Friday: the type
            // filter must classify per root, not per expiration date (pre-fork it memoized by date)
            var sharedExpiry = new DateTime(2020, 3, 20);
            var standard = CreateContract("ES", sharedExpiry);
            var weekly = CreateContract("EW3", sharedExpiry);

            List<Symbol> weeklys = CreateFilterUniverse(standard, weekly).WeeklysOnly().ApplyTypesFilter();
            CollectionAssert.AreEquivalent(new[] { weekly }, weeklys);

            List<Symbol> standards = CreateFilterUniverse(standard, weekly).StandardsOnly().ApplyTypesFilter();
            CollectionAssert.AreEquivalent(new[] { standard }, standards);
        }
    }
}
