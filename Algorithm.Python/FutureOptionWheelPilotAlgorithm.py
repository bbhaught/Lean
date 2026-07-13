# QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
# Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

# ---------------------------------------------------------------------------------------------
# DEBUGGING THIS ALGORITHM IN VS CODE (debugpy), exactly like a lean-cli python project:
#
#   1. Run the launcher with the debug config (from the fork root):
#        export DOTNET_ROOT=/home/bb/.dotnet \
#               PATH=/home/bb/.conda/envs/LEAN/bin:/home/bb/.dotnet:$PATH \
#               PYTHONNET_PYDLL=/home/bb/.conda/envs/LEAN/lib/libpython3.11.so \
#               PYTHONHOME=/home/bb/.conda/envs/LEAN
#      NOTE: the conda env bin MUST be first on PATH - the debugpy adapter is spawned as a
#      separate "python" subprocess; if PATH resolves to a different python than PYTHONHOME
#      points to, the adapter dies with "No module named 'encodings'" and no port opens.
#        cd Launcher/bin/Release
#        dotnet QuantConnect.Lean.Launcher.dll --config config-pilot-wheel-py.json
#      The engine prints:
#        "DebuggerHelper.Initialize(): debugpy waiting for attach at port 5678..."
#      and blocks until a debugger attaches.
#
#   2. In VS Code (workspace = fork root), set breakpoints in this file. Most useful:
#        - try_enter(): the strike/DTE candidate loop and the "best" choice
#        - on_data(): every minute bar, inspect slice.option_chains
#      Then press F5 with the "Python: Attach to LEAN (debugpy)" configuration
#      (see .vscode/launch.json) which attaches to localhost:5678.
#
#   3. The backtest resumes under the debugger; breakpoints hit inside on_data /
#      try_enter with full variable inspection of chains, contracts and state.
#
#   No-debugger inspection path: run with --config config-pilot-wheel-py-nodebug.json and
#   pass parameter "debug_chains": "true" (in the config "parameters" block) to log the
#   full candidate table (root, expiry, dte, strike, moneyness, bid, ask, volume, OI)
#   at each selection point before the choice is made.
# ---------------------------------------------------------------------------------------------

from AlgorithmImports import *
from QuantConnect.Securities.FutureOption import FutureOptionExpiryCycles
from datetime import datetime, time, timedelta
import math

FRIDAY_WEEKLY_ROOTS = {"EW", "EW1", "EW2", "EW3", "EW4"}
ENTRY_START = time(10, 0, 0)   # ET
ENTRY_END = time(15, 30, 0)    # ET
OTM_TARGET = 0.035             # 3.5% OTM strike target
STOP_PCT = 0.10                # stop: future 10% below assignment strike
MIN_DTE = 3
MAX_DTE = 10
ROLL_EXIT_DAYS = 10            # liquidate future this close to its expiry


class FutureOptionWheelPilotAlgorithm(QCAlgorithm):
    """fop-weeklies fork: classic WHEEL strategy on ES weekly futures options, run against the
    REAL converted Databento pilot year (2025-07-01..2026-06-30, /home/bb/data/lean-fop-pilot).
    Python port of Algorithm.CSharp/FutureOptionWheelPilotAlgorithm.cs (same parameters, logic
    and log labels; results should match the C# run).

    Rules:
    - State A (flat): sell 1 OTM Friday-weekly put (roots EW/EW1-4), expiry 3-10 calendar days
      out (nearest expiry first). Strike nearest to 3.5% below the chain underlying price.
      Entry from 10:00 ET. Hold to expiry: OTM -> keep premium; ITM -> European auto-exercise
      at the 15:00 CT settlement mark assigns LONG 1 ES future at strike.
    - State B (long 1 future from assignment): sell 1 OTM weekly call, same selection logic,
      strike nearest 3.5% ABOVE price, restricted to contracts whose mapped underlying equals
      the held future (always covered, never naked). Called away -> back to State A.
    - Fills: limit at bid/ask mid (rounded to tick); if unfilled 30 minutes later the limit is
      moved to the current bid; if still unfilled 30 minutes after that, fall back to market.
    - Risk: if the held future drops more than 10% below the assignment strike, buy back the
      open call and liquidate the future (STOP), restart State A. If the held future is within
      10 days of its own expiry and no call is open, liquidate it (ROLL EXIT).
    - Never more than 1 wheel position; 1 contract (ES multiplier 50, ~$330k notional on $500k).
    """

    def initialize(self):
        self.log(f"Live Mode: {self.live_mode}")
        self.set_start_date(2025, 7, 1)
        self.set_end_date(2026, 6, 30)
        self.set_cash(500000)
        self.set_benchmark(lambda x: 0)

        # debug flag: when "true", log the full candidate table at each selection point
        self._debug_chains = self.get_parameter("debug_chains", "false").lower() == "true"

        self._es = self.add_future(Futures.Indices.SP_500_E_MINI, Resolution.MINUTE, Market.CME)
        self._es.set_filter(lambda universe: universe.expiration(0, 120))

        self.add_future_option(self._es.symbol,
            lambda universe: universe.strikes(-80, +80).expiration(0, MAX_DTE),
            FutureOptionExpiryCycles.WEEKLY | FutureOptionExpiryCycles.END_OF_MONTH)

        # wheel state
        self._long_future = False          # False = State A (flat), True = State B (long 1 future)
        self._held_future = None
        self._assignment_strike = 0.0
        self._open_option = None           # the currently-short weekly option
        self._pending_ticket = None
        self._pending_since = datetime.min
        self._crossed_to_bid = False
        self._stop_in_progress = False
        self._skip_log_date = None
        self._debug_log_date = None

        # accounting
        self._premium_collected = 0.0
        self._premium_paid_back = 0.0
        self._puts_sold = 0
        self._calls_sold = 0
        self._assignments = 0
        self._called_away = 0
        self._stops = 0
        self._roll_exits = 0
        self._put_lapses = 0
        self._call_lapses = 0
        self._peak_equity = self.portfolio.total_portfolio_value
        self._max_drawdown = 0.0
        self._last_month_logged = datetime.min

    def on_data(self, slice):
        self._track_equity()
        self._manage_pending_order()
        self._check_stop()
        self._check_roll_exit()
        self._try_enter(slice)

    def _track_equity(self):
        equity = self.portfolio.total_portfolio_value
        if equity > self._peak_equity:
            self._peak_equity = equity
        elif self._peak_equity > 0:
            dd = (self._peak_equity - equity) / self._peak_equity
            if dd > self._max_drawdown:
                self._max_drawdown = dd

        if self.time.month != self._last_month_logged.month or self.time.year != self._last_month_logged.year:
            self._last_month_logged = self.time
            self.log(f"{self.time:%Y-%m-%d} EQUITY {equity:.0f} "
                     f"(peak {self._peak_equity:.0f}, maxDD {self._max_drawdown:.2%})")

    @staticmethod
    def _is_closed(status):
        """Order status closed check (mirrors the C# OrderStatus.IsClosed() extension)."""
        return status in (OrderStatus.FILLED, OrderStatus.CANCELED, OrderStatus.INVALID)

    def _manage_pending_order(self):
        """Limit-at-mid entry management: cross to bid after 30 min, market after 60."""
        if self._pending_ticket is None:
            return
        if self._pending_ticket.status == OrderStatus.FILLED:
            self._pending_ticket = None
            return
        if self._pending_ticket.status in (OrderStatus.CANCELED, OrderStatus.INVALID):
            self._pending_ticket = None
            return

        elapsed = self.time - self._pending_since
        symbol = self._pending_ticket.symbol
        if elapsed >= timedelta(minutes=60):
            self.log(f"{self.time:%Y-%m-%d %H:%M} ENTRY unfilled 60 min, "
                     f"cancel limit and go market on {symbol.value}")
            self._pending_ticket.cancel()
            self._pending_ticket = self.market_order(symbol, -1, asynchronous=True)
            self._pending_since = self.time
            self._crossed_to_bid = True
        elif elapsed >= timedelta(minutes=30) and not self._crossed_to_bid:
            bid = self.securities[symbol].bid_price
            if bid > 0 and bid != self._pending_ticket.get(OrderField.LIMIT_PRICE):
                self.log(f"{self.time:%Y-%m-%d %H:%M} ENTRY unfilled 30 min, "
                         f"moving limit to bid {bid} on {symbol.value}")
                self._pending_ticket.update_limit_price(bid)
            self._crossed_to_bid = True

    def _check_stop(self):
        """State B stop: future more than 10% below the assignment strike -> flatten everything."""
        if not self._long_future or self._stop_in_progress or self._held_future is None:
            return
        price = self.securities[self._held_future].price
        if price <= 0 or price >= self._assignment_strike * (1.0 - STOP_PCT):
            return

        self._stop_in_progress = True
        self.log(f"{self.time:%Y-%m-%d %H:%M} STOP: {self._held_future.value} @ {price} is "
                 f">{STOP_PCT:.0%} below assignment strike {self._assignment_strike}. "
                 f"Flattening future and buying back open call.")
        if self._pending_ticket is not None and not self._is_closed(self._pending_ticket.status):
            self._pending_ticket.cancel()
            self._pending_ticket = None
        if self._open_option is not None and self.portfolio[self._open_option].invested:
            self.market_order(self._open_option, 1)
        self._open_option = None
        self.market_order(self._held_future, -self.portfolio[self._held_future].quantity)
        self._stops += 1
        self._long_future = False
        self._held_future = None
        self._assignment_strike = 0.0
        self._stop_in_progress = False

    def _check_roll_exit(self):
        """State B calendar exit: within ROLL_EXIT_DAYS of the held future's own expiry no weekly
        maps to it any more (weeklies map to the next quarterly), so once no call is open,
        liquidate."""
        if not self._long_future or self._held_future is None or self._open_option is not None:
            return
        if self._pending_ticket is not None and not self._is_closed(self._pending_ticket.status):
            return
        if (self._held_future.id.date.date() - self.time.date()).days >= ROLL_EXIT_DAYS:
            return

        price = self.securities[self._held_future].price
        self.log(f"{self.time:%Y-%m-%d %H:%M} ROLL EXIT: {self._held_future.value} expires "
                 f"{self._held_future.id.date:%Y-%m-%d}, liquidating @ ~{price} "
                 f"(assignment strike was {self._assignment_strike})")
        self.market_order(self._held_future, -self.portfolio[self._held_future].quantity)
        self._roll_exits += 1
        self._long_future = False
        self._held_future = None
        self._assignment_strike = 0.0

    def _try_enter(self, slice):
        """Sell the weekly put (State A) or covered weekly call (State B).

        Selection: Friday-weekly roots only (EW/EW1-4), 3-10 calendar DTE, nearest expiry first;
        within that expiry the strike nearest to 3.5% OTM (below price for puts, above for calls,
        calls restricted to the held future's own chain); contract must have a bid.
        """
        if self._pending_ticket is not None or self._open_option is not None:
            return
        tod = self.time.time()
        if tod < ENTRY_START or tod > ENTRY_END:
            return
        # never sell a call unless the future is actually in the portfolio (covered)
        if self._long_future and (self._held_future is None
                                  or not self.portfolio[self._held_future].invested):
            return

        best = None                    # <-- BREAKPOINT: strike/DTE selection starts here
        best_expiry_date = None
        best_score = float("inf")
        best_reference = 0.0
        debug_rows = [] if self._debug_chains else None

        for kvp in slice.option_chains:
            chain = kvp.value
            if chain.underlying is None or chain.underlying.price == 0:
                continue
            reference = chain.underlying.price
            for contract in chain.contracts.values():
                if contract.symbol.id.symbol not in FRIDAY_WEEKLY_ROOTS:
                    continue
                dte = (contract.expiry.date() - self.time.date()).days
                if dte < MIN_DTE or dte > MAX_DTE:
                    continue
                if debug_rows is not None:
                    moneyness = (contract.strike - reference) / reference
                    debug_rows.append(
                        f"  {contract.symbol.id.symbol:>4} {contract.expiry:%Y-%m-%d} dte={dte:>2} "
                        f"{('C' if contract.right == OptionRight.CALL else 'P')} "
                        f"strike={contract.strike:>8.2f} moneyness={moneyness:>7.2%} "
                        f"bid={contract.bid_price:>8.2f} ask={contract.ask_price:>8.2f} "
                        f"vol={contract.volume:>6.0f} oi={contract.open_interest:>7.0f}")
                if self._long_future:
                    if (contract.right != OptionRight.CALL
                            or contract.symbol.underlying != self._held_future
                            or contract.strike <= reference):
                        continue
                else:
                    if contract.right != OptionRight.PUT or contract.strike >= reference:
                        continue
                if contract.bid_price <= 0:
                    continue

                target = reference * (1.0 + OTM_TARGET) if self._long_future \
                    else reference * (1.0 - OTM_TARGET)
                score = abs(contract.strike - target)
                expiry_date = contract.expiry.date()
                if (best_expiry_date is None or expiry_date < best_expiry_date
                        or (expiry_date == best_expiry_date and score < best_score)):
                    best = contract    # <-- BREAKPOINT: chosen contract updated here
                    best_expiry_date = expiry_date
                    best_score = score
                    best_reference = reference

        if debug_rows is not None and (best is not None or self._debug_log_date != self.time.date()):
            self._debug_log_date = self.time.date()
            side = "call" if self._long_future else "put"
            self.log(f"{self.time:%Y-%m-%d %H:%M} DEBUG CHAINS ({side} selection, "
                     f"{len(debug_rows)} candidates in {MIN_DTE}-{MAX_DTE} dte window):")
            for row in debug_rows:
                self.log(row)

        if best is None:
            if self._skip_log_date != self.time.date() and tod >= time(12, 0, 0):
                self._skip_log_date = self.time.date()
                side = "call" if self._long_future else "put"
                self.log(f"{self.time:%Y-%m-%d %H:%M} no eligible Friday-weekly {side} "
                         f"candidate ({MIN_DTE}-{MAX_DTE} dte with a bid) - skipping")
            return

        security = self.securities[best.symbol]
        tick = security.symbol_properties.minimum_price_variation
        mid = (best.bid_price + best.ask_price) / 2.0 \
            if best.ask_price > best.bid_price and best.ask_price > 0 else best.bid_price
        if tick > 0:
            # floor to tick; the 1e-9 epsilon guards float error on exact multiples
            mid = math.floor(mid / tick + 1e-9) * tick
            mid = round(mid, 10)
        if mid < best.bid_price:
            mid = best.bid_price

        otm_pct = (best.strike - best_reference) / best_reference if self._long_future \
            else (best_reference - best.strike) / best_reference
        self.log(f"{self.time:%Y-%m-%d %H:%M} SELL {'CALL' if self._long_future else 'PUT'} "
                 f"{best.symbol.value} (root {best.symbol.id.symbol}, strike {best.strike}, "
                 f"expiry {best.expiry:%Y-%m-%d}, underlying {best.symbol.underlying.value} "
                 f"@ {best_reference}, {otm_pct:.2%} OTM, bid {best.bid_price} "
                 f"ask {best.ask_price}) limit @ mid {mid}")
        self._pending_ticket = self.limit_order(best.symbol, -1, mid)
        self._pending_since = self.time
        self._crossed_to_bid = False

    def on_order_event(self, order_event):
        if order_event.status != OrderStatus.FILLED:
            return

        multiplier = self.securities[order_event.symbol].symbol_properties.contract_multiplier
        self.log(f"{self.time:%Y-%m-%d %H:%M} FILL {order_event.symbol.value} "
                 f"{order_event.fill_quantity}@{order_event.fill_price} "
                 f"({order_event.ticket.order_type}) {order_event.message}")

        if order_event.symbol.security_type == SecurityType.FUTURE_OPTION:
            if order_event.ticket.order_type == OrderType.OPTION_EXERCISE:
                # expiry processing of our short weekly (P6 settlement-mark exercise model)
                self._open_option = None
                if "OTM" in (order_event.message or ""):
                    if order_event.symbol.id.option_right == OptionRight.PUT:
                        self._put_lapses += 1
                        self.log(f"{self.time:%Y-%m-%d %H:%M} PUT EXPIRED OTM - premium kept")
                    else:
                        self._call_lapses += 1
                        self.log(f"{self.time:%Y-%m-%d %H:%M} CALL EXPIRED OTM - "
                                 f"premium kept, still long future")
            elif order_event.fill_quantity < 0:
                # opening short sale
                self._open_option = order_event.symbol
                premium = order_event.fill_price * multiplier * abs(order_event.fill_quantity)
                self._premium_collected += premium
                if order_event.symbol.id.option_right == OptionRight.PUT:
                    self._puts_sold += 1
                    self.log(f"{self.time:%Y-%m-%d %H:%M} PUT SOLD {order_event.symbol.value} "
                             f"@ {order_event.fill_price} (premium {premium:.0f}, "
                             f"total collected {self._premium_collected:.0f})")
                else:
                    self._calls_sold += 1
                    self.log(f"{self.time:%Y-%m-%d %H:%M} CALL SOLD {order_event.symbol.value} "
                             f"@ {order_event.fill_price} (premium {premium:.0f}, "
                             f"total collected {self._premium_collected:.0f})")
            else:
                # buyback (stop)
                paid = order_event.fill_price * multiplier * order_event.fill_quantity
                self._premium_paid_back += paid
                self.log(f"{self.time:%Y-%m-%d %H:%M} OPTION BOUGHT BACK "
                         f"{order_event.symbol.value} @ {order_event.fill_price} (paid {paid:.0f})")
            return

        if (order_event.symbol.security_type == SecurityType.FUTURE
                and order_event.ticket.order_type == OrderType.OPTION_EXERCISE):
            if order_event.fill_quantity > 0:
                # short put assigned -> long the mapped future at the strike
                self._assignments += 1
                self._long_future = True
                self._held_future = order_event.symbol
                self._assignment_strike = order_event.fill_price
                self.log(f"{self.time:%Y-%m-%d %H:%M} ASSIGNED: long {order_event.symbol.value} "
                         f"@ {order_event.fill_price} (future expiry "
                         f"{order_event.symbol.id.date:%Y-%m-%d}) - State B, selling covered calls")
            else:
                # short call exercised against us -> short future delivered, nets flat
                self._called_away += 1
                self._long_future = False
                self.log(f"{self.time:%Y-%m-%d %H:%M} CALLED AWAY: delivered short "
                         f"{order_event.symbol.value} @ {order_event.fill_price}, "
                         f"net flat - back to State A")
                self._held_future = None
                self._assignment_strike = 0.0

    def on_end_of_algorithm(self):
        final_equity = self.portfolio.total_portfolio_value
        self.log("==== WHEEL SUMMARY ====")
        self.log(f"Puts sold: {self._puts_sold} (lapsed OTM: {self._put_lapses}, "
                 f"assigned: {self._assignments})")
        self.log(f"Calls sold: {self._calls_sold} (lapsed OTM: {self._call_lapses}, "
                 f"called away: {self._called_away})")
        self.log(f"Stops: {self._stops}, Roll exits: {self._roll_exits}")
        self.log(f"Premium collected: {self._premium_collected:.2f}, paid back on stops: "
                 f"{self._premium_paid_back:.2f}, "
                 f"net {self._premium_collected - self._premium_paid_back:.2f}")
        self.log(f"Final NLV: {final_equity:.2f} (return {final_equity / 500000.0 - 1.0:.2%}), "
                 f"self-tracked max DD: {self._max_drawdown:.2%}")
        if self._long_future and self._held_future is not None:
            self.log(f"Still long {self._held_future.value} at end "
                     f"(assignment strike {self._assignment_strike})")

        if self._puts_sold == 0:
            raise Exception("WHEEL FAIL: no puts were ever sold - chains or entry logic broken")
        self.log("WHEEL PILOT COMPLETE")
