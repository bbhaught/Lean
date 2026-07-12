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
using QuantConnect.Securities.Future;
using QuantConnect.Securities.FutureOption;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// fop-weeklies fork: classic WHEEL strategy on ES weekly futures options, run against the
    /// REAL converted Databento pilot year (2025-07-01..2026-06-30, /home/bb/data/lean-fop-pilot).
    /// First real strategy test of the weekly-FOP fork.
    ///
    /// Rules:
    /// - State A (flat): sell 1 OTM Friday-weekly put (roots EW/EW1-4), expiry 3-10 calendar days
    ///   out (nearest expiry first; the spec's "5-9 days from Monday" window contains no Friday -
    ///   Monday to same-week Friday is 4 days - so the window is widened to 3-10 and the nearest
    ///   Friday weekly is taken, i.e. the classic same-week Friday when entered on Monday).
    ///   Strike nearest to 3.5% below the chain underlying price (proxy for ~25-30 delta).
    ///   Entry from 10:00 ET. Hold to expiry: OTM -> keep premium; ITM -> European auto-exercise
    ///   at the 15:00 CT settlement mark assigns LONG 1 ES future at strike (P6 exercise model).
    /// - State B (long 1 future from assignment): sell 1 OTM weekly call, same selection logic,
    ///   strike nearest 3.5% ABOVE price, restricted to contracts whose mapped underlying equals
    ///   the held future (always covered, never naked). Called away (ITM at expiry -> short future
    ///   delivered, netting flat) -> back to State A. OTM -> sell again next week.
    /// - Fills: limit at bid/ask mid (rounded to tick); if unfilled 30 minutes later the limit is
    ///   moved to the current bid (cross the spread); if still unfilled 30 minutes after that,
    ///   fall back to a market order. Quote bars are tbbo-derived (trade-time-sampled BBO).
    /// - Risk: if the held future drops more than 10% below the assignment strike, buy back the
    ///   open call and liquidate the future (STOP), restart State A. If the held future is within
    ///   10 days of its own expiry and no call is open, liquidate it (ROLL EXIT) - weeklies in
    ///   that window map to the next quarterly so no covered call exists on the held contract.
    /// - Holiday weeks with no listed weekly chain are skipped naturally (no candidate).
    /// - Never more than 1 wheel position; 1 contract (ES multiplier 50, ~$330k notional on $500k).
    /// </summary>
    public class FutureOptionWheelPilotAlgorithm : QCAlgorithm
    {
        private static readonly HashSet<string> FridayWeeklyRoots = new() { "EW", "EW1", "EW2", "EW3", "EW4" };
        private static readonly TimeSpan EntryStart = new(10, 0, 0);   // ET
        private static readonly TimeSpan EntryEnd = new(15, 30, 0);    // ET
        private const decimal OtmTarget = 0.035m;                      // 3.5% OTM strike target
        private const decimal StopPct = 0.10m;                         // stop: future 10% below assignment strike
        private const int MinDte = 3;
        private const int MaxDte = 10;
        private const int RollExitDays = 10;                           // liquidate future this close to its expiry

        private Future _es;

        // wheel state
        private bool _longFuture;                 // false = State A (flat), true = State B (long 1 future)
        private Symbol _heldFuture;
        private decimal _assignmentStrike;
        private Symbol _openOption;               // the currently-short weekly option
        private OrderTicket _pendingTicket;
        private DateTime _pendingSince;
        private bool _crossedToBid;
        private bool _stopInProgress;
        private DateTime _skipLogDate;

        // accounting
        private decimal _premiumCollected;
        private decimal _premiumPaidBack;
        private int _putsSold;
        private int _callsSold;
        private int _assignments;
        private int _calledAway;
        private int _stops;
        private int _rollExits;
        private int _putLapses;
        private int _callLapses;
        private decimal _peakEquity;
        private decimal _maxDrawdown;
        private DateTime _lastMonthLogged;

        public override void Initialize()
        {
            Log($"Live Mode: {LiveMode}");
            SetStartDate(2025, 7, 1);
            SetEndDate(2026, 6, 30);
            SetCash(500000);
            SetBenchmark(_ => 0m);

            _es = AddFuture(Futures.Indices.SP500EMini, Resolution.Minute, Market.CME);
            _es.SetFilter(universe => universe.Expiration(0, 120));

            AddFutureOption(_es.Symbol,
                universe => universe.Strikes(-80, +80).Expiration(0, MaxDte),
                FutureOptionExpiryCycles.Weekly | FutureOptionExpiryCycles.EndOfMonth);

            _peakEquity = Portfolio.TotalPortfolioValue;
        }

        public override void OnData(Slice slice)
        {
            TrackEquity();
            ManagePendingOrder();
            CheckStop();
            CheckRollExit();
            TryEnter(slice);
        }

        private void TrackEquity()
        {
            var equity = Portfolio.TotalPortfolioValue;
            if (equity > _peakEquity)
            {
                _peakEquity = equity;
            }
            else if (_peakEquity > 0)
            {
                var dd = (_peakEquity - equity) / _peakEquity;
                if (dd > _maxDrawdown)
                {
                    _maxDrawdown = dd;
                }
            }

            if (Time.Month != _lastMonthLogged.Month || Time.Year != _lastMonthLogged.Year)
            {
                _lastMonthLogged = Time.Date;
                Log($"{Time:yyyy-MM-dd} EQUITY {equity:F0} (peak {_peakEquity:F0}, maxDD {_maxDrawdown:P2})");
            }
        }

        /// <summary>Limit-at-mid entry management: cross to bid after 30 min, market after 60.</summary>
        private void ManagePendingOrder()
        {
            if (_pendingTicket == null)
            {
                return;
            }
            if (_pendingTicket.Status == OrderStatus.Filled)
            {
                _pendingTicket = null;
                return;
            }
            if (_pendingTicket.Status == OrderStatus.Canceled || _pendingTicket.Status == OrderStatus.Invalid)
            {
                _pendingTicket = null;
                return;
            }

            var elapsed = Time - _pendingSince;
            var symbol = _pendingTicket.Symbol;
            if (elapsed >= TimeSpan.FromMinutes(60))
            {
                Log($"{Time:yyyy-MM-dd HH:mm} ENTRY unfilled 60 min, cancel limit and go market on {symbol.Value}");
                _pendingTicket.Cancel();
                _pendingTicket = MarketOrder(symbol, -1, asynchronous: true);
                _pendingSince = Time;
                _crossedToBid = true;
            }
            else if (elapsed >= TimeSpan.FromMinutes(30) && !_crossedToBid)
            {
                var bid = Securities[symbol].BidPrice;
                if (bid > 0 && bid != _pendingTicket.Get(OrderField.LimitPrice))
                {
                    Log($"{Time:yyyy-MM-dd HH:mm} ENTRY unfilled 30 min, moving limit to bid {bid} on {symbol.Value}");
                    _pendingTicket.UpdateLimitPrice(bid);
                }
                _crossedToBid = true;
            }
        }

        /// <summary>State B stop: future more than 10% below the assignment strike -> flatten everything.</summary>
        private void CheckStop()
        {
            if (!_longFuture || _stopInProgress || _heldFuture == null)
            {
                return;
            }
            var price = Securities[_heldFuture].Price;
            if (price <= 0 || price >= _assignmentStrike * (1m - StopPct))
            {
                return;
            }

            _stopInProgress = true;
            Log($"{Time:yyyy-MM-dd HH:mm} STOP: {_heldFuture.Value} @ {price} is >{StopPct:P0} below assignment strike " +
                $"{_assignmentStrike}. Flattening future and buying back open call.");
            if (_pendingTicket != null && !_pendingTicket.Status.IsClosed())
            {
                _pendingTicket.Cancel();
                _pendingTicket = null;
            }
            if (_openOption != null && Portfolio[_openOption].Invested)
            {
                MarketOrder(_openOption, 1);
            }
            _openOption = null;
            MarketOrder(_heldFuture, -Portfolio[_heldFuture].Quantity);
            _stops++;
            _longFuture = false;
            _heldFuture = null;
            _assignmentStrike = 0;
            _stopInProgress = false;
        }

        /// <summary>
        /// State B calendar exit: within RollExitDays of the held future's own expiry no weekly maps
        /// to it any more (weeklies map to the next quarterly), so once no call is open, liquidate.
        /// </summary>
        private void CheckRollExit()
        {
            if (!_longFuture || _heldFuture == null || _openOption != null)
            {
                return;
            }
            if (_pendingTicket != null && !_pendingTicket.Status.IsClosed())
            {
                return;
            }
            if ((_heldFuture.ID.Date.Date - Time.Date).TotalDays >= RollExitDays)
            {
                return;
            }

            var price = Securities[_heldFuture].Price;
            Log($"{Time:yyyy-MM-dd HH:mm} ROLL EXIT: {_heldFuture.Value} expires {_heldFuture.ID.Date:yyyy-MM-dd}, " +
                $"liquidating @ ~{price} (assignment strike was {_assignmentStrike})");
            MarketOrder(_heldFuture, -Portfolio[_heldFuture].Quantity);
            _rollExits++;
            _longFuture = false;
            _heldFuture = null;
            _assignmentStrike = 0;
        }

        /// <summary>Sell the weekly put (State A) or covered weekly call (State B).</summary>
        private void TryEnter(Slice slice)
        {
            if (_pendingTicket != null || _openOption != null)
            {
                return;
            }
            if (Time.TimeOfDay < EntryStart || Time.TimeOfDay > EntryEnd)
            {
                return;
            }
            // never sell a call unless the future is actually in the portfolio (covered)
            if (_longFuture && (_heldFuture == null || !Portfolio[_heldFuture].Invested))
            {
                return;
            }

            OptionContract best = null;
            var bestExpiry = DateTime.MaxValue;
            var bestScore = decimal.MaxValue;
            decimal bestReference = 0;

            foreach (var (_, chain) in slice.OptionChains)
            {
                if (chain.Underlying == null || chain.Underlying.Price == 0)
                {
                    continue;
                }
                var reference = chain.Underlying.Price;
                foreach (var contract in chain.Contracts.Values)
                {
                    if (!FridayWeeklyRoots.Contains(contract.Symbol.ID.Symbol))
                    {
                        continue;
                    }
                    var dte = (contract.Expiry.Date - Time.Date).TotalDays;
                    if (dte < MinDte || dte > MaxDte)
                    {
                        continue;
                    }
                    if (_longFuture)
                    {
                        if (contract.Right != OptionRight.Call
                            || !contract.Symbol.Underlying.Equals(_heldFuture)
                            || contract.Strike <= reference)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        if (contract.Right != OptionRight.Put || contract.Strike >= reference)
                        {
                            continue;
                        }
                    }
                    if (contract.BidPrice <= 0)
                    {
                        continue;
                    }

                    var target = _longFuture ? reference * (1m + OtmTarget) : reference * (1m - OtmTarget);
                    var score = Math.Abs(contract.Strike - target);
                    if (contract.Expiry.Date < bestExpiry.Date
                        || (contract.Expiry.Date == bestExpiry.Date && score < bestScore))
                    {
                        best = contract;
                        bestExpiry = contract.Expiry;
                        bestScore = score;
                        bestReference = reference;
                    }
                }
            }

            if (best == null)
            {
                if (_skipLogDate != Time.Date && Time.TimeOfDay >= new TimeSpan(12, 0, 0))
                {
                    _skipLogDate = Time.Date;
                    Log($"{Time:yyyy-MM-dd HH:mm} no eligible Friday-weekly {(_longFuture ? "call" : "put")} " +
                        $"candidate ({MinDte}-{MaxDte} dte with a bid) - skipping");
                }
                return;
            }

            var security = Securities[best.Symbol];
            var tick = security.SymbolProperties.MinimumPriceVariation;
            var mid = best.AskPrice > best.BidPrice && best.AskPrice > 0
                ? (best.BidPrice + best.AskPrice) / 2m
                : best.BidPrice;
            if (tick > 0)
            {
                mid = Math.Floor(mid / tick) * tick;
            }
            if (mid < best.BidPrice)
            {
                mid = best.BidPrice;
            }

            var otmPct = _longFuture
                ? (best.Strike - bestReference) / bestReference
                : (bestReference - best.Strike) / bestReference;
            Log($"{Time:yyyy-MM-dd HH:mm} SELL {(_longFuture ? "CALL" : "PUT")} {best.Symbol.Value} " +
                $"(root {best.Symbol.ID.Symbol}, strike {best.Strike}, expiry {best.Expiry:yyyy-MM-dd}, " +
                $"underlying {best.Symbol.Underlying.Value} @ {bestReference}, {otmPct:P2} OTM, " +
                $"bid {best.BidPrice} ask {best.AskPrice}) limit @ mid {mid}");
            _pendingTicket = LimitOrder(best.Symbol, -1, mid);
            _pendingSince = Time;
            _crossedToBid = false;
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }

            var multiplier = Securities[orderEvent.Symbol].SymbolProperties.ContractMultiplier;
            Log($"{Time:yyyy-MM-dd HH:mm} FILL {orderEvent.Symbol.Value} " +
                $"{orderEvent.FillQuantity}@{orderEvent.FillPrice} ({orderEvent.Ticket.OrderType}) {orderEvent.Message}");

            if (orderEvent.Symbol.SecurityType == SecurityType.FutureOption)
            {
                if (orderEvent.Ticket.OrderType == OrderType.OptionExercise)
                {
                    // expiry processing of our short weekly (P6 settlement-mark exercise model)
                    _openOption = null;
                    if (orderEvent.Message.Contains("OTM"))
                    {
                        var right = orderEvent.Symbol.ID.OptionRight;
                        if (right == OptionRight.Put)
                        {
                            _putLapses++;
                            Log($"{Time:yyyy-MM-dd HH:mm} PUT EXPIRED OTM - premium kept");
                        }
                        else
                        {
                            _callLapses++;
                            Log($"{Time:yyyy-MM-dd HH:mm} CALL EXPIRED OTM - premium kept, still long future");
                        }
                    }
                }
                else if (orderEvent.FillQuantity < 0)
                {
                    // opening short sale
                    _openOption = orderEvent.Symbol;
                    var premium = orderEvent.FillPrice * multiplier * Math.Abs(orderEvent.FillQuantity);
                    _premiumCollected += premium;
                    if (orderEvent.Symbol.ID.OptionRight == OptionRight.Put)
                    {
                        _putsSold++;
                        Log($"{Time:yyyy-MM-dd HH:mm} PUT SOLD {orderEvent.Symbol.Value} @ {orderEvent.FillPrice} " +
                            $"(premium {premium:F0}, total collected {_premiumCollected:F0})");
                    }
                    else
                    {
                        _callsSold++;
                        Log($"{Time:yyyy-MM-dd HH:mm} CALL SOLD {orderEvent.Symbol.Value} @ {orderEvent.FillPrice} " +
                            $"(premium {premium:F0}, total collected {_premiumCollected:F0})");
                    }
                }
                else
                {
                    // buyback (stop)
                    var paid = orderEvent.FillPrice * multiplier * orderEvent.FillQuantity;
                    _premiumPaidBack += paid;
                    Log($"{Time:yyyy-MM-dd HH:mm} OPTION BOUGHT BACK {orderEvent.Symbol.Value} @ {orderEvent.FillPrice} " +
                        $"(paid {paid:F0})");
                }
                return;
            }

            if (orderEvent.Symbol.SecurityType == SecurityType.Future
                && orderEvent.Ticket.OrderType == OrderType.OptionExercise)
            {
                if (orderEvent.FillQuantity > 0)
                {
                    // short put assigned -> long the mapped future at the strike
                    _assignments++;
                    _longFuture = true;
                    _heldFuture = orderEvent.Symbol;
                    _assignmentStrike = orderEvent.FillPrice;
                    Log($"{Time:yyyy-MM-dd HH:mm} ASSIGNED: long {orderEvent.Symbol.Value} @ {orderEvent.FillPrice} " +
                        $"(future expiry {orderEvent.Symbol.ID.Date:yyyy-MM-dd}) - State B, selling covered calls");
                }
                else
                {
                    // short call exercised against us -> short future delivered, nets flat
                    _calledAway++;
                    _longFuture = false;
                    Log($"{Time:yyyy-MM-dd HH:mm} CALLED AWAY: delivered short {orderEvent.Symbol.Value} " +
                        $"@ {orderEvent.FillPrice}, net flat - back to State A");
                    _heldFuture = null;
                    _assignmentStrike = 0;
                }
            }
        }

        public override void OnEndOfAlgorithm()
        {
            var finalEquity = Portfolio.TotalPortfolioValue;
            Log("==== WHEEL SUMMARY ====");
            Log($"Puts sold: {_putsSold} (lapsed OTM: {_putLapses}, assigned: {_assignments})");
            Log($"Calls sold: {_callsSold} (lapsed OTM: {_callLapses}, called away: {_calledAway})");
            Log($"Stops: {_stops}, Roll exits: {_rollExits}");
            Log($"Premium collected: {_premiumCollected:F2}, paid back on stops: {_premiumPaidBack:F2}, " +
                $"net {_premiumCollected - _premiumPaidBack:F2}");
            Log($"Final NLV: {finalEquity:F2} (return {(finalEquity / 500000m - 1m):P2}), " +
                $"self-tracked max DD: {_maxDrawdown:P2}");
            if (_longFuture && _heldFuture != null)
            {
                Log($"Still long {_heldFuture.Value} at end (assignment strike {_assignmentStrike})");
            }

            if (_putsSold == 0)
            {
                throw new Exception("WHEEL FAIL: no puts were ever sold - chains or entry logic broken");
            }
            Log("WHEEL PILOT COMPLETE");
        }
    }
}
