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
    /// Fork-only tests (fop-weeklies) for the registry-driven
    /// <see cref="FutureOptionSymbol.IsStandard"/> classification (design A section 1.4)
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionSymbolIsStandardTests
    {
        // standard monthly/quarterly roots
        [TestCase("ES", "ES", Market.CME, true)]
        [TestCase("ZN", "OZN", Market.CBOT, true)]
        [TestCase("CL", "LO", Market.NYMEX, true)]
        [TestCase("GC", "OG", Market.COMEX, true)]
        [TestCase("6J", "JPU", Market.CME, true)]
        // weekly roots
        [TestCase("ES", "EW3", Market.CME, false)]
        [TestCase("ES", "E1A", Market.CME, false)]
        [TestCase("ES", "E5D", Market.CME, false)]
        [TestCase("CL", "LO1", Market.NYMEX, false)]
        [TestCase("GC", "OG5", Market.COMEX, false)]
        [TestCase("ZN", "ZN2", Market.CBOT, false)]
        [TestCase("ZN", "WY4", Market.CBOT, false)]
        // end-of-month root
        [TestCase("ES", "EW", Market.CME, false)]
        // unknown roots preserve the legacy always-standard behavior
        [TestCase("ES", "XYZ9", Market.CME, true)]
        [TestCase("6J", "WJ1", Market.CME, true)]
        public void ClassifiesContractsByRegistryExpiryCycle(string futureTicker, string optionRoot, string market, bool expected)
        {
            var future = Symbol.CreateFuture(futureTicker, market, new DateTime(2026, 6, 19));
            var option = Symbol.CreateOption(future, optionRoot, market, OptionStyle.American,
                OptionRight.Call, 100m, new DateTime(2026, 3, 6));

            Assert.AreEqual(expected, FutureOptionSymbol.IsStandard(option));
        }
    }
}
