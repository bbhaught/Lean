#!/usr/bin/env python
"""Databento GLBX DBN -> LEAN futures-options data converter (fop-weeklies fork P7 core).

Converts day-split Databento DBN datasets (definition / statistics / ohlcv-1m / tbbo /
bbo-1m for a futures-options complex) into the LEAN fork's on-disk format:

  futureoption/<market>/minute/<root>/<underlying yyyyMM>/<yyyyMMdd>_trade_american.zip
      entries <yyyyMMdd>_<root>_minute_trade_american_<call|put>_<strike*10000>_<expiry>.csv
      rows    ms_of_day_utc,open,high,low,close,volume            (from OPT ohlcv-1m)
  .../<yyyyMMdd>_quote_american.zip
      rows    ms,bidO,bidH,bidL,bidC,lastBidSize,askO,askH,askL,askC,lastAskSize
      (from OPT tbbo: BBO sampled at trade times, minute-bucketed; one-sided allowed;
      a side with no data in the minute is left empty - documented approximation, tbbo
      is trade-time-sampled, not a full MBP-1 stream)
  futureoption/<market>/daily/<root>/<yyyyMM>/<root>_<year>_trade_american.zip
      rows    "yyyyMMdd 00:00,open,high,low,close,volume"
      DESIGN DECISION: the daily CLOSE is the exchange SETTLEMENT price (statistics
      stat_type 3) when published - settlement is the mark the fork's exercise model
      keys on (design_B issue 3/9); open/high/low come from the day's minute
      aggregation, falling back to the settlement for settlement-only (untraded) marks.
  futureoption/<market>/universes/<root>/<yyyyMM>/<yyyyMMdd>.csv
      per-OBSERVATION-DATE point-in-time universes (design_B issue 8a): built ONLY from
      that day's definitions + that day's settlements + open interest. First row is the
      underlying future's daily bar. Rows with volume==0 AND open_interest==0 are
      dropped (OI=0 phantom rule, design_B issue 8b).
  future/<market>/minute/<ticker>/<yyyyMMdd>_{trade,quote}.zip     (FUT ohlcv-1m/bbo-1m)
  future/<market>/daily/<ticker>_trade.zip
  future/<market>/universes/<ticker>/<yyyyMMdd>.csv

Underlying mapping: an option's underlying future is resolved from the definitions'
underlying instrument id joined to the SAME DAY's future definitions (authoritative per
design_B issue 2). The engine-side P3 resolver parity against this exact join is proven
by tools/databento/validate_registry.py + EngineParity; the converter never re-derives
the mapping from rules. If the id join fails, the record's raw 'underlying' symbol
string is parsed as a logged fallback.

Rejection rules (all logged, per-day counts in the state file):
  - user-defined instruments (UDS spreads)
  - strikes with more than 6 decimal places (DECISIONS.md D1)
  - bars/quotes stamped at/after the instrument's exchange expiration (rows after
    last-trade, design_B issue 8d)
  - minute trade rows with volume == 0
  - options whose underlying cannot be resolved

Restartability: one state json per day under <out>/_state/; days with an existing state
file are skipped (use --force to redo). Daily zips and future daily zips span the whole
range and are composed by the finalize pass (--finalize, automatic after day pass) from
per-day aggregate csvs stored in _state/.

Usage:
  python tools/databento/dbn_to_lean_fop.py \
      --dbn-dir /home/bb/data/databento/pilot_es/GLBX-... [--dbn-dir ...] \
      --out /home/bb/data/lean-fop-pilot [--market cme] \
      [--start 20250701 --end 20260701] [--force] [--finalize-only]

Directories are auto-classified by DBN metadata (schema + .OPT/.FUT parent symbols);
pass every dataset directory of the pilot (multiple OPT complexes may be mixed, e.g.
the quarterly ES.OPT pull plus the weekly EW/E1A/... re-pull).
"""
from __future__ import annotations

import argparse
import collections
import glob
import io
import json
import os
import re
import sys
import zipfile
from datetime import datetime, timedelta

import pandas as pd

try:
    import databento as db
except ImportError:  # pragma: no cover
    sys.exit("databento package required (conda LEAN env)")

MONTH_CODES = {"F": 1, "G": 2, "H": 3, "J": 4, "K": 5, "M": 6,
               "N": 7, "Q": 8, "U": 9, "V": 10, "X": 11, "Z": 12}


def log(msg):
    print(msg, flush=True)


# --------------------------------------------------------------------------------------
# dataset discovery
# --------------------------------------------------------------------------------------

def classify_dirs(dbn_dirs):
    """Classify each directory into (side, schema) via a sample file's DBN metadata."""
    roles = collections.defaultdict(list)  # (side, schema) -> [dir]
    for d in dbn_dirs:
        files = sorted(glob.glob(os.path.join(d, "*.dbn.zst")))
        if not files:
            log(f"WARNING: no dbn files in {d}, skipping")
            continue
        meta = db.DBNStore.from_file(files[0]).metadata
        side = "OPT" if any(str(s).endswith(".OPT") for s in meta.symbols) else \
               "FUT" if any(str(s).endswith(".FUT") for s in meta.symbols) else "?"
        roles[(side, str(meta.schema))].append(d)
        log(f"dataset {d}: side={side} schema={meta.schema} symbols={list(meta.symbols)[:4]}")
    return roles


def day_files(dirs, day):
    out = []
    for d in dirs or []:
        out.extend(glob.glob(os.path.join(d, f"*{day}*.dbn.zst")))
    return sorted(out)


def read_concat(dirs, day):
    frames = [db.DBNStore.from_file(f).to_df() for f in day_files(dirs, day)]
    frames = [f for f in frames if len(f)]
    if not frames:
        return None
    return pd.concat(frames) if len(frames) > 1 else frames[0]


# --------------------------------------------------------------------------------------
# helpers
# --------------------------------------------------------------------------------------

def scale_strike(strike):
    """LEAN zip-entry strike component: strike * 10000, trailing zeros trimmed."""
    v = strike * 10000
    if abs(v - round(v)) < 1e-6:
        return str(int(round(v)))
    s = f"{v:.6f}".rstrip("0").rstrip(".")
    return s


def strike_ok(strike):
    """DECISIONS.md D1: reject strikes with more than 6 decimal places."""
    return abs(strike * 1e6 - round(strike * 1e6)) < 1e-6


def parse_contract_month(raw_symbol, ref_year):
    """Fallback parse 'ESZ5' -> (year, month) resolving the single year digit near ref_year."""
    m = re.match(r"^[A-Z0-9]+?([FGHJKMNQUVXZ])(\d)$", raw_symbol)
    if not m:
        return None
    month = MONTH_CODES[m.group(1)]
    digit = int(m.group(2))
    year = ref_year - ref_year % 10 + digit
    if year < ref_year - 1:
        year += 10
    return year * 100 + month


def fmt_px(v):
    if pd.isna(v):
        return ""
    s = f"{v:.10f}".rstrip("0").rstrip(".")
    return s


def ms_of_day(ts, day_midnight):
    return int((ts - day_midnight).total_seconds() * 1000)


def write_zip(path, entries):
    """entries: {name: text}. Writes deterministically (sorted names)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for name in sorted(entries):
            z.writestr(name, entries[name])


# --------------------------------------------------------------------------------------
# per-day conversion
# --------------------------------------------------------------------------------------

class DayContext:
    """Same-day definitions: instrument maps for options and future outrights."""

    def __init__(self, day, opt_defs, fut_defs, counters):
        self.day = day
        self.counters = counters
        self.fut = {}       # instrument_id -> dict(cm 'yyyyMM' int-free str, raw, expiration)
        self.fut_raw = {}   # raw_symbol -> cm  (fallback join)
        if fut_defs is not None:
            f = fut_defs[fut_defs["instrument_class"] == "F"]
            for row in f.itertuples():
                cm = f"{int(row.maturity_year):04d}{int(row.maturity_month):02d}"
                self.fut[int(row.instrument_id)] = {
                    "cm": cm, "raw": row.raw_symbol, "expiration": row.expiration}
                self.fut_raw[row.raw_symbol] = cm

        self.opt = {}       # instrument_id -> dict(root, cm, expiry ts, strike, right, raw)
        if opt_defs is not None:
            o = opt_defs[(opt_defs["instrument_class"].isin(["C", "P"]))
                         & (opt_defs["user_defined_instrument"] != "Y")]
            for row in o.itertuples():
                strike = float(row.strike_price)
                if not strike_ok(strike):
                    counters["reject_strike_precision"] += 1
                    log(f"REJECT strike precision {row.raw_symbol} strike={strike}")
                    continue
                u = self.fut.get(int(row.underlying_id))
                if u is not None:
                    cm = u["cm"]
                else:
                    cm = self.fut_raw.get(row.underlying)
                    if cm is None:
                        parsed = parse_contract_month(row.underlying, int(day[:4]))
                        if parsed is None:
                            counters["reject_no_underlying"] += 1
                            log(f"REJECT no underlying {row.raw_symbol} underlying_id="
                                f"{row.underlying_id} underlying={row.underlying}")
                            continue
                        counters["underlying_string_fallback"] += 1
                        cm = str(parsed)
                self.opt[int(row.instrument_id)] = {
                    "root": row.asset, "cm": cm, "expiration": pd.Timestamp(row.expiration),
                    "strike": strike, "right": row.instrument_class, "raw": row.raw_symbol}

    def option_entry_name(self, info, day, resolution="minute", tick_type="trade"):
        right = "call" if info["right"] == "C" else "put"
        expiry = info["expiration"].strftime("%Y%m%d")
        root = info["root"].lower()
        return (f"{day}_{root}_{resolution}_{tick_type}_american_{right}_"
                f"{scale_strike(info['strike'])}_{expiry}.csv")

    def option_daily_entry_name(self, info, tick_type="trade"):
        right = "call" if info["right"] == "C" else "put"
        expiry = info["expiration"].strftime("%Y%m%d")
        root = info["root"].lower()
        return f"{root}_{tick_type}_american_{right}_{scale_strike(info['strike'])}_{expiry}.csv"


def convert_day(day, roles, out_dir, market, counters):
    """Convert one UTC day. Returns state dict (daily aggregates for the finalize pass)."""
    midnight = pd.Timestamp(datetime.strptime(day, "%Y%m%d"), tz="UTC")
    next_midnight = midnight + timedelta(days=1)

    opt_defs = read_concat(roles.get(("OPT", "definition")), day)
    fut_defs = read_concat(roles.get(("FUT", "definition")), day)
    ctx = DayContext(day, opt_defs, fut_defs, counters)

    state = {"day": day, "opt_daily": [], "fut_daily": [], "counters_day": {}}
    day_counters = collections.Counter()

    # ---------------- option minute TRADE bars (OPT ohlcv-1m) ----------------
    opt_ohlcv = read_concat(roles.get(("OPT", "ohlcv-1m")), day)
    opt_day_agg = {}   # instrument_id -> [o,h,l,last_close,volume]
    zips = collections.defaultdict(dict)  # (root, cm, ticktype) -> {entry: rows[]}
    if opt_ohlcv is not None:
        opt_ohlcv = opt_ohlcv.reset_index()
        tcol = "ts_event" if "ts_event" in opt_ohlcv.columns else "ts_recv"
        for row in opt_ohlcv.itertuples():
            info = ctx.opt.get(int(row.instrument_id))
            if info is None:
                day_counters["skip_bar_unknown_instrument"] += 1
                continue
            ts = getattr(row, tcol)
            if ts >= info["expiration"]:
                day_counters["skip_bar_after_expiry"] += 1
                continue
            if not (midnight <= ts < next_midnight):
                day_counters["skip_bar_wrong_day"] += 1
                continue
            vol = int(row.volume)
            if vol <= 0:
                day_counters["skip_bar_zero_volume"] += 1
                continue
            entry = ctx.option_entry_name(info, day, tick_type="trade")
            zips[(info["root"], info["cm"], "trade")].setdefault(entry, []).append(
                f"{ms_of_day(ts, midnight)},{fmt_px(row.open)},{fmt_px(row.high)},"
                f"{fmt_px(row.low)},{fmt_px(row.close)},{vol}")
            agg = opt_day_agg.get(row.instrument_id)
            if agg is None:
                opt_day_agg[row.instrument_id] = [row.open, row.high, row.low, row.close, vol]
            else:
                agg[1] = max(agg[1], row.high)
                agg[2] = min(agg[2], row.low)
                agg[3] = row.close
                agg[4] += vol

    # ---------------- option minute QUOTE bars (OPT tbbo) ----------------
    tbbo = read_concat(roles.get(("OPT", "tbbo")), day)
    if tbbo is not None:
        tbbo = tbbo.reset_index()
        tbbo["minute"] = tbbo["ts_event"].dt.floor("min")
        grouped = tbbo.groupby(["instrument_id", "minute"])
        for (iid, minute), g in grouped:
            info = ctx.opt.get(int(iid))
            if info is None:
                day_counters["skip_quote_unknown_instrument"] += 1
                continue
            if minute >= info["expiration"]:
                day_counters["skip_quote_after_expiry"] += 1
                continue
            if not (midnight <= minute < next_midnight):
                day_counters["skip_quote_wrong_day"] += 1
                continue
            bid = g["bid_px_00"].dropna()
            ask = g["ask_px_00"].dropna()
            if bid.empty and ask.empty:
                day_counters["skip_quote_empty"] += 1
                continue
            if not bid.empty:
                bcsv = (f"{fmt_px(bid.iloc[0])},{fmt_px(bid.max())},{fmt_px(bid.min())},"
                        f"{fmt_px(bid.iloc[-1])},{int(g['bid_sz_00'].iloc[-1])}")
            else:
                bcsv = ",,,,"
            if not ask.empty:
                acsv = (f"{fmt_px(ask.iloc[0])},{fmt_px(ask.max())},{fmt_px(ask.min())},"
                        f"{fmt_px(ask.iloc[-1])},{int(g['ask_sz_00'].iloc[-1])}")
            else:
                acsv = ",,,,"
            entry = ctx.option_entry_name(info, day, tick_type="quote")
            zips[(info["root"], info["cm"], "quote")].setdefault(entry, []).append(
                f"{ms_of_day(minute, midnight)},{bcsv},{acsv}")

    for (root, cm, tick_type), entries in zips.items():
        path = os.path.join(out_dir, "futureoption", market, "minute", root.lower(), cm,
                            f"{day}_{tick_type}_american.zip")
        write_zip(path, {name: "\n".join(rows) + "\n" for name, rows in entries.items()})
        day_counters[f"zip_option_{tick_type}"] += 1

    # ---------------- statistics: settlements + open interest ----------------
    opt_stats = read_concat(roles.get(("OPT", "statistics")), day)
    settle, oi = {}, {}
    if opt_stats is not None:
        s = opt_stats[opt_stats["stat_type"] == 3]
        for row in s.itertuples():
            settle[int(row.instrument_id)] = float(row.price)
        s = opt_stats[opt_stats["stat_type"] == 9]
        for row in s.itertuples():
            oi[int(row.instrument_id)] = int(row.quantity)

    fut_stats = read_concat(roles.get(("FUT", "statistics")), day)
    fut_settle, fut_oi = {}, {}
    if fut_stats is not None:
        s = fut_stats[fut_stats["stat_type"] == 3]
        for row in s.itertuples():
            fut_settle[int(row.instrument_id)] = float(row.price)
        s = fut_stats[fut_stats["stat_type"] == 9]
        for row in s.itertuples():
            fut_oi[int(row.instrument_id)] = int(row.quantity)

    # ---------------- futures minute trade/quote + day aggregates ----------------
    fut_ohlcv = read_concat(roles.get(("FUT", "ohlcv-1m")), day)
    fut_day_agg = {}
    fut_tickers = set()
    if fut_ohlcv is not None:
        fut_ohlcv = fut_ohlcv.reset_index()
        tcol = "ts_event" if "ts_event" in fut_ohlcv.columns else "ts_recv"
        fzips = collections.defaultdict(dict)  # ticker -> {entry: rows}
        for row in fut_ohlcv.itertuples():
            f = ctx.fut.get(int(row.instrument_id))
            if f is None:
                day_counters["skip_fut_bar_not_outright"] += 1
                continue
            ts = getattr(row, tcol)
            if not (midnight <= ts < next_midnight):
                day_counters["skip_fut_bar_wrong_day"] += 1
                continue
            if pd.Timestamp(f["expiration"]) <= ts:
                day_counters["skip_fut_bar_after_expiry"] += 1
                continue
            vol = int(row.volume)
            if vol <= 0:
                day_counters["skip_fut_bar_zero_volume"] += 1
                continue
            ticker = re.sub(r"[FGHJKMNQUVXZ]\d+$", "", f["raw"]).lower()
            fut_tickers.add(ticker)
            entry = f"{day}_{ticker}_minute_trade_{f['cm']}.csv"
            fzips[ticker].setdefault(entry, []).append(
                f"{ms_of_day(ts, midnight)},{fmt_px(row.open)},{fmt_px(row.high)},"
                f"{fmt_px(row.low)},{fmt_px(row.close)},{vol}")
            agg = fut_day_agg.get(row.instrument_id)
            if agg is None:
                fut_day_agg[row.instrument_id] = [row.open, row.high, row.low, row.close, vol]
            else:
                agg[1] = max(agg[1], row.high)
                agg[2] = min(agg[2], row.low)
                agg[3] = row.close
                agg[4] += vol
        for ticker, entries in fzips.items():
            path = os.path.join(out_dir, "future", market, "minute", ticker, f"{day}_trade.zip")
            write_zip(path, {n: "\n".join(r) + "\n" for n, r in entries.items()})
            day_counters["zip_future_trade"] += 1

    bbo = read_concat(roles.get(("FUT", "bbo-1m")), day)
    if bbo is not None:
        bbo = bbo.reset_index()
        # bbo-1m: one BBO snapshot per minute boundary; treat the snapshot at T as the
        # (flat) quote bar starting at T - documented approximation
        fqzips = collections.defaultdict(dict)
        for row in bbo.itertuples():
            f = ctx.fut.get(int(row.instrument_id))
            if f is None:
                continue
            ts = row.ts_recv if hasattr(row, "ts_recv") else row.Index
            if not (midnight <= ts < next_midnight) or pd.Timestamp(f["expiration"]) <= ts:
                continue
            bid, ask = row.bid_px_00, row.ask_px_00
            if pd.isna(bid) and pd.isna(ask):
                continue
            b = f"{fmt_px(bid)},{fmt_px(bid)},{fmt_px(bid)},{fmt_px(bid)},{int(row.bid_sz_00) if not pd.isna(bid) else 0}" \
                if not pd.isna(bid) else ",,,,"
            a = f"{fmt_px(ask)},{fmt_px(ask)},{fmt_px(ask)},{fmt_px(ask)},{int(row.ask_sz_00) if not pd.isna(ask) else 0}" \
                if not pd.isna(ask) else ",,,,"
            ticker = re.sub(r"[FGHJKMNQUVXZ]\d+$", "", f["raw"]).lower()
            entry = f"{day}_{ticker}_minute_quote_{f['cm']}.csv"
            fqzips[ticker].setdefault(entry, []).append(f"{ms_of_day(ts, midnight)},{b},{a}")
        for ticker, entries in fqzips.items():
            path = os.path.join(out_dir, "future", market, "minute", ticker, f"{day}_quote.zip")
            write_zip(path, {n: "\n".join(r) + "\n" for n, r in entries.items()})
            day_counters["zip_future_quote"] += 1

    # ---------------- per-observation-date universe files ----------------
    # options grouped by (root, underlying contract month); PIT: only this day's
    # definitions/marks. Universe row close = settlement (mark), fallback last trade.
    uni = collections.defaultdict(list)
    for iid, info in ctx.opt.items():
        agg = opt_day_agg.get(iid)
        stl = settle.get(iid)
        koi = oi.get(iid, 0)
        vol = agg[4] if agg else 0
        if vol == 0 and koi == 0:
            day_counters["universe_drop_phantom"] += 1
            continue
        close = stl if stl is not None else (agg[3] if agg else None)
        if close is None:
            day_counters["universe_drop_no_mark"] += 1
            continue
        o, h, l = (agg[0], agg[1], agg[2]) if agg else (close, close, close)
        uni[(info["root"], info["cm"])].append(
            (info["expiration"].strftime("%Y%m%d"), info["strike"], info["right"],
             o, h, l, close, vol, koi))
        # daily aggregate for the finalize pass (daily zips): close = settlement mark
        state["opt_daily"].append({
            "root": info["root"], "cm": info["cm"],
            "expiry": info["expiration"].strftime("%Y%m%d"), "strike": info["strike"],
            "right": info["right"], "o": o, "h": h, "l": l, "c": close, "v": vol})

    # underlying daily bar per contract month for universe headers; settlement-only
    # fallback (flat bar, volume 0) when the outright printed no minute bars that day
    fut_bar_by_cm = {}
    for iid, f in ctx.fut.items():
        agg = fut_day_agg.get(iid)
        if agg is None:
            stl = fut_settle.get(iid)
            if stl is not None and f["cm"] not in fut_bar_by_cm:
                fut_bar_by_cm[f["cm"]] = [stl, stl, stl, stl, 0]
            continue
        fut_bar_by_cm[f["cm"]] = agg
        ticker = re.sub(r"[FGHJKMNQUVXZ]\d+$", "", f["raw"]).lower()
        state["fut_daily"].append({
            "ticker": ticker, "cm": f["cm"], "o": agg[0], "h": agg[1], "l": agg[2],
            "c": agg[3], "v": agg[4], "oi": fut_oi.get(iid, 0)})

    for (root, cm), rows in uni.items():
        ubar = fut_bar_by_cm.get(cm)
        path = os.path.join(out_dir, "futureoption", market, "universes", root.lower(), cm,
                            f"{day}.csv")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w") as fh:
            fh.write("#expiry,strike,right,open,high,low,close,volume,open_interest\n")
            if ubar:
                fh.write(f",,,{fmt_px(ubar[0])},{fmt_px(ubar[1])},{fmt_px(ubar[2])},"
                         f"{fmt_px(ubar[3])},{int(ubar[4])},\n")
            else:
                day_counters["universe_no_underlying_bar"] += 1
            for r in sorted(rows):
                fh.write(f"{r[0]},{fmt_px(r[1])},{r[2]},{fmt_px(r[3])},{fmt_px(r[4])},"
                         f"{fmt_px(r[5])},{fmt_px(r[6])},{r[7]},{r[8]}\n")
        day_counters["universe_files"] += 1

    # future universes (chain selection for AddFuture)
    fut_uni = collections.defaultdict(list)
    for iid, f in ctx.fut.items():
        agg = fut_day_agg.get(iid)
        koi = fut_oi.get(iid, 0)
        if agg is None and koi == 0:
            continue
        ticker = re.sub(r"[FGHJKMNQUVXZ]\d+$", "", f["raw"]).lower()
        if agg is None:
            stl = fut_settle.get(iid)
            if stl is None:
                continue
            agg = [stl, stl, stl, stl, 0]
        fut_uni[ticker].append((f["cm"], agg, koi))
    for ticker, rows in fut_uni.items():
        path = os.path.join(out_dir, "future", market, "universes", ticker, f"{day}.csv")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w") as fh:
            fh.write("#expiry,open,high,low,close,volume,open_interest\n")
            for cm, agg, koi in sorted(rows):
                fh.write(f"{cm},{fmt_px(agg[0])},{fmt_px(agg[1])},{fmt_px(agg[2])},"
                         f"{fmt_px(agg[3])},{int(agg[4])},{koi}\n")
        day_counters["future_universe_files"] += 1

    state["counters_day"] = dict(day_counters)
    counters.update(day_counters)
    return state


# --------------------------------------------------------------------------------------
# finalize: daily zips spanning the converted range
# --------------------------------------------------------------------------------------

def finalize(out_dir, market, state_dir):
    """Compose year-spanning daily zips from per-day aggregates."""
    opt_frames, fut_frames = [], []
    for path in sorted(glob.glob(os.path.join(state_dir, "*.json"))):
        st = json.load(open(path))
        day = st["day"]
        for r in st["opt_daily"]:
            r["day"] = day
            opt_frames.append(r)
        for r in st["fut_daily"]:
            r["day"] = day
            fut_frames.append(r)

    if opt_frames:
        df = pd.DataFrame(opt_frames)
        df["year"] = df["day"].str[:4]
        for (root, cm, year), g in df.groupby(["root", "cm", "year"]):
            entries = {}
            for (expiry, strike, right), gg in g.groupby(["expiry", "strike", "right"]):
                right_name = "call" if right == "C" else "put"
                name = (f"{root.lower()}_trade_american_{right_name}_"
                        f"{scale_strike(strike)}_{expiry}.csv")
                rows = [f"{r.day} 00:00,{fmt_px(r.o)},{fmt_px(r.h)},{fmt_px(r.l)},"
                        f"{fmt_px(r.c)},{int(r.v)}" for r in gg.sort_values("day").itertuples()]
                entries[name] = "\n".join(rows) + "\n"
            path = os.path.join(out_dir, "futureoption", market, "daily", root.lower(), cm,
                                f"{root.lower()}_{year}_trade_american.zip")
            write_zip(path, entries)
        log(f"finalize: {df.groupby(['root','cm','year']).ngroups} option daily zips")

    if fut_frames:
        df = pd.DataFrame(fut_frames)
        for ticker, g in df.groupby("ticker"):
            entries = {}
            for cm, gg in g.groupby("cm"):
                rows = [f"{r.day} 00:00,{fmt_px(r.o)},{fmt_px(r.h)},{fmt_px(r.l)},"
                        f"{fmt_px(r.c)},{int(r.v)}" for r in gg.sort_values("day").itertuples()]
                entries[f"{ticker}_trade_{cm}.csv"] = "\n".join(rows) + "\n"
            path = os.path.join(out_dir, "future", market, "daily", f"{ticker}_trade.zip")
            write_zip(path, entries)
            oi_entries = {}
            for cm, gg in g.groupby("cm"):
                rows = [f"{r.day} 00:00,{int(r.oi)}" for r in gg.sort_values("day").itertuples()]
                oi_entries[f"{ticker}_openinterest_{cm}.csv"] = "\n".join(rows) + "\n"
            write_zip(os.path.join(out_dir, "future", market, "daily",
                                   f"{ticker}_openinterest.zip"), oi_entries)
        log(f"finalize: future daily zips for {df['ticker'].nunique()} tickers")


# --------------------------------------------------------------------------------------
# main
# --------------------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dbn-dir", action="append", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--market", default="cme")
    ap.add_argument("--start", default=None)
    ap.add_argument("--end", default=None)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--finalize-only", action="store_true")
    ap.add_argument("--no-finalize", action="store_true")
    args = ap.parse_args()

    state_dir = os.path.join(args.out, "_state")
    os.makedirs(state_dir, exist_ok=True)

    if not args.finalize_only:
        roles = classify_dirs(args.dbn_dir)
        # trades schema intentionally unused: ohlcv-1m already provides trade bars and
        # tick output is out of scope for the pilot; kept on disk for spot-validation
        days = sorted({m.group(1) for d in args.dbn_dir
                       for f in glob.glob(os.path.join(d, "*.dbn.zst"))
                       for m in [re.search(r"(\d{8})\.\w", os.path.basename(f))] if m})
        if args.start:
            days = [d for d in days if d >= args.start]
        if args.end:
            days = [d for d in days if d <= args.end]
        log(f"{len(days)} days to convert -> {args.out}")

        counters = collections.Counter()
        for i, day in enumerate(days):
            state_path = os.path.join(state_dir, f"{day}.json")
            if os.path.exists(state_path) and not args.force:
                continue
            state = convert_day(day, roles, args.out, args.market, counters)
            with open(state_path + ".tmp", "w") as fh:
                json.dump(state, fh)
            os.replace(state_path + ".tmp", state_path)
            if i % 10 == 0:
                log(f"[{i+1}/{len(days)}] {day} done "
                    f"(universe_files={state['counters_day'].get('universe_files', 0)})")
        log("day pass complete; cumulative counters:")
        for k, v in sorted(counters.items()):
            log(f"  {k} = {v}")

    if not args.no_finalize:
        finalize(args.out, args.market, state_dir)
    log("DONE")


if __name__ == "__main__":
    main()
