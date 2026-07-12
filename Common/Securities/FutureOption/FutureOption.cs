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
using NodaTime;
using QuantConnect.Data;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.Fills;
using QuantConnect.Orders.OptionExercise;
using QuantConnect.Orders.Slippage;
using QuantConnect.Securities.Option;

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Futures Options security
    /// </summary>
    public class FutureOption : Option.Option
    {
        private readonly FutureOptionRootDefinition _rootDefinition;
        private DateTime? _settlementMarkTimeUtc;
        private DateTime _settlementMarkDataTimeUtc;

        /// <summary>
        /// Constructor for the future option security
        /// </summary>
        /// <param name="symbol">Symbol of the future option</param>
        /// <param name="exchangeHours">Exchange hours of the future option</param>
        /// <param name="quoteCurrency">Quoted currency of the future option</param>
        /// <param name="symbolProperties">Symbol properties of the future option</param>
        /// <param name="currencyConverter">Currency converter</param>
        /// <param name="registeredTypes">Provides all data types registered to the algorithm</param>
        /// <param name="securityCache">Cache of security objects</param>
        /// <param name="underlying">Future underlying security</param>
        public FutureOption(Symbol symbol,
            SecurityExchangeHours exchangeHours,
            Cash quoteCurrency,
            OptionSymbolProperties symbolProperties,
            ICurrencyConverter currencyConverter,
            IRegisteredSecurityDataTypesProvider registeredTypes,
            SecurityCache securityCache,
            Security underlying)
            : base(symbol,
                quoteCurrency,
                symbolProperties,
                new OptionExchange(exchangeHours),
                securityCache,
                new OptionPortfolioModel(),
                new FutureOptionFillModel(),
                new InteractiveBrokersFeeModel(),
                NullSlippageModel.Instance,
                new ImmediateSettlementModel(),
                Securities.VolatilityModel.Null,
                null,
                new OptionDataFilter(),
                new SecurityPriceVariationModel(),
                currencyConverter,
                registeredTypes,
                underlying,
                null
        )
        {
            BuyingPowerModel = new FuturesOptionsMarginModel(0, this);

            if (FutureOptionsRootRegistry.TryGetDefinition(symbol.ID.Symbol, symbol.ID.Market, out var definition))
            {
                _rootDefinition = definition;
                if (definition.HasSettlementSemantics)
                {
                    // roots carrying settlement metadata (exercise style, settlement mark time) get the
                    // future-option exercise model, which decides exercise at the settlement mark instead
                    // of the underlying's last close at delisting time. Roots without metadata keep the
                    // legacy DefaultExerciseModel behavior unchanged
                    OptionExerciseModel = new FutureOptionExerciseModel(definition);
                }
            }
        }

        /// <summary>
        /// Gets the option exercise style. The root registry is authoritative: weekly and
        /// end-of-month equity-index roots are European per CME contract specs even though the
        /// security identifier keeps <see cref="OptionStyle.American"/> for data-model consistency
        /// </summary>
        public override OptionStyle Style => _rootDefinition?.ExerciseStyle ?? base.Style;

        /// <summary>
        /// The underlying future price captured at (or at the last data point just before) this
        /// contract's settlement mark time on its expiration date, per the root registry's
        /// settlement metadata. Null when the root defines no settlement mark time or no
        /// underlying data at/before the mark has been observed yet
        /// </summary>
        public decimal? SettlementMark { get; private set; }

        /// <summary>
        /// The settlement mark time of this contract in UTC, resolved from the root registry's
        /// exchange-local <see cref="FutureOptionRootDefinition.SettlementMarkTime"/> on the
        /// contract's expiration date. Null when the root defines no settlement mark time
        /// </summary>
        public DateTime? SettlementMarkTimeUtc
        {
            get
            {
                if (_settlementMarkTimeUtc == null && _rootDefinition?.SettlementMarkTime != null && !Symbol.IsCanonical())
                {
                    var timeZone = Exchange.TimeZone;
                    if (!string.IsNullOrEmpty(_rootDefinition.SettlementTimeZone))
                    {
                        timeZone = DateTimeZoneProviders.Tzdb[_rootDefinition.SettlementTimeZone];
                    }

                    var markLocal = Symbol.ID.Date.Date.Add(_rootDefinition.SettlementMarkTime.Value);
                    _settlementMarkTimeUtc = markLocal.ConvertToUtc(timeZone);
                }

                return _settlementMarkTimeUtc;
            }
        }

        /// <summary>
        /// Consumes market price data and tracks the underlying settlement mark
        /// </summary>
        /// <param name="data">Market price data</param>
        /// <remarks>
        /// The exercise decision at expiry must use the underlying future's price at the root's
        /// settlement mark time (e.g. the 3:00pm CT fixing for ES weeklies), not the last trade
        /// before the delisting event is processed. There is no engine callback on the underlying
        /// security's updates, so the mark is sampled here on every option data update: the latest
        /// underlying data point whose end time does not pass the mark time wins, converging on the
        /// bar at or just before the mark regardless of intra-slice update ordering
        /// </remarks>
        protected override void UpdateConsumersMarketPrice(BaseData data)
        {
            base.UpdateConsumersMarketPrice(data);
            TryCaptureSettlementMark();
        }

        private void TryCaptureSettlementMark()
        {
            var markTimeUtc = SettlementMarkTimeUtc;
            if (markTimeUtc == null || Underlying == null)
            {
                return;
            }

            var lastData = Underlying.GetLastData();
            if (lastData == null || lastData.Price == 0m)
            {
                return;
            }

            var dataTimeUtc = lastData.EndTime.ConvertToUtc(Underlying.Exchange.TimeZone);
            if (dataTimeUtc <= markTimeUtc.Value && (SettlementMark == null || dataTimeUtc >= _settlementMarkDataTimeUtc))
            {
                _settlementMarkDataTimeUtc = dataTimeUtc;
                SettlementMark = lastData.Price;
            }
        }

        /// <summary>
        /// Returns the securities symbol
        /// </summary>
        public static implicit operator Symbol(FutureOption security) => security.Symbol;
    }
}
