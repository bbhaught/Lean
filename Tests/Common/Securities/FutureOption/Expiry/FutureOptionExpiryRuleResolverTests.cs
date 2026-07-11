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

namespace QuantConnect.Tests.Common.Securities.FutureOption.Expiry
{
    /// <summary>
    /// Fork-only tests (fop-weeklies) for <see cref="FutureOptionExpiryRuleResolver"/>
    /// </summary>
    [TestFixture, Category("FopFork")]
    public class FutureOptionExpiryRuleResolverTests
    {
        private static FutureOptionRootDefinition Definition(string ruleId, DayOfWeek? dayOfWeek = null, int? weekOfMonth = null)
        {
            return new FutureOptionRootDefinition
            {
                FutureTicker = "ES",
                OptionTicker = "TEST",
                Market = Market.CME,
                Cycle = FutureOptionExpiryCycles.Weekly,
                ExpiryDayOfWeek = dayOfWeek,
                WeekOfMonth = weekOfMonth,
                ExpiryRuleId = ruleId
            };
        }

        [TestCase(FutureOptionExpiryRuleResolver.Legacy)]
        [TestCase(FutureOptionExpiryRuleResolver.UnderlyingFuture)]
        [TestCase(null)]
        [TestCase("")]
        public void LegacyStyleRuleIdsAreNotResolvable(string ruleId)
        {
            var resolved = FutureOptionExpiryRuleResolver.TryResolve(Definition(ruleId), out var rule);

            Assert.IsFalse(resolved);
            Assert.IsNull(rule);
        }

        [Test]
        public void ResolvesNthWeekdayRule()
        {
            var resolved = FutureOptionExpiryRuleResolver.TryResolve(
                Definition(FutureOptionExpiryRuleResolver.NthWeekday, DayOfWeek.Friday, 3), out var rule);

            Assert.IsTrue(resolved);
            Assert.IsInstanceOf<NthWeekdayOfMonthExpiryRule>(rule);
        }

        [Test]
        public void ResolvesLastBusinessDayRule()
        {
            var resolved = FutureOptionExpiryRuleResolver.TryResolve(
                Definition(FutureOptionExpiryRuleResolver.LastBusinessDay), out var rule);

            Assert.IsTrue(resolved);
            Assert.IsInstanceOf<LastBusinessDayOfMonthExpiryRule>(rule);
        }

        [Test]
        public void ResolvesDailyRule()
        {
            var resolved = FutureOptionExpiryRuleResolver.TryResolve(
                Definition(FutureOptionExpiryRuleResolver.Daily), out var rule);

            Assert.IsTrue(resolved);
            Assert.IsInstanceOf<DailyExpiryRule>(rule);
        }

        [Test]
        public void NthWeekdayWithoutWeekdayThrows()
        {
            Assert.Throws<ArgumentException>(() => FutureOptionExpiryRuleResolver.TryResolve(
                Definition(FutureOptionExpiryRuleResolver.NthWeekday), out _));
        }

        [Test]
        public void UnknownRuleIdThrowsNamingRootAndRuleId()
        {
            var exception = Assert.Throws<NotSupportedException>(() => FutureOptionExpiryRuleResolver.TryResolve(
                Definition("no_such_rule"), out _));

            StringAssert.Contains("no_such_rule", exception.Message);
            StringAssert.Contains("TEST", exception.Message);
        }

        [Test]
        public void EquallyParameterizedRulesAreCached()
        {
            FutureOptionExpiryRuleResolver.TryResolve(
                Definition(FutureOptionExpiryRuleResolver.NthWeekday, DayOfWeek.Friday, 2), out var first);
            FutureOptionExpiryRuleResolver.TryResolve(
                Definition(FutureOptionExpiryRuleResolver.NthWeekday, DayOfWeek.Friday, 2), out var second);

            Assert.AreSame(first, second);
        }

        [Test]
        public void NullDefinitionThrows()
        {
            Assert.Throws<ArgumentNullException>(() => FutureOptionExpiryRuleResolver.TryResolve(null, out _));
        }
    }
}
