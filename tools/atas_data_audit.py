#!/usr/bin/env python3
"""Audit one CashReaper replay before using it for offline optimisation.

The ATAS bridge uploads append-only CSV files in byte-range chunks. This tool
selects a strategy instance, deduplicates repeated bar uploads and verifies
that the market data and the strategy's own diagnostic events agree. It does
not simulate trades or recommend parameters.
"""

from __future__ import annotations

import argparse
import csv
import json
import re
from collections import Counter
from decimal import Decimal, InvalidOperation
from pathlib import Path


CHUNK_PATTERN = re.compile(r"-v1-(\d+)-(\d+)\.csv$")


def number(value: str) -> Decimal | None:
    try:
        return Decimal(value) if value else None
    except InvalidOperation:
        return None


def chunks(root: Path, prefix: str, instance: str) -> list[tuple[int, int, Path]]:
    found = []
    for path in root.glob(prefix + "-v1-*.csv"):
        match = CHUNK_PATTERN.search(path.name)
        if not match:
            continue
        with path.open(newline="", encoding="utf-8-sig", errors="replace") as stream:
            first = next(csv.DictReader(stream), None)
        if first and instance in first.get("instance", ""):
            found.append((int(match[1]), int(match[2]), path))
    return sorted(found)


def audit_stream_ranges(files: list[tuple[int, int, Path]]) -> dict:
    gaps = []
    overlaps = []
    for previous, current in zip(files, files[1:]):
        if current[0] > previous[1]:
            gaps.append([previous[1], current[0]])
        elif current[0] < previous[1]:
            overlaps.append([current[0], previous[1]])
    return {"files": len(files), "byte_gaps": gaps[:20], "byte_overlaps": overlaps[:20]}


def read_rows(files: list[tuple[int, int, Path]]):
    for _, _, path in files:
        with path.open(newline="", encoding="utf-8-sig", errors="replace") as stream:
            for row in csv.DictReader(stream):
                yield row


def audit(root: Path, instance: str) -> dict:
    stats_files = chunks(root, "CashReaperStats-imbalance", instance)
    bar_files = chunks(root, "CashReaperBars-full", instance)
    footprint_files = chunks(root, "CashReaperFootprint-imbalance", instance)
    tick_files = chunks(root, "CashReaperTicks", instance)
    market_files = chunks(root, "CashReaperMarketEvents", instance)
    issues = []
    if not stats_files:
        issues.append("no_statistics_chunks")
    if not bar_files:
        issues.append("no_full_bar_chunks")

    bars = {}
    repeated_bars = 0
    revised_bars = 0
    bar_segments = 1
    previous_index = None
    for row in read_rows(bar_files):
        key = row.get("bar", "")
        if not key.isdigit():
            issues.append("invalid_bar_index")
            continue
        index = int(key)
        if previous_index is not None and index < previous_index:
            # The same instance can be replayed again. Old bars must not be
            # mixed with the new pass while the bridge catches up.
            bar_segments += 1
            bars = {}
        previous_index = index
        previous = bars.get(index)
        if previous is not None:
            repeated_bars += 1
            if previous != row:
                revised_bars += 1
        # A replay restarted in the same strategy instance can rewrite history.
        # Use the most recent export, and disclose the revision count.
        bars[index] = row

    missing_bars = []
    if bars:
        missing_bars = sorted(set(range(min(bars), max(bars) + 1)) - bars.keys())
        if missing_bars:
            issues.append("missing_bar_indices")
    segments = [[]]
    previous_chart_time = None
    for row in read_rows(stats_files):
        chart_time = row.get("chart_time", "")
        if previous_chart_time and chart_time and chart_time < previous_chart_time:
            segments.append([])
        segments[-1].append(row)
        if chart_time:
            previous_chart_time = chart_time
    if len(segments) > 1:
        issues.append("multiple_replays_same_instance")
    selected_rows = segments[-1]

    events = Counter()
    outcomes = Counter()
    signals = []
    accepted = []
    fills = []
    closes = []
    config = None
    latest_chart_time = None
    bad_macd = []
    bad_delta = []
    missing_delta = []
    bad_imbalance = []
    for row in selected_rows:
        event = row.get("event", "")
        events[event] += 1
        latest_chart_time = row.get("chart_time") or latest_chart_time
        if event == "signal_mode":
            config = row.get("reason")
        elif event == "bar" and row.get("accepted") == "True":
            accepted.append(row)
        elif event == "signal":
            signals.append(row)
            bar_index = row.get("bar", "")
            direction = row.get("direction")
            difference = number(row.get("macd_difference", ""))
            if difference is None or (direction == "Buy" and difference <= 0) or (
                direction == "Sell" and difference >= 0
            ):
                bad_macd.append(bar_index)
            market_bar = bars.get(int(bar_index)) if bar_index.isdigit() else None
            delta = number(market_bar.get("delta", "")) if market_bar else None
            volume = number(market_bar.get("volume", "")) if market_bar else None
            if delta is None or volume is None or volume <= 0:
                missing_delta.append(bar_index)
            elif config and "delta_filter=True" in config and (
                delta if direction == "Buy" else -delta
            ) * 100 / volume <= Decimal(
                re.search(r"delta_threshold_pct=([\d.]+)", config).group(1)
            ):
                bad_delta.append(bar_index)
            imbalance = number(row.get("imbalance", ""))
            if imbalance is not None and config and "imbalance_filter=True" in config and imbalance < Decimal(
                re.search(r"imbalance_threshold_pct=([\d.]+)", config).group(1)
            ):
                bad_imbalance.append(bar_index)
        elif event == "entry_filled":
            fills.append(row)
        elif event == "trade_closed":
            closes.append(row)
            outcomes[row.get("reason", "")] += 1

    if bad_macd:
        issues.append("macd_signal_mismatch")
    if bad_delta:
        issues.append("delta_signal_mismatch")
    if missing_delta:
        issues.append("signal_bar_missing_delta")
    if bad_imbalance:
        issues.append("imbalance_signal_mismatch")
    if len(accepted) != len(signals):
        issues.append("accepted_signal_count_mismatch")
    if len(signals) != len(fills):
        issues.append("entry_fill_count_mismatch_or_open_entry")
    if len(fills) != len(closes):
        issues.append("trade_count_mismatch_or_open_position")

    tick_count = 0
    tick_time_regressions = 0
    tick_sequence_gaps = 0
    first_tick_time = None
    last_tick_time = None
    previous_tick_sequence = None
    for row in read_rows(tick_files):
        tick_count += 1
        tick_time = row.get("time", "")
        sequence = row.get("sequence", "")
        if first_tick_time is None:
            first_tick_time = tick_time
        if last_tick_time and tick_time and tick_time < last_tick_time:
            tick_time_regressions += 1
        if previous_tick_sequence is not None and sequence.isdigit() and int(sequence) != previous_tick_sequence + 1:
            tick_sequence_gaps += 1
        if sequence.isdigit():
            previous_tick_sequence = int(sequence)
        last_tick_time = tick_time
    if tick_sequence_gaps:
        issues.append("tick_sequence_gaps")

    market_event_counts = Counter()
    market_sequence_gaps = 0
    previous_market_sequence = None
    for row in read_rows(market_files):
        market_event_counts[row.get("kind", "")] += 1
        sequence = row.get("sequence", "")
        if previous_market_sequence is not None and sequence.isdigit() and int(sequence) != previous_market_sequence + 1:
            market_sequence_gaps += 1
        if sequence.isdigit():
            previous_market_sequence = int(sequence)
    if market_sequence_gaps:
        issues.append("market_event_sequence_gaps")

    pnl = sum((number(row.get("last_trade_pnl_points", "")) or Decimal(0) for row in closes), Decimal(0))
    final_pnl = number(closes[-1].get("total_pnl_points", "")) if closes else None
    if final_pnl is not None and pnl != final_pnl:
        issues.append("trade_pnl_sum_mismatch")

    # Footprints currently exist only for bars that reached RecordBarDecision;
    # they are not a complete counterfactual feed for arbitrary signal settings.
    return {
        "instance": instance,
        "replay_segments": len(segments),
        "selected_segment": len(segments),
        "latest_chart_time": latest_chart_time,
        "ready_for_parameter_search": False,
        "reason_not_ready": "strategy_replay_not_yet_validated; intrabar_order_unavailable",
        "streams": {
            "statistics": audit_stream_ranges(stats_files),
            "bars": audit_stream_ranges(bar_files),
            "footprints": audit_stream_ranges(footprint_files),
            "ticks": audit_stream_ranges(tick_files),
            "market_events": audit_stream_ranges(market_files),
        },
        "market_events": {
            "counts_by_kind": dict(market_event_counts),
            "sequence_gaps": market_sequence_gaps,
            "has_trade_prints": market_event_counts["trade"] > 0,
            "has_level2_updates": market_event_counts["depth"] > 0,
            "has_initial_depth_snapshot": market_event_counts["snapshot_begin"] > 0
                and market_event_counts["snapshot"] > 0
                and market_event_counts["snapshot_end"] > 0,
        },
        "ticks": {
            "count": tick_count,
            "first_time": first_tick_time,
            "last_time": last_tick_time,
            "time_regressions": tick_time_regressions,
            "sequence_gaps": tick_sequence_gaps,
        },
        "bars": {
            "unique": len(bars),
            "first_index": min(bars) if bars else None,
            "last_index": max(bars) if bars else None,
            "replay_segments": bar_segments,
            "repeated_upload_rows": repeated_bars,
            "revised_bar_rows": revised_bars,
            "missing_indices": len(missing_bars),
            "first_missing_indices": missing_bars[:20],
        },
        "events": dict(events),
        "checks": {
            "accepted_bars": len(accepted),
            "signals": len(signals),
            "entry_fills": len(fills),
            "closed_trades": len(closes),
            "outcomes": dict(outcomes),
            "closed_pnl_points": str(pnl),
            "recorded_total_pnl_points": str(final_pnl) if final_pnl is not None else None,
            "bad_macd_signal_bars": bad_macd[:20],
            "bad_delta_signal_bars": bad_delta[:20],
            "signals_without_delta_bar": missing_delta[:20],
            "bad_imbalance_signal_bars": bad_imbalance[:20],
            "signal_mode": config,
        },
        "issues": sorted(set(issues)),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("data_dir", type=Path, help="Directory containing ATAS bridge CSV chunks")
    parser.add_argument("--instance", required=True, help="Unique instance ID from CashReaper[...; ID]")
    args = parser.parse_args()
    print(json.dumps(audit(args.data_dir, args.instance), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
