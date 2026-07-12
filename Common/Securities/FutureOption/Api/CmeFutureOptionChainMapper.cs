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
using System.Text.RegularExpressions;
using QuantConnect.Logging;
using QuantConnect.Securities.Future;

namespace QuantConnect.Securities.FutureOption.Api
{
    /// <summary>
    /// A single CME option series mapped to a registry root and resolved to a specific option expiration,
    /// ready for the live chain provider to fetch strikes for
    /// </summary>
    public class CmeMappedOptionSeries
    {
        /// <summary>
        /// The CME trade-dates-and-expirations entry the series was mapped from
        /// </summary>
        public CMEOptionsTradeDatesAndExpiration Entry { get; }

        /// <summary>
        /// The option root ticker the series belongs to, e.g. "LO" or "EW3"
        /// </summary>
        public string OptionTicker { get; }

        /// <summary>
        /// The CME expiration entry of the series, whose code is used to query the quotes endpoint
        /// </summary>
        public CMEOptionsExpiration Expiration { get; }

        /// <summary>
        /// The option contracts' expiration date, resolved through the expiry-rule engine
        /// </summary>
        public DateTime OptionExpiry { get; }

        /// <summary>
        /// Creates a new instance of the <see cref="CmeMappedOptionSeries"/> class
        /// </summary>
        public CmeMappedOptionSeries(CMEOptionsTradeDatesAndExpiration entry, string optionTicker,
            CMEOptionsExpiration expiration, DateTime optionExpiry)
        {
            Entry = entry;
            OptionTicker = optionTicker;
            Expiration = expiration;
            OptionExpiry = optionExpiry;
        }
    }

    /// <summary>
    /// Maps CME trade-dates-and-expirations API entries onto <see cref="FutureOptionsRootRegistry"/> roots.
    /// Replaces the legacy single-entry selection (first non-daily/non-weekly/non-sto American entry) with a
    /// mapping of every entry to a registry-known root, so weekly and end-of-month series are emitted and
    /// entries the local data model does not know are dropped instead of being emitted with wrong metadata
    /// (upstream issue #8427's failure mode). Pure and network-free so it is unit-testable offline against
    /// canned CME JSON responses
    /// </summary>
    public static class CmeFutureOptionChainMapper
    {
        /// <summary>
        /// CME option style codes accepted for mapping. Most futures options are American ("AME"); CME lists
        /// equity-index weekly and end-of-month series as European ("EUR"), which must be included too.
        /// Emitted symbols currently always carry the LEAN future option default style (American); per-series
        /// settlement/exercise style is downstream (P6) work
        /// </summary>
        private static readonly HashSet<string> _supportedOptionTypes = new() { "AME", "EUR" };

        private static readonly Regex _weekOfMonthRegex = new(@"(?:week|wk)\s*#?\s*([1-5])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Maps the CME expiration entries of a future's option products onto the registry roots enabled by the
        /// given expiry cycles, resolving each relevant expiration to its exact option expiry date and keeping
        /// only the series that exercise into the requested underlying future contract
        /// </summary>
        /// <param name="futureContractSymbol">The specific underlying future contract the chain is requested for</param>
        /// <param name="entries">The parsed CME trade-dates-and-expirations response</param>
        /// <param name="cycles">The expiry cycles to include</param>
        /// <param name="rootFilter">Optional explicit set of option root tickers to restrict the mapping to</param>
        /// <returns>The mapped series to fetch strikes for</returns>
        public static List<CmeMappedOptionSeries> MapSeries(Symbol futureContractSymbol,
            IEnumerable<CMEOptionsTradeDatesAndExpiration> entries,
            FutureOptionExpiryCycles cycles,
            IReadOnlyCollection<string> rootFilter = null)
        {
            var result = new List<CmeMappedOptionSeries>();
            var futureTicker = futureContractSymbol.ID.Symbol;
            var market = futureContractSymbol.ID.Market;

            var definitions = FutureOptionsRootRegistry.MapAll(futureTicker, market, cycles)
                .Where(definition => rootFilter == null || rootFilter.Contains(definition.OptionTicker))
                .ToList();

            if (definitions.Count == 0)
            {
                // future unknown to the registry: preserve the legacy behavior of serving the standard
                // monthly root, using the legacy root mapping
                var legacyEntry = entries.FirstOrDefault(x => !x.Daily && !x.Weekly && !x.Sto && x.OptionType == "AME");
                if (legacyEntry != null)
                {
                    AddStandardSeries(result, futureContractSymbol, legacyEntry, FuturesOptionsSymbolMappings.Map(futureTicker));
                }

                return result;
            }

            var mappedDefinitions = new HashSet<string>();
            foreach (var entry in entries)
            {
                var definition = MatchDefinition(entry, definitions);
                if (definition == null)
                {
                    Log.Trace("CmeFutureOptionChainMapper.MapSeries(): skipping CME option entry " +
                        $"'{entry.Label}' / '{entry.Name}' (productId {entry.ProductId}, optionType '{entry.OptionType}', " +
                        $"daily {entry.Daily}, weekly {entry.Weekly}, sto {entry.Sto}) for future '{futureTicker}': " +
                        "no matching root in the future option roots registry for the requested cycles");
                    continue;
                }

                if (!mappedDefinitions.Add(definition.OptionTicker))
                {
                    Log.Error("CmeFutureOptionChainMapper.MapSeries(): multiple CME option entries mapped to root " +
                        $"'{definition.OptionTicker}' for future '{futureTicker}', keeping the first and skipping " +
                        $"entry '{entry.Label}' / '{entry.Name}' (productId {entry.ProductId})");
                    continue;
                }

                if ((definition.Cycle & FutureOptionExpiryCycles.Standard) != 0)
                {
                    AddStandardSeries(result, futureContractSymbol, entry, definition.OptionTicker);
                }
                else
                {
                    AddNonStandardSeries(result, futureContractSymbol, entry, definition);
                }
            }

            return result;
        }

        /// <summary>
        /// Matches a CME expiration entry to a registry root definition using the entry's series flags
        /// (daily/weekly/sto) and its label and name fields
        /// </summary>
        /// <param name="entry">The CME entry to match</param>
        /// <param name="definitions">The candidate root definitions</param>
        /// <returns>The matched definition, or null when the entry maps to no known root</returns>
        private static FutureOptionRootDefinition MatchDefinition(CMEOptionsTradeDatesAndExpiration entry,
            List<FutureOptionRootDefinition> definitions)
        {
            if (entry.Sto || !_supportedOptionTypes.Contains(entry.OptionType))
            {
                // short-term options and unknown style codes are not modeled
                return null;
            }

            var text = $"{entry.Label} {entry.Name}";

            // 1) explicit root ticker token in the label/name (the most reliable identification when present)
            var byToken = definitions.FirstOrDefault(definition =>
                Regex.IsMatch(text, $@"\b{Regex.Escape(definition.OptionTicker)}\b", RegexOptions.IgnoreCase));
            if (byToken != null)
            {
                return CycleMatchesFlags(byToken, entry) ? byToken : null;
            }

            if (entry.Daily)
            {
                var dailyDefinitions = definitions.Where(definition =>
                    (definition.Cycle & FutureOptionExpiryCycles.Daily) != 0).ToList();
                // without a root token we can only map a daily entry when it is unambiguous
                return dailyDefinitions.Count == 1 ? dailyDefinitions[0] : null;
            }

            if (entry.Weekly)
            {
                var weekday = ParseWeekday(text) ?? DayOfWeek.Friday;
                var week = ParseWeekOfMonth(text);
                if (week == null)
                {
                    // a weekly entry we cannot pin to a specific week is never guessed
                    return null;
                }

                return definitions.FirstOrDefault(definition =>
                    (definition.Cycle & FutureOptionExpiryCycles.Weekly) != 0
                    && definition.ExpiryDayOfWeek == weekday
                    && definition.WeekOfMonth == week);
            }

            if (IsEndOfMonth(text))
            {
                return definitions.FirstOrDefault(definition =>
                    (definition.Cycle & FutureOptionExpiryCycles.EndOfMonth) != 0);
            }

            // plain entry: the standard monthly/quarterly root, which CME lists as American
            if (entry.OptionType == "AME")
            {
                return definitions.FirstOrDefault(definition =>
                    (definition.Cycle & FutureOptionExpiryCycles.Standard) != 0);
            }

            return null;
        }

        /// <summary>
        /// Verifies that the entry's series flags are consistent with the cycle of a token-matched definition
        /// </summary>
        private static bool CycleMatchesFlags(FutureOptionRootDefinition definition, CMEOptionsTradeDatesAndExpiration entry)
        {
            if (entry.Daily)
            {
                return (definition.Cycle & FutureOptionExpiryCycles.Daily) != 0;
            }

            if (entry.Weekly)
            {
                return (definition.Cycle & (FutureOptionExpiryCycles.Weekly | FutureOptionExpiryCycles.EndOfMonth)) != 0;
            }

            return (definition.Cycle & (FutureOptionExpiryCycles.Standard | FutureOptionExpiryCycles.EndOfMonth)) != 0;
        }

        /// <summary>
        /// Adds the standard root series for the expiration whose underlying future matches the requested
        /// contract, preserving the exact legacy expiration-selection and expiry-derivation semantics
        /// </summary>
        private static void AddStandardSeries(List<CmeMappedOptionSeries> result, Symbol futureContractSymbol,
            CMEOptionsTradeDatesAndExpiration entry, string optionTicker)
        {
            // Gather the month code and the year's last number to query the quotes API, which expects an
            // expiration as <MONTH_CODE><YEAR_LAST_NUMBER>. This mirrors the legacy provider: the expiration
            // whose (year, month) future expiry matches the requested future contract is the one queried
            var expiryFunction = FuturesExpiryFunctions.FuturesExpiryFunction(futureContractSymbol.Canonical);

            var expiration = entry.Expirations
                .Select(x => new KeyValuePair<CMEOptionsExpiration, DateTime>(x,
                    expiryFunction(new DateTime(x.Expiration.Year, x.Expiration.Month, 1))))
                .FirstOrDefault(x => x.Value.Year == futureContractSymbol.ID.Date.Year
                    && x.Value.Month == futureContractSymbol.ID.Date.Month)
                .Key;

            if (expiration == null)
            {
                Log.Error("CmeFutureOptionChainMapper.AddStandardSeries(): found no future options with matching " +
                    $"expiry year and month for contract {futureContractSymbol}");
                return;
            }

            var canonicalOption = Symbol.CreateCanonicalOption(futureContractSymbol, optionTicker,
                futureContractSymbol.ID.Market, null);
            var optionExpiry = FuturesOptionsExpiryFunctions.GetFutureOptionExpiryFromFutureExpiry(
                futureContractSymbol, canonicalOption);

            result.Add(new CmeMappedOptionSeries(entry, optionTicker, expiration, optionExpiry));
        }

        /// <summary>
        /// Adds one series per expiration of a weekly/end-of-month/daily root whose resolved underlying
        /// future is the requested contract (design B issue 1: chains attach to the exact held underlying)
        /// </summary>
        private static void AddNonStandardSeries(List<CmeMappedOptionSeries> result, Symbol futureContractSymbol,
            CMEOptionsTradeDatesAndExpiration entry, FutureOptionRootDefinition definition)
        {
            var market = futureContractSymbol.ID.Market;
            var canonicalFuture = Symbol.Create(futureContractSymbol.ID.Symbol, SecurityType.Future, market);
            var canonicalOption = Symbol.CreateCanonicalOption(canonicalFuture, definition.OptionTicker, market, null);

            foreach (var expiration in entry.Expirations)
            {
                try
                {
                    var contractMonth = new DateTime(expiration.Expiration.Year, expiration.Expiration.Month, 1);
                    var optionExpiry = FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalOption, contractMonth);
                    var underlyingFuture = FuturesOptionsUnderlyingMapper.GetUnderlyingFutureFromFutureOption(
                        definition.OptionTicker, market, optionExpiry);

                    if (underlyingFuture == null || underlyingFuture.ID.Date.Date != futureContractSymbol.ID.Date.Date)
                    {
                        // this expiration exercises into a different future contract
                        continue;
                    }

                    result.Add(new CmeMappedOptionSeries(entry, definition.OptionTicker, expiration, optionExpiry));
                }
                catch (Exception exception)
                {
                    // never let a single misconfigured root/expiration abort the whole live chain fetch
                    Log.Error("CmeFutureOptionChainMapper.AddNonStandardSeries(): failed to resolve expiration " +
                        $"'{expiration.Label}' of root '{definition.OptionTicker}': {exception.Message}");
                }
            }
        }

        private static DayOfWeek? ParseWeekday(string text)
        {
            foreach (DayOfWeek weekday in Enum.GetValues(typeof(DayOfWeek)))
            {
                if (Regex.IsMatch(text, $@"\b{weekday}\b", RegexOptions.IgnoreCase))
                {
                    return weekday;
                }
            }

            return null;
        }

        private static int? ParseWeekOfMonth(string text)
        {
            var match = _weekOfMonthRegex.Match(text);
            if (!match.Success)
            {
                return null;
            }

            return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool IsEndOfMonth(string text)
        {
            return Regex.IsMatch(text, @"\bEOM\b|end[\s-]?of[\s-]?month", RegexOptions.IgnoreCase);
        }
    }
}
