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

using NUnit.Framework;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Tests.Common.Securities.FutureOption
{
    /// <summary>
    /// fop-weeklies fork P4: the algorithm-level future option cycle registrations consulted by the
    /// chain providers
    /// </summary>
    [TestFixture, NonParallelizable, Category("FopFork")]
    public class FutureOptionChainCycleSettingsTests
    {
        [SetUp]
        public void SetUp()
        {
            FutureOptionChainCycleSettings.Reset();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            FutureOptionChainCycleSettings.Reset();
        }

        [Test]
        public void DefaultsToStandardWithNoRootFilter()
        {
            Assert.AreEqual(FutureOptionExpiryCycles.Standard, FutureOptionChainCycleSettings.GetCycles("ES", Market.CME));
            Assert.IsNull(FutureOptionChainCycleSettings.GetRootFilter("ES", Market.CME));
        }

        [Test]
        public void RegistrationIsCaseInsensitiveAndPerProduct()
        {
            FutureOptionChainCycleSettings.Register("es", "CME",
                FutureOptionExpiryCycles.Weekly, new[] { "ew3" });

            Assert.AreEqual(FutureOptionExpiryCycles.Weekly, FutureOptionChainCycleSettings.GetCycles("ES", Market.CME));
            CollectionAssert.AreEquivalent(new[] { "EW3" }, FutureOptionChainCycleSettings.GetRootFilter("ES", Market.CME));

            // other products remain at the default
            Assert.AreEqual(FutureOptionExpiryCycles.Standard, FutureOptionChainCycleSettings.GetCycles("GC", Market.COMEX));
        }

        [Test]
        public void MultipleRegistrationsUnionCyclesAndRootFilters()
        {
            FutureOptionChainCycleSettings.Register("ES", Market.CME, FutureOptionExpiryCycles.Standard, new[] { "ES" });
            FutureOptionChainCycleSettings.Register("ES", Market.CME, FutureOptionExpiryCycles.Weekly, new[] { "EW3" });

            Assert.AreEqual(FutureOptionExpiryCycles.Standard | FutureOptionExpiryCycles.Weekly,
                FutureOptionChainCycleSettings.GetCycles("ES", Market.CME));
            CollectionAssert.AreEquivalent(new[] { "ES", "EW3" }, FutureOptionChainCycleSettings.GetRootFilter("ES", Market.CME));
        }

        [Test]
        public void UnrestrictedRootFilterWinsOverExplicitLists()
        {
            FutureOptionChainCycleSettings.Register("ES", Market.CME, FutureOptionExpiryCycles.Weekly, new[] { "EW3" });
            FutureOptionChainCycleSettings.Register("ES", Market.CME, FutureOptionExpiryCycles.Standard);

            Assert.IsNull(FutureOptionChainCycleSettings.GetRootFilter("ES", Market.CME));
        }
    }
}
