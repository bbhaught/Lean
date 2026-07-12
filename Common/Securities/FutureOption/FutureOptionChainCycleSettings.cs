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

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Algorithm-level registration of the future option expiry cycles requested per future product.
    /// <see cref="QuantConnect.Interfaces.IOptionChainProvider.GetOptionContractList"/> only receives a symbol and a
    /// date, so the chain providers consult this registry to decide which option roots to fan out over when they are
    /// asked for the chain of a future (or of its default option root). Populated by AddFutureOption; the default for
    /// unregistered products is <see cref="FutureOptionExpiryCycles.Standard"/>, which is identical to the legacy
    /// single-root behavior
    /// </summary>
    public static class FutureOptionChainCycleSettings
    {
        private static readonly ConcurrentDictionary<string, Entry> _entries = new();

        /// <summary>
        /// Registers the expiry cycles and optional option-root filter requested for the given future product.
        /// Multiple registrations for the same product are merged: cycles are unioned and root filters are
        /// unioned, with an unrestricted (null) root filter taking precedence over any explicit list
        /// </summary>
        /// <param name="futureTicker">The future's GLOBEX ticker, e.g. "ES"</param>
        /// <param name="market">The market of the future, e.g. Market.CME</param>
        /// <param name="cycles">The requested expiry cycles</param>
        /// <param name="rootFilter">Optional explicit list of option root tickers to restrict the chain to.
        /// Null means all roots matching the requested cycles</param>
        public static void Register(string futureTicker, string market, FutureOptionExpiryCycles cycles,
            IEnumerable<string> rootFilter = null)
        {
            var rootFilterSet = rootFilter?.Select(root => root.ToUpperInvariant()).ToHashSet();

            _entries.AddOrUpdate(Key(futureTicker, market),
                _ => new Entry(cycles, rootFilterSet),
                (_, existing) =>
                {
                    HashSet<string> mergedFilter = null;
                    if (existing.RootFilter != null && rootFilterSet != null)
                    {
                        mergedFilter = new HashSet<string>(existing.RootFilter);
                        mergedFilter.UnionWith(rootFilterSet);
                    }

                    return new Entry(existing.Cycles | cycles, mergedFilter);
                });
        }

        /// <summary>
        /// Gets the expiry cycles registered for the given future product,
        /// defaulting to <see cref="FutureOptionExpiryCycles.Standard"/>
        /// </summary>
        /// <param name="futureTicker">The future's GLOBEX ticker, e.g. "ES"</param>
        /// <param name="market">The market of the future, e.g. Market.CME</param>
        /// <returns>The registered cycles, or Standard when the product was never registered</returns>
        public static FutureOptionExpiryCycles GetCycles(string futureTicker, string market)
        {
            return _entries.TryGetValue(Key(futureTicker, market), out var entry)
                ? entry.Cycles
                : FutureOptionExpiryCycles.Standard;
        }

        /// <summary>
        /// Gets the option-root filter registered for the given future product, or null when unrestricted
        /// </summary>
        /// <param name="futureTicker">The future's GLOBEX ticker, e.g. "ES"</param>
        /// <param name="market">The market of the future, e.g. Market.CME</param>
        /// <returns>The set of allowed option root tickers, or null when all roots are allowed</returns>
        public static IReadOnlyCollection<string> GetRootFilter(string futureTicker, string market)
        {
            return _entries.TryGetValue(Key(futureTicker, market), out var entry) ? entry.RootFilter : null;
        }

        /// <summary>
        /// Clears all registrations. Intended for tests and algorithm restarts within the same process
        /// </summary>
        public static void Reset()
        {
            _entries.Clear();
        }

        private static string Key(string futureTicker, string market)
        {
            return $"{market.ToLowerInvariant()}-{futureTicker.ToUpperInvariant()}";
        }

        private sealed class Entry
        {
            public FutureOptionExpiryCycles Cycles { get; }
            public HashSet<string> RootFilter { get; }

            public Entry(FutureOptionExpiryCycles cycles, HashSet<string> rootFilter)
            {
                Cycles = cycles;
                RootFilter = rootFilter;
            }
        }
    }
}
