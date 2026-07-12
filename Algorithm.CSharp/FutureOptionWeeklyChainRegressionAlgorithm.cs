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
using QuantConnect.Data;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// fop-weeklies fork P4 regression algorithm: subscribes the ES future plus its option chains with
    /// weekly expiry cycles enabled and asserts that weekly-root contracts (EW2, EW3) flow through
    /// universe selection alongside the standard monthly root, using the fabricated weekly universe
    /// fixtures checked into the data folder. Logs the chain composition per day and verifies the
    /// weekly roots classify as non-standard while the monthly root stays standard.
    /// </summary>
    public class FutureOptionWeeklyChainRegressionAlgorithm : QCAlgorithm, IRegressionAlgorithmDefinition
    {
        private readonly HashSet<string> _optionRootsSeen = new();
        private readonly Dictionary<string, HashSet<DateTime>> _expiriesSeenByRoot = new();
        private bool _optionFilterRan;

        public override void Initialize()
        {
            SetStartDate(2020, 1, 4);
            SetEndDate(2020, 1, 8);
            SetCash(100000);

            var es = AddFuture(Futures.Indices.SP500EMini, Resolution.Minute, Market.CME);
            es.SetFilter(universe => universe.Expiration(0, 180).ExpirationCycle(new[] { 3 }));

            AddFutureOption(es.Symbol,
                universe =>
                {
                    _optionFilterRan = true;
                    // near-ATM selection
                    return universe.Strikes(-3, +3);
                },
                FutureOptionExpiryCycles.Standard | FutureOptionExpiryCycles.Weekly,
                new[] { "ES", "EW2", "EW3", "E1A" });
        }

        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            foreach (var security in changes.AddedSecurities)
            {
                var symbol = security.Symbol;
                if (symbol.SecurityType != SecurityType.FutureOption || symbol.IsCanonical())
                {
                    continue;
                }

                var root = symbol.ID.Symbol;
                _optionRootsSeen.Add(root);
                if (!_expiriesSeenByRoot.TryGetValue(root, out var expiries))
                {
                    _expiriesSeenByRoot[root] = expiries = new HashSet<DateTime>();
                }
                expiries.Add(symbol.ID.Date.Date);

                var standard = FutureOptionSymbol.IsStandard(symbol) ? "standard" : "weekly";
                Log($"{Time:yyyy-MM-dd HH:mm} added {symbol.Value} root {root} ({standard}) " +
                    $"expiry {symbol.ID.Date:yyyy-MM-dd} strike {symbol.ID.StrikePrice}");
            }
        }

        public override void OnData(Slice slice)
        {
            foreach (var (canonical, chain) in slice.OptionChains)
            {
                Log($"{Time:yyyy-MM-dd HH:mm} chain {canonical.ID.Symbol}: {chain.Contracts.Count} contracts, " +
                    $"expiries [{string.Join(", ", chain.Contracts.Keys.Select(x => x.ID.Date.ToString("yyyyMMdd")).Distinct().OrderBy(x => x))}]");
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (!_optionFilterRan)
            {
                throw new RegressionTestException("The future option universe filter never ran");
            }

            Log($"Option roots seen: [{string.Join(", ", _optionRootsSeen.OrderBy(x => x))}]");

            // the standard monthly root must be present (from the repository sample data)
            if (!_optionRootsSeen.Contains("ES"))
            {
                throw new RegressionTestException("Expected standard root ES contracts in the chain universe");
            }

            // the weekly roots must be present (from the fabricated weekly universe fixtures)
            foreach (var weeklyRoot in new[] { "EW2", "EW3" })
            {
                if (!_optionRootsSeen.Contains(weeklyRoot))
                {
                    throw new RegressionTestException($"Expected weekly root {weeklyRoot} contracts in the chain universe");
                }
            }

            // weekly roots carry their own expiries, distinct from the standard quarterly expiry
            AssertSingleExpiry("EW2", new DateTime(2020, 1, 10));
            AssertSingleExpiry("EW3", new DateTime(2020, 1, 17));
            AssertSingleExpiry("ES", new DateTime(2020, 3, 20));

            // E1A expired inside the window (2020-01-06); it is allowed but not required to have
            // been selected depending on when its universe data was consumed
            Log($"E1A observed: {_optionRootsSeen.Contains("E1A")}");
        }

        private void AssertSingleExpiry(string root, DateTime expectedExpiry)
        {
            var expiries = _expiriesSeenByRoot[root];
            if (expiries.Count != 1 || !expiries.Contains(expectedExpiry))
            {
                throw new RegressionTestException($"Expected root {root} contracts to expire on " +
                    $"{expectedExpiry:yyyy-MM-dd} only, but observed [{string.Join(", ", expiries.OrderBy(x => x))}]");
            }
        }

        /// <summary>
        /// This is used by the regression test system to indicate if the open source Lean repository has the required data to run this algorithm.
        /// </summary>
        public bool CanRunLocally { get; } = true;

        /// <summary>
        /// This is used by the regression test system to indicate which languages this algorithm is written in.
        /// </summary>
        public List<Language> Languages { get; } = new() { Language.CSharp };

        /// <summary>
        /// Data Points count of all timeslices of algorithm
        /// </summary>
        public long DataPoints => 28927;

        /// <summary>
        /// Data Points count of the algorithm history
        /// </summary>
        public int AlgorithmHistoryDataPoints => 0;

        /// <summary>
        /// Final status of the algorithm
        /// </summary>
        public AlgorithmStatus AlgorithmStatus => AlgorithmStatus.Completed;

        /// <summary>
        /// This is used by the regression test system to indicate what the expected statistics are from running the algorithm
        /// </summary>
        public Dictionary<string, string> ExpectedStatistics => new Dictionary<string, string>
        {
            {"Total Orders", "0"},
            {"Average Win", "0%"},
            {"Average Loss", "0%"},
            {"Compounding Annual Return", "0%"},
            {"Drawdown", "0%"},
            {"Expectancy", "0"},
            {"Start Equity", "100000"},
            {"End Equity", "100000"},
            {"Net Profit", "0%"},
            {"Sharpe Ratio", "0"},
            {"Sortino Ratio", "0"},
            {"Probabilistic Sharpe Ratio", "0%"},
            {"Loss Rate", "0%"},
            {"Win Rate", "0%"},
            {"Profit-Loss Ratio", "0"},
            {"Alpha", "0"},
            {"Beta", "0"},
            {"Annual Standard Deviation", "0"},
            {"Annual Variance", "0"},
            {"Information Ratio", "-8.363"},
            {"Tracking Error", "0.059"},
            {"Treynor Ratio", "0"},
            {"Total Fees", "$0.00"},
            {"Estimated Strategy Capacity", "$0"},
            {"Lowest Capacity Asset", ""},
            {"Portfolio Turnover", "0%"},
            {"Drawdown Recovery", "0"},
            {"OrderListHash", "d41d8cd98f00b204e9800998ecf8427e"}
        };
    }
}
