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
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Securities.FutureOption;
using QuantConnect.Securities.FutureOption.Api;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// fop-weeklies fork P4: offline tests of the CME trade-dates-and-expirations to registry-root
    /// mapping used by the live option chain provider. The canned JSON below is a representative
    /// fixture of the CME response schema as parsed by <see cref="CMEOptionsTradeDatesAndExpiration"/>
    /// (label, name, optionType, productId, daily/sto/weekly flags, expirations with month/year/code).
    /// The network-dependent LiveOptionChainProviderReturnsFutureOptionData test cannot run from this
    /// host (CME 403), so the mapping logic is verified against this fixture instead; P5 validates the
    /// label conventions against live responses
    /// </summary>
    [TestFixture, NonParallelizable, Category("FopFork")]
    public class CmeFutureOptionChainMapperTests
    {
        private static readonly Symbol _esMarch2020 = Symbol.CreateFuture(
            QuantConnect.Securities.Futures.Indices.SP500EMini, Market.CME, new DateTime(2020, 3, 20));

        private static readonly Symbol _esJune2020 = Symbol.CreateFuture(
            QuantConnect.Securities.Futures.Indices.SP500EMini, Market.CME, new DateTime(2020, 6, 19));

        private const string FixtureJson = @"[
  {
    ""label"": ""Options"", ""name"": ""E-mini S&P 500 Options"", ""optionType"": ""AME"", ""productId"": 133,
    ""daily"": false, ""sto"": false, ""weekly"": false,
    ""expirations"": [
      { ""label"": ""Mar 2020"", ""productId"": 133, ""contractId"": ""ESH20"",
        ""expiration"": { ""month"": 3, ""year"": 2020, ""code"": ""H0"", ""twoDigitsCode"": ""H20"" } },
      { ""label"": ""Jun 2020"", ""productId"": 133, ""contractId"": ""ESM20"",
        ""expiration"": { ""month"": 6, ""year"": 2020, ""code"": ""M0"", ""twoDigitsCode"": ""M20"" } }
    ]
  },
  {
    ""label"": ""Weekly Options Wk 2"", ""name"": ""E-mini S&P 500 Weekly Options - Week 2"",
    ""optionType"": ""EUR"", ""productId"": 2916, ""daily"": false, ""sto"": false, ""weekly"": true,
    ""expirations"": [
      { ""label"": ""Jan 2020"", ""productId"": 2916, ""contractId"": ""EW2F0"",
        ""expiration"": { ""month"": 1, ""year"": 2020, ""code"": ""F0"", ""twoDigitsCode"": ""F20"" } },
      { ""label"": ""May 2020"", ""productId"": 2916, ""contractId"": ""EW2K0"",
        ""expiration"": { ""month"": 5, ""year"": 2020, ""code"": ""K0"", ""twoDigitsCode"": ""K20"" } }
    ]
  },
  {
    ""label"": ""Monday Weekly Options Wk 1"", ""name"": ""E-mini S&P 500 Monday Weekly Options - Week 1"",
    ""optionType"": ""EUR"", ""productId"": 8437, ""daily"": false, ""sto"": false, ""weekly"": true,
    ""expirations"": [
      { ""label"": ""Jan 2020"", ""productId"": 8437, ""contractId"": ""E1AF0"",
        ""expiration"": { ""month"": 1, ""year"": 2020, ""code"": ""F0"", ""twoDigitsCode"": ""F20"" } }
    ]
  },
  {
    ""label"": ""End Of Month Options"", ""name"": ""E-mini S&P 500 EOM Options"",
    ""optionType"": ""EUR"", ""productId"": 2915, ""daily"": false, ""sto"": false, ""weekly"": false,
    ""expirations"": [
      { ""label"": ""Jan 2020"", ""productId"": 2915, ""contractId"": ""EWF0"",
        ""expiration"": { ""month"": 1, ""year"": 2020, ""code"": ""F0"", ""twoDigitsCode"": ""F20"" } }
    ]
  },
  {
    ""label"": ""Short-Dated Options"", ""name"": ""E-mini S&P 500 Short-Dated Options"",
    ""optionType"": ""AME"", ""productId"": 9000, ""daily"": false, ""sto"": true, ""weekly"": false,
    ""expirations"": [
      { ""label"": ""Mar 2020"", ""productId"": 9000, ""contractId"": ""STDH20"",
        ""expiration"": { ""month"": 3, ""year"": 2020, ""code"": ""H0"", ""twoDigitsCode"": ""H20"" } }
    ]
  },
  {
    ""label"": ""Weekly Options"", ""name"": ""Some unidentifiable weekly series"",
    ""optionType"": ""EUR"", ""productId"": 9001, ""daily"": false, ""sto"": false, ""weekly"": true,
    ""expirations"": [
      { ""label"": ""Jan 2020"", ""productId"": 9001, ""contractId"": ""XXF0"",
        ""expiration"": { ""month"": 1, ""year"": 2020, ""code"": ""F0"", ""twoDigitsCode"": ""F20"" } }
    ]
  }
]";

        private static List<CMEOptionsTradeDatesAndExpiration> ParseFixture()
        {
            var entries = JsonConvert.DeserializeObject<List<CMEOptionsTradeDatesAndExpiration>>(FixtureJson);
            Assert.AreEqual(6, entries.Count);
            return entries;
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            FutureOptionsRootRegistry.Reset();
        }

        [Test]
        public void StandardMaskMatchesLegacySelection()
        {
            var series = CmeFutureOptionChainMapper.MapSeries(_esMarch2020, ParseFixture(),
                FutureOptionExpiryCycles.Standard);

            // only the plain American monthly entry, resolved to the requested contract's expiration
            var single = series.Single();
            Assert.AreEqual("ES", single.OptionTicker);
            Assert.AreEqual(133, single.Entry.ProductId);
            Assert.AreEqual("H0", single.Expiration.Expiration.Code);
            Assert.AreEqual(new DateTime(2020, 3, 20), single.OptionExpiry.Date);
        }

        [Test]
        public void WeeklyMaskAddsEuropeanWeeklySeries()
        {
            var series = CmeFutureOptionChainMapper.MapSeries(_esMarch2020, ParseFixture(),
                FutureOptionExpiryCycles.Standard | FutureOptionExpiryCycles.Weekly);

            var byRoot = series.ToLookup(x => x.OptionTicker);
            Assert.AreEqual(new[] { "E1A", "ES", "EW2" }, byRoot.Select(x => x.Key).OrderBy(x => x).ToArray());

            // Friday week-2 weekly, January 2020: expires 2020-01-10 and exercises into the March future
            var ew2 = byRoot["EW2"].Single();
            Assert.AreEqual(new DateTime(2020, 1, 10), ew2.OptionExpiry.Date);
            Assert.AreEqual("F0", ew2.Expiration.Expiration.Code);
            // the European style code was accepted
            Assert.AreEqual("EUR", ew2.Entry.OptionType);

            // Monday week-1 weekly, January 2020: expires 2020-01-06
            var e1a = byRoot["E1A"].Single();
            Assert.AreEqual(new DateTime(2020, 1, 6), e1a.OptionExpiry.Date);
        }

        [Test]
        public void WeeklyExpirationsRouteToTheirExactUnderlyingContract()
        {
            // the May 2020 EW2 weekly (2020-05-08) exercises into the June future, not March:
            // it must appear for the June contract request and be excluded from the March one
            var marchSeries = CmeFutureOptionChainMapper.MapSeries(_esMarch2020, ParseFixture(),
                FutureOptionExpiryCycles.Weekly);
            Assert.AreEqual(new[] { new DateTime(2020, 1, 10) },
                marchSeries.Where(x => x.OptionTicker == "EW2").Select(x => x.OptionExpiry.Date).ToArray());

            var juneSeries = CmeFutureOptionChainMapper.MapSeries(_esJune2020, ParseFixture(),
                FutureOptionExpiryCycles.Weekly);
            Assert.AreEqual(new[] { new DateTime(2020, 5, 8) },
                juneSeries.Where(x => x.OptionTicker == "EW2").Select(x => x.OptionExpiry.Date).ToArray());
        }

        [Test]
        public void ShortDatedAndUnidentifiableEntriesAreDropped()
        {
            var series = CmeFutureOptionChainMapper.MapSeries(_esMarch2020, ParseFixture(),
                FutureOptionExpiryCycles.All);

            // the sto entry (productId 9000) and the weekly entry without a parseable week (9001)
            // must never be emitted
            Assert.IsFalse(series.Any(x => x.Entry.ProductId == 9000));
            Assert.IsFalse(series.Any(x => x.Entry.ProductId == 9001));
        }

        [Test]
        public void EndOfMonthSeriesRequireTheEndOfMonthCycle()
        {
            var withoutEom = CmeFutureOptionChainMapper.MapSeries(_esMarch2020, ParseFixture(),
                FutureOptionExpiryCycles.Standard | FutureOptionExpiryCycles.Weekly);
            Assert.IsFalse(withoutEom.Any(x => x.OptionTicker == "EW"));

            var withEom = CmeFutureOptionChainMapper.MapSeries(_esMarch2020, ParseFixture(),
                FutureOptionExpiryCycles.All);
            var eom = withEom.Single(x => x.OptionTicker == "EW");

            // January 2020 end-of-month option: expires on the last business day, into the March future
            Assert.AreEqual(new DateTime(2020, 1, 31), eom.OptionExpiry.Date);
            Assert.AreEqual(2915, eom.Entry.ProductId);
        }

        [Test]
        public void RootFilterRestrictsMapping()
        {
            var series = CmeFutureOptionChainMapper.MapSeries(_esMarch2020, ParseFixture(),
                FutureOptionExpiryCycles.All, new[] { "EW2" });

            Assert.IsNotEmpty(series);
            Assert.IsTrue(series.All(x => x.OptionTicker == "EW2"));
        }

        [Test]
        public void UnregisteredFutureFallsBackToLegacySingleEntrySelection()
        {
            try
            {
                // empty the registry: the mapper must reproduce the legacy provider behavior of
                // serving only the first non-daily/non-weekly/non-sto American entry
                FutureOptionsRootRegistry.SetDefinitionsForTesting(new List<FutureOptionRootDefinition>());

                var series = CmeFutureOptionChainMapper.MapSeries(_esMarch2020, ParseFixture(),
                    FutureOptionExpiryCycles.All);

                var single = series.Single();
                Assert.AreEqual("ES", single.OptionTicker);
                Assert.AreEqual(133, single.Entry.ProductId);
                Assert.AreEqual("H0", single.Expiration.Expiration.Code);
                Assert.AreEqual(new DateTime(2020, 3, 20), single.OptionExpiry.Date);
            }
            finally
            {
                FutureOptionsRootRegistry.Reset();
            }
        }
    }
}
