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
using System.Globalization;
using NUnit.Framework;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption.Expiry
{
    /// <summary>
    /// Fork-only weekly-expiry correctness fixtures (fop-weeklies P2), exercised end-to-end
    /// through <see cref="FuturesOptionsExpiryFunctions.FuturesOptionExpiry"/> with the registry
    /// seed roots and the LEAN market-hours-database holiday calendar.
    ///
    /// VERIFICATION STATUS: ALL FIXTURES ASSUMED. The CME expiration calendar could not be fetched
    /// from this host (WebFetch of https://www.cmegroup.com/markets/equities/sp/e-mini-sandp500.calendar.options.html,
    /// https://www.cmegroup.com/CmeWS/mvc/ProductCalendar/Options/138 and
    /// https://www.cmegroup.com/education/articles-and-reports/a-traders-guide-to-futures-options-expiration.html
    /// all timed out on 2026-07-11; cmegroup.com is blocked from this environment, see DECISIONS.md D2).
    /// Expected dates are therefore computed per the CME listing rule (week N = Nth occurrence of
    /// the series weekday in the calendar month; contracts scheduled on an exchange holiday expire
    /// the prior business day) against the MHDB holiday calendar, which contains Good Friday
    /// 2024-03-29 and 2025-04-18 in the Future-{cme,cbot,nymex,comex}-[*] wildcard entries and
    /// Good Friday 2026-04-03 only in the specific Future-comex-GC / Future-nymex-CL entries.
    /// P5 validates every generated expiry against actually-listed instruments
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionWeeklyExpiryFixtureTests
    {
        private static DateTime Expiry(string root, string futureTicker, string market, int year, int month)
        {
            var future = Symbol.Create(futureTicker, SecurityType.Future, market);
            var canonicalOption = Symbol.CreateCanonicalOption(future, root, market, null);
            return FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalOption, new DateTime(year, month, 1));
        }

        // -------- ES Friday weeklies EW1-EW4 (CME) --------
        [TestCase("EW1", 2024, 1, "2024-01-05")]
        [TestCase("EW2", 2024, 1, "2024-01-12")]
        [TestCase("EW3", 2024, 1, "2024-01-19")]
        [TestCase("EW4", 2024, 1, "2024-01-26")]
        // March 2024: five Fridays, the fifth (2024-03-29) is Good Friday. The Friday weekly roots
        // cover weeks 1-4 only (EW1-EW4); the fifth Friday belongs to the EOM series, see below
        [TestCase("EW1", 2024, 3, "2024-03-01")]
        [TestCase("EW2", 2024, 3, "2024-03-08")]
        [TestCase("EW3", 2024, 3, "2024-03-15")]
        [TestCase("EW4", 2024, 3, "2024-03-22")]
        // April 2025: Good Friday 2025-04-18 is the third Friday, EW3 rolls back to Thursday
        [TestCase("EW1", 2025, 4, "2025-04-04")]
        [TestCase("EW2", 2025, 4, "2025-04-11")]
        [TestCase("EW3", 2025, 4, "2025-04-17")]
        [TestCase("EW4", 2025, 4, "2025-04-25")]
        // July 2025: Independence Day 2025-07-04 is the first Friday. REAL-DATA CORRECTION
        // (P5-lite pilot 2026-07-12): CME does NOT roll holiday-week weeklies - it does not list
        // them at all (GLBX definitions show no EW1 for Jul-2025, Apr-2026 (Good Friday) or
        // Jul-2026, and no E1D for Jan-2026 (New Year Thursday); zero off-nominal-weekday
        // expirations exist anywhere in the pilot year). Independence Day is an MHDB BANK holiday
        // (partial session) and bank holidays no longer adjust registry-rule expiries (real
        // contracts E2A/E2B expire ON Columbus/Veterans Day), so the engine returns the nominal
        // Friday for this NEVER-LISTED contract - a documented phantom, harmless because chains
        // are data-gated and the live provider maps only actually-listed instruments
        [TestCase("EW1", 2025, 7, "2025-07-04")]
        [TestCase("EW2", 2025, 7, "2025-07-11")]
        // December 2025: Christmas is Thursday the 25th, Friday the 26th trades normally
        [TestCase("EW4", 2025, 12, "2025-12-26")]
        // New Year week edge: January 2026 starts on a Thursday, week-1 Friday is January 2nd
        [TestCase("EW1", 2026, 1, "2026-01-02")]
        // -------- ES Monday weeklies E1A-E5A (CME) --------
        [TestCase("E1A", 2025, 3, "2025-03-03")]
        [TestCase("E3A", 2025, 3, "2025-03-17")]
        [TestCase("E5A", 2025, 3, "2025-03-31")]
        [TestCase("E2A", 2025, 6, "2025-06-09")]
        [TestCase("E5A", 2025, 6, "2025-06-30")]
        // REAL-DATA VERIFIED (P5-lite pilot 2026-07-12, GLBX definitions): bank holidays do NOT
        // roll equity-index weeklies - E2A expires ON Columbus Day and E2B ON Veterans Day
        // (equity futures trade through bank holidays; only full closures adjust the expiry)
        [TestCase("E2A", 2025, 10, "2025-10-13")]
        // -------- ES Tuesday weekly E2B: Veterans Day 2025-11-11 is the second Tuesday --------
        [TestCase("E2B", 2025, 11, "2025-11-11")]
        // -------- ES Wednesday weeklies E1C-E5C (CME) --------
        // New Year week edge: January 2026 starts on a Thursday, week-1 Wednesday is January 7th
        [TestCase("E1C", 2026, 1, "2026-01-07")]
        // Christmas week 2025: Wednesday December 24th is a trading day (early close, not holiday)
        [TestCase("E4C", 2025, 12, "2025-12-24")]
        [TestCase("E5C", 2025, 12, "2025-12-31")]
        // -------- ES Thursday weekly E3D: Juneteenth 2025-06-19 is the third Thursday --------
        // Juneteenth is an MHDB BANK holiday (partial session): no adjustment; per the pilot
        // real-data finding the affected weekly is simply not listed (phantom date, see EW1 above)
        [TestCase("E3D", 2025, 6, "2025-06-19")]
        public void EsWeeklyExpiries(string root, int year, int month, string expected)
        {
            Assert.AreEqual(DateTime.Parse(expected, CultureInfo.InvariantCulture),
                Expiry(root, "ES", Market.CME, year, month));
        }

        // -------- ES end-of-month root EW (CME) --------
        // Good Friday 2024-03-29: March ends Sunday, last business day rolls back over the holiday
        // Friday to Thursday the 28th
        [TestCase(2024, 3, "2024-03-28")]
        // November 2024: month ends Saturday, Friday the 29th (day after Thanksgiving) trades
        [TestCase(2024, 11, "2024-11-29")]
        [TestCase(2025, 5, "2025-05-30")]
        [TestCase(2025, 12, "2025-12-31")]
        [TestCase(2026, 2, "2026-02-27")]
        public void EsEndOfMonthExpiries(int year, int month, string expected)
        {
            Assert.AreEqual(DateTime.Parse(expected, CultureInfo.InvariantCulture),
                Expiry("EW", "ES", Market.CME, year, month));
        }

        // -------- Crude oil Friday weeklies LO1-LO5 (NYMEX) --------
        // LO1 for every month of 2025
        [TestCase("LO1", 2025, 1, "2025-01-03")]
        [TestCase("LO1", 2025, 2, "2025-02-07")]
        [TestCase("LO1", 2025, 3, "2025-03-07")]
        [TestCase("LO1", 2025, 4, "2025-04-04")]
        [TestCase("LO1", 2025, 5, "2025-05-02")]
        [TestCase("LO1", 2025, 6, "2025-06-06")]
        // Independence Day 2025-07-04: bank holiday, no adjustment (phantom date, see EW1 note)
        [TestCase("LO1", 2025, 7, "2025-07-04")]
        [TestCase("LO1", 2025, 8, "2025-08-01")]
        [TestCase("LO1", 2025, 9, "2025-09-05")]
        [TestCase("LO1", 2025, 10, "2025-10-03")]
        [TestCase("LO1", 2025, 11, "2025-11-07")]
        [TestCase("LO1", 2025, 12, "2025-12-05")]
        // Good Friday 2025-04-18 is the third Friday of April
        [TestCase("LO3", 2025, 4, "2025-04-17")]
        // 2025 months with five Fridays: January, May, August, October
        [TestCase("LO5", 2025, 1, "2025-01-31")]
        [TestCase("LO5", 2025, 5, "2025-05-30")]
        [TestCase("LO5", 2025, 8, "2025-08-29")]
        [TestCase("LO5", 2025, 10, "2025-10-31")]
        // Good Friday 2026-04-03 (first Friday): present in the Future-nymex-CL holiday entry
        [TestCase("LO1", 2026, 4, "2026-04-02")]
        public void CrudeWeeklyExpiries(string root, int year, int month, string expected)
        {
            Assert.AreEqual(DateTime.Parse(expected, CultureInfo.InvariantCulture),
                Expiry(root, "CL", Market.NYMEX, year, month));
        }

        // -------- Gold Friday weeklies OG1-OG5 (COMEX) --------
        [TestCase("OG1", 2025, 1, "2025-01-03")]
        [TestCase("OG2", 2025, 2, "2025-02-14")]
        // Good Friday 2025-04-18 is the third Friday of April
        [TestCase("OG3", 2025, 4, "2025-04-17")]
        // Independence Day 2025-07-04 (bank holiday, partial session): no adjustment, contract
        // presumed not listed per the pilot equity-index finding (phantom date; metals weeklies
        // are outside the pilot data - P5 verifies)
        [TestCase("OG1", 2025, 7, "2025-07-04")]
        [TestCase("OG5", 2025, 8, "2025-08-29")]
        // Friday after Thanksgiving 2025 trades normally
        [TestCase("OG4", 2025, 11, "2025-11-28")]
        // Good Friday 2026-04-03: present in the Future-comex-GC holiday entry
        [TestCase("OG1", 2026, 4, "2026-04-02")]
        public void GoldWeeklyExpiries(string root, int year, int month, string expected)
        {
            Assert.AreEqual(DateTime.Parse(expected, CultureInfo.InvariantCulture),
                Expiry(root, "GC", Market.COMEX, year, month));
        }

        // -------- 10-Year note Friday weeklies ZN1-ZN5 (CBOT) --------
        [TestCase("ZN1", 2025, 1, "2025-01-03")]
        // Good Friday 2025-04-18 is the third Friday of April
        [TestCase("ZN3", 2025, 4, "2025-04-17")]
        // Independence Day 2025-07-04: bank holiday, no adjustment (phantom date, see EW1 note)
        [TestCase("ZN1", 2025, 7, "2025-07-04")]
        [TestCase("ZN4", 2025, 9, "2025-09-26")]
        [TestCase("ZN5", 2025, 10, "2025-10-31")]
        public void TreasuryFridayWeeklyExpiries(string root, int year, int month, string expected)
        {
            Assert.AreEqual(DateTime.Parse(expected, CultureInfo.InvariantCulture),
                Expiry(root, "ZN", Market.CBOT, year, month));
        }

        // -------- 10-Year note Wednesday weeklies WY1-WY5 (CBOT) --------
        // Christmas holiday week 2025: Wednesday December 24th is a regular (early-close) trading
        // day and December 25th is a Thursday, so no Wednesday roll applies - WY4 stays on the 24th
        [TestCase("WY1", 2025, 12, "2025-12-03")]
        [TestCase("WY2", 2025, 12, "2025-12-10")]
        [TestCase("WY3", 2025, 12, "2025-12-17")]
        [TestCase("WY4", 2025, 12, "2025-12-24")]
        [TestCase("WY5", 2025, 12, "2025-12-31")]
        [TestCase("WY2", 2025, 1, "2025-01-08")]
        [TestCase("WY3", 2025, 6, "2025-06-18")]
        public void TreasuryWednesdayWeeklyExpiries(string root, int year, int month, string expected)
        {
            Assert.AreEqual(DateTime.Parse(expected, CultureInfo.InvariantCulture),
                Expiry(root, "ZN", Market.CBOT, year, month));
        }

        [Test]
        public void TreasuryWednesdayWeekOneOnNewYearsDayStaysNominal()
        {
            // 2025-01-01 is the first Wednesday of January 2025. New Year's Day is an MHDB BANK
            // holiday, and bank holidays no longer adjust registry-rule expiries (P5-lite pilot,
            // real-data verified on equity index: E2A expires ON Columbus Day, E2B ON Veterans
            // Day, while holiday-week series are simply NOT LISTED - no E1D exists for Jan-2026).
            // The engine therefore returns the nominal Wednesday for this presumed-never-listed
            // contract; treasury weeklies are outside the pilot data, P5 verifies
            Assert.AreEqual(new DateTime(2025, 1, 1), Expiry("WY1", "ZN", Market.CBOT, 2025, 1));
        }

        [TestCase("E5A", "ES", Market.CME, 2025, 4, Description = "April 2025 has four Mondays")]
        [TestCase("LO5", "CL", Market.NYMEX, 2025, 4, Description = "April 2025 has four Fridays")]
        [TestCase("ZN5", "ZN", Market.CBOT, 2025, 2, Description = "February 2025 has four Fridays")]
        public void MissingFifthWeekContractsThrow(string root, string futureTicker, string market, int year, int month)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Expiry(root, futureTicker, market, year, month));
        }

        [Test]
        public void EsGoodFriday2026KnownMhdbGap()
        {
            // KNOWN MHDB DATA GAP: Good Friday 2026-04-03 is missing from the Future-cme-[*] and
            // Future-cbot-[*] holiday lists (it is present in Future-comex-GC and Future-nymex-CL),
            // so ES/ZN weeklies do NOT roll off 2026-04-03 through the full MHDB-backed path even
            // though CME will not expire weekly options on Good Friday. The rule itself handles the
            // roll correctly given the holiday (see NthWeekdayOfMonthExpiryRuleTests). This test
            // pins the current behavior and MUST BE UPDATED to expect 2026-04-02 when the holiday
            // is added to the MHDB (P5/P6 calendar work)
            Assert.AreEqual(new DateTime(2026, 4, 3), Expiry("EW1", "ES", Market.CME, 2026, 4));
        }
    }
}
