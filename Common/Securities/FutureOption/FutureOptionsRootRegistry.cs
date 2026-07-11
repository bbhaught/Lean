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
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Expiry cycle families a futures option root can belong to
    /// </summary>
    [Flags]
    public enum FutureOptionExpiryCycles
    {
        /// <summary>
        /// Monthly (including serial month) expiries
        /// </summary>
        Monthly = 1,

        /// <summary>
        /// Quarterly (Mar/Jun/Sep/Dec) expiries
        /// </summary>
        Quarterly = 2,

        /// <summary>
        /// End-of-month expiries
        /// </summary>
        EndOfMonth = 4,

        /// <summary>
        /// Weekly expiries (a specific weekday, week 1-5 of the month)
        /// </summary>
        Weekly = 8,

        /// <summary>
        /// Daily expiries
        /// </summary>
        Daily = 16,

        /// <summary>
        /// Standard contracts: the legacy monthly/quarterly cycle
        /// </summary>
        Standard = Monthly | Quarterly,

        /// <summary>
        /// All expiry cycles
        /// </summary>
        All = Monthly | Quarterly | EndOfMonth | Weekly | Daily
    }

    /// <summary>
    /// Settlement style of a futures option root at exercise/expiry
    /// </summary>
    public enum FutureOptionSettlement
    {
        /// <summary>
        /// Exercises into the underlying future contract
        /// </summary>
        FuturesSettled,

        /// <summary>
        /// Cash settled at expiry
        /// </summary>
        CashSettled
    }

    /// <summary>
    /// A single futures-option root definition: which future it belongs to, its expiry cycle,
    /// and forward references to the expiry/underlying-mapping rules that govern it
    /// </summary>
    public class FutureOptionRootDefinition
    {
        /// <summary>
        /// The underlying future's GLOBEX ticker, e.g. "ES"
        /// </summary>
        [JsonProperty("futureTicker")]
        public string FutureTicker { get; set; }

        /// <summary>
        /// The option root's GLOBEX ticker, e.g. "EW3"
        /// </summary>
        [JsonProperty("optionTicker")]
        public string OptionTicker { get; set; }

        /// <summary>
        /// The market of the root, e.g. Market.CME
        /// </summary>
        [JsonProperty("market")]
        public string Market { get; set; }

        /// <summary>
        /// The expiry cycle family of this root
        /// </summary>
        [JsonProperty("cycle")]
        public FutureOptionExpiryCycles Cycle { get; set; }

        /// <summary>
        /// The weekday the contract expires on, for weekly cycles
        /// </summary>
        [JsonProperty("expiryDayOfWeek")]
        public DayOfWeek? ExpiryDayOfWeek { get; set; }

        /// <summary>
        /// The week of the month (1-5, Nth occurrence of <see cref="ExpiryDayOfWeek"/>), for weekly cycles
        /// </summary>
        [JsonProperty("weekOfMonth")]
        public int? WeekOfMonth { get; set; }

        /// <summary>
        /// Identifier of the expiry rule that generates expiration dates for this root.
        /// "legacy" refers to the hardcoded rules in FuturesOptionsExpiryFunctions; other
        /// identifiers are forward references to the P2 rules engine
        /// </summary>
        [JsonProperty("expiryRuleId")]
        public string ExpiryRuleId { get; set; }

        /// <summary>
        /// Identifier of the rule mapping an option expiry to its underlying future contract.
        /// "legacy" refers to the hardcoded rules in FuturesOptionsUnderlyingMapper; other
        /// identifiers are forward references to the P3 mapper extension
        /// </summary>
        [JsonProperty("underlyingRuleId")]
        public string UnderlyingRuleId { get; set; }

        /// <summary>
        /// Approximate first listing date of this root; chain providers skip roots before this date
        /// </summary>
        [JsonProperty("listedSince")]
        public DateTime ListedSince { get; set; }

        /// <summary>
        /// Settlement style at exercise/expiry
        /// </summary>
        [JsonProperty("settlement")]
        public FutureOptionSettlement Settlement { get; set; }

        /// <summary>
        /// True if the root is enabled for chain resolution. Disabled roots remain resolvable
        /// for reverse mapping and classification but are excluded from MapAll
        /// </summary>
        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Data-driven registry of futures-option roots. Replaces the hardcoded one-to-one
    /// future-to-option-root dictionary with a one-to-many mapping supporting weekly,
    /// end-of-month and daily expiry roots in addition to the legacy monthly roots.
    /// Backed by Data/symbol-properties/future-option-roots.json; falls back to the
    /// built-in legacy monthly seed when the file is missing so existing deployments
    /// keep working unchanged.
    /// </summary>
    public static class FutureOptionsRootRegistry
    {
        private static readonly object _lock = new object();
        private static Lazy<State> _state = new Lazy<State>(Load);

        /// <summary>
        /// Returns the legacy monthly/standard futures-option root for the given future's ticker.
        /// Preserves the exact behavior of the original FuturesOptionsSymbolMappings.Map:
        /// defaults to the future ticker (upper-cased) when no mapping is found
        /// </summary>
        /// <param name="futureTicker">Future GLOBEX ticker to get the standard option root for</param>
        /// <returns>Standard futures-option root ticker</returns>
        public static string Map(string futureTicker)
        {
            futureTicker = futureTicker.ToUpperInvariant();

            string result;
            if (!_state.Value.FutureToStandardRoot.TryGetValue(futureTicker, out result))
            {
                return futureTicker;
            }

            return result;
        }

        /// <summary>
        /// Maps a futures-option root ticker to its underlying future's ticker. Many-to-one:
        /// all roots of a product (e.g. EW3, E1A, EW and ES) map back to the same future (ES).
        /// Preserves the exact behavior of the original FuturesOptionsSymbolMappings.MapFromOption:
        /// defaults to the option ticker (upper-cased) when no mapping is found
        /// </summary>
        /// <param name="futureOptionTicker">Futures-option root ticker to map to the underlying</param>
        /// <returns>Future ticker</returns>
        public static string MapFromOption(string futureOptionTicker)
        {
            futureOptionTicker = futureOptionTicker.ToUpperInvariant();

            string result;
            if (!_state.Value.OptionToFuture.TryGetValue(futureOptionTicker, out result))
            {
                return futureOptionTicker;
            }

            return result;
        }

        /// <summary>
        /// Returns all enabled option root definitions for the given future and market whose
        /// expiry cycle intersects the requested cycles. Standard (monthly/quarterly) roots
        /// are returned first
        /// </summary>
        /// <param name="futureTicker">Future GLOBEX ticker, e.g. "ES"</param>
        /// <param name="market">Market of the future, e.g. Market.CME</param>
        /// <param name="cycles">Expiry cycles to include</param>
        /// <returns>Matching root definitions, standard roots first</returns>
        public static IReadOnlyList<FutureOptionRootDefinition> MapAll(string futureTicker,
            string market,
            FutureOptionExpiryCycles cycles = FutureOptionExpiryCycles.All)
        {
            futureTicker = futureTicker.ToUpperInvariant();
            market = market.ToLowerInvariant();

            return _state.Value.Definitions
                .Where(definition => definition.Enabled
                    && definition.FutureTicker == futureTicker
                    && definition.Market == market
                    && (definition.Cycle & cycles) != 0)
                .OrderByDescending(definition => (definition.Cycle & FutureOptionExpiryCycles.Standard) != 0)
                .ToList();
        }

        /// <summary>
        /// Attempts to get the root definition for the given option root ticker and market
        /// </summary>
        /// <param name="optionTicker">Futures-option root ticker, e.g. "EW3"</param>
        /// <param name="market">Market of the option, e.g. Market.CME</param>
        /// <param name="definition">The definition if found</param>
        /// <returns>True if a definition exists for the given root and market</returns>
        public static bool TryGetDefinition(string optionTicker, string market, out FutureOptionRootDefinition definition)
        {
            return _state.Value.DefinitionsByOptionAndMarket.TryGetValue(
                Key(optionTicker, market), out definition);
        }

        /// <summary>
        /// Returns all root definitions known to the registry, including disabled roots
        /// </summary>
        /// <returns>All root definitions</returns>
        public static IReadOnlyList<FutureOptionRootDefinition> GetDefinitions()
        {
            return _state.Value.Definitions;
        }

        /// <summary>
        /// Reloads the registry from the current data folder. Intended for tests and for
        /// deployments that change <see cref="Globals.DataFolder"/> at runtime
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _state = new Lazy<State>(Load);
            }
        }

        private static string Key(string optionTicker, string market)
        {
            return $"{market.ToLowerInvariant()}-{optionTicker.ToUpperInvariant()}";
        }

        private static State Load()
        {
            var path = Path.Combine(Globals.GetDataFolderPath("symbol-properties"), "future-option-roots.json");
            List<FutureOptionRootDefinition> definitions = null;

            if (File.Exists(path))
            {
                try
                {
                    var file = JsonConvert.DeserializeObject<RootsFile>(File.ReadAllText(path));
                    definitions = file?.Roots;
                }
                catch (Exception exception)
                {
                    Log.Error($"FutureOptionsRootRegistry.Load(): failed to parse '{path}', " +
                        $"falling back to built-in monthly seed: {exception.Message}");
                }
            }

            if (definitions == null || definitions.Count == 0)
            {
                definitions = GetBuiltInMonthlySeed();
            }

            return new State(definitions);
        }

        /// <summary>
        /// Option roots present in the legacy mapping dictionary but absent from the legacy
        /// hardcoded expiry table in FuturesOptionsExpiryFunctions. These roots historically
        /// resolved their expiry through the silent fallback to the underlying future's expiry;
        /// the registry declares that fallback explicitly via the "underlying_future" rule id
        /// so that the strict unknown-root throw (design B issue 7) does not break them
        /// </summary>
        private static readonly HashSet<string> _underlyingFutureExpiryRoots = new HashSet<string>
        {
            "OEH", "HCO", "OH", "PAO", "PO", "OB", "OYG", "OZG", "OZI"
        };

        /// <summary>
        /// Built-in seed preserving the legacy hardcoded GLOBEX future-to-option-root mappings.
        /// Used when future-option-roots.json is missing or unreadable so that existing
        /// deployments never break
        /// </summary>
        private static List<FutureOptionRootDefinition> GetBuiltInMonthlySeed()
        {
            // (futureTicker, optionTicker, market) - exact copy of the legacy
            // FuturesOptionsSymbolMappings._futureToFutureOptionsGLOBEX dictionary
            var legacyMappings = new[]
            {
                new[] { "EH", "OEH", QuantConnect.Market.CBOT },
                new[] { "KE", "OKE", QuantConnect.Market.CBOT },
                new[] { "TN", "OTN", QuantConnect.Market.CBOT },
                new[] { "UB", "OUB", QuantConnect.Market.CBOT },
                new[] { "YM", "OYM", QuantConnect.Market.CBOT },
                new[] { "ZB", "OZB", QuantConnect.Market.CBOT },
                new[] { "ZC", "OZC", QuantConnect.Market.CBOT },
                new[] { "ZF", "OZF", QuantConnect.Market.CBOT },
                new[] { "ZL", "OZL", QuantConnect.Market.CBOT },
                new[] { "ZM", "OZM", QuantConnect.Market.CBOT },
                new[] { "ZN", "OZN", QuantConnect.Market.CBOT },
                new[] { "ZO", "OZO", QuantConnect.Market.CBOT },
                new[] { "ZS", "OZS", QuantConnect.Market.CBOT },
                new[] { "ZT", "OZT", QuantConnect.Market.CBOT },
                new[] { "ZW", "OZW", QuantConnect.Market.CBOT },
                new[] { "RTY", "RTO", QuantConnect.Market.CME },
                new[] { "GC", "OG", QuantConnect.Market.COMEX },
                new[] { "HG", "HXE", QuantConnect.Market.COMEX },
                new[] { "SI", "SO", QuantConnect.Market.COMEX },
                new[] { "CL", "LO", QuantConnect.Market.NYMEX },
                new[] { "HCL", "HCO", QuantConnect.Market.NYMEX },
                new[] { "HO", "OH", QuantConnect.Market.NYMEX },
                new[] { "NG", "ON", QuantConnect.Market.NYMEX },
                new[] { "PA", "PAO", QuantConnect.Market.NYMEX },
                new[] { "PL", "PO", QuantConnect.Market.NYMEX },
                new[] { "RB", "OB", QuantConnect.Market.NYMEX },
                new[] { "YG", "OYG", QuantConnect.Market.NYSELIFFE },
                new[] { "ZG", "OZG", QuantConnect.Market.NYSELIFFE },
                new[] { "ZI", "OZI", QuantConnect.Market.NYSELIFFE },
                new[] { "6A", "ADU", QuantConnect.Market.CME },
                new[] { "6B", "GBU", QuantConnect.Market.CME },
                new[] { "6C", "CAU", QuantConnect.Market.CME },
                new[] { "6E", "EUU", QuantConnect.Market.CME },
                new[] { "6J", "JPU", QuantConnect.Market.CME },
                new[] { "6S", "CHU", QuantConnect.Market.CME }
            };

            var seed = legacyMappings.Select(mapping => new FutureOptionRootDefinition
            {
                FutureTicker = mapping[0],
                OptionTicker = mapping[1],
                Market = mapping[2],
                Cycle = FutureOptionExpiryCycles.Standard,
                ExpiryRuleId = _underlyingFutureExpiryRoots.Contains(mapping[1]) ? "underlying_future" : "legacy",
                UnderlyingRuleId = "legacy",
                ListedSince = new DateTime(1900, 1, 1),
                Settlement = FutureOptionSettlement.FuturesSettled,
                Enabled = true
            }).ToList();

            // Feeder Cattle: identity root without a legacy expiry-table entry. CME GF options
            // terminate with the underlying future, so the historical silent-fallback value was
            // correct for it; declared explicitly so the strict unknown-root throw does not break it
            seed.Add(new FutureOptionRootDefinition
            {
                FutureTicker = "GF",
                OptionTicker = "GF",
                Market = QuantConnect.Market.CME,
                Cycle = FutureOptionExpiryCycles.Standard,
                ExpiryRuleId = "underlying_future",
                UnderlyingRuleId = "legacy",
                ListedSince = new DateTime(1900, 1, 1),
                Settlement = FutureOptionSettlement.FuturesSettled,
                Enabled = true
            });

            return seed;
        }

        private class RootsFile
        {
            [JsonProperty("schema_version")]
            public int SchemaVersion { get; set; }

            [JsonProperty("generated")]
            public string Generated { get; set; }

            [JsonProperty("roots")]
            public List<FutureOptionRootDefinition> Roots { get; set; }
        }

        private class State
        {
            public IReadOnlyList<FutureOptionRootDefinition> Definitions { get; }
            public Dictionary<string, string> FutureToStandardRoot { get; }
            public Dictionary<string, string> OptionToFuture { get; }
            public Dictionary<string, FutureOptionRootDefinition> DefinitionsByOptionAndMarket { get; }

            public State(List<FutureOptionRootDefinition> definitions)
            {
                foreach (var definition in definitions)
                {
                    definition.FutureTicker = definition.FutureTicker?.ToUpperInvariant();
                    definition.OptionTicker = definition.OptionTicker?.ToUpperInvariant();
                    definition.Market = definition.Market?.ToLowerInvariant();
                }

                Definitions = definitions;
                FutureToStandardRoot = new Dictionary<string, string>();
                OptionToFuture = new Dictionary<string, string>();
                DefinitionsByOptionAndMarket = new Dictionary<string, FutureOptionRootDefinition>();

                foreach (var definition in definitions)
                {
                    if ((definition.Cycle & FutureOptionExpiryCycles.Standard) != 0
                        && !FutureToStandardRoot.ContainsKey(definition.FutureTicker))
                    {
                        FutureToStandardRoot[definition.FutureTicker] = definition.OptionTicker;
                    }

                    if (!OptionToFuture.ContainsKey(definition.OptionTicker))
                    {
                        OptionToFuture[definition.OptionTicker] = definition.FutureTicker;
                    }

                    var key = Key(definition.OptionTicker, definition.Market);
                    if (!DefinitionsByOptionAndMarket.ContainsKey(key))
                    {
                        DefinitionsByOptionAndMarket[key] = definition;
                    }
                    else
                    {
                        Log.Error("FutureOptionsRootRegistry(): duplicate root definition " +
                            $"'{definition.OptionTicker}' for market '{definition.Market}', keeping first");
                    }
                }
            }
        }
    }
}
