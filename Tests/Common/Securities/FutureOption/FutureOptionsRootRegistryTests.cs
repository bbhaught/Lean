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
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Securities.Future;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// Fork-only tests (fop-weeklies) for <see cref="FutureOptionsRootRegistry"/>. The golden
    /// dictionary below is a verbatim copy of the retired hardcoded
    /// FuturesOptionsSymbolMappings._futureToFutureOptionsGLOBEX dictionary; the registry and
    /// the shim must reproduce it exactly so that legacy SIDs and backtests are unchanged
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionsRootRegistryTests
    {
        /// <summary>
        /// Verbatim copy of the legacy hardcoded future-to-option-root dictionary
        /// </summary>
        private static readonly Dictionary<string, string> _legacyFutureToFutureOptionsGLOBEX = new Dictionary<string, string>
        {
            { "EH", "OEH" },
            { "KE", "OKE" },
            { "TN", "OTN" },
            { "UB", "OUB" },
            { "YM", "OYM" },
            { "ZB", "OZB" },
            { "ZC", "OZC" },
            { "ZF", "OZF" },
            { "ZL", "OZL" },
            { "ZM", "OZM" },
            { "ZN", "OZN" },
            { "ZO", "OZO" },
            { "ZS", "OZS" },
            { "ZT", "OZT" },
            { "ZW", "OZW" },
            { "RTY", "RTO" },
            { "GC", "OG" },
            { "HG", "HXE" },
            { "SI", "SO" },
            { "CL", "LO" },
            { "HCL", "HCO" },
            { "HO", "OH" },
            { "NG", "ON" },
            { "PA", "PAO" },
            { "PL", "PO" },
            { "RB", "OB" },
            { "YG", "OYG" },
            { "ZG", "OZG" },
            { "ZI", "OZI" },
            { "6A", "ADU" },
            { "6B", "GBU" },
            { "6C", "CAU" },
            { "6E", "EUU" },
            { "6J", "JPU" },
            { "6S", "CHU" }
        };

        [Test]
        public void LegacyMappingsAreGoldenIdentical()
        {
            foreach (var kvp in _legacyFutureToFutureOptionsGLOBEX)
            {
                Assert.AreEqual(kvp.Value, FutureOptionsRootRegistry.Map(kvp.Key), $"Map({kvp.Key})");
                Assert.AreEqual(kvp.Key, FutureOptionsRootRegistry.MapFromOption(kvp.Value), $"MapFromOption({kvp.Value})");

                // the legacy public API shim must behave byte-identically
                Assert.AreEqual(kvp.Value, FuturesOptionsSymbolMappings.Map(kvp.Key), $"shim Map({kvp.Key})");
                Assert.AreEqual(kvp.Key, FuturesOptionsSymbolMappings.MapFromOption(kvp.Value), $"shim MapFromOption({kvp.Value})");
            }
        }

        [TestCase("ES", "ES")]
        [TestCase("es", "ES")]
        [TestCase("abc", "ABC")]
        [TestCase("zn", "OZN")]
        public void MapPreservesLegacyDefaultingAndCasing(string futureTicker, string expected)
        {
            Assert.AreEqual(expected, FutureOptionsRootRegistry.Map(futureTicker));
            Assert.AreEqual(expected, FuturesOptionsSymbolMappings.Map(futureTicker));
        }

        [TestCase("XYZ9", "XYZ9")]
        [TestCase("ew3", "ES")]
        public void MapFromOptionPreservesLegacyDefaultingAndCasing(string optionTicker, string expected)
        {
            Assert.AreEqual(expected, FutureOptionsRootRegistry.MapFromOption(optionTicker));
            Assert.AreEqual(expected, FuturesOptionsSymbolMappings.MapFromOption(optionTicker));
        }

        [TestCase("ES", Market.CME, "ES")]
        [TestCase("ZN", Market.CBOT, "OZN")]
        [TestCase("CL", Market.NYMEX, "LO")]
        [TestCase("GC", Market.COMEX, "OG")]
        public void MapAllReturnsStandardRootFirst(string futureTicker, string market, string expectedFirstRoot)
        {
            var definitions = FutureOptionsRootRegistry.MapAll(futureTicker, market);

            Assert.Greater(definitions.Count, 1, "expected weekly roots in addition to the standard root");
            Assert.AreEqual(expectedFirstRoot, definitions[0].OptionTicker);
            Assert.AreNotEqual(0, definitions[0].Cycle & FutureOptionExpiryCycles.Standard);
        }

        [TestCase("EW3", "ES")]
        [TestCase("LO2", "CL")]
        [TestCase("OG1", "GC")]
        [TestCase("ZN1", "ZN")]
        [TestCase("WY3", "ZN")]
        [TestCase("E1A", "ES")]
        [TestCase("E5D", "ES")]
        [TestCase("EW", "ES")]
        public void WeeklyRootsReverseMapToTheirFuture(string optionTicker, string expectedFutureTicker)
        {
            Assert.AreEqual(expectedFutureTicker, FutureOptionsRootRegistry.MapFromOption(optionTicker));
            Assert.AreEqual(expectedFutureTicker, FuturesOptionsSymbolMappings.MapFromOption(optionTicker));
        }

        [Test]
        public void TryGetDefinitionReturnsCorrectFields()
        {
            FutureOptionRootDefinition definition;
            Assert.IsTrue(FutureOptionsRootRegistry.TryGetDefinition("EW3", Market.CME, out definition));
            Assert.AreEqual("ES", definition.FutureTicker);
            Assert.AreEqual("EW3", definition.OptionTicker);
            Assert.AreEqual(Market.CME, definition.Market);
            Assert.AreEqual(FutureOptionExpiryCycles.Weekly, definition.Cycle);
            Assert.AreEqual(DayOfWeek.Friday, definition.ExpiryDayOfWeek);
            Assert.AreEqual(3, definition.WeekOfMonth);
            Assert.AreEqual("nth_weekday", definition.ExpiryRuleId);
            Assert.AreEqual("NextQuarterly", definition.UnderlyingRuleId);
            Assert.AreEqual(FutureOptionSettlement.FuturesSettled, definition.Settlement);
            Assert.IsTrue(definition.Enabled);

            Assert.IsTrue(FutureOptionsRootRegistry.TryGetDefinition("WY2", Market.CBOT, out definition));
            Assert.AreEqual("ZN", definition.FutureTicker);
            Assert.AreEqual(FutureOptionExpiryCycles.Weekly, definition.Cycle);
            Assert.AreEqual(DayOfWeek.Wednesday, definition.ExpiryDayOfWeek);
            Assert.AreEqual(2, definition.WeekOfMonth);
            Assert.AreEqual("NextQuarterlyTreasury", definition.UnderlyingRuleId);

            Assert.IsTrue(FutureOptionsRootRegistry.TryGetDefinition("OZN", Market.CBOT, out definition));
            Assert.AreEqual("ZN", definition.FutureTicker);
            Assert.AreEqual(FutureOptionExpiryCycles.Standard, definition.Cycle);
            Assert.AreEqual("legacy", definition.ExpiryRuleId);
            Assert.AreEqual("legacy", definition.UnderlyingRuleId);

            Assert.IsFalse(FutureOptionsRootRegistry.TryGetDefinition("XYZ9", Market.CME, out definition));
            Assert.IsNull(definition);

            // known root, wrong market
            Assert.IsFalse(FutureOptionsRootRegistry.TryGetDefinition("EW3", Market.COMEX, out definition));
        }

        [Test]
        public void CyclesFilteringWorks()
        {
            var standard = FutureOptionsRootRegistry.MapAll("ES", Market.CME, FutureOptionExpiryCycles.Standard);
            Assert.AreEqual(1, standard.Count);
            Assert.AreEqual("ES", standard[0].OptionTicker);

            var endOfMonth = FutureOptionsRootRegistry.MapAll("ES", Market.CME, FutureOptionExpiryCycles.EndOfMonth);
            Assert.AreEqual(1, endOfMonth.Count);
            Assert.AreEqual("EW", endOfMonth[0].OptionTicker);

            var weekly = FutureOptionsRootRegistry.MapAll("ES", Market.CME, FutureOptionExpiryCycles.Weekly);
            // EW1-EW4 Friday + 5 each of Monday/Tuesday/Wednesday/Thursday
            Assert.AreEqual(24, weekly.Count);
            Assert.IsTrue(weekly.All(definition => definition.Cycle == FutureOptionExpiryCycles.Weekly));
            Assert.IsFalse(weekly.Any(definition => definition.OptionTicker == "ES" || definition.OptionTicker == "EW"));

            var all = FutureOptionsRootRegistry.MapAll("ES", Market.CME, FutureOptionExpiryCycles.All);
            Assert.AreEqual(26, all.Count);

            var zNWeekly = FutureOptionsRootRegistry.MapAll("ZN", Market.CBOT, FutureOptionExpiryCycles.Weekly);
            // P5-lite (2026-07-12): Friday ZN1-5 + Wednesday WY1-5 from the P1 seed plus the
            // Monday VY1-5, Tuesday GY1-5 and Thursday HY1-5 series added from the Databento
            // ALL_SYMBOLS enumeration (weekday codes data-verified, see registry _comments_p5lite)
            Assert.AreEqual(25, zNWeekly.Count);

            // wrong market yields nothing
            Assert.AreEqual(0, FutureOptionsRootRegistry.MapAll("ES", Market.COMEX).Count);
        }

        [Test, NonParallelizable]
        public void MissingJsonFallsBackToBuiltInMonthlySeed()
        {
            var originalDataFolder = Config.Get("data-folder");
            var emptyFolder = Path.Combine(Path.GetTempPath(), $"fop-registry-missing-json-{Guid.NewGuid():N}");
            Directory.CreateDirectory(emptyFolder);

            try
            {
                Config.Set("data-folder", emptyFolder);
                Globals.Reset();
                FutureOptionsRootRegistry.Reset();

                // the built-in seed preserves every legacy monthly mapping
                foreach (var kvp in _legacyFutureToFutureOptionsGLOBEX)
                {
                    Assert.AreEqual(kvp.Value, FutureOptionsRootRegistry.Map(kvp.Key), $"fallback Map({kvp.Key})");
                    Assert.AreEqual(kvp.Key, FutureOptionsRootRegistry.MapFromOption(kvp.Value), $"fallback MapFromOption({kvp.Value})");
                }

                // weekly roots come from the json file only, so they are unknown in fallback mode
                Assert.AreEqual("EW3", FutureOptionsRootRegistry.MapFromOption("EW3"));
                FutureOptionRootDefinition definition;
                Assert.IsFalse(FutureOptionsRootRegistry.TryGetDefinition("EW3", Market.CME, out definition));
            }
            finally
            {
                Config.Set("data-folder", originalDataFolder);
                Globals.Reset();
                FutureOptionsRootRegistry.Reset();
                Directory.Delete(emptyFolder, true);
            }

            // sanity: after restore, weekly roots resolve again
            Assert.AreEqual("ES", FutureOptionsRootRegistry.MapFromOption("EW3"));
        }
    }
}
