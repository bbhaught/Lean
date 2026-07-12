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
using System.Reflection;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators;
using QuantConnect.Securities;
using QuantConnect.Securities.FutureOption;
using QuantConnect.Securities.Option;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// P6 downstream hardening tests: live-mode hard block for non-standard cycles, daily-root
    /// delisting warning guard and the Black-76 default price model for future options
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionP6HardeningTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            FutureOptionsRootRegistry.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            FutureOptionsRootRegistry.Reset();
        }

        private static Symbol EsFuture => Symbol.CreateFuture(
            QuantConnect.Securities.Futures.Indices.SP500EMini, Market.CME, new DateTime(2020, 6, 19));

        [TestCase(FutureOptionExpiryCycles.Weekly)]
        [TestCase(FutureOptionExpiryCycles.Daily)]
        [TestCase(FutureOptionExpiryCycles.EndOfMonth)]
        [TestCase(FutureOptionExpiryCycles.All)]
        public void LiveModeAddFutureOptionThrowsForNonStandardCycles(FutureOptionExpiryCycles cycles)
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetLiveMode(true);
            var future = algorithm.AddFuture(QuantConnect.Securities.Futures.Indices.SP500EMini);

            var exception = Assert.Throws<InvalidOperationException>(
                () => algorithm.AddFutureOption(future.Symbol, cycles: cycles));
            StringAssert.Contains("live trading of non-standard future option expiry cycles", exception.Message);
        }

        [Test]
        public void LiveModeAddFutureOptionAllowsStandardCycles()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetLiveMode(true);
            var future = algorithm.AddFuture(QuantConnect.Securities.Futures.Indices.SP500EMini);

            Assert.DoesNotThrow(() => algorithm.AddFutureOption(future.Symbol));
        }

        [Test]
        public void LiveModeAddFutureOptionContractThrowsForWeeklyRoot()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetLiveMode(true);

            var weekly = Symbol.CreateOption(EsFuture, "EW3", Market.CME, OptionStyle.American,
                OptionRight.Call, 3000m, new DateTime(2020, 6, 19));

            var exception = Assert.Throws<InvalidOperationException>(
                () => algorithm.AddFutureOptionContract(weekly));
            StringAssert.Contains("live trading of non-standard future option contracts", exception.Message);
        }

        [Test]
        public void LiveModeAddFutureOptionContractAllowsStandardRoot()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetLiveMode(true);

            var standard = Symbol.CreateOption(EsFuture, Market.CME, OptionStyle.American,
                OptionRight.Call, 3000m, new DateTime(2020, 6, 19));

            Assert.DoesNotThrow(() => algorithm.AddFutureOptionContract(standard));
        }

        [Test]
        public void BacktestingAddFutureOptionAllowsNonStandardCycles()
        {
            var algorithm = new AlgorithmStub();
            var future = algorithm.AddFuture(QuantConnect.Securities.Futures.Indices.SP500EMini);

            Assert.DoesNotThrow(() => algorithm.AddFutureOption(future.Symbol,
                cycles: FutureOptionExpiryCycles.All));
        }

        private static SubscriptionDataConfig CreateConfig(Symbol symbol)
        {
            return new SubscriptionDataConfig(typeof(TradeBar), symbol, Resolution.Minute,
                TimeZones.Chicago, TimeZones.Chicago, true, false, false);
        }

        private static void InstallSyntheticDailyRoot()
        {
            FutureOptionsRootRegistry.SetDefinitionsForTesting(new List<FutureOptionRootDefinition>
            {
                new FutureOptionRootDefinition
                {
                    FutureTicker = "ES",
                    OptionTicker = "ESD",
                    Market = Market.CME,
                    Cycle = FutureOptionExpiryCycles.Daily,
                    ExpiryRuleId = "daily",
                    UnderlyingRuleId = "NextQuarterly",
                    ListedSince = new DateTime(2020, 1, 1),
                    Settlement = FutureOptionSettlement.FuturesSettled,
                    Enabled = true
                }
            });
        }

        /// <summary>
        /// Daily-cycle roots list on (or days before) their expiration date. The delisting warning
        /// is pulled forward to the day before expiry when the subscription starts earlier, so the
        /// algorithm can act during the contract's short life (design A section 5)
        /// </summary>
        [Test]
        public void DailyRootDelistingWarningIsPulledForwardToDayBeforeExpiry()
        {
            InstallSyntheticDailyRoot();

            var expiry = new DateTime(2020, 6, 12);
            var option = Symbol.CreateOption(EsFuture, "ESD", Market.CME, OptionStyle.American,
                OptionRight.Call, 3000m, expiry);

            var eventProvider = new DelistingEventProvider();
            // subscription starts days before expiry (contract listed early)
            eventProvider.Initialize(CreateConfig(option), null, null, new DateTime(2020, 6, 8));

            // no warning two days before expiry
            var events = eventProvider.GetEvents(new NewTradableDateEventArgs(
                expiry.AddDays(-2), null, option, null)).GetEnumerator();
            Assert.IsFalse(events.MoveNext());
            events.Dispose();

            // warning the day before expiry (pulled forward)
            events = eventProvider.GetEvents(new NewTradableDateEventArgs(
                expiry.AddDays(-1), null, option, null)).GetEnumerator();
            Assert.IsTrue(events.MoveNext());
            Assert.AreEqual(DelistingType.Warning, ((Delisting)events.Current).Type);
            Assert.IsFalse(events.MoveNext());
            events.Dispose();

            // delisted the day after expiry, unchanged
            events = eventProvider.GetEvents(new NewTradableDateEventArgs(
                expiry.AddDays(1), null, option, null)).GetEnumerator();
            Assert.IsTrue(events.MoveNext());
            Assert.AreEqual(DelistingType.Delisted, ((Delisting)events.Current).Type);
            events.Dispose();
        }

        /// <summary>
        /// When the subscription starts on the expiration date itself (listing == expiry), the
        /// warning is clamped to the listing date: it must never precede the contract's existence
        /// </summary>
        [Test]
        public void DailyRootDelistingWarningIsClampedToSubscriptionStart()
        {
            InstallSyntheticDailyRoot();

            var expiry = new DateTime(2020, 6, 12);
            var option = Symbol.CreateOption(EsFuture, "ESD", Market.CME, OptionStyle.American,
                OptionRight.Call, 3000m, expiry);

            var eventProvider = new DelistingEventProvider();
            // subscription starts on the expiry date: listing == expiry
            eventProvider.Initialize(CreateConfig(option), null, null, expiry);

            // no warning before the expiry date
            var events = eventProvider.GetEvents(new NewTradableDateEventArgs(
                expiry.AddDays(-1), null, option, null)).GetEnumerator();
            Assert.IsFalse(events.MoveNext());
            events.Dispose();

            // warning on the expiry date itself
            events = eventProvider.GetEvents(new NewTradableDateEventArgs(
                expiry, null, option, null)).GetEnumerator();
            Assert.IsTrue(events.MoveNext());
            Assert.AreEqual(DelistingType.Warning, ((Delisting)events.Current).Type);
            events.Dispose();
        }

        /// <summary>
        /// Non-daily roots keep the upstream behavior byte-identical: warning on the delisting date
        /// </summary>
        [Test]
        public void WeeklyAndStandardRootDelistingWarningIsUnchanged()
        {
            var expiry = new DateTime(2020, 6, 12);
            var weekly = Symbol.CreateOption(EsFuture, "EW2", Market.CME, OptionStyle.American,
                OptionRight.Call, 3000m, expiry);

            var eventProvider = new DelistingEventProvider();
            eventProvider.Initialize(CreateConfig(weekly), null, null, new DateTime(2020, 6, 1));

            var events = eventProvider.GetEvents(new NewTradableDateEventArgs(
                expiry.AddDays(-1), null, weekly, null)).GetEnumerator();
            Assert.IsFalse(events.MoveNext());
            events.Dispose();

            events = eventProvider.GetEvents(new NewTradableDateEventArgs(
                expiry, null, weekly, null)).GetEnumerator();
            Assert.IsTrue(events.MoveNext());
            Assert.AreEqual(DelistingType.Warning, ((Delisting)events.Current).Type);
            events.Dispose();
        }

        /// <summary>
        /// Design B issue 9: the default price model for future options must be Black-76-consistent,
        /// i.e. built on a driftless process (dividend yield estimator coupled to the risk-free
        /// rate), not BSM-on-spot with zero dividend yield
        /// </summary>
        [Test]
        public void DefaultFutureOptionPriceModelUsesRiskFreeRateAsDividendYield()
        {
            var option = Symbol.CreateOption(EsFuture, Market.CME, OptionStyle.American,
                OptionRight.Call, 3000m, new DateTime(2020, 6, 19));

            var model = QLOptionPriceModelProvider.Instance.GetOptionPriceModel(option);
            Assert.IsInstanceOf<QLOptionPriceModel>(model);

            var estimatorField = typeof(QLOptionPriceModel).GetField("_dividendYieldEstimator",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(estimatorField);
            Assert.IsInstanceOf<RiskFreeRateQLDividendYieldEstimator>(estimatorField.GetValue(model));

            // equity options keep the flat zero dividend yield default
            var equityOption = Symbol.CreateOption(Symbols.SPY, Market.USA, OptionStyle.American,
                OptionRight.Call, 300m, new DateTime(2020, 6, 19));
            var equityModel = QLOptionPriceModelProvider.Instance.GetOptionPriceModel(equityOption);
            Assert.IsNotInstanceOf<RiskFreeRateQLDividendYieldEstimator>(
                estimatorField.GetValue(equityModel));
        }

        [Test]
        public void RiskFreeRateDividendYieldEstimatorMirrorsRiskFreeRate()
        {
            var riskFree = new FedRateQLRiskFreeRateEstimator();
            var estimator = new RiskFreeRateQLDividendYieldEstimator(riskFree);

            var expected = Convert.ToDouble(riskFree.Estimate(null, null, null));
            Assert.AreEqual(expected, estimator.Estimate(null, null, null));
        }
    }
}
