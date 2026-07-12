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
 *
*/

using System;
using System.Collections.Generic;
using QuantConnect.Logging;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities.FutureOption;
using QuantConnect.Securities.Option;
using static QuantConnect.Extensions;

namespace QuantConnect.Orders.OptionExercise
{
    /// <summary>
    /// Option exercise model for future options with per-root settlement semantics from the
    /// <see cref="FutureOptionsRootRegistry"/> (design B issue 3):
    /// (a) the exercise decision at expiry uses the settlement MARK - the underlying future's
    /// price at the root's settlement mark time (e.g. the 3:00pm CT fixing for ES weeklies, the
    /// 2:00pm CT mark for treasury weeklies) - captured by the <see cref="FutureOption"/> security,
    /// not the underlying's last trade before the delisting event is processed;
    /// (b) a contract is exercised when it is in the money by at least one underlying tick at the
    /// mark, and lapses otherwise (ASSUMED threshold, to be verified against CME rulebook 730);
    /// (c) European-style roots (equity index weeklies/EOM) permit no early exercise; contrarian
    /// exercise instructions are prohibited for those roots and are modeled as a no-op;
    /// (d) delivery is the underlying future position at the strike price, identical to
    /// <see cref="DefaultExerciseModel"/>. For quarterly equity-index options the delivered future
    /// expires the same day: <c>BacktestingBrokerage.ProcessDelistings</c> orders option delistings
    /// before future delistings, so the delivered position is picked up by the future's same-day
    /// cash settlement (design B issue 8c)
    /// </summary>
    public class FutureOptionExerciseModel : DefaultExerciseModel
    {
        private readonly FutureOptionRootDefinition _definition;
        private bool _loggedMarkFallback;

        /// <summary>
        /// Creates a new instance for the given option root definition
        /// </summary>
        /// <param name="definition">The root registry definition carrying the settlement semantics</param>
        public FutureOptionExerciseModel(FutureOptionRootDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            _definition = definition;
        }

        /// <summary>
        /// Future option exercise model taking the root's settlement semantics into account
        /// </summary>
        /// <param name="option">Option we're trading this order</param>
        /// <param name="order">Order to update</param>
        public override IEnumerable<OrderEvent> OptionExercise(Option option, OptionExerciseOrder order)
        {
            var underlying = option.Underlying;
            var utcTime = option.LocalTime.ConvertToUtc(option.Exchange.TimeZone);

            // European style: exercise is only possible at expiration. Early exercise requests are
            // already rejected upstream (QCAlgorithm.ExerciseOption checks Option.Style, and the
            // default assignment model never assigns European contracts before their expiration
            // date), so this is a defensive guard for custom models routing orders here directly
            if (_definition.ExerciseStyle == OptionStyle.European
                && option.LocalTime.Date < option.Symbol.ID.Date.Date)
            {
                Log.Error("FutureOptionExerciseModel.OptionExercise(): " +
                    $"{option.Symbol.Value} is European style and cannot be exercised before its expiration date " +
                    $"{option.Symbol.ID.Date.Date:yyyy-MM-dd}. No exercise events were generated.");
                yield break;
            }

            var markPrice = GetSettlementMark(option);
            var inTheMoney = IsInTheMoneyAtMark(option, markPrice);
            var isAssignment = inTheMoney && option.Holdings.IsShort;

            yield return new OrderEvent(
                order.Id,
                option.Symbol,
                utcTime,
                OrderStatus.Filled,
                GetOrderDirection(order.Quantity),
                0.0m,
                order.Quantity,
                OrderFee.Zero,
                Messages.DefaultExerciseModel.ContractHoldingsAdjustmentFillTag(inTheMoney, isAssignment, option)
            )
            {
                IsAssignment = isAssignment,
                IsInTheMoney = inTheMoney
            };

            if (inTheMoney && option.ExerciseSettlement == SettlementType.PhysicalDelivery)
            {
                var exerciseQuantity = option.GetExerciseQuantity(order.Quantity);

                yield return new OrderEvent(
                    order.Id,
                    underlying.Symbol,
                    utcTime,
                    OrderStatus.Filled,
                    GetOrderDirection(exerciseQuantity),
                    option.StrikePrice,
                    exerciseQuantity,
                    OrderFee.Zero,
                    isAssignment ? Messages.DefaultExerciseModel.OptionAssignment : Messages.DefaultExerciseModel.OptionExercise
                ) { IsInTheMoney = true };
            }
        }

        /// <summary>
        /// Resolves the settlement mark used for the exercise decision: the underlying price
        /// captured at the root's settlement mark time when available, falling back to the
        /// underlying's last close (the legacy behavior) when no mark was captured, e.g. when
        /// the root defines no mark time or no underlying data before the mark was observed
        /// </summary>
        /// <param name="option">The option being exercised</param>
        /// <returns>The underlying price to decide exercise against</returns>
        protected virtual decimal GetSettlementMark(Option option)
        {
            var futureOption = option as FutureOption;
            if (futureOption?.SettlementMark != null)
            {
                return futureOption.SettlementMark.Value;
            }

            if (_definition.SettlementMarkTime != null && !_loggedMarkFallback)
            {
                _loggedMarkFallback = true;
                Log.Trace("FutureOptionExerciseModel.GetSettlementMark(): " +
                    $"no settlement mark captured for {option.Symbol.Value}, " +
                    "falling back to the underlying's last close for the exercise decision.");
            }

            return option.Underlying.Close;
        }

        /// <summary>
        /// Determines whether the contract is exercised at expiry: in the money by at least one
        /// underlying tick at the settlement mark (ASSUMED threshold per design B issue 3,
        /// pending verification against CME rulebook 730)
        /// </summary>
        /// <param name="option">The option being exercised</param>
        /// <param name="markPrice">The settlement mark price of the underlying</param>
        /// <returns>True when the contract exercises, false when it lapses</returns>
        protected virtual bool IsInTheMoneyAtMark(Option option, decimal markPrice)
        {
            var tick = option.Underlying?.SymbolProperties?.MinimumPriceVariation ?? 0m;
            if (tick <= 0m)
            {
                // fall back to the legacy in-the-money definition when the underlying tick is unknown
                return option.IsAutoExercised(markPrice);
            }

            return option.GetIntrinsicValue(markPrice) >= tick;
        }
    }
}
