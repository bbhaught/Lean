#!/usr/bin/env python
"""fop-weeklies fork P5-lite registry validation against real Databento GLBX definitions.

Reads ALL pilot definition records (option and future datasets), extracts per-instrument
identity (raw_symbol, asset/root, instrument_class, expiration, strike, underlying id),
resolves each option's underlying future via the definitions' underlying instrument id
(authoritative, design_B issue 2), and validates the fork against the exchange:

  (a) root coverage      every observed option asset/root is present in the fork registry
                         (Data/symbol-properties/future-option-roots.json)
  (b) expiry parity      the P2 expiry engine reproduces the exchange expiration date for
                         every observed (root, contract key) - via the EngineParity helper
  (c) underlying parity  the P3 underlying resolver reproduces the exchange's underlying
                         contract month - THE design-B issue-2 parity test on real data
  (d) listed-since       registry ListedSince versus first-seen / activation dates per root

Optionally (--classify) classifies the option asset codes enumerated from ALL_SYMBOLS
definition snapshots (e.g. /home/bb/data/databento/enum/) against the registry and derives
per-asset weekday/week-of-month/underlying facts from the snapshot definitions themselves.

Usage:
  python tools/databento/validate_registry.py \
      --opt-defs <dir-with-*.definition.dbn.zst> [--opt-defs <dir2> ...] \
      --fut-defs <dir-with-*.definition.dbn.zst> \
      --lean-root <fork repo root> \
      [--engine-parity-bin <path to EngineParity binary>] \
      [--classify <enum dir with all_defs_*.dbn.zst and option_assets_union.txt>] \
      [--report <output markdown>] [--workdir <scratch dir>]

The EngineParity helper (tools/databento/EngineParity) is built with:
  dotnet build tools/databento/EngineParity -c Release
"""
from __future__ import annotations

import argparse
import calendar
import collections
import glob
import json
import os
import re
import subprocess
import sys
from datetime import datetime

import pandas as pd

try:
    import databento as db
except ImportError:  # pragma: no cover
    sys.exit("databento package required (conda LEAN env)")

MONTH_CODES = {"F": 1, "G": 2, "H": 3, "J": 4, "K": 5, "M": 6,
               "N": 7, "Q": 8, "U": 9, "V": 10, "X": 11, "Z": 12}
DAY_NAMES = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]


def log(msg):
    print(msg, flush=True)


def read_definition_file(path):
    """Read one definition DBN file into a DataFrame (raw records, no filtering)."""
    return db.DBNStore.from_file(path).to_df()


def file_date(path):
    m = re.search(r"(\d{8})\.definition", os.path.basename(path))
    return m.group(1) if m else None


def week_of_month(date):
    """CME convention: week N = Nth occurrence of that weekday in the calendar month."""
    return (date.day - 1) // 7 + 1


def extract(opt_dirs, fut_dirs):
    """Scan every definition day; return (options df, futures df) of unique instruments.

    Options keyed by (instrument_id, raw_symbol): fields asset, expiration, strike, right,
    underlying_id, underlying (raw string), first_seen (first file date the instrument
    appears in), activation. Futures: outrights only (instrument_class F), fields
    raw_symbol, maturity_year, maturity_month, expiration.
    """
    opt_rows = {}
    fut_rows = {}
    inconsistent = []

    fut_files = sorted(f for d in fut_dirs for f in glob.glob(os.path.join(d, "*.definition.dbn.zst")))
    opt_files = sorted(f for d in opt_dirs for f in glob.glob(os.path.join(d, "*.definition.dbn.zst")))
    log(f"extract: {len(opt_files)} option definition days, {len(fut_files)} future definition days")

    for path in fut_files:
        day = file_date(path)
        df = read_definition_file(path)
        fut = df[df["instrument_class"] == "F"]
        for row in fut.itertuples():
            key = (row.instrument_id, row.raw_symbol)
            if key not in fut_rows:
                fut_rows[key] = {
                    "instrument_id": row.instrument_id,
                    "raw_symbol": row.raw_symbol,
                    "asset": row.asset,
                    "maturity_year": int(row.maturity_year),
                    "maturity_month": int(row.maturity_month),
                    "expiration": row.expiration,
                    "first_seen": day,
                }

    for path in opt_files:
        day = file_date(path)
        df = read_definition_file(path)
        opts = df[(df["instrument_class"].isin(["C", "P"])) & (df["user_defined_instrument"] != "Y")]
        for row in opts.itertuples():
            key = (row.instrument_id, row.raw_symbol)
            prev = opt_rows.get(key)
            if prev is None:
                opt_rows[key] = {
                    "instrument_id": row.instrument_id,
                    "raw_symbol": row.raw_symbol,
                    "asset": row.asset,
                    "right": row.instrument_class,
                    "expiration": row.expiration,
                    "activation": row.activation,
                    "strike": float(row.strike_price),
                    "underlying_id": int(row.underlying_id),
                    "underlying": row.underlying,
                    "first_seen": day,
                }
            else:
                # identity fields must be stable across daily snapshots
                if (prev["expiration"] != row.expiration or prev["strike"] != float(row.strike_price)
                        or prev["underlying"] != row.underlying):
                    inconsistent.append((day, row.raw_symbol))

    opt = pd.DataFrame(opt_rows.values())
    fut = pd.DataFrame(fut_rows.values())
    log(f"extract: {len(opt)} unique option instruments, {len(fut)} unique future outrights, "
        f"{len(inconsistent)} field-instability events")
    return opt, fut, inconsistent


def join_underlying(opt, fut):
    """Resolve each option's underlying contract month via the underlying instrument id
    (authoritative), with the record's own raw 'underlying' string as a cross-check."""
    fut_by_id = {}
    for row in fut.itertuples():
        fut_by_id.setdefault(row.instrument_id, row)

    months, mismatches, unresolved = [], [], []
    for row in opt.itertuples():
        f = fut_by_id.get(row.underlying_id)
        if f is None:
            unresolved.append(row.raw_symbol)
            months.append(None)
            continue
        if f.raw_symbol != row.underlying:
            mismatches.append((row.raw_symbol, row.underlying, f.raw_symbol))
        months.append(f"{f.maturity_year:04d}{f.maturity_month:02d}")
    opt = opt.copy()
    opt["underlying_month"] = months
    return opt, mismatches, unresolved


def load_registry(lean_root):
    path = os.path.join(lean_root, "Data", "symbol-properties", "future-option-roots.json")
    data = json.load(open(path))
    by_ticker = {}
    for r in data["roots"]:
        by_ticker[(r["optionTicker"], r["market"])] = r
    return data, by_ticker


def build_parity_input(opt, registry_by_ticker, market, workdir):
    """One parity row per unique (root, contract key month, exchange expiry date).

    Contract key month: for standard/legacy roots the underlying future contract month
    (the option 'contract month'); for weekly/EOM/daily roots the expiration's calendar
    month (the P2 engine's contract key per design A section 2)."""
    rows = set()
    skipped = []
    for row in opt.itertuples():
        root = row.asset
        exp = pd.Timestamp(row.expiration)
        reg = registry_by_ticker.get((root, market))
        standard = reg is None or reg.get("cycle") in ("Standard", "Monthly", "Quarterly")
        if standard and row.underlying_month:
            key = row.underlying_month
        else:
            key = f"{exp.year:04d}{exp.month:02d}"
        if key is None:
            skipped.append(row.raw_symbol)
            continue
        rows.add((root, market, key, exp.strftime("%Y%m%d")))
    path = os.path.join(workdir, "parity_input.csv")
    with open(path, "w") as fh:
        fh.write("optionTicker,market,contractKeyMonth,exchangeExpiry\n")
        for r in sorted(rows):
            fh.write(",".join(r) + "\n")
    log(f"parity input: {len(rows)} unique (root, key, expiry) rows -> {path}")
    return path, len(rows)


def run_engine_parity(lean_root, parity_bin, input_csv, workdir):
    out = os.path.join(workdir, "parity_output.csv")
    data_folder = os.path.join(lean_root, "Data")
    if parity_bin and os.path.exists(parity_bin):
        cmd = [parity_bin]
    else:
        cmd = ["dotnet", "run", "--project",
               os.path.join(lean_root, "tools", "databento", "EngineParity"),
               "-c", "Release", "--"]
    cmd += ["--data-folder", data_folder, "--input", input_csv, "--output", out]
    log("running: " + " ".join(cmd))
    subprocess.run(cmd, check=True)
    return pd.read_csv(out, dtype=str, keep_default_na=False)


def compare(opt, parity, registry_by_ticker, market):
    """Returns (expiry mismatches, underlying mismatches, engine errors, stats)."""
    parity_key = {}
    for row in parity.itertuples():
        parity_key[(row.optionTicker, row.contractKeyMonth, row.exchangeExpiry)] = row

    expiry_bad, underlying_bad, errors = [], [], []
    n_expiry = n_underlying = 0
    checked_keys = set()
    for row in parity.itertuples():
        if row.error:
            errors.append(row)
            continue
        n_expiry += 1
        if row.engineExpiry != row.exchangeExpiry:
            expiry_bad.append(row)

    # underlying parity is per unique (root, expiry, exchange underlying month)
    uniq = opt.dropna(subset=["underlying_month"]).drop_duplicates(
        subset=["asset", "expiration", "underlying_month"])
    for row in uniq.itertuples():
        exp = pd.Timestamp(row.expiration)
        reg = registry_by_ticker.get((row.asset, market))
        standard = reg is None or reg.get("cycle") in ("Standard", "Monthly", "Quarterly")
        key = row.underlying_month if standard else f"{exp.year:04d}{exp.month:02d}"
        p = parity_key.get((row.asset, key, exp.strftime("%Y%m%d")))
        if p is None or p.error:
            continue
        n_underlying += 1
        if p.engineUnderlyingMonth != row.underlying_month:
            underlying_bad.append((row.asset, exp.strftime("%Y%m%d"),
                                   row.underlying_month, p.engineUnderlyingMonth, row.raw_symbol))
    return expiry_bad, underlying_bad, errors, {"n_expiry": n_expiry, "n_underlying": n_underlying}


# LEAN market per future family (registry roots live on the product's exchange market)
FAMILY_MARKET = {"ES": "cme", "NQ": "cme", "ZN": "cbot", "ZB": "cbot",
                 "GC": "comex", "CL": "nymex"}


def classify_enum(enum_dir, registry_by_ticker, market):
    """Classify enumerated option asset codes from ALL_SYMBOLS snapshots: derive weekday
    histogram, week-of-month histogram, underlying asset, first/last observed snapshot."""
    union_path = os.path.join(enum_dir, "option_assets_union.txt")
    union = [tuple(l.strip().split(",")) for l in open(union_path) if l.strip()]
    snaps = sorted(glob.glob(os.path.join(enum_dir, "all_defs_*.dbn.zst")))
    per_asset = collections.defaultdict(lambda: {
        "family": None, "snapshots": set(), "weekdays": collections.Counter(),
        "weeks": collections.Counter(), "underlying_assets": collections.Counter(),
        "n": 0, "expiry_times": collections.Counter()})
    wanted = {a for a, _ in union}
    for snap in snaps:
        tag = re.search(r"all_defs_(\d{8})", snap).group(1)
        df = read_definition_file(snap)
        fut = df[df["instrument_class"] == "F"][["instrument_id", "asset"]]
        fut_asset = dict(zip(fut.instrument_id, fut.asset))
        opts = df[(df["instrument_class"].isin(["C", "P"])) & (df["user_defined_instrument"] != "Y")]
        opts = opts[opts["asset"].isin(wanted)]
        for row in opts.itertuples():
            rec = per_asset[row.asset]
            rec["snapshots"].add(tag)
            exp = pd.Timestamp(row.expiration)
            rec["weekdays"][DAY_NAMES[exp.weekday()]] += 1
            rec["weeks"][week_of_month(exp)] += 1
            rec["expiry_times"][exp.strftime("%H:%M")] += 1
            ua = fut_asset.get(row.underlying_id)
            if ua:
                rec["underlying_assets"][ua] += 1
            rec["n"] += 1
    lines = []
    for asset, family in sorted(union):
        rec = per_asset.get(asset)
        in_reg = (asset, FAMILY_MARKET.get(family, market)) in registry_by_ticker
        if rec is None:
            lines.append((asset, family, in_reg, "NOT OBSERVED in sampled snapshots", "", "", ""))
            continue
        wd = ",".join(f"{k}:{v}" for k, v in rec["weekdays"].most_common(3))
        wk = ",".join(f"w{k}:{v}" for k, v in sorted(rec["weeks"].items()))
        ua = ",".join(f"{k}:{v}" for k, v in rec["underlying_assets"].most_common(2))
        lines.append((asset, family, in_reg, wd, wk, ua, ",".join(sorted(rec["snapshots"]))))
    return lines


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--opt-defs", action="append", required=True)
    ap.add_argument("--fut-defs", action="append", required=True)
    ap.add_argument("--lean-root", required=True)
    ap.add_argument("--engine-parity-bin", default=None)
    ap.add_argument("--market", default="cme")
    ap.add_argument("--classify", default=None)
    ap.add_argument("--report", default=None)
    ap.add_argument("--workdir", default="/tmp/fop_registry_validation")
    args = ap.parse_args()

    os.makedirs(args.workdir, exist_ok=True)
    registry_data, registry_by_ticker = load_registry(args.lean_root)

    opt, fut, inconsistent = extract(args.opt_defs, args.fut_defs)
    opt, xchk_mismatch, unresolved = join_underlying(opt, fut)
    opt.to_csv(os.path.join(args.workdir, "instruments_options.csv"), index=False)
    fut.to_csv(os.path.join(args.workdir, "instruments_futures.csv"), index=False)

    # (a) root coverage
    observed_roots = sorted(opt["asset"].unique())
    missing_roots = [r for r in observed_roots if (r, args.market) not in registry_by_ticker]

    # (b)+(c) engine parity
    parity_in, _ = build_parity_input(opt, registry_by_ticker, args.market, args.workdir)
    parity = run_engine_parity(args.lean_root, args.engine_parity_bin, parity_in, args.workdir)
    expiry_bad, underlying_bad, errors, stats = compare(opt, parity, registry_by_ticker, args.market)

    # (d) listed-since sanity
    listed = []
    for root in observed_roots:
        sub = opt[opt["asset"] == root]
        first_seen = sub["first_seen"].min()
        first_activation = pd.to_datetime(sub["activation"]).min()
        reg = registry_by_ticker.get((root, args.market))
        listed_since = reg["listedSince"] if reg else "N/A"
        ok = reg is None or str(first_seen) >= listed_since.replace("-", "")
        listed.append((root, len(sub), first_seen, str(first_activation)[:10], listed_since, ok))

    # ---- report ----
    out = []
    out.append(f"- option definition days scanned: unique instruments = {len(opt)}, "
               f"future outrights = {len(fut)}")
    out.append(f"- field-instability events across daily snapshots: {len(inconsistent)}")
    out.append(f"- underlying id->raw-symbol cross-check mismatches: {len(xchk_mismatch)}")
    out.append(f"- options with unresolvable underlying_id: {len(unresolved)}")
    out.append(f"- observed roots: {observed_roots}")
    out.append(f"- roots MISSING from registry: {missing_roots or 'none'}")
    ep = 100.0 * (1 - len(expiry_bad) / max(stats["n_expiry"], 1))
    up = 100.0 * (1 - len(underlying_bad) / max(stats["n_underlying"], 1))
    out.append(f"- expiry parity: {stats['n_expiry'] - len(expiry_bad)}/{stats['n_expiry']} ({ep:.2f}%)")
    out.append(f"- underlying parity: {stats['n_underlying'] - len(underlying_bad)}/{stats['n_underlying']} ({up:.2f}%)")
    out.append(f"- engine errors: {len(errors)}")
    for r in expiry_bad[:50]:
        out.append(f"  EXPIRY MISMATCH {r.optionTicker} key={r.contractKeyMonth} "
                   f"exchange={r.exchangeExpiry} engine={r.engineExpiry}")
    for r in underlying_bad[:50]:
        out.append(f"  UNDERLYING MISMATCH {r[0]} expiry={r[1]} exchange={r[2]} engine={r[3]} ({r[4]})")
    for r in errors[:20]:
        out.append(f"  ENGINE ERROR {r.optionTicker} key={r.contractKeyMonth}: {r.error}")
    out.append("- listed-since (root, contracts, first_seen_file, first_activation, registry, ok):")
    for row in listed:
        out.append(f"  {row}")

    if args.classify:
        out.append("")
        out.append("## Enumerated asset classification (ALL_SYMBOLS snapshots)")
        out.append("asset | family | in_registry | weekdays | weeks | underlying_assets | snapshots")
        for line in classify_enum(args.classify, registry_by_ticker, args.market):
            out.append(" | ".join(str(x) for x in line))

    text = "\n".join(out)
    print(text)
    if args.report:
        with open(args.report, "w") as fh:
            fh.write(text + "\n")
        log(f"wrote {args.report}")


if __name__ == "__main__":
    main()
