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
using QuantConnect.Securities.Future;

namespace QuantConnect.Securities.FutureOption
{
    /// <summary>
    /// Date-aware underlying-future resolution for weekly and end-of-month futures-option roots.
    /// Part of the fop-weeklies fork (design A section 3, design B issue 2).
    ///
    /// Maps the <see cref="FutureOptionRootDefinition.UnderlyingRuleId"/> of a registry root
    /// definition to a policy that resolves the underlying future's CONTRACT MONTH from the exact
    /// option expiration DATE (not just its contract month). Month-keyed rules cannot express the
    /// date-dependent-within-month behavior of weekly series: two weeklies of the same calendar
    /// month can exercise into different futures depending on whether they expire before or after
    /// the relevant standard option / future expiration.
    ///
    /// Verification status of each policy is documented on its method. Policies derive the
    /// standard (monthly/quarterly) option expiries they key on from the legacy expiry table via
    /// <see cref="FuturesOptionsExpiryFunctions.FuturesOptionExpiry"/>, never re-deriving them
    /// </summary>
    public static class FutureOptionUnderlyingRuleResolver
    {
        /// <summary>
        /// Rule id of roots resolved by the hardcoded legacy tables in
        /// <see cref="FuturesOptionsUnderlyingMapper"/>. Not resolvable by this class
        /// </summary>
        public const string Legacy = "legacy";

        /// <summary>
        /// Rule id of equity-index weekly/EOM/daily roots (ES/NQ/...): the underlying is the
        /// nearest Mar/Jun/Sep/Dec future whose futures expiration is on or after the option's
        /// expiration
        /// </summary>
        public const string NextQuarterly = "NextQuarterly";

        /// <summary>
        /// Rule id of energy weekly roots (CL LO1-LO5): the underlying is the nearest monthly
        /// future whose associated standard monthly option expires strictly after the weekly
        /// </summary>
        public const string FrontMonthly = "FrontMonthly";

        /// <summary>
        /// Rule id of metals weekly roots (GC OG1-OG5): the underlying is the closest-to-expiry
        /// non-spot active-cycle future, or the second closest when the weekly expires after the
        /// associated standard monthly option
        /// </summary>
        public const string NearestActiveMonth = "NearestActiveMonth";

        /// <summary>
        /// Rule id of treasury weekly roots (ZN1-ZN5, WY1-WY5): the underlying is the same future
        /// as the nearest subsequent quarterly option's
        /// </summary>
        public const string NextQuarterlyTreasury = "NextQuarterlyTreasury";

        /// <summary>
        /// Safety bound on cycle-month iteration; every policy resolves within at most two steps
        /// past the front contract, anything longer indicates a broken expiry rule
        /// </summary>
        private const int MaxCycleIterations = 24;

        /// <summary>
        /// The COMEX gold active option cycle: February, April, June, August, October, December.
        /// VERIFIED against the CME/COMEX weekly options rule language: a gold weekly option
        /// exercises into "the closest to expiry of a non-spot February, April, June, August,
        /// October, or December Gold Futures contract" (CME Metals Weekly Options FAQ /
        /// CFTC weekly metals filings). Note: October IS part of the cycle
        /// </summary>
        private static readonly int[] _evenMonthCycle = { 2, 4, 6, 8, 10, 12 };

        /// <summary>
        /// The quarterly cycle: March, June, September, December
        /// </summary>
        private static readonly int[] _quarterlyCycle = { 3, 6, 9, 12 };

        /// <summary>
        /// Attempts to resolve the underlying future's contract month for the given root
        /// definition and exact option expiration date.
        /// Returns false for the <see cref="Legacy"/> rule id (and empty/null rule ids), which are
        /// handled by the hardcoded tables in <see cref="FuturesOptionsUnderlyingMapper"/>.
        /// Throws for unknown rule ids: a misconfigured registry must fail loudly, never fall back
        /// to a plausible-looking wrong underlying (design B issue 2 - the underlying contract
        /// month is also a data-directory key, so a silent mismatch corrupts data paths)
        /// </summary>
        /// <param name="definition">The registry root definition to resolve</param>
        /// <param name="optionExpiration">The exact expiration date of the option contract</param>
        /// <param name="futureContractMonth">The resolved underlying future contract month</param>
        /// <returns>True when a rule resolved the contract month</returns>
        public static bool TryResolveContractMonth(FutureOptionRootDefinition definition,
            DateTime optionExpiration,
            out DateTime futureContractMonth)
        {
            ArgumentNullException.ThrowIfNull(definition);

            switch (definition.UnderlyingRuleId)
            {
                case null:
                case "":
                case Legacy:
                    futureContractMonth = default;
                    return false;

                case NextQuarterly:
                    futureContractMonth = ResolveNextQuarterly(definition, optionExpiration);
                    return true;

                case FrontMonthly:
                    futureContractMonth = ResolveFrontMonthly(definition, optionExpiration);
                    return true;

                case NearestActiveMonth:
                    futureContractMonth = ResolveNearestActiveMonth(definition, optionExpiration);
                    return true;

                case NextQuarterlyTreasury:
                    futureContractMonth = ResolveNextQuarterlyTreasury(definition, optionExpiration);
                    return true;

                default:
                    throw new NotSupportedException(
                        "FutureOptionUnderlyingRuleResolver.TryResolveContractMonth(): unknown " +
                        $"underlying rule id '{definition.UnderlyingRuleId}' for future option root " +
                        $"'{definition.OptionTicker}' (market '{definition.Market}')");
            }
        }

        /// <summary>
        /// Equity-index weeklies/EOM (ES EW1-EW4, E1A-E5D, EW, ...): the underlying is the
        /// nearest-expiring quarterly (Mar/Jun/Sep/Dec) future as of the option's expiration,
        /// i.e. the nearest quarterly whose FUTURES expiration date is on or after the option's
        /// expiration date.
        /// VERIFIED: CME "Weekly and EOM Options on S&amp;P 500 Futures" FAQ - "the underlying
        /// instrument ... is the nearest-expiring quarterly E-mini S&amp;P 500 futures contract as
        /// of the expiration of the option". Weeklies expiring after the third Friday of a
        /// quarterly month therefore exercise into the NEXT quarterly. Because equity-index
        /// quarterly options and quarterly futures expire the same third-Friday morning, there is
        /// no window between quarterly option expiry and quarterly futures expiry to disambiguate.
        /// ASSUMED (unreachable in listings): a weekly expiring exactly on the quarterly futures
        /// expiration date resolves to that quarterly; CME lists no weekly on the quarterly
        /// expiration Friday since the quarterly option occupies it
        /// </summary>
        private static DateTime ResolveNextQuarterly(FutureOptionRootDefinition definition, DateTime optionExpiration)
        {
            var futureExpiryFunction = FuturesExpiryFunctions.FuturesExpiryFunction(
                Symbol.Create(definition.FutureTicker, SecurityType.Future, definition.Market));

            var contractMonth = FirstCycleMonthOnOrAfter(optionExpiration, _quarterlyCycle);
            for (var i = 0; i < MaxCycleIterations; i++)
            {
                if (futureExpiryFunction(contractMonth).Date >= optionExpiration.Date)
                {
                    return contractMonth;
                }

                contractMonth = NextCycleMonth(contractMonth, _quarterlyCycle);
            }

            throw NoContractResolved(definition, optionExpiration, NextQuarterly);
        }

        /// <summary>
        /// Energy weeklies (CL LO1-LO5): the underlying is the nearest monthly future whose
        /// associated standard monthly option (LO) expires strictly AFTER the weekly. A weekly
        /// expiring between the front monthly option's expiration and the front future's own
        /// last-trade date therefore exercises into the SECOND month.
        /// VERIFIED (CME weekly WTI options FAQs): weekly WTI options exercise into the front
        /// future, and a weekly expiring on the same day as the standard monthly option exercises
        /// into the second listed futures month - hence the strict comparison against the monthly
        /// option's expiration rather than the future's last-trade date
        /// </summary>
        private static DateTime ResolveFrontMonthly(FutureOptionRootDefinition definition, DateTime optionExpiration)
        {
            var monthlyOptionExpiry = StandardOptionExpiryFunction(definition);

            // start at the option expiration's calendar month: its future (and monthly option)
            // expired the month before, so the loop lands on the true front month within one step
            var contractMonth = new DateTime(optionExpiration.Year, optionExpiration.Month, 1);
            for (var i = 0; i < MaxCycleIterations; i++)
            {
                if (monthlyOptionExpiry(contractMonth).Date > optionExpiration.Date)
                {
                    return contractMonth;
                }

                contractMonth = contractMonth.AddMonths(1);
            }

            throw NoContractResolved(definition, optionExpiration, FrontMonthly);
        }

        /// <summary>
        /// Metals weeklies (GC OG1-OG5): the underlying is the closest-to-expiry NON-SPOT
        /// active-cycle future (Feb/Apr/Jun/Aug/Oct/Dec for gold), unless the weekly expires
        /// after the expiration of the associated standard monthly option (OG), in which case it
        /// is the SECOND closest active-cycle future.
        /// VERIFIED against the CME/COMEX rule language quoted in the CME Metals Weekly Options
        /// FAQ and CFTC weekly metals option filings: "an option to assume a long position in the
        /// closest to expiry of a non-spot February, April, June, August, October, or December
        /// Gold Futures contract, unless such expiration day is after the expiry of the
        /// associated monthly option. In such case, the contract will be exercisable into a
        /// future in the second closest to expiry February, April, June, August, October, or
        /// December Gold Futures contract".
        /// Non-spot is modeled as: the contract's delivery month begins strictly after the
        /// weekly's expiration date (COMEX delivery runs through the contract month, and the
        /// associated monthly option always expires before the delivery month starts).
        /// "After the expiry of the associated monthly option" is strict; equality is unreachable
        /// for the Friday weekly roots because the OG monthly expiry rule skips Fridays
        /// </summary>
        private static DateTime ResolveNearestActiveMonth(FutureOptionRootDefinition definition, DateTime optionExpiration)
        {
            var monthlyOptionExpiry = StandardOptionExpiryFunction(definition);

            // closest non-spot active-cycle contract: first cycle month beginning strictly after
            // the weekly expires (a contract already in its delivery month is the spot month)
            var contractMonth = FirstCycleMonthOnOrAfter(optionExpiration, _evenMonthCycle);
            if (contractMonth <= optionExpiration.Date)
            {
                contractMonth = NextCycleMonth(contractMonth, _evenMonthCycle);
            }

            // second closest when the weekly outlives the associated monthly option
            if (optionExpiration.Date > monthlyOptionExpiry(contractMonth).Date)
            {
                contractMonth = NextCycleMonth(contractMonth, _evenMonthCycle);
            }

            return contractMonth;
        }

        /// <summary>
        /// Treasury weeklies (ZN1-ZN5 Fridays, WY1-WY5 Wednesdays): a weekly option exercises
        /// into the same future as its nearest subsequent quarterly option - the front quarterly
        /// future, unless the weekly expires after that quarterly OPTION's expiration (which
        /// precedes the quarterly FUTURE's last-trade date by roughly three weeks), in which case
        /// it jumps to the next quarterly.
        /// VERIFIED: CME "Weekly options on U.S. Treasury futures" FAQ - "a Weekly option will
        /// exercise into the same futures contract as its nearest subsequent quarterly option",
        /// with the worked example: the August 2025 Week 5 Friday weekly (Aug 29) exercises into
        /// the DECEMBER 2025 future because the September 2025 quarterly options expired
        /// August 22, 2025.
        /// ASSUMED (unreachable in listings): a weekly expiring exactly on the quarterly option's
        /// expiration date resolves to that quarterly; CME lists no Friday weekly on the
        /// quarterly option expiration Friday
        /// </summary>
        private static DateTime ResolveNextQuarterlyTreasury(FutureOptionRootDefinition definition, DateTime optionExpiration)
        {
            var quarterlyOptionExpiry = StandardOptionExpiryFunction(definition);

            var contractMonth = FirstCycleMonthOnOrAfter(optionExpiration, _quarterlyCycle);
            for (var i = 0; i < MaxCycleIterations; i++)
            {
                if (quarterlyOptionExpiry(contractMonth).Date >= optionExpiration.Date)
                {
                    return contractMonth;
                }

                contractMonth = NextCycleMonth(contractMonth, _quarterlyCycle);
            }

            throw NoContractResolved(definition, optionExpiration, NextQuarterlyTreasury);
        }

        /// <summary>
        /// Returns the expiry function of the product's STANDARD (monthly/quarterly) option root
        /// for a given underlying future contract month, resolved through the legacy expiry table
        /// (single source of truth for standard expiries, golden-tested byte-identical)
        /// </summary>
        private static Func<DateTime, DateTime> StandardOptionExpiryFunction(FutureOptionRootDefinition definition)
        {
            var canonicalFuture = Symbol.Create(definition.FutureTicker, SecurityType.Future, definition.Market);
            var canonicalStandardOption = Symbol.CreateCanonicalOption(canonicalFuture);
            return contractMonth => FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalStandardOption, contractMonth);
        }

        /// <summary>
        /// Returns the first day of the first cycle month whose month begins on or after the
        /// month of the given date
        /// </summary>
        private static DateTime FirstCycleMonthOnOrAfter(DateTime date, int[] cycleMonths)
        {
            var month = new DateTime(date.Year, date.Month, 1);
            while (Array.IndexOf(cycleMonths, month.Month) < 0)
            {
                month = month.AddMonths(1);
            }

            return month;
        }

        /// <summary>
        /// Returns the first day of the next cycle month strictly after the given cycle month
        /// </summary>
        private static DateTime NextCycleMonth(DateTime contractMonth, int[] cycleMonths)
        {
            return FirstCycleMonthOnOrAfter(contractMonth.AddMonths(1), cycleMonths);
        }

        private static InvalidOperationException NoContractResolved(FutureOptionRootDefinition definition,
            DateTime optionExpiration,
            string ruleId)
        {
            return new InvalidOperationException(
                $"FutureOptionUnderlyingRuleResolver: rule '{ruleId}' failed to resolve an underlying " +
                $"contract month for root '{definition.OptionTicker}' (market '{definition.Market}') " +
                $"expiring {optionExpiration:yyyy-MM-dd} within {MaxCycleIterations} cycle months. " +
                "This indicates a broken expiry rule for the product's standard option or future");
        }
    }
}
