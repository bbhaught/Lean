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
using QuantConnect.Configuration;
using QuantConnect.Securities.Future;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption.Expiry
{
    /// <summary>
    /// Fork-only tests (fop-weeklies P2, design B issue 7): the upstream silent fallback to the
    /// underlying future's expiry for unknown futures-option roots is deleted. Unknown roots throw
    /// <see cref="NotSupportedException"/> naming the root and market; the 'fop-strict-expiry'
    /// flag (default true) can restore the legacy fallback for migration scenarios.
    /// Upstream call-site audit: the nine legacy-mapping roots without expiry-table entries
    /// (OEH, HCO, OH, PAO, PO, OB, OYG, OZG, OZI) legitimately relied on the fallback and were
    /// moved to the explicit 'underlying_future' registry rule instead (covered by the golden
    /// snapshot test); no upstream test exercises the fallback for any other root
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FuturesOptionsExpiryStrictFallbackTests
    {
        private const string ConfigKey = "fop-strict-expiry";

        [TearDown]
        public void TearDown()
        {
            Config.Set(ConfigKey, "true");
        }

        [Test]
        public void UnknownRootThrowsNamingRootAndMarket()
        {
            // BCF (Black Sea Corn, CBOT) has a futures expiry function but no futures-option root
            // in either the legacy expiry table or the registry
            var future = Symbol.Create("BCF", SecurityType.Future, Market.CBOT);
            var canonicalOption = Symbol.CreateCanonicalOption(future);

            var exception = Assert.Throws<NotSupportedException>(
                () => FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalOption, new DateTime(2025, 6, 1)));

            StringAssert.Contains("'BCF'", exception.Message);
            StringAssert.Contains($"'{Market.CBOT}'", exception.Message);
        }

        [Test]
        public void UnknownWeeklyStyleRootThrowsNamingRootAndMarket()
        {
            // A custom target-option root unknown to both the legacy table and the registry.
            // Pre-fork this silently returned the ES future's expiry
            var future = Symbol.Create("ES", SecurityType.Future, Market.CME);
            var canonicalOption = Symbol.CreateCanonicalOption(future, "XX9", Market.CME, null);

            var exception = Assert.Throws<NotSupportedException>(
                () => FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalOption, new DateTime(2025, 6, 1)));

            StringAssert.Contains("'XX9'", exception.Message);
            StringAssert.Contains($"'{Market.CME}'", exception.Message);
        }

        [Test]
        public void GetFutureOptionExpiryFromFutureExpiryAlsoThrowsForUnknownRoot()
        {
            var futureExpiry = FuturesExpiryFunctions.FuturesExpiryFunction(
                Symbol.Create("BCF", SecurityType.Future, Market.CBOT))(new DateTime(2025, 6, 1));
            var future = Symbol.CreateFuture("BCF", Market.CBOT, futureExpiry);

            Assert.Throws<NotSupportedException>(
                () => FuturesOptionsExpiryFunctions.GetFutureOptionExpiryFromFutureExpiry(future));
        }

        [Test]
        public void DisablingStrictExpiryRestoresLegacySilentFallback()
        {
            Config.Set(ConfigKey, "false");

            var future = Symbol.Create("BCF", SecurityType.Future, Market.CBOT);
            var canonicalOption = Symbol.CreateCanonicalOption(future);
            var contractMonth = new DateTime(2025, 6, 1);

            var expiry = FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalOption, contractMonth);
            var futureExpiry = FuturesExpiryFunctions.FuturesExpiryFunction(future)(contractMonth);

            Assert.AreEqual(futureExpiry, expiry);
        }

        [Test]
        public void FeederCattleResolvesToFutureExpiryViaExplicitUnderlyingFutureRule()
        {
            // GF was found by the upstream call-site audit: DataDownloadConfigTests parses
            // "GFH6 C368.5" through SymbolRepresentation.ParseFutureOptionSymbol, which relied on
            // the silent fallback. GF options terminate with the future, so the value was correct;
            // the registry now declares it via the explicit "underlying_future" rule
            var future = Symbol.Create("GF", SecurityType.Future, Market.CME);
            var canonicalOption = Symbol.CreateCanonicalOption(future);
            var contractMonth = new DateTime(2026, 3, 1);

            var expiry = FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalOption, contractMonth);

            Assert.AreEqual(FuturesExpiryFunctions.FuturesExpiryFunction(future)(contractMonth), expiry);
        }

        [Test]
        public void KnownWeeklyRootsDoNotHitTheFallback()
        {
            var future = Symbol.Create("ES", SecurityType.Future, Market.CME);
            var canonicalOption = Symbol.CreateCanonicalOption(future, "EW2", Market.CME, null);

            Assert.DoesNotThrow(
                () => FuturesOptionsExpiryFunctions.FuturesOptionExpiry(canonicalOption, new DateTime(2025, 6, 1)));
        }
    }
}
