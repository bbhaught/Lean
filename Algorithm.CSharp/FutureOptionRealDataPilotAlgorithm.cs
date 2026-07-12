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
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// fop-weeklies fork P7 pilot algorithm: runs against REAL converted Databento data
    /// (tools/databento/dbn_to_lean_fop.py output). Subscribes the ES future plus its option
    /// chains with Standard AND Weekly cycles enabled, buys one near-ATM ITM call and one deep
    /// OTM put on the front contract, holds both through expiration and logs the full
    /// exercise/lapse/delivery lifecycle. Asserts at the end that chains populated from the
    /// converted universes, orders filled on real bars, the ITM option delivered its underlying
    /// and the OTM option lapsed. Weekly-root contracts are logged when present in the data
    /// (the quarterly-only pilot pull produces none; the weekly re-pull populates them).
    /// </summary>
    public class FutureOptionRealDataPilotAlgorithm : QCAlgorithm
    {
        private readonly HashSet<string> _optionRootsSeen = new();
        private readonly Dictionary<string, HashSet<DateTime>> _expiriesByRoot = new();
        private int _chainSnapshots;
        private Symbol _itmCall;
        private Symbol _otmPut;
        private bool _bought;
        private bool _sawDelivery;
        private bool _sawOptionTrade;

        public override void Initialize()
        {
            SetStartDate(2025, 9, 8);
            SetEndDate(2025, 9, 23);
            SetCash(500000);
            SetBenchmark(_ => 0m);

            var es = AddFuture(Futures.Indices.SP500EMini, Resolution.Minute, Market.CME);
            es.SetFilter(universe => universe.Expiration(0, 120));

            AddFutureOption(es.Symbol,
                universe => universe.Strikes(-30, +6).Expiration(0, 21),
                FutureOptionExpiryCycles.Standard | FutureOptionExpiryCycles.Weekly);
        }

        public override void OnData(Slice slice)
        {
            foreach (var (canonical, chain) in slice.OptionChains)
            {
                _chainSnapshots++;
                foreach (var contract in chain.Contracts.Keys)
                {
                    var root = contract.ID.Symbol;
                    _optionRootsSeen.Add(root);
                    if (!_expiriesByRoot.TryGetValue(root, out var expiries))
                    {
                        _expiriesByRoot[root] = expiries = new HashSet<DateTime>();
                    }
                    expiries.Add(contract.ID.Date.Date);
                }

                if (_bought || chain.Underlying == null || chain.Underlying.Price == 0)
                {
                    continue;
                }

                var underlyingPrice = chain.Underlying.Price;
                var calls = chain.Contracts.Values
                    .Where(x => x.Right == OptionRight.Call && x.Expiry.Date == new DateTime(2025, 9, 19)
                        && x.Strike < underlyingPrice)
                    .OrderByDescending(x => x.Strike)
                    .ToList();
                var puts = chain.Contracts.Values
                    .Where(x => x.Right == OptionRight.Put && x.Expiry.Date == new DateTime(2025, 9, 19)
                        && x.Strike < underlyingPrice - 50)
                    .OrderByDescending(x => x.Strike)
                    .ToList();
                if (calls.Count == 0 || puts.Count == 0)
                {
                    continue;
                }

                // wait until both freshly-subscribed contracts have received a real bar
                if (Securities[calls[0].Symbol].Price == 0 || Securities[puts[0].Symbol].Price == 0)
                {
                    continue;
                }

                _itmCall = calls[0].Symbol;
                _otmPut = puts[0].Symbol;
                Log($"{Time:yyyy-MM-dd HH:mm} BUY near-ATM ITM call {_itmCall.Value} " +
                    $"(strike {calls[0].Strike}, underlying {underlyingPrice}) and OTM put " +
                    $"{_otmPut.Value} (strike {puts[0].Strike})");
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
                $"{orderEvent.FillQuantity}@{orderEvent.FillPrice} ({orderEvent.Ticket.OrderType})");

            if (orderEvent.Symbol.SecurityType == SecurityType.Future)
            {
                _sawDelivery = true;
            }
            if (orderEvent.Symbol.SecurityType == SecurityType.FutureOption
                && orderEvent.Ticket.OrderType == OrderType.Market)
            {
                _sawOptionTrade = true;
            }
            if (orderEvent.IsAssignment || orderEvent.Ticket.OrderType == OrderType.OptionExercise)
            {
                Log($"{Time:yyyy-MM-dd HH:mm} EXERCISE/ASSIGNMENT event: {orderEvent}");
            }
        }

        public override void OnEndOfAlgorithm()
        {
            Log($"Chain snapshots: {_chainSnapshots}");
            Log($"Option roots seen: [{string.Join(", ", _optionRootsSeen.OrderBy(x => x))}]");
            foreach (var (root, expiries) in _expiriesByRoot.OrderBy(x => x.Key))
            {
                FutureOptionsRootRegistry.TryGetDefinition(root, Market.CME, out var definition);
                Log($"  root {root}: expiries [{string.Join(", ", expiries.OrderBy(x => x).Select(x => x.ToString("yyyyMMdd")))}] " +
                    $"cycle={definition?.Cycle.ToString() ?? "unknown"}");
            }

            if (_chainSnapshots == 0)
            {
                throw new Exception("PILOT FAIL: no option chains populated from converted universes");
            }
            if (!_sawOptionTrade)
            {
                throw new Exception("PILOT FAIL: option market orders never filled on real bars");
            }
            if (!_sawDelivery)
            {
                throw new Exception("PILOT FAIL: ITM exercise never delivered the underlying future");
            }
            if (Portfolio[_otmPut].Invested)
            {
                throw new Exception("PILOT FAIL: OTM put still open after expiration (should have lapsed)");
            }

            Log("PILOT OK: chains + fills + exercise/delivery + OTM lapse all observed on real data");
        }
    }
}
