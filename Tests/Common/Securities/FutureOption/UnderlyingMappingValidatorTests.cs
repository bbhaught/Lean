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
    /// Fork-only tests (fop-weeklies P3) for <see cref="UnderlyingMappingValidator"/>, the
    /// consistency check between an externally resolved option-to-future mapping and the
    /// mapper's own date-aware resolution (design B issue 2). Wired into the data converter in
    /// P7; these tests pin its contract until then
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class UnderlyingMappingValidatorTests
    {
        [Test]
        public void ConsistentWeeklyMappingValidates()
        {
            // CME-documented treasury case: Aug 2025 W5 weekly exercises into the Dec 2025 future
            var future = Symbol.CreateFuture("ZN", Market.CBOT, new DateTime(2025, 12, 19));
            var option = Symbol.CreateOption(future, "ZN5", Market.CBOT,
                OptionStyle.American, OptionRight.Call, 112.5m, new DateTime(2025, 8, 29));

            Assert.IsTrue(UnderlyingMappingValidator.TryValidate(option, out var error), error);
            Assert.IsNull(error);
        }

        [Test]
        public void ConsistentLegacyMonthlyMappingValidates()
        {
            // legacy path: GC monthly option for the Feb 2021 contract (upstream mapper test case)
            var future = Symbol.CreateFuture("GC", Market.COMEX, new DateTime(2021, 2, 24));
            var option = Symbol.CreateOption(future, "OG", Market.COMEX,
                OptionStyle.American, OptionRight.Call, 1900m, new DateTime(2021, 1, 26));

            Assert.IsTrue(UnderlyingMappingValidator.TryValidate(option, out var error), error);
        }

        [Test]
        public void MismatchedUnderlyingIsRejected()
        {
            // the Aug 2025 W5 weekly paired with the SEPTEMBER future: exactly the silent
            // corruption design B issue 2 describes, must be caught
            var future = Symbol.CreateFuture("ZN", Market.CBOT, new DateTime(2025, 9, 19));
            var option = Symbol.CreateOption(future, "ZN5", Market.CBOT,
                OptionStyle.American, OptionRight.Call, 112.5m, new DateTime(2025, 8, 29));

            Assert.IsFalse(UnderlyingMappingValidator.TryValidate(option, out var error));
            StringAssert.Contains("mismatch", error);
        }

        [Test]
        public void MismatchedExplicitExpiryIsRejected()
        {
            var future = Symbol.CreateFuture("ZN", Market.CBOT, new DateTime(2025, 12, 19));
            var option = Symbol.CreateOption(future, "ZN5", Market.CBOT,
                OptionStyle.American, OptionRight.Call, 112.5m, new DateTime(2025, 8, 29));

            Assert.IsTrue(UnderlyingMappingValidator.TryValidate(option, future.ID.Date, out var error), error);
            Assert.IsFalse(UnderlyingMappingValidator.TryValidate(option, new DateTime(2025, 9, 19, 12, 1, 0), out error));
            StringAssert.Contains("mismatch", error);
        }

        [Test]
        public void CanonicalSymbolIsRejected()
        {
            var future = Symbol.Create("ES", SecurityType.Future, Market.CME);
            var canonical = Symbol.CreateCanonicalOption(future, "EW1", Market.CME, null);

            Assert.IsFalse(UnderlyingMappingValidator.TryValidate(canonical, out var error));
            StringAssert.Contains("canonical", error);
        }

        [Test]
        public void NonFutureOptionIsRejected()
        {
            var equityOption = Symbol.CreateOption("SPY", Market.USA,
                OptionStyle.American, OptionRight.Call, 400m, new DateTime(2025, 8, 15));

            Assert.IsFalse(UnderlyingMappingValidator.TryValidate(equityOption, out var error));
            StringAssert.Contains("not a futures option", error);
        }

        [Test]
        public void NullSymbolThrows()
        {
            Assert.Throws<ArgumentNullException>(() => UnderlyingMappingValidator.TryValidate(null, out _));
        }
    }
}
