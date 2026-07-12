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
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// fop-weeklies fork P7 pilot algorithm, WEEKLY gate: runs against REAL converted Databento
    /// weekly-series data (the pilot_es_weekly re-pull). Subscribes ES with Standard+Weekly
    /// cycles, requires multiple weekly roots in the chains, buys one near-ATM ITM WEEKLY call
    /// (the week-2 Friday EW2 expiring 2025-10-10) and holds it through expiration. Asserts the
    /// weekly exercised into the NEXT QUARTERLY future (ESZ5, December) - the design-B issue-2
    /// mapping proven end-to-end on real data - and that a deep OTM weekly put lapsed.
    /// </summary>
    public class FutureOptionWeeklyRealDataPilotAlgorithm : QCAlgorithm
    {
        private static readonly DateTime WeeklyExpiry = new(2025, 10, 10);
        private readonly HashSet<string> _optionRootsSeen = new();
        private int _chainSnapshots;
        private Symbol _itmCall;
        private Symbol _otmPut;
        private bool _bought;
        private Symbol _deliveredFuture;
        private bool _sawOptionTrade;

        public override void Initialize()
        {
            SetStartDate(2025, 10, 6);
            SetEndDate(2025, 10, 14);
            SetCash(500000);
            SetBenchmark(_ => 0m);

            var es = AddFuture(Futures.Indices.SP500EMini, Resolution.Minute, Market.CME);
            es.SetFilter(universe => universe.Expiration(0, 120));

            AddFutureOption(es.Symbol,
                universe => universe.Strikes(-30, +6).Expiration(0, 14),
                FutureOptionExpiryCycles.Standard | FutureOptionExpiryCycles.Weekly | FutureOptionExpiryCycles.EndOfMonth);
        }

        public override void OnData(Slice slice)
        {
            foreach (var (_, chain) in slice.OptionChains)
            {
                _chainSnapshots++;
                foreach (var contract in chain.Contracts.Keys)
                {
                    if (_optionRootsSeen.Add(contract.ID.Symbol))
                    {
                        Log($"{Time:yyyy-MM-dd HH:mm} first contract for root {contract.ID.Symbol}: " +
                            $"{contract.Value} expiry {contract.ID.Date:yyyyMMdd} underlying {contract.Underlying.Value}");
                    }
                }

                if (_bought || chain.Underlying == null || chain.Underlying.Price == 0)
                {
                    continue;
                }

                var underlyingPrice = chain.Underlying.Price;
                var weeklys = chain.Contracts.Values
                    .Where(x => x.Symbol.ID.Symbol == "EW2" && x.Expiry.Date == WeeklyExpiry)
                    .ToList();
                var calls = weeklys.Where(x => x.Right == OptionRight.Call && x.Strike < underlyingPrice)
                    .OrderByDescending(x => x.Strike).ToList();
                var puts = weeklys.Where(x => x.Right == OptionRight.Put && x.Strike < underlyingPrice - 50)
                    .OrderByDescending(x => x.Strike).ToList();
                if (calls.Count == 0 || puts.Count == 0)
                {
                    continue;
                }
                if (Securities[calls[0].Symbol].Price == 0 || Securities[puts[0].Symbol].Price == 0)
                {
                    continue;
                }

                _itmCall = calls[0].Symbol;
                _otmPut = puts[0].Symbol;
                Log($"{Time:yyyy-MM-dd HH:mm} BUY weekly ITM call {_itmCall.Value} (root {_itmCall.ID.Symbol}, " +
                    $"strike {calls[0].Strike}, underlying {_itmCall.Underlying.Value} @ {underlyingPrice}) " +
                    $"and weekly OTM put {_otmPut.Value} (strike {puts[0].Strike})");
                MarketOrder(_itmCall, 1);
                MarketOrder(_otmPut, 1);
                _bought = true;
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }

            Log($"{Time:yyyy-MM-dd HH:mm} FILL {orderEvent.Symbol.Value} " +
                $"{orderEvent.FillQuantity}@{orderEvent.FillPrice} ({orderEvent.Ticket.OrderType}) {orderEvent.Message}");

            if (orderEvent.Symbol.SecurityType == SecurityType.Future
                && orderEvent.Ticket.OrderType == OrderType.OptionExercise)
            {
                _deliveredFuture = orderEvent.Symbol;
            }
            if (orderEvent.Symbol.SecurityType == SecurityType.FutureOption
                && orderEvent.Ticket.OrderType == OrderType.Market)
            {
                _sawOptionTrade = true;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Log($"Chain snapshots: {_chainSnapshots}");
            Log($"Option roots seen: [{string.Join(", ", _optionRootsSeen.OrderBy(x => x))}]");

            var weeklyRoots = _optionRootsSeen.Where(root =>
                FutureOptionsRootRegistry.TryGetDefinition(root, Market.CME, out var definition)
                && definition.Cycle == FutureOptionExpiryCycles.Weekly).ToList();
            if (weeklyRoots.Count < 3)
            {
                throw new Exception($"PILOT WEEKLY FAIL: expected >=3 weekly roots in real-data chains, saw [{string.Join(", ", weeklyRoots)}]");
            }
            if (!_sawOptionTrade)
            {
                throw new Exception("PILOT WEEKLY FAIL: weekly option market orders never filled on real bars");
            }
            if (_deliveredFuture == null)
            {
                throw new Exception("PILOT WEEKLY FAIL: ITM weekly exercise never delivered the underlying future");
            }
            // the October weekly must exercise into the NEXT QUARTERLY future: December (ESZ5)
            if (_deliveredFuture.ID.Date.Month != 12 || _deliveredFuture.ID.Date.Year != 2025)
            {
                throw new Exception($"PILOT WEEKLY FAIL: weekly delivered {_deliveredFuture.Value} " +
                    $"(expiry {_deliveredFuture.ID.Date:yyyyMMdd}), expected the December 2025 quarterly");
            }
            if (Portfolio[_otmPut].Invested)
            {
                throw new Exception("PILOT WEEKLY FAIL: OTM weekly put still open after expiration");
            }

            Log("PILOT WEEKLY OK: weekly chains + fills + European exercise into next-quarterly delivery + OTM lapse on real data");
        }
    }
}
