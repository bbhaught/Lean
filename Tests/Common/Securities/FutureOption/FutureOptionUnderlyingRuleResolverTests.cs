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
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// Fork-only per-policy tests (fop-weeklies P3) for the date-aware underlying-future
    /// resolution of weekly/EOM roots, exercised end-to-end through
    /// <see cref="FuturesOptionsUnderlyingMapper.GetUnderlyingFutureFromFutureOption"/>.
    ///
    /// VERIFICATION STATUS: the RULES are verified against CME documentation (quoted per policy
    /// below and on the <see cref="FutureOptionUnderlyingRuleResolver"/> policy methods); the
    /// specific dates are computed by the engine's own expiry functions and hand-checked. The
    /// treasury September 2025 quarterly option expiry independently reproduces the exact
    /// August 22, 2025 date from CME's weekly treasury options FAQ worked example
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionUnderlyingRuleResolverTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // reload the market hours database from disk: other fixtures mutate entries in
            // memory, which would shift holiday-adjusted expiries and future expirations
            QuantConnect.Securities.MarketHoursDatabase.Reset();
        }

        // -------- NextQuarterly: ES weeklies/EOM (CME weekly & EOM equity FAQ: "the nearest-
        // expiring quarterly E-mini S&P 500 futures contract as of the expiration of the option").
        // ES quarterly futures expire the third Friday of Mar/Jun/Sep/Dec: 2025-03-21, 2025-06-20,
        // 2026-03-20.
        // mid-quarter weeklies -> front quarterly:
        [TestCase("EW1", "ES", Market.CME, "2025-04-04", "2025-06-20")]
        [TestCase("EW2", "ES", Market.CME, "2025-03-14", "2025-03-21")]
        [TestCase("E3C", "ES", Market.CME, "2025-02-19", "2025-03-21")]
        // Wednesday weekly of the quarterly expiration week, expiring BEFORE the Friday quarterly
        // futures expiry -> still the front (March) quarterly:
        [TestCase("E3C", "ES", Market.CME, "2025-03-19", "2025-03-21")]
        // roll week: weeklies expiring AFTER the quarterly futures expiration (2025-03-21) roll to
        // the next quarterly, including the Monday weekly and the March EOM:
        [TestCase("EW4", "ES", Market.CME, "2025-03-28", "2025-06-20")]
        [TestCase("E1A", "ES", Market.CME, "2025-03-24", "2025-06-20")]
        [TestCase("EW", "ES", Market.CME, "2025-03-31", "2025-06-20")]
        // non-quarterly-month EOM -> front quarterly:
        [TestCase("EW", "ES", Market.CME, "2025-02-28", "2025-03-21")]
        // year boundary: post-quarterly December weekly -> March of the next year:
        [TestCase("EW4", "ES", Market.CME, "2025-12-26", "2026-03-20")]
        // REAL-DATA CORRECTION (P5-lite pilot 2026-07-12): the week-3 weekly expiring exactly ON
        // the quarterly futures expiration date is listed in every quarterly month (PM-settled
        // European EW3 alongside the AM-settled quarterly) and exercises into the NEXT quarterly -
        // the future cash-settled at the 8:30am SOQ before the weekly's 3:00pm mark. Verified via
        // Databento GLBX definitions underlying ids (EW3U5 -> ESZ5, EW3Z5 -> ESH6):
        [TestCase("EW3", "ES", Market.CME, "2025-09-19", "2025-12-19")]
        [TestCase("EW3", "ES", Market.CME, "2025-12-19", "2026-03-20")]

        // -------- NextQuarterlyTreasury: ZN Friday (ZN1-ZN5) and Wednesday (WY1-WY5) weeklies.
        // CME weekly treasury options FAQ: "a Weekly option will exercise into the same futures
        // contract as its nearest subsequent quarterly option", with the documented example:
        // September 2025 quarterly options expired Friday 2025-08-22, so the August 2025 Week 5
        // weekly (2025-08-29) exercises into the DECEMBER 2025 future. ZN futures expiries:
        // Sep 2025 = 2025-09-19, Dec 2025 = 2025-12-19, Jun 2025 = 2025-06-18.
        // W1-W3 August 2025 expire before 2025-08-22 -> September future:
        [TestCase("ZN1", "ZN", Market.CBOT, "2025-08-01", "2025-09-19")]
        [TestCase("ZN2", "ZN", Market.CBOT, "2025-08-08", "2025-09-19")]
        [TestCase("ZN3", "ZN", Market.CBOT, "2025-08-15", "2025-09-19")]
        // W4 August 2025 falls ON the quarterly option expiration (2025-08-22): such a weekly is
        // never listed (the quarterly occupies that Friday); the resolver keeps it on the same
        // future as the same-day quarterly option (ASSUMED, unreachable in listings):
        [TestCase("ZN4", "ZN", Market.CBOT, "2025-08-22", "2025-09-19")]
        // W5 August 2025 (the CME-documented case) -> December 2025:
        [TestCase("ZN5", "ZN", Market.CBOT, "2025-08-29", "2025-12-19")]
        // Wednesday weekly after the September quarterly option expired -> December:
        [TestCase("WY1", "ZN", Market.CBOT, "2025-09-03", "2025-12-19")]
        // delivery-month weekly: June quarterly option expired 2025-05-23, so a June weekly
        // exercises into September (never into the June future inside its delivery month):
        [TestCase("ZN1", "ZN", Market.CBOT, "2025-06-06", "2025-09-19")]
        // before/on the June quarterly option expiry (2025-05-23) -> June future:
        [TestCase("ZN3", "ZN", Market.CBOT, "2025-05-16", "2025-06-18")]
        [TestCase("ZN4", "ZN", Market.CBOT, "2025-05-23", "2025-06-18")]

        // -------- NearestActiveMonth: GC Friday weeklies OG1-OG5. CME/COMEX rule (metals weekly
        // FAQ / CFTC filings): "the closest to expiry of a non-spot February, April, June, August,
        // October, or December Gold Futures contract, unless such expiration day is after the
        // expiry of the associated monthly option. In such case ... the second closest".
        // OG monthly expiries (engine-derived, 4th-last business day of the preceding month, no
        // Fridays): Apr 2025 contract -> 2025-03-26; Oct 2025 -> 2025-09-25; Dec 2025 -> 2025-11-24.
        // GC futures expiries: Apr 2025 = 2025-04-28, Jun 2025 = 2025-06-26, Oct 2025 = 2025-10-29,
        // Dec 2025 = 2025-12-29, Feb 2026 = 2026-02-25.
        // weekly before the associated monthly option expiry -> closest non-spot cycle contract:
        [TestCase("OG3", "GC", Market.COMEX, "2025-03-21", "2025-04-28")]
        // weekly AFTER the associated monthly option (2025-03-26) -> SECOND closest (June):
        [TestCase("OG4", "GC", Market.COMEX, "2025-03-28", "2025-06-26")]
        // spot-month weekly: April contract is in its delivery month -> June:
        [TestCase("OG1", "GC", Market.COMEX, "2025-04-04", "2025-06-26")]
        // October IS part of the gold weekly cycle (deviation from a design-note paraphrase that
        // omitted it; the verbatim CME rule language includes October):
        [TestCase("OG2", "GC", Market.COMEX, "2025-09-12", "2025-10-29")]
        // weekly after the associated October monthly option (2025-09-25) -> December:
        [TestCase("OG4", "GC", Market.COMEX, "2025-09-26", "2025-12-29")]
        // late-October weekly: October contract is spot, December monthly option (2025-11-24)
        // still alive -> December:
        [TestCase("OG5", "GC", Market.COMEX, "2025-10-31", "2025-12-29")]
        // weekly after the December monthly option (2025-11-24) -> February of the next year:
        [TestCase("OG4", "GC", Market.COMEX, "2025-11-28", "2026-02-25")]

        // -------- FrontMonthly: CL Friday weeklies LO1-LO5. CME weekly WTI FAQs: weeklies
        // exercise into the front future, and a weekly expiring on the same day as the standard
        // monthly option exercises into the second listed month - i.e. the underlying is the
        // nearest monthly future whose standard option (LO) expires strictly after the weekly.
        // LO monthly expiries (engine-derived): Feb 2025 contract -> 2025-01-15 (MLK-adjusted),
        // Mar 2025 -> 2025-02-14, Jun 2025 -> 2025-05-15, Aug 2024 -> 2024-07-17.
        // CL futures expiries: Feb 2025 = 2025-01-21, Mar 2025 = 2025-02-20,
        // Jun 2025 = 2025-05-20, Aug 2024 = 2024-07-22.
        // weekly before the front monthly option expiry -> front future:
        [TestCase("LO2", "CL", Market.NYMEX, "2025-01-10", "2025-01-21")]
        [TestCase("LO1", "CL", Market.NYMEX, "2025-05-02", "2025-05-20")]
        // weekly in the window between the front monthly option expiry (2025-01-15) and the front
        // future's last-trade date (2025-01-21) -> SECOND month (March):
        [TestCase("LO3", "CL", Market.NYMEX, "2025-01-17", "2025-02-20")]
        // month-end straddle: the last-Friday weekly expires in the old calendar month but its
        // underlying is the future whose monthly option lives in the next month:
        [TestCase("LO5", "CL", Market.NYMEX, "2025-01-31", "2025-02-20")]
        [TestCase("LO4", "CL", Market.NYMEX, "2024-06-28", "2024-07-22")]
        public void ResolvesWeeklyRootToDocumentedUnderlyingFuture(string optionRoot, string futureTicker,
            string market, string optionExpiration, string expectedFutureExpiry)
        {
            var expiration = Parse(optionExpiration);

            var underlying = FuturesOptionsUnderlyingMapper.GetUnderlyingFutureFromFutureOption(
                optionRoot, market, expiration, expiration);

            Assert.IsNotNull(underlying);
            Assert.AreEqual(futureTicker, underlying.ID.Symbol);
            Assert.AreEqual(SecurityType.Future, underlying.SecurityType);
            Assert.AreEqual(Parse(expectedFutureExpiry), underlying.ID.Date.Date,
                $"{optionRoot} expiring {optionExpiration} resolved to {underlying.ID.Date:yyyy-MM-dd}");
        }

        [Test]
        public void LegacyRuleIdIsNotResolvedByTheRuleResolver()
        {
            var definition = new FutureOptionRootDefinition
            {
                FutureTicker = "ES",
                OptionTicker = "ES",
                Market = Market.CME,
                UnderlyingRuleId = "legacy"
            };

            Assert.IsFalse(FutureOptionUnderlyingRuleResolver.TryResolveContractMonth(
                definition, new DateTime(2025, 3, 21), out _));
        }

        [TestCase(null)]
        [TestCase("")]
        public void EmptyRuleIdIsNotResolvedByTheRuleResolver(string ruleId)
        {
            var definition = new FutureOptionRootDefinition
            {
                FutureTicker = "ES",
                OptionTicker = "EW1",
                Market = Market.CME,
                UnderlyingRuleId = ruleId
            };

            Assert.IsFalse(FutureOptionUnderlyingRuleResolver.TryResolveContractMonth(
                definition, new DateTime(2025, 3, 7), out _));
        }

        [Test]
        public void UnknownRuleIdThrowsInsteadOfGuessing()
        {
            var definition = new FutureOptionRootDefinition
            {
                FutureTicker = "ES",
                OptionTicker = "EW1",
                Market = Market.CME,
                UnderlyingRuleId = "NoSuchRule"
            };

            Assert.Throws<NotSupportedException>(
                () => FutureOptionUnderlyingRuleResolver.TryResolveContractMonth(
                    definition, new DateTime(2025, 3, 7), out _));
        }

        [Test]
        public void ResolverRejectsNullDefinition()
        {
            Assert.Throws<ArgumentNullException>(
                () => FutureOptionUnderlyingRuleResolver.TryResolveContractMonth(
                    null, new DateTime(2025, 3, 7), out _));
        }

        private static DateTime Parse(string date)
        {
            return DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
