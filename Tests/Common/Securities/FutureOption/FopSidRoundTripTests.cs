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
using QuantConnect.Util;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// Fork-only tests (fop-weeklies) porting the P0 strike-encoding audit (DECISIONS.md D1):
    /// weekly futures-option roots and real strike grids must round-trip losslessly through
    /// SecurityIdentifier construction, SID-string parsing and zip entry names. The two known
    /// encoding limits (truncation beyond six decimal places, rejection of 64th-point strikes)
    /// are asserted as the current contract; no real product's strike grid hits them
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FopSidRoundTripTests
    {
        // real-grid PASS cases from the D1 strike audit: JPY 5-decimal strikes, treasury
        // quarter points, equity index halves, metals integers, energy cents
        [TestCase("6J", "WJ1", Market.CME, "0.00925")]
        [TestCase("6J", "WJ1", Market.CME, "0.009255")]
        [TestCase("ZN", "ZN1", Market.CBOT, "112.25")]
        [TestCase("ES", "EW3", Market.CME, "5432.5")]
        [TestCase("GC", "OG1", Market.COMEX, "2345")]
        [TestCase("CL", "LO1", Market.NYMEX, "78.25")]
        public void RealGridStrikesRoundTripThroughSidStringAndZipEntries(string futureTicker,
            string optionRoot,
            string market,
            string strikeString)
        {
            var strike = decimal.Parse(strikeString, CultureInfo.InvariantCulture);
            var future = Symbol.CreateFuture(futureTicker, market, new DateTime(2026, 3, 20));
            var option = Symbol.CreateOption(future, optionRoot, market, OptionStyle.American,
                OptionRight.Call, strike, new DateTime(2026, 3, 6));

            // layer 1: the SID preserves the strike
            Assert.AreEqual(strike, option.ID.StrikePrice);

            // layer 2: SID string round-trip (what map/serialization uses)
            var parsed = SecurityIdentifier.Parse(option.ID.ToString());
            Assert.AreEqual(strike, parsed.StrikePrice);
            Assert.AreEqual(optionRoot, parsed.Symbol);

            // layer 3: zip entry name round-trip, minute and daily forms
            var entryMinute = LeanData.GenerateZipEntryName(option, new DateTime(2026, 3, 2), Resolution.Minute, TickType.Quote);
            var backMinute = LeanData.ReadSymbolFromZipEntry(option.Canonical, Resolution.Minute, entryMinute);
            Assert.AreEqual(strike, backMinute.ID.StrikePrice);

            var entryDaily = LeanData.GenerateZipEntryName(option, new DateTime(2026, 3, 2), Resolution.Daily, TickType.Quote);
            var backDaily = LeanData.ReadSymbolFromZipEntry(option.Canonical, Resolution.Daily, entryDaily);
            Assert.AreEqual(strike, backDaily.ID.StrikePrice);
        }

        [Test]
        public void EveryRegistryRootRoundTripsSidStrings()
        {
            var strikes = new[] { 0.00925m, 112.25m, 5432.5m, 2345m, 78.25m };
            var rights = new[] { OptionRight.Call, OptionRight.Put };
            var expiries = new[] { new DateTime(2026, 3, 6), new DateTime(2026, 6, 19) };

            var checkedCombinations = 0;
            foreach (var definition in FutureOptionsRootRegistry.GetDefinitions())
            {
                var future = Symbol.CreateFuture(definition.FutureTicker, definition.Market, new DateTime(2026, 6, 19));

                foreach (var strike in strikes)
                {
                    foreach (var right in rights)
                    {
                        foreach (var expiry in expiries)
                        {
                            var option = Symbol.CreateOption(future, definition.OptionTicker, definition.Market,
                                OptionStyle.American, right, strike, expiry);
                            var parsed = SecurityIdentifier.Parse(option.ID.ToString());

                            var context = $"root={definition.OptionTicker} market={definition.Market} strike={strike} right={right}";
                            Assert.AreEqual(strike, parsed.StrikePrice, context);
                            Assert.AreEqual(definition.OptionTicker, parsed.Symbol, context);
                            Assert.AreEqual(expiry, parsed.Date, context);
                            Assert.AreEqual(right, parsed.OptionRight, context);
                            Assert.AreEqual(future.ID, parsed.Underlying, context);

                            checkedCombinations++;
                        }
                    }
                }
            }

            // 81 seed roots x 5 strikes x 2 rights x 2 expiries
            Assert.GreaterOrEqual(checkedCombinations, 1000);
        }

        [Test]
        public void StrikeBeyondSixDecimalPlacesIsTruncated()
        {
            // documented encoding LIMIT, DECISIONS.md D1: the SID string stores strikes in
            // millionths, so a synthetic 7-decimal strike loses its last digit on the string
            // round-trip (0.0000925 -> 0.000092) while the in-memory SID keeps the full value.
            // No real CME strike grid uses more than 6 decimal places; the P5 generator rejects
            // any definitions strike that fails exact round-trip
            var future = Symbol.CreateFuture("6J", Market.CME, new DateTime(2026, 3, 20));
            var option = Symbol.CreateOption(future, "WJ1", Market.CME, OptionStyle.American,
                OptionRight.Call, 0.0000925m, new DateTime(2026, 3, 6));

            Assert.AreEqual(0.0000925m, option.ID.StrikePrice);
            Assert.AreEqual(0.000092m, SecurityIdentifier.Parse(option.ID.ToString()).StrikePrice);
        }

        [Test]
        public void SixtyFourthPointStrikeThrows()
        {
            // documented encoding LIMIT, DECISIONS.md D1: 64th-point values overflow the strike
            // width and SecurityIdentifier rejects them. Treasury STRIKES are quarter/half points
            // (only prices are quoted in 64ths), so no listed contract is affected
            var future = Symbol.CreateFuture("ZN", Market.CBOT, new DateTime(2026, 3, 20));

            var exception = Assert.Throws<ArgumentException>(() => Symbol.CreateOption(future, "ZN1", Market.CBOT,
                OptionStyle.American, OptionRight.Call, 112.265625m, new DateTime(2026, 3, 6)));
            StringAssert.Contains("precision is too high", exception.Message);
        }
    }
}
