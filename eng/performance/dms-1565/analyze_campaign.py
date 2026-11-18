#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.
"""DMS-1565 campaign analysis.

Usage: analyze_campaign.py <output-dir>   (writes <output-dir>/analysis.json and block-medians.csv)

The captures are validated first and the analysis refuses to run on anything invalid: block ids must be
unique and form at least two complete baseline/changed pairs of consecutive block ids with equal settings and resource order,
every block must report a successful smoke GET, the same effective schema hash and a READ_WRITE Query
Store at both ends, and every resource must have exactly the declared number of latency samples and root
UPDATE executions with non-negative counter deltas.

Per resource and block: n, median, p95 of client-side PUT latency; Query Store root-UPDATE deltas over the
measured window. Regression rule (per resource, pooled over blocks): the relative median change
    delta = (median(changed samples) - median(baseline samples)) / median(baseline samples)
is flagged when delta > max(0.05, 2 * s / m), where s = sqrt((var(baseline block medians) +
var(changed block medians)) / 2) with sample variances, and m = mean(baseline block medians). p95 rises
above +10% are flagged separately. A wide threshold is reported as inconclusive, never as "no regression".
"""
import csv
import json
import math
import numbers
import pathlib
import statistics
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from summarize import compiles_by_query, execution_total, pct, resource_summary, runtime_total  # noqa: E402

RESOURCES = ["sessions", "courseOfferings", "studentSchoolAssociations", "classPeriods", "sections"]
RUNTIME_METRICS = ["avg_duration", "avg_cpu_time", "avg_logical_io_reads"]


class InvalidCapture(Exception):
    pass


def require(condition, message):
    if not condition:
        raise InvalidCapture(message)


def load_blocks(output_dir):
    blocks = [json.loads(path.read_text()) for path in sorted(pathlib.Path(output_dir).glob("block-*.json"))]
    require(blocks, f"no block-*.json files in {output_dir}")
    return blocks


def finite(value):
    return isinstance(value, numbers.Real) and math.isfinite(value)


def validate_block(block):
    label = f"block {block.get('block')} ({block.get('arm')})"
    require(block["arm"] in ("baseline", "changed"), f"{label}: unknown arm")
    require(block["smokeStatus"] == 200, f"{label}: smoke GET returned {block['smokeStatus']}")
    for key in ("queryStoreStateAtStart", "queryStoreStateAtEnd"):
        state = block[key][0]
        require(
            state["actual_state_desc"] == "READ_WRITE" and int(state["readonly_reason"]) == 0,
            f"{label}: Query Store {key} is {state['actual_state_desc']} (readonly_reason {state['readonly_reason']})",
        )
    require(sorted(block["resourceOrder"]) == sorted(RESOURCES), f"{label}: resource order {block['resourceOrder']}")
    require(sorted(block["results"]) == sorted(RESOURCES), f"{label}: results for {sorted(block['results'])}")
    measured = int(block["measured"])

    for resource in RESOURCES:
        result = block["results"][resource]
        where = f"{label} {resource}"
        latency = result["latencyMs"]
        require(len(latency) == measured, f"{where}: {len(latency)} latency samples, expected {measured}")
        require(all(finite(value) and value > 0 for value in latency), f"{where}: non-finite or non-positive latency sample")
        before, after = result["queryStoreBefore"], result["queryStoreAfter"]
        executions = execution_total(after) - execution_total(before)
        require(
            executions == measured and result["rootUpdateExecutionDelta"] == measured,
            f"{where}: root UPDATE execution delta {executions} (recorded {result['rootUpdateExecutionDelta']}), "
            f"expected {measured}",
        )
        for row in before["runtime"] + after["runtime"]:
            require(
                finite(row["count_executions"]) and all(finite(row[metric] or 0) for metric in RUNTIME_METRICS),
                f"{where}: non-finite Query Store runtime value",
            )
        for metric in RUNTIME_METRICS:
            require(
                runtime_total(after, metric) >= runtime_total(before, metric),
                f"{where}: negative {metric} delta",
            )
        compiles_before = compiles_by_query(before)
        for query_id, count in compiles_by_query(after).items():
            require(count >= compiles_before.get(query_id, 0), f"{where}: compile count fell for query {query_id}")


def validate(blocks):
    ids = [int(block["block"]) for block in blocks]
    require(len(ids) == len(set(ids)), f"duplicate block ids {sorted(ids)}")
    blocks = sorted(blocks, key=lambda block: int(block["block"]))
    require(len(blocks) % 2 == 0, f"{len(blocks)} blocks do not form complete pairs")
    require(len(blocks) >= 4, f"{len(blocks) // 2} pair(s) cannot support the regression threshold; at least two are required")
    hashes = {block["effectiveSchema"][0]["EffectiveSchemaHash"] for block in blocks}
    require(len(hashes) == 1, f"effective schema hashes differ across blocks: {sorted(hashes)}")
    settings = {(block["warmup"], block["measured"]) for block in blocks}
    require(len(settings) == 1, f"warmup/measured settings differ across blocks: {sorted(settings)}")

    for block in blocks:
        validate_block(block)

    pairs = []
    for index in range(0, len(blocks), 2):
        baseline, changed = blocks[index], blocks[index + 1]
        label = f"pair of blocks {baseline['block']} and {changed['block']}"
        require(
            baseline["arm"] == "baseline" and changed["arm"] == "changed",
            f"{label}: expected baseline then changed, found {baseline['arm']} then {changed['arm']}",
        )
        require(
            int(changed["block"]) == int(baseline["block"]) + 1,
            f"{label}: a changed block must immediately follow its baseline block",
        )
        require(
            baseline["resourceOrder"] == changed["resourceOrder"],
            f"{label}: resource orders differ ({baseline['resourceOrder']} vs {changed['resourceOrder']})",
        )
        pairs.append((baseline, changed))
    return pairs


def analyze(pairs):
    report = {"pairs": [], "resources": {}}
    for baseline, changed in pairs:
        report["pairs"].append(
            {
                "blocks": [int(baseline["block"]), int(changed["block"])],
                "order": baseline["resourceOrder"],
                "baseline": {r: resource_summary(baseline["results"][r]) for r in RESOURCES},
                "changed": {r: resource_summary(changed["results"][r]) for r in RESOURCES},
            }
        )

    for resource in RESOURCES:
        per_arm = {arm: [pair[arm][resource] for pair in report["pairs"]] for arm in ("baseline", "changed")}
        samples = {
            "baseline": [v for baseline, _ in pairs for v in baseline["results"][resource]["latencyMs"]],
            "changed": [v for _, changed in pairs for v in changed["results"][resource]["latencyMs"]],
        }
        medians = {arm: [summary["median"] for summary in per_arm[arm]] for arm in per_arm}
        pooled = {
            arm: {"n": len(samples[arm]), "median": statistics.median(samples[arm]), "p95": pct(samples[arm], 0.95)}
            for arm in samples
        }
        spread = math.sqrt((statistics.variance(medians["baseline"]) + statistics.variance(medians["changed"])) / 2)
        threshold = max(0.05, 2 * spread / statistics.mean(medians["baseline"]))
        median_change = (pooled["changed"]["median"] - pooled["baseline"]["median"]) / pooled["baseline"]["median"]
        p95_change = (pooled["changed"]["p95"] - pooled["baseline"]["p95"]) / pooled["baseline"]["p95"]
        pair_changes = [(c - b) / b for b, c in zip(medians["baseline"], medians["changed"])]

        def mean_of(arm, key):
            return statistics.mean(summary[key] for summary in per_arm[arm])

        report["resources"][resource] = {
            "pooled": pooled,
            "block_medians": medians,
            "pair_relative_changes": pair_changes,
            "s_pooled_ms": spread,
            "threshold": threshold,
            "relative_median_change": median_change,
            "relative_p95_change": p95_change,
            "regression_flag": median_change > threshold,
            "p95_flag": p95_change > 0.10,
            "pairs_lower": sum(change < 0 for change in pair_changes),
            "root": {
                arm: {
                    "duration_us": mean_of(arm, "root_duration_us"),
                    "cpu_us": mean_of(arm, "root_cpu_us"),
                    "logical_reads": mean_of(arm, "root_logical_reads"),
                    "compiles_window": [summary["compiles_window"] for summary in per_arm[arm]],
                    "compiles_total": [summary["compiles_total"] for summary in per_arm[arm]],
                    "plans": [summary["plans"] for summary in per_arm[arm]],
                }
                for arm in per_arm
            },
        }
    return report


def write_block_medians(report, path):
    with open(path, "w", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(["baseline_block", "changed_block", "resource", "baseline_median_ms", "changed_median_ms",
                         "baseline_p95_ms", "changed_p95_ms"])
        for pair in report["pairs"]:
            for resource in RESOURCES:
                b, c = pair["baseline"][resource], pair["changed"][resource]
                writer.writerow([*pair["blocks"], resource, f"{b['median']:.3f}", f"{c['median']:.3f}",
                                 f"{b['p95']:.3f}", f"{c['p95']:.3f}"])


def main(output_dir):
    try:
        pairs = validate(load_blocks(output_dir))
    except InvalidCapture as error:
        print(f"invalid capture: {error}", file=sys.stderr)
        return 2

    report = analyze(pairs)
    pathlib.Path(output_dir, "analysis.json").write_text(json.dumps(report, indent=1))
    write_block_medians(report, pathlib.Path(output_dir, "block-medians.csv"))

    for resource, result in report["resources"].items():
        b, c = result["pooled"]["baseline"], result["pooled"]["changed"]
        print(
            f"{resource:26} median {b['median']:6.2f} -> {c['median']:6.2f} ms "
            f"({result['relative_median_change']:+.1%}, threshold {result['threshold']:.1%}, "
            f"flag={result['regression_flag']}) p95 {b['p95']:6.2f} -> {c['p95']:6.2f} "
            f"({result['relative_p95_change']:+.1%}, flag={result['p95_flag']}) "
            f"pairs lower {result['pairs_lower']}/{len(report['pairs'])}"
        )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
