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
using System.Linq;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using System.Collections.Generic;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Securities.Future;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// An implementation of <see cref="IOptionChainProvider"/> that reads the list of contracts from open interest zip data files
    /// </summary>
    public class BacktestingOptionChainProvider : BacktestingChainProvider, IOptionChainProvider
    {
        /// <summary>
        /// Gets the list of option contracts for a given underlying symbol
        /// </summary>
        /// <param name="symbol">The option or the underlying symbol to get the option chain for.
        /// Providing the option allows targeting an option ticker different than the default e.g. SPXW</param>
        /// <param name="date">The date for which to request the option chain (only used in backtesting)</param>
        /// <returns>The list of option contracts</returns>
        public virtual IEnumerable<Symbol> GetOptionContractList(Symbol symbol, DateTime date)
        {
            Symbol canonicalSymbol;
            if (!symbol.SecurityType.HasOptions())
            {
                // we got an option
                if (symbol.SecurityType.IsOption() && symbol.Underlying != null)
                {
                    canonicalSymbol = GetCanonical(symbol, date);
                }
                else
                {
                    throw new NotSupportedException($"BacktestingOptionChainProvider.GetOptionContractList(): " +
                        $"{nameof(SecurityType.Equity)}, {nameof(SecurityType.Future)}, or {nameof(SecurityType.Index)} is expected but was {symbol.SecurityType}");
                }
            }
            else
            {
                // we got the underlying
                var mappedUnderlyingSymbol = MapUnderlyingSymbol(symbol, date);
                canonicalSymbol = Symbol.CreateCanonicalOption(mappedUnderlyingSymbol);
            }

            if (canonicalSymbol.SecurityType == SecurityType.FutureOption && canonicalSymbol.Underlying != null)
            {
                // fop-weeklies fork: a future can list options under multiple roots (standard monthly plus
                // weekly, end-of-month and daily roots), so the chain is the union across the registry roots
                return GetFutureOptionSymbols(canonicalSymbol, date);
            }

            return GetSymbols(canonicalSymbol, date);
        }

        /// <summary>
        /// Gets the future option chain for the given canonical symbol as the union of the chains of every
        /// registry root enabled for the requested expiry cycles (design A section 4.2).
        /// When the canonical carries a non-default root (e.g. a weekly root like EW3), only that root is
        /// served, mirroring the equity option targeting behavior (e.g. SPXW)
        /// </summary>
        /// <param name="canonicalSymbol">The canonical future option symbol</param>
        /// <param name="date">The date for which to request the option chain</param>
        /// <returns>The union of the option contracts of all matching roots</returns>
        private IEnumerable<Symbol> GetFutureOptionSymbols(Symbol canonicalSymbol, DateTime date)
        {
            var underlyingFuture = canonicalSymbol.Underlying;
            var futureTicker = underlyingFuture.ID.Symbol;
            var market = canonicalSymbol.ID.Market;

            IEnumerable<FutureOptionRootDefinition> definitions;
            if (canonicalSymbol.ID.Symbol != FuturesOptionsSymbolMappings.Map(futureTicker))
            {
                // an explicit non-default root was requested: serve only that root
                if (!FutureOptionsRootRegistry.TryGetDefinition(canonicalSymbol.ID.Symbol, market, out var definition))
                {
                    // unknown root: preserve the legacy single-request behavior
                    return GetSymbols(canonicalSymbol, date);
                }

                definitions = new[] { definition };
            }
            else
            {
                var cycles = FutureOptionChainCycleSettings.GetCycles(futureTicker, market);
                var rootFilter = FutureOptionChainCycleSettings.GetRootFilter(futureTicker, market);

                var allDefinitions = FutureOptionsRootRegistry.MapAll(futureTicker, market, cycles);
                if (allDefinitions.Count == 0)
                {
                    // future unknown to the registry: preserve the legacy single-request behavior
                    return GetSymbols(canonicalSymbol, date);
                }

                definitions = rootFilter == null
                    ? allDefinitions
                    : allDefinitions.Where(definition => rootFilter.Contains(definition.OptionTicker));
            }

            return definitions.SelectMany(definition => GetSymbolsForRoot(underlyingFuture, definition, date));
        }

        private IEnumerable<Symbol> GetSymbolsForRoot(Symbol underlyingFuture, FutureOptionRootDefinition definition, DateTime date)
        {
            if (date.Date < definition.ListedSince.Date)
            {
                // point-in-time gating: the root did not exist yet on the requested date
                return Enumerable.Empty<Symbol>();
            }

            var rootCanonical = Symbol.CreateCanonicalOption(underlyingFuture, definition.OptionTicker, definition.Market, null);

            // Daily-cycle roots change chain composition every day, so the inherited stale-universe
            // fallback would serve a different instrument set (design B issue 8): a missing universe
            // file must yield an empty chain for them instead
            var allowStaleUniverseFallback = (definition.Cycle & FutureOptionExpiryCycles.Daily) == 0;

            var symbols = GetSymbols(rootCanonical, date, allowStaleUniverseFallback);
            if (!allowStaleUniverseFallback && !symbols.Any())
            {
                Log.Trace("BacktestingOptionChainProvider.GetSymbolsForRoot(): no universe file for " +
                    $"daily-cycle root '{definition.OptionTicker}' on {date:yyyy-MM-dd}; the stale-universe " +
                    "fallback is disabled for daily roots, returning an empty chain");
            }

            return symbols;
        }

        private Symbol GetCanonical(Symbol optionSymbol, DateTime date)
        {
            // Resolve any mapping before requesting option contract list for equities
            // Needs to be done in order for the data file key to be accurate
            if (optionSymbol.Underlying.RequiresMapping())
            {
                var mappedUnderlyingSymbol = MapUnderlyingSymbol(optionSymbol.Underlying, date);

                return Symbol.CreateCanonicalOption(mappedUnderlyingSymbol);
            }
            else
            {
                return optionSymbol.Canonical;
            }
        }

        private Symbol MapUnderlyingSymbol(Symbol underlying, DateTime date)
        {
            if (underlying.RequiresMapping())
            {
                var mapFileResolver = MapFileProvider.Get(AuxiliaryDataKey.Create(underlying));
                var mapFile = mapFileResolver.ResolveMapFile(underlying);
                var ticker = mapFile.GetMappedSymbol(date, underlying.Value);
                return underlying.UpdateMappedSymbol(ticker);
            }
            else
            {
                return underlying;
            }
        }
    }
}
