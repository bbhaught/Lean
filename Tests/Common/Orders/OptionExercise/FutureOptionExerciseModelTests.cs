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
using QuantConnect.Brokerages.Backtesting;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.FutureOption;
using QuantConnect.Securities.Option;
using QuantConnect.Tests.Engine;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Common.Orders.OptionExercise
{
    /// <summary>
    /// Integration-style tests for the fop-fork FutureOptionExerciseModel: settlement-mark
    /// exercise decisions, European no-early-exercise, expiry-day event ordering (design B issue 3)
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionExerciseModelTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // ensure the data-folder registry (with P6 settlement metadata) is loaded, other
            // fixtures may have installed synthetic definitions; also reset the MHDB, some
            // upstream fixtures mutate entries in-memory (see DECISIONS.md D5)
            FutureOptionsRootRegistry.Reset();
            MarketHoursDatabase.Reset();
        }

        private static (AlgorithmStub algorithm, BacktestingTransactionHandler handler, BacktestingBrokerage brokerage,
            List<OrderEvent> orderEvents) CreateAlgorithm()
        {
            var algorithm = new AlgorithmStub(new MockDataFeed());
            algorithm.SetFinishedWarmingUp();
            algorithm.SetStatus(AlgorithmStatus.Running);
            var handler = new BacktestingTransactionHandler();
            var brokerage = new BacktestingBrokerage(algorithm);
            algorithm.Transactions.SetOrderProcessor(handler);
            handler.Initialize(algorithm, brokerage, new TestResultHandler());

            var orderEvents = new List<OrderEvent>();
            handler.NewOrderEvent += (sender, orderEvent) => orderEvents.Add(orderEvent);

            return (algorithm, handler, brokerage, orderEvents);
        }

        private static Option AddContracts(AlgorithmStub algorithm, Symbol optionSymbol, Symbol futureSymbol)
        {
            var optionSecurity = (Option)algorithm.AddFutureOptionContract(optionSymbol);
            optionSecurity.Underlying = algorithm.AddFutureContract(futureSymbol);
            return optionSecurity;
        }

        /// <summary>
        /// Sets a market price at the given Chicago wall-clock time, converting to the security's
        /// exchange time zone (LEAN keeps e.g. CME equity index futures in New York time)
        /// </summary>
        private static void SetPriceAtChicagoTime(Security security, DateTime chicagoTime, decimal value)
        {
            var localTime = chicagoTime.ConvertToUtc(TimeZones.Chicago).ConvertFromUtc(security.Exchange.TimeZone);
            security.SetMarketPrice(new Tick { Value = value, Time = localTime });
        }

        /// <summary>
        /// Design B issue 3 scenario (i): a short ES weekly call, in the money at the 3:00pm CT
        /// settlement mark, is assigned at expiry: a short future position at the strike appears,
        /// margin is charged on the delivered future, and cash is unchanged (exercise carries no fee)
        /// </summary>
        [Test]
        public void ShortEsWeeklyCallAssignedItmAtMarkDeliversShortFutureAtStrike()
        {
            var (algorithm, handler, brokerage, orderEvents) = CreateAlgorithm();
            try
            {
                var future = Symbol.CreateFuture(QuantConnect.Securities.Futures.Indices.SP500EMini,
                    Market.CME, new DateTime(2020, 6, 19));
                // week-2 Friday weekly, 3 days to expiry at entry
                var expiry = new DateTime(2020, 6, 12);
                var option = Symbol.CreateOption(future, "EW2", Market.CME, OptionStyle.American,
                    OptionRight.Call, 3200m, expiry);

                var optionSecurity = AddContracts(algorithm, option, future);
                var futureSecurity = optionSecurity.Underlying;

                Assert.IsInstanceOf<QuantConnect.Orders.OptionExercise.FutureOptionExerciseModel>(
                    optionSecurity.OptionExerciseModel);

                // entry, 3 days before expiry
                algorithm.SetDateTime(new DateTime(2020, 6, 9, 15, 0, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 9, 10, 0, 0), 3100m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 9, 10, 0, 0), 25m);
                optionSecurity.Holdings.SetHoldings(25m, -1);

                var cashBefore = algorithm.Portfolio.Cash;
                var marginBefore = algorithm.Portfolio.TotalMarginUsed;
                Assert.Greater(marginBefore, 0m);

                // expiry day: underlying at 3250 just before the 3:00pm CT mark => ITM by 50 points
                algorithm.SetDateTime(new DateTime(2020, 6, 12, 14, 59, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 12, 14, 59, 0), 3250m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 12, 14, 59, 0), 50m);
                Assert.AreEqual(3250m, ((FutureOption)optionSecurity).SettlementMark);

                // after the mark the future rallies further; must not alter the captured mark
                algorithm.SetDateTime(new DateTime(2020, 6, 12, 16, 0, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 12, 16, 0, 0), 3300m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 12, 16, 0, 0), 100m);
                Assert.AreEqual(3250m, ((FutureOption)optionSecurity).SettlementMark);

                // the option delisting is processed the day after expiry
                algorithm.SetDateTime(new DateTime(2020, 6, 13, 0, 0, 0).ConvertToUtc(TimeZones.Chicago));
                var delistings = new Delistings
                {
                    { option, new Delisting(option, expiry, 100m, DelistingType.Delisted) }
                };
                brokerage.ProcessDelistings(delistings);

                // assignment: option position closed, short future at the strike delivered
                Assert.AreEqual(0m, optionSecurity.Holdings.Quantity);
                Assert.AreEqual(-1m, futureSecurity.Holdings.Quantity);
                Assert.AreEqual(3200m, futureSecurity.Holdings.AveragePrice);

                var assignmentEvent = orderEvents.Single(orderEvent => orderEvent.IsAssignment);
                Assert.AreEqual(option, assignmentEvent.Symbol);
                Assert.IsTrue(assignmentEvent.IsInTheMoney);

                // no cash moved: exercise order events carry zero fees, delivery is at the strike
                Assert.AreEqual(cashBefore, algorithm.Portfolio.Cash);

                // margin is now charged on the delivered short future position
                var marginAfter = algorithm.Portfolio.TotalMarginUsed;
                Assert.Greater(marginAfter, 0m);
                Assert.AreNotEqual(marginBefore, marginAfter);
            }
            finally
            {
                handler.Exit();
                brokerage.Dispose();
            }
        }

        /// <summary>
        /// Design B issue 3 scenario (ii) and issue 8c event ordering: a long ES quarterly call in
        /// the money at the 8:30am CT SOQ mark is exercised into the expiring June future, which is
        /// cash-settled by its own delisting the same day: future delivery strictly precedes the
        /// future's liquidation and the final equity equals the intrinsic value at settlement
        /// </summary>
        [Test]
        public void LongEsQuarterlyCallExercisesIntoSameDayExpiringFutureAndSettlesToIntrinsic()
        {
            var (algorithm, handler, brokerage, orderEvents) = CreateAlgorithm();
            try
            {
                var expiry = new DateTime(2020, 6, 19);
                var future = Symbol.CreateFuture(QuantConnect.Securities.Futures.Indices.SP500EMini,
                    Market.CME, expiry);
                // quarterly option on the expiring June future, standard root "ES"
                var option = Symbol.CreateOption(future, Market.CME, OptionStyle.American,
                    OptionRight.Call, 3000m, expiry);

                var optionSecurity = AddContracts(algorithm, option, future);
                var futureSecurity = optionSecurity.Underlying;
                var multiplier = optionSecurity.SymbolProperties.ContractMultiplier;

                // 8:29am CT on expiry day: the SOQ proxy bar just before the 8:30am mark
                algorithm.SetDateTime(new DateTime(2020, 6, 19, 8, 29, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 19, 8, 29, 0), 3100m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 19, 8, 29, 0), 100m);
                optionSecurity.Holdings.SetHoldings(100m, 1);
                Assert.AreEqual(3100m, ((FutureOption)optionSecurity).SettlementMark);

                // the future drifts lower into its final settlement print
                algorithm.SetDateTime(new DateTime(2020, 6, 19, 16, 0, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 19, 16, 0, 0), 3050m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 19, 16, 0, 0), 50m);

                var cashBefore = algorithm.Portfolio.Cash;

                // option and its underlying future delist the same day; the brokerage must process
                // the option first (delivery) and the future second (same-day cash settlement)
                algorithm.SetDateTime(new DateTime(2020, 6, 20, 0, 0, 0).ConvertToUtc(TimeZones.Chicago));
                var delistings = new Delistings
                {
                    { future, new Delisting(future, expiry, 3050m, DelistingType.Delisted) },
                    { option, new Delisting(option, expiry, 50m, DelistingType.Delisted) }
                };
                brokerage.ProcessDelistings(delistings);

                // everything is flat: option exercised, delivered future liquidated the same day
                Assert.AreEqual(0m, optionSecurity.Holdings.Quantity);
                Assert.AreEqual(0m, futureSecurity.Holdings.Quantity);

                // event ordering invariant (design B issue 8c): the option exercise fill and the
                // future delivery fill must precede the delivered future's liquidation fill
                var optionFillIndex = orderEvents.FindIndex(orderEvent =>
                    orderEvent.Symbol == option && orderEvent.Status == OrderStatus.Filled);
                var deliveryIndex = orderEvents.FindIndex(orderEvent =>
                    orderEvent.Symbol == future && orderEvent.FillPrice == 3000m);
                var liquidationIndex = orderEvents.FindIndex(orderEvent =>
                    orderEvent.Symbol == future && orderEvent.FillPrice == 3050m);
                Assert.GreaterOrEqual(optionFillIndex, 0);
                Assert.GreaterOrEqual(deliveryIndex, 0);
                Assert.GreaterOrEqual(liquidationIndex, 0);
                Assert.Less(optionFillIndex, liquidationIndex);
                Assert.Less(deliveryIndex, liquidationIndex);

                // final equity: the delivered future entered at the 3000 strike and settled at 3050,
                // so the portfolio gains exactly the intrinsic value at settlement (no fees on
                // exercise/delisting fills)
                var intrinsic = (3050m - 3000m) * multiplier;
                Assert.AreEqual(cashBefore + intrinsic, algorithm.Portfolio.TotalPortfolioValue);
            }
            finally
            {
                handler.Exit();
                brokerage.Dispose();
            }
        }

        /// <summary>
        /// Design B issue 3 scenario (iii): a ZN weekly that is out of the money by one tick at the
        /// 2:00pm CT settlement mark lapses, even though the underlying trades in the money by the
        /// 4:00pm close. The legacy behavior (deciding on the underlying's last close) would have
        /// exercised it
        /// </summary>
        [Test]
        public void ZnWeeklyOtmAtTwoPmMarkLapsesEvenIfItmAtTheClose()
        {
            var (algorithm, handler, brokerage, orderEvents) = CreateAlgorithm();
            try
            {
                var future = Symbol.CreateFuture(QuantConnect.Securities.Futures.Financials.Y10TreasuryNote,
                    Market.CBOT, new DateTime(2020, 9, 21));
                var expiry = new DateTime(2020, 6, 12);
                var option = Symbol.CreateOption(future, "ZN1", Market.CBOT, OptionStyle.American,
                    OptionRight.Call, 139m, expiry);

                var optionSecurity = AddContracts(algorithm, option, future);
                var futureSecurity = optionSecurity.Underlying;
                var tick = futureSecurity.SymbolProperties.MinimumPriceVariation;
                Assert.Greater(tick, 0m);

                // 1:59pm CT on expiry day: one tick out of the money at the mark
                algorithm.SetDateTime(new DateTime(2020, 6, 12, 13, 59, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 12, 13, 59, 0), 139m - tick);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 12, 13, 59, 0), 0.015625m);
                optionSecurity.Holdings.SetHoldings(0.015625m, 1);
                Assert.AreEqual(139m - tick, ((FutureOption)optionSecurity).SettlementMark);

                // in the money by the 4:00pm close - must be ignored, the 2:00pm mark decides
                algorithm.SetDateTime(new DateTime(2020, 6, 12, 16, 0, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 12, 16, 0, 0), 139.5m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 12, 16, 0, 0), 0.5m);
                Assert.AreEqual(139m - tick, ((FutureOption)optionSecurity).SettlementMark);

                var cashBefore = algorithm.Portfolio.Cash;

                algorithm.SetDateTime(new DateTime(2020, 6, 13, 0, 0, 0).ConvertToUtc(TimeZones.Chicago));
                var delistings = new Delistings
                {
                    { option, new Delisting(option, expiry, 0.5m, DelistingType.Delisted) }
                };
                brokerage.ProcessDelistings(delistings);

                // the contract lapses: position cleared, no future delivered, no cash moved
                Assert.AreEqual(0m, optionSecurity.Holdings.Quantity);
                Assert.AreEqual(0m, futureSecurity.Holdings.Quantity);
                Assert.AreEqual(cashBefore, algorithm.Portfolio.Cash);
                Assert.IsFalse(orderEvents.Any(orderEvent => orderEvent.IsInTheMoney));
                Assert.IsFalse(orderEvents.Any(orderEvent => orderEvent.Symbol == future));
            }
            finally
            {
                handler.Exit();
                brokerage.Dispose();
            }
        }

        /// <summary>
        /// A ZN weekly in the money by one tick at the mark exercises: the one-tick threshold is
        /// the boundary between lapse and exercise
        /// </summary>
        [Test]
        public void ZnWeeklyItmByOneTickAtMarkExercises()
        {
            var (algorithm, handler, brokerage, _) = CreateAlgorithm();
            try
            {
                var future = Symbol.CreateFuture(QuantConnect.Securities.Futures.Financials.Y10TreasuryNote,
                    Market.CBOT, new DateTime(2020, 9, 21));
                var expiry = new DateTime(2020, 6, 12);
                var option = Symbol.CreateOption(future, "ZN1", Market.CBOT, OptionStyle.American,
                    OptionRight.Call, 139m, expiry);

                var optionSecurity = AddContracts(algorithm, option, future);
                var futureSecurity = optionSecurity.Underlying;
                var tick = futureSecurity.SymbolProperties.MinimumPriceVariation;

                algorithm.SetDateTime(new DateTime(2020, 6, 12, 13, 59, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 12, 13, 59, 0), 139m + tick);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 12, 13, 59, 0), tick);
                optionSecurity.Holdings.SetHoldings(tick, 1);

                algorithm.SetDateTime(new DateTime(2020, 6, 13, 0, 0, 0).ConvertToUtc(TimeZones.Chicago));
                var delistings = new Delistings
                {
                    { option, new Delisting(option, expiry, tick, DelistingType.Delisted) }
                };
                brokerage.ProcessDelistings(delistings);

                Assert.AreEqual(0m, optionSecurity.Holdings.Quantity);
                Assert.AreEqual(1m, futureSecurity.Holdings.Quantity);
                Assert.AreEqual(139m, futureSecurity.Holdings.AveragePrice);
            }
            finally
            {
                handler.Exit();
                brokerage.Dispose();
            }
        }

        /// <summary>
        /// European-style weekly roots (ES weeklies per registry) cannot be exercised before their
        /// expiration date: the exercise request is rejected with
        /// <see cref="OrderResponseErrorCode.EuropeanOptionNotExpiredOnExercise"/>
        /// </summary>
        [Test]
        public void EuropeanEsWeeklyCannotBeExercisedEarly()
        {
            var (algorithm, handler, brokerage, _) = CreateAlgorithm();
            try
            {
                var future = Symbol.CreateFuture(QuantConnect.Securities.Futures.Indices.SP500EMini,
                    Market.CME, new DateTime(2020, 6, 19));
                var expiry = new DateTime(2020, 6, 12);
                var option = Symbol.CreateOption(future, "EW2", Market.CME, OptionStyle.American,
                    OptionRight.Call, 3000m, expiry);

                var optionSecurity = AddContracts(algorithm, option, future);
                var futureSecurity = optionSecurity.Underlying;

                // the registry declares ES weeklies European even though the SID stays American
                Assert.AreEqual(OptionStyle.American, option.ID.OptionStyle);
                Assert.AreEqual(OptionStyle.European, optionSecurity.Style);

                // deep in the money, days before expiry
                algorithm.SetDateTime(new DateTime(2020, 6, 9, 10, 0, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 9, 10, 0, 0), 3200m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 9, 10, 0, 0), 200m);
                optionSecurity.Holdings.SetHoldings(200m, 1);

                var ticket = algorithm.ExerciseOption(option, 1);
                Assert.AreEqual(OrderStatus.Invalid, ticket.Status);
                Assert.AreEqual(OrderResponseErrorCode.EuropeanOptionNotExpiredOnExercise,
                    ticket.SubmitRequest.Response.ErrorCode);

                // the holdings are untouched
                Assert.AreEqual(1m, optionSecurity.Holdings.Quantity);
                Assert.AreEqual(0m, futureSecurity.Holdings.Quantity);
            }
            finally
            {
                handler.Exit();
                brokerage.Dispose();
            }
        }

        /// <summary>
        /// The default assignment model must never simulate early assignment of a European-style
        /// future option root before its expiration date, even deep in the money
        /// </summary>
        [Test]
        public void EuropeanEsWeeklyShortIsNotEarlyAssignedBySimulation()
        {
            var (algorithm, handler, brokerage, _) = CreateAlgorithm();
            try
            {
                var future = Symbol.CreateFuture(QuantConnect.Securities.Futures.Indices.SP500EMini,
                    Market.CME, new DateTime(2020, 6, 19));
                var expiry = new DateTime(2020, 6, 12);
                var option = Symbol.CreateOption(future, "EW2", Market.CME, OptionStyle.American,
                    OptionRight.Call, 2500m, expiry);

                var optionSecurity = AddContracts(algorithm, option, future);
                var futureSecurity = optionSecurity.Underlying;

                // two days before expiry, deep ITM with wide arbitrage, short position
                algorithm.SetDateTime(new DateTime(2020, 6, 10, 10, 0, 0).ConvertToUtc(TimeZones.Chicago));
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 10, 10, 0, 0), 3200m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 10, 10, 0, 0), 700m);
                optionSecurity.Holdings.SetHoldings(700m, -1);

                var result = new DefaultOptionAssignmentModel().GetAssignment(
                    new OptionAssignmentParameters(optionSecurity));
                Assert.AreEqual(OptionAssignmentResult.Null, result);
            }
            finally
            {
                handler.Exit();
                brokerage.Dispose();
            }
        }

        /// <summary>
        /// The settlement mark is captured from the latest underlying data point at or before the
        /// mark time and later data never overwrites it
        /// </summary>
        [Test]
        public void SettlementMarkCaptureConvergesOnBarAtTheMark()
        {
            var (algorithm, handler, brokerage, _) = CreateAlgorithm();
            try
            {
                var future = Symbol.CreateFuture(QuantConnect.Securities.Futures.Indices.SP500EMini,
                    Market.CME, new DateTime(2020, 6, 19));
                var expiry = new DateTime(2020, 6, 12);
                var option = Symbol.CreateOption(future, "EW2", Market.CME, OptionStyle.American,
                    OptionRight.Call, 3000m, expiry);

                var optionSecurity = (FutureOption)AddContracts(algorithm, option, future);
                var futureSecurity = optionSecurity.Underlying;

                algorithm.SetDateTime(new DateTime(2020, 6, 12, 10, 0, 0).ConvertToUtc(TimeZones.Chicago));

                // successive samples before the 3:00pm CT mark: latest one wins
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 12, 10, 0, 0), 3010m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 12, 10, 0, 0), 15m);
                Assert.AreEqual(3010m, optionSecurity.SettlementMark);

                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 12, 15, 0, 0), 3020m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 12, 15, 0, 0), 22m);
                Assert.AreEqual(3020m, optionSecurity.SettlementMark);

                // after the mark: ignored
                SetPriceAtChicagoTime(futureSecurity, new DateTime(2020, 6, 12, 15, 1, 0), 3100m);
                SetPriceAtChicagoTime(optionSecurity, new DateTime(2020, 6, 12, 15, 1, 0), 100m);
                Assert.AreEqual(3020m, optionSecurity.SettlementMark);

                Assert.AreEqual(new DateTime(2020, 6, 12, 15, 0, 0).ConvertToUtc(TimeZones.Chicago),
                    optionSecurity.SettlementMarkTimeUtc);
            }
            finally
            {
                handler.Exit();
                brokerage.Dispose();
            }
        }

        /// <summary>
        /// Roots without settlement metadata (legacy standard roots) keep the DefaultExerciseModel:
        /// the fork changes nothing for them
        /// </summary>
        [Test]
        public void LegacyRootsWithoutSettlementMetadataKeepDefaultExerciseModel()
        {
            var (algorithm, handler, brokerage, _) = CreateAlgorithm();
            try
            {
                var future = Symbol.CreateFuture(QuantConnect.Securities.Futures.Metals.Gold,
                    Market.COMEX, new DateTime(2020, 6, 26));
                // standard gold root OG carries no P6 settlement metadata
                var option = Symbol.CreateOption(future, Market.COMEX, OptionStyle.American,
                    OptionRight.Call, 1700m, new DateTime(2020, 5, 26));

                var optionSecurity = AddContracts(algorithm, option, future);

                Assert.IsNotInstanceOf<QuantConnect.Orders.OptionExercise.FutureOptionExerciseModel>(
                    optionSecurity.OptionExerciseModel);
                Assert.IsNull(((FutureOption)optionSecurity).SettlementMarkTimeUtc);
                Assert.AreEqual(OptionStyle.American, optionSecurity.Style);
            }
            finally
            {
                handler.Exit();
                brokerage.Dispose();
            }
        }
    }
}
