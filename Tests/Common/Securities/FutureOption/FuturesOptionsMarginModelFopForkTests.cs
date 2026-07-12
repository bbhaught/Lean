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
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;
using QuantConnect.Securities.Option;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// Fop-fork margin tests (design B issue 4): long future options are premium-only and short
    /// positions carry a days-to-expiry margin ramp. Covered-pair (future, FOP) offsets require
    /// position-group buying power models and are deferred; shorts are margined naked
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FuturesOptionsMarginModelFopForkTests
    {
        private static QuantConnect.Securities.FutureOption.FutureOption CreateFutureOption(
            DateTime expiry, decimal strike, out Future future)
        {
            var tz = TimeZones.NewYork;
            var futureSymbol = Symbol.CreateFuture(QuantConnect.Securities.Futures.Indices.SP500EMini,
                Market.CME, expiry);
            var optionSymbol = Symbol.CreateOption(futureSymbol, Market.CME, OptionStyle.American,
                OptionRight.Call, strike, expiry);

            future = new Future(
                SecurityExchangeHours.AlwaysOpen(tz),
                new SubscriptionDataConfig(typeof(TradeBar), futureSymbol, Resolution.Minute, tz, tz, true, false, false),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties(SymbolProperties.GetDefault(Currencies.USD)),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null
            );
            return new QuantConnect.Securities.FutureOption.FutureOption(optionSymbol,
                SecurityExchangeHours.AlwaysOpen(tz),
                new Cash(Currencies.USD, 0, 1m),
                new OptionSymbolProperties(SymbolProperties.GetDefault(Currencies.USD)),
                ErrorCurrencyConverter.Instance,
                RegisteredSecurityDataTypesProvider.Null,
                new SecurityCache(),
                future
            );
        }

        private static void SetLocalTime(Security security, DateTime utcTime)
        {
            var timeKeeper = new TimeKeeper(utcTime, security.Exchange.TimeZone);
            security.SetLocalTimeKeeper(timeKeeper.GetLocalTimeKeeper(security.Exchange.TimeZone));
        }

        [TestCase(60, 1.0)]
        [TestCase(30, 1.0)]
        [TestCase(1, 1.5)]
        [TestCase(0, 1.5)]
        public void ShortExpiryRampMultiplierBoundaries(int daysToExpiry, double expected)
        {
            var expiry = new DateTime(2021, 3, 19);
            var option = CreateFutureOption(expiry, 4000m, out _);
            SetLocalTime(option, expiry.AddDays(-daysToExpiry).ConvertToUtc(option.Exchange.TimeZone));

            Assert.AreEqual((decimal)expected, FuturesOptionsMarginModel.GetShortExpiryRampMultiplier(option));
        }

        [Test]
        public void ShortExpiryRampMultiplierRisesMonotonically()
        {
            var expiry = new DateTime(2021, 3, 19);
            var previous = 0m;
            for (var daysToExpiry = 30; daysToExpiry >= 1; daysToExpiry--)
            {
                var option = CreateFutureOption(expiry, 4000m, out _);
                SetLocalTime(option, expiry.AddDays(-daysToExpiry).ConvertToUtc(option.Exchange.TimeZone));
                var multiplier = FuturesOptionsMarginModel.GetShortExpiryRampMultiplier(option);

                Assert.GreaterOrEqual(multiplier, previous);
                Assert.GreaterOrEqual(multiplier, 1m);
                Assert.LessOrEqual(multiplier, FuturesOptionsMarginModel.ShortExpiryRampMaxMultiplier);
                previous = multiplier;
            }
        }

        [Test]
        public void OneDteShortRequirementIsRampTimesThirtyDteRequirement()
        {
            var expiry = new DateTime(2021, 3, 19);
            const decimal underlyingRequirement = 15632m;

            var thirtyDte = CreateFutureOption(expiry, 4200m, out var thirtyDteFuture);
            SetLocalTime(thirtyDte, expiry.AddDays(-30).ConvertToUtc(thirtyDte.Exchange.TimeZone));
            SetLocalTime(thirtyDteFuture, expiry.AddDays(-30).ConvertToUtc(thirtyDteFuture.Exchange.TimeZone));
            thirtyDte.Underlying.SetMarketPrice(new Tick { Value = 4172m, Time = new DateTime(2021, 2, 17) });

            var oneDte = CreateFutureOption(expiry, 4200m, out var oneDteFuture);
            SetLocalTime(oneDte, expiry.AddDays(-1).ConvertToUtc(oneDte.Exchange.TimeZone));
            SetLocalTime(oneDteFuture, expiry.AddDays(-1).ConvertToUtc(oneDteFuture.Exchange.TimeZone));
            oneDte.Underlying.SetMarketPrice(new Tick { Value = 4172m, Time = new DateTime(2021, 3, 18) });

            var thirtyDteMargin = FuturesOptionsMarginModel.GetMarginRequirement(
                thirtyDte, underlyingRequirement, PositionSide.Short);
            var oneDteMargin = FuturesOptionsMarginModel.GetMarginRequirement(
                oneDte, underlyingRequirement, PositionSide.Short);

            Assert.Greater(thirtyDteMargin, 0);
            // identical option and underlying price, only time to expiry differs: the 1-DTE short
            // requirement is the 30-DTE requirement scaled by the maximum ramp multiplier
            Assert.AreEqual(
                (double)(FuturesOptionsMarginModel.ShortExpiryRampMaxMultiplier * thirtyDteMargin),
                oneDteMargin, 2.0);
        }

        [Test]
        public void LongPositionsArePremiumOnly()
        {
            var expiry = new DateTime(2021, 3, 19);
            var option = CreateFutureOption(expiry, 4200m, out _);
            option.Underlying.SetMarketPrice(new Tick { Value = 4172m, Time = new DateTime(2021, 2, 17) });
            option.SetMarketPrice(new Tick { Value = 80m, Time = new DateTime(2021, 2, 17) });

            var model = new FuturesOptionsMarginModel(futureOption: option);

            // maintenance: zero for longs
            option.Holdings.SetHoldings(80m, 2);
            Assert.AreEqual(0m, model.GetMaintenanceMargin(
                MaintenanceMarginParameters.ForQuantityAtCurrentPrice(option, 2)).Value);

            // initial: the premium only
            var initial = (OptionInitialMargin)model.GetInitialMarginRequirement(
                new InitialMarginParameters(option, 2));
            Assert.AreEqual(0m, initial.ValueWithoutPremium);
            Assert.AreEqual(80m * 2 * option.SymbolProperties.ContractMultiplier, initial.Premium);
            Assert.AreEqual(initial.Premium, initial.Value);
        }

        [Test]
        public void ShortPositionsStillCarryMargin()
        {
            var expiry = new DateTime(2021, 3, 19);
            var option = CreateFutureOption(expiry, 4200m, out _);
            option.Underlying.SetMarketPrice(new Tick { Value = 4172m, Time = new DateTime(2021, 2, 17) });
            option.SetMarketPrice(new Tick { Value = 80m, Time = new DateTime(2021, 2, 17) });

            var model = new FuturesOptionsMarginModel(futureOption: option);

            option.Holdings.SetHoldings(80m, -2);
            Assert.AreNotEqual(0m, model.GetMaintenanceMargin(
                MaintenanceMarginParameters.ForQuantityAtCurrentPrice(option, -2)).Value);
        }
    }
}
