#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.
"""DMS-1565: per-block summary of a campaign output directory.

Usage: summarize.py <output-dir>
"""
import json
import pathlib
import statistics
import sys


def pct(values, p):
    """Linear-interpolated percentile, p in [0, 1]."""
    ordered = sorted(values)
    rank = (len(ordered) - 1) * p
    lower = int(rank)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (rank - lower)


def runtime_total(snapshot, metric):
    """Sum of count_executions * avg_<metric> over every runtime-stats row in a snapshot."""
    return sum(row["count_executions"] * (row[metric] or 0) for row in snapshot["runtime"])


def execution_total(snapshot):
    return sum(row["count_executions"] for row in snapshot["runtime"])


def compiles_by_query(snapshot):
    return {query["query_id"]: query["count_compiles"] for query in snapshot["queries"]}


def resource_summary(result):
    """Latency percentiles and measured-window Query Store deltas for one resource of one block."""
    before = result["queryStoreBefore"]
    after = result["queryStoreAfter"]
    executions = execution_total(after) - execution_total(before)
    compiles_before = compiles_by_query(before)
    compiles_after = compiles_by_query(after)
    latency = result["latencyMs"]

    return {
        "n": len(latency),
        "median": statistics.median(latency),
        "p95": pct(latency, 0.95),
        "exec_delta": executions,
        "root_duration_us": (runtime_total(after, "avg_duration") - runtime_total(before, "avg_duration")) / executions,
        "root_cpu_us": (runtime_total(after, "avg_cpu_time") - runtime_total(before, "avg_cpu_time")) / executions,
        "root_logical_reads": (
            runtime_total(after, "avg_logical_io_reads") - runtime_total(before, "avg_logical_io_reads")
        )
        / executions,
        "compiles_window": sum(count - compiles_before.get(query_id, 0) for query_id, count in compiles_after.items()),
        "compiles_total": sum(compiles_after.values()),
        "plans": len({row["plan_id"] for row in after["runtime"]}),
    }


def main(output_dir):
    for path in sorted(pathlib.Path(output_dir).glob("block-*.json")):
        block = json.loads(path.read_text())
        print(
            f"== block {block['block']} {block['arm']} smoke={block['smokeStatus']} "
            f"hash={block['effectiveSchema'][0]['EffectiveSchemaHash'][:8]} "
            f"qs={block['queryStoreStateAtEnd'][0]['actual_state_desc']}"
        )
        for resource, result in block["results"].items():
            summary = resource_summary(result)
            print(
                f"  {resource:26} n={summary['n']} median={summary['median']:6.2f} p95={summary['p95']:6.2f} "
                f"execDelta={summary['exec_delta']} rootDuration={summary['root_duration_us']:6.0f}us "
                f"cpu={summary['root_cpu_us']:6.0f}us reads={summary['root_logical_reads']:6.1f} "
                f"compilesInWindow={summary['compiles_window']} compiles={summary['compiles_total']} "
                f"plans={summary['plans']}"
            )


if __name__ == "__main__":
    main(sys.argv[1])
