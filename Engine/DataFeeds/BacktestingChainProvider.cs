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
using QuantConnect.Util;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Data;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Base backtesting cache provider which will source symbols from local zip files
    /// </summary>
    public abstract class BacktestingChainProvider
    {
        /// <summary>
        /// The map file provider instance to use
        /// </summary>
        protected IMapFileProvider MapFileProvider { get; private set; }

        /// <summary>
        /// The history provider instance to use
        /// </summary>
        protected IHistoryProvider HistoryProvider { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="BacktestingChainProvider"/> class
        /// </summary>
        protected BacktestingChainProvider()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BacktestingChainProvider"/> class
        /// </summary>
        /// <param name="parameters">The initialization parameters</param>
        // TODO: This should be in the chain provider interfaces.
        // They might be even be unified in a single interface (futures and options chains providers)
        public void Initialize(ChainProviderInitializeParameters parameters)
        {
            HistoryProvider = parameters.HistoryProvider;
            MapFileProvider = parameters.MapFileProvider;
        }

        /// <summary>
        /// Get the contract symbols associated with the given canonical symbol and date
        /// </summary>
        /// <param name="canonicalSymbol">The canonical symbol</param>
        /// <param name="date">The date to search for</param>
        protected IEnumerable<Symbol> GetSymbols(Symbol canonicalSymbol, DateTime date)
        {
            return GetSymbols(canonicalSymbol, date, allowStaleUniverseFallback: true);
        }

        /// <summary>
        /// Get the contract symbols associated with the given canonical symbol and date
        /// </summary>
        /// <param name="canonicalSymbol">The canonical symbol</param>
        /// <param name="date">The date to search for</param>
        /// <param name="allowStaleUniverseFallback">True to allow serving the latest universe file within the
        /// last 3 trading days when the requested date's universe file is unavailable. Chains that change
        /// composition every day, like daily-expiry future option roots, must pass false: a stale chain is a
        /// different instrument set for them, so a missing universe file yields an empty chain instead</param>
        protected IEnumerable<Symbol> GetSymbols(Symbol canonicalSymbol, DateTime date, bool allowStaleUniverseFallback)
        {
            var marketHoursDataBase = MarketHoursDatabase.FromDataFolder();
            var universeType = canonicalSymbol.SecurityType.IsOption() ? typeof(OptionUniverse) : typeof(FutureUniverse);
            // Use this GetEntry extension method since it's data type dependent, so we get the correct entry for the option universe
            var marketHoursEntry = marketHoursDataBase.GetEntry(canonicalSymbol, new[] { universeType });

            // We will add a safety measure in case the universe file for the current time is not available:
            // we will use the latest available universe file within the last 3 trading dates.
            // This is useful in cases like live trading when the algorithm is deployed at a time of day when
            // the universe file is not available yet.
            var maxPeriods = allowStaleUniverseFallback ? 3 : 1;
            var history = (List<Slice>)null;
            var periods = 1;
            while ((history == null || history.Count == 0) && periods <= maxPeriods)
            {
                var startDate = Time.GetStartTimeForTradeBars(marketHoursEntry.ExchangeHours, date, Time.OneDay, periods++,
                    extendedMarketHours: false, marketHoursEntry.DataTimeZone);
                var request = new HistoryRequest(
                    startDate.ConvertToUtc(marketHoursEntry.ExchangeHours.TimeZone),
                    date.ConvertToUtc(marketHoursEntry.ExchangeHours.TimeZone),
                    universeType,
                    canonicalSymbol,
                    Resolution.Daily,
                    marketHoursEntry.ExchangeHours,
                    marketHoursEntry.DataTimeZone,
                    null,
                    false,
                    false,
                    DataNormalizationMode.Raw,
                    TickType.Quote);
                history = HistoryProvider.GetHistory([request], marketHoursEntry.DataTimeZone)?.ToList();
            }

            var universeDataPoints = history == null || history.Count == 0
                ? Enumerable.Empty<BaseData>()
                : history.Take(1).GetUniverseData().SelectMany(x => x.Values.Single());

            if (!allowStaleUniverseFallback)
            {
                // Universe files follow LEAN's daily convention: the file of trading date D carries
                // EndTime D+1 and is the chain known at the start of D+1. On-time data for the
                // requested date therefore has EndTime.Date == date; anything older is a stale file
                // resolved through the lookback and must not be served when the fallback is disabled
                universeDataPoints = universeDataPoints.Where(dataPoint => dataPoint.EndTime.Date == date.Date);
            }

            var symbols = universeDataPoints.Select(x => x.Symbol);

            if (canonicalSymbol.SecurityType.IsOption())
            {
                symbols = symbols.Where(symbol => symbol.SecurityType.IsOption());
            }

            return symbols.Where(symbol => symbol.ID.Date >= date.Date);
        }

        /// <summary>
        /// Helper method to determine if a contract is expired for the requested date
        /// </summary>
        protected static bool IsContractExpired(Symbol symbol, DateTime date)
        {
            return symbol.ID.Date.Date < date.Date;
        }
    }
}
