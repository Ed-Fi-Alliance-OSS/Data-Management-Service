#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

"""Offline analysis of /connect/token timing evidence. Reads samples; never measures.

Input is the CSV the timing collectors write: one row per measured request, with the columns
round, arm, position, elapsed_us, where every round holds exactly one sample of every arm.

The estimand is the difference of the two arms' medians, median(A) - median(B). Its 95%
interval comes from a moving-block bootstrap that resamples blocks of consecutive rounds, both
arms of a round travelling together, so serial dependence between rounds is kept inside each
block. The decision is equivalence by interval inclusion against a fixed bound delta:

    EQUIVALENT      the interval lies inside [-delta, +delta]
    NOT EQUIVALENT  the interval lies wholly outside it
    INCONCLUSIVE    anything else (never a pass)

Commands (numpy is the only dependency):

    selftest                           deterministic checks of this script; exits nonzero on failure
    analyze  --csv F --delta D         estimate, interval, decision and diagnostics for one run
    plan     --csv PILOT --delta D     smallest candidate N whose predicted half-width is <= D/2
    fit      --csv PILOT               lognormal and drift parameters for `simulate`, from a pilot
    simulate --rounds N --delta D ...  decision rates on synthetic data with known effects
    observer --csv F                   supplementary: one median-of-Q observer's true-positive rate

Every command prints JSON. `analyze` and `observer` include the SHA-256 of the input file.
"""

import argparse
import csv
import hashlib
import json
import math
import os
import sys
import tempfile

import numpy as np

METHOD_VERSION = "1"
DEFAULT_RESAMPLES = 5000
DEFAULT_SEED = 1
BATCH = 250


# --------------------------------------------------------------------------------------------
# Input


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_rounds(path):
    """Returns {arm: array indexed by round}. Every round must hold exactly one sample per arm."""
    by_round = {}
    arms = set()
    # utf-8-sig also accepts a file that starts with a byte order mark.
    with open(path, newline="", encoding="utf-8-sig") as handle:
        for row in csv.DictReader(handle):
            round_index = int(row["round"])
            arm = row["arm"]
            arms.add(arm)
            samples = by_round.setdefault(round_index, {})
            if arm in samples:
                raise ValueError(f"round {round_index} has two samples for arm {arm!r}")
            samples[arm] = float(row["elapsed_us"])

    rounds = sorted(by_round)
    if rounds != list(range(len(rounds))):
        raise ValueError("rounds are not numbered 0..R-1 without gaps")
    for round_index in rounds:
        missing = arms - set(by_round[round_index])
        if missing:
            raise ValueError(f"round {round_index} lacks a sample for {sorted(missing)}")

    return {arm: np.array([by_round[r][arm] for r in rounds]) for arm in sorted(arms)}


# --------------------------------------------------------------------------------------------
# Estimate, interval and decision


def estimate(a, b):
    """The predeclared estimand: the difference of the two marginal medians."""
    return float(np.median(a) - np.median(b))


def block_length(rounds):
    """Pre-registered convention: ceil(R ** (1/3)). The integer check guards float rounding."""
    length = max(1, int(round(rounds ** (1.0 / 3.0))))
    while length ** 3 < rounds:
        length += 1
    while length > 1 and (length - 1) ** 3 >= rounds:
        length -= 1
    return length


def block_indices(rng, rounds, length, resamples):
    """Moving-block resample of round indices: (resamples, rounds) rows of consecutive blocks."""
    length = min(length, rounds)
    blocks = math.ceil(rounds / length)
    starts = rng.integers(0, rounds - length + 1, size=(resamples, blocks))
    indices = starts[:, :, None] + np.arange(length)[None, None, :]
    return indices.reshape(resamples, blocks * length)[:, :rounds]


def bootstrap_interval(a, b, length, resamples, seed):
    """95% percentile interval of median(A*) - median(B*) over moving-block resamples of rounds."""
    rng = np.random.default_rng(seed)
    estimates = np.empty(resamples)
    done = 0
    while done < resamples:
        size = min(BATCH, resamples - done)
        indices = block_indices(rng, len(a), length, size)
        estimates[done : done + size] = np.median(a[indices], axis=1) - np.median(b[indices], axis=1)
        done += size
    low, high = np.percentile(estimates, [2.5, 97.5])
    return float(low), float(high)


def decide(low, high, delta):
    if -delta <= low and high <= delta:
        return "EQUIVALENT"
    if high < -delta or low > delta:
        return "NOT EQUIVALENT"
    return "INCONCLUSIVE"


# --------------------------------------------------------------------------------------------
# Diagnostics


def summary(values):
    p10, p25, p50, p75, p90 = np.percentile(values, [10, 25, 50, 75, 90])
    return {
        "n": int(values.size),
        "mean": float(np.mean(values)),
        "sd": float(np.std(values, ddof=1)) if values.size > 1 else 0.0,
        "min": float(np.min(values)),
        "p10": float(p10),
        "p25": float(p25),
        "p50": float(p50),
        "p75": float(p75),
        "p90": float(p90),
        "max": float(np.max(values)),
    }


def autocorrelation(series, max_lag=20):
    centered = series - np.mean(series)
    variance = float(np.dot(centered, centered))
    if variance == 0.0:
        return [0.0] * min(max_lag, len(series) - 1)
    return [
        float(np.dot(centered[:-lag], centered[lag:]) / variance)
        for lag in range(1, min(max_lag, len(series) - 1) + 1)
    ]


def halves(values):
    middle = len(values) // 2
    return {"first_half_median": float(np.median(values[:middle])), "second_half_median": float(np.median(values[middle:]))}


# --------------------------------------------------------------------------------------------
# Commands


def analyze(path, arm_a, arm_b, delta, resamples, seed):
    arms = load_rounds(path)
    a, b = arms[arm_a], arms[arm_b]
    rounds = len(a)
    length = block_length(rounds)
    low, high = bootstrap_interval(a, b, length, resamples, seed)
    low2, high2 = bootstrap_interval(a, b, min(2 * length, rounds), resamples, seed)
    return {
        "method_version": METHOD_VERSION,
        "input": os.path.basename(path),
        "input_sha256": sha256_of(path),
        "arms": {"a": arm_a, "b": arm_b},
        "rounds": rounds,
        "delta_us": delta,
        "block_length": length,
        "resamples": resamples,
        "seed": seed,
        "estimate_us": estimate(a, b),
        "interval_us": [low, high],
        "decision": decide(low, high, delta),
        "sensitivity_block_length": min(2 * length, rounds),
        "sensitivity_interval_us": [low2, high2],
        "sensitivity_decision": decide(low2, high2, delta),
        "summaries": {arm: summary(values) for arm, values in arms.items()},
        "halves": {arm: halves(values) for arm, values in arms.items()},
        "autocorrelation": {
            **{arm: autocorrelation(values) for arm, values in arms.items()},
            f"{arm_a}-minus-{arm_b} per round": autocorrelation(a - b),
        },
    }


def plan(path, arm_a, arm_b, delta, candidates, resamples, seed):
    """Spec rule: smallest N whose pilot half-width scaled by sqrt(R/N) is <= delta/2. Planning only."""
    arms = load_rounds(path)
    a, b = arms[arm_a], arms[arm_b]
    rounds = len(a)
    low, high = bootstrap_interval(a, b, block_length(rounds), resamples, seed)
    half_width = (high - low) / 2.0
    predicted = {str(n): half_width * math.sqrt(rounds / n) for n in candidates}
    chosen = next((n for n in candidates if predicted[str(n)] <= delta / 2.0), None)
    return {
        "method_version": METHOD_VERSION,
        "input": os.path.basename(path),
        "input_sha256": sha256_of(path),
        "pilot_rounds": rounds,
        "pilot_interval_us": [low, high],
        "pilot_half_width_us": half_width,
        "predicted_half_width_us": predicted,
        "target_half_width_us": delta / 2.0,
        "chosen_rounds": chosen,
        "block_length_for_chosen": block_length(chosen) if chosen else None,
        "note": "planning heuristic; the final run's own interval decides",
    }


def fit(path, arm_b, arm_a):
    """Parameters for `simulate`, taken from a pilot. Approximations for planning, stated as such."""
    arms = load_rounds(path)
    b = arms[arm_b]
    positive = b[b > 0]
    logs = np.log(positive)
    round_means = (arms[arm_a] + b) / 2.0
    rho = autocorrelation(round_means, 1)[0] if len(round_means) > 2 else 0.0
    rho = max(0.0, min(rho, 0.99))
    return {
        "input_sha256": sha256_of(path),
        "mu": float(np.mean(logs)),
        "sigma": float(np.std(logs, ddof=1)),
        "rho": float(rho),
        "drift_sd": float(np.std(round_means, ddof=1) * math.sqrt(rho)),
        "note": "mu/sigma: lognormal fit of the B arm; rho: lag-1 autocorrelation of per-round means; "
        "drift_sd: the share of their spread that rho attributes to drift",
    }


def synthetic(rng, rounds, effect, mu, sigma, rho, drift_sd):
    """Two arms with identical marginal shape; A is shifted by `effect` exactly, by construction.
    A shared AR(1) drift per round mimics an environment both arms of a round experience."""
    drift = np.empty(rounds)
    drift[0] = rng.normal(0.0, drift_sd) if drift_sd > 0 else 0.0
    innovation_sd = drift_sd * math.sqrt(max(0.0, 1.0 - rho * rho))
    for r in range(1, rounds):
        drift[r] = rho * drift[r - 1] + (rng.normal(0.0, innovation_sd) if innovation_sd > 0 else 0.0)
    b = np.exp(mu + sigma * rng.standard_normal(rounds)) + drift
    a = np.exp(mu + sigma * rng.standard_normal(rounds)) + drift + effect
    return a, b


def simulate(rounds, delta, mu, sigma, rho, drift_sd, replicates, resamples, seed, multiples):
    rng = np.random.default_rng(seed)
    length = block_length(rounds)
    rows = []
    for multiple in multiples:
        counts = {"EQUIVALENT": 0, "INCONCLUSIVE": 0, "NOT EQUIVALENT": 0}
        for _ in range(replicates):
            a, b = synthetic(rng, rounds, multiple * delta, mu, sigma, rho, drift_sd)
            low, high = bootstrap_interval(a, b, length, resamples, int(rng.integers(0, 2**31 - 1)))
            counts[decide(low, high, delta)] += 1
        rates = {k: v / replicates for k, v in counts.items()}
        rows.append(
            {
                "effect_in_deltas": multiple,
                "effect_us": multiple * delta,
                "rates": rates,
                "monte_carlo_se": {k: math.sqrt(p * (1 - p) / replicates) for k, p in rates.items()},
            }
        )

    def rate(multiple, decision):
        return next((r["rates"][decision] for r in rows if r["effect_in_deltas"] == multiple), None)

    expectations = {
        "equivalent_at_0_at_least_0.90": rate(0.0, "EQUIVALENT"),
        "equivalent_at_delta_at_most_0.05": rate(1.0, "EQUIVALENT"),
        "not_equivalent_at_3_deltas_at_least_0.95": rate(3.0, "NOT EQUIVALENT"),
    }
    met = {
        "equivalent_at_0_at_least_0.90": expectations["equivalent_at_0_at_least_0.90"] is not None
        and expectations["equivalent_at_0_at_least_0.90"] >= 0.90,
        "equivalent_at_delta_at_most_0.05": expectations["equivalent_at_delta_at_most_0.05"] is not None
        and expectations["equivalent_at_delta_at_most_0.05"] <= 0.05,
        "not_equivalent_at_3_deltas_at_least_0.95": expectations["not_equivalent_at_3_deltas_at_least_0.95"]
        is not None
        and expectations["not_equivalent_at_3_deltas_at_least_0.95"] >= 0.95,
    }
    return {
        "method_version": METHOD_VERSION,
        "rounds": rounds,
        "delta_us": delta,
        "block_length": length,
        "parameters": {"mu": mu, "sigma": sigma, "rho": rho, "drift_sd": drift_sd},
        "replicates": replicates,
        "resamples": resamples,
        "seed": seed,
        "rows": rows,
        "expectations": expectations,
        "expectations_met": met,
        "limitation": "validates the analysis on synthetic data, not the measurement pipeline",
    }


def observer(path, arm_a, arm_b, qs, draws, seed):
    """Supplementary. H0: candidate and reference both from A. H1: candidate from B, reference from A.
    Score |median(candidate_Q) - median(reference_Q)|; threshold = 95th percentile under H0; the
    true-positive rate is measured directly under H1. One statistic, no claim it is the strongest."""
    arms = load_rounds(path)
    a, b = arms[arm_a], arms[arm_b]
    rng = np.random.default_rng(seed)

    def medians(source, q, n):
        out = np.empty(n)
        step = max(1, min(n, (2_000_000 // q) or 1))
        done = 0
        while done < n:
            size = min(step, n - done)
            out[done : done + size] = np.median(source[rng.integers(0, source.size, size=(size, q))], axis=1)
            done += size
        return out

    results = []
    for q in qs:
        h0 = np.abs(medians(a, q, draws) - medians(a, q, draws))
        threshold = float(np.percentile(h0, 95))
        h1 = np.abs(medians(b, q, draws) - medians(a, q, draws))
        results.append(
            {
                "q": q,
                "threshold_us": threshold,
                "false_positive_rate": float(np.mean(h0 > threshold)),
                "true_positive_rate": float(np.mean(h1 > threshold)),
            }
        )
    return {
        "method_version": METHOD_VERSION,
        "input": os.path.basename(path),
        "input_sha256": sha256_of(path),
        "arms": {"reference_and_h0_candidate": arm_a, "h1_candidate": arm_b},
        "draws": draws,
        "seed": seed,
        "results": results,
        "limits": "resamples of one host's loopback data; one statistic; concurrency, candidate selection "
        "and reference reuse are not modelled; describes the data, does not bound an attacker",
    }


# --------------------------------------------------------------------------------------------
# Self-test


def selftest():
    checks = []

    def check(name, condition):
        if not condition:
            raise AssertionError(f"selftest failed: {name}")
        checks.append(name)

    # The estimand is the difference of medians, not the median of paired differences.
    a = np.array([10.0, 110.0, 111.0])
    b = np.array([10.0, 11.0, 112.0])
    check("difference of medians is 99", estimate(a, b) == 99.0)
    check("median of paired differences differs (0)", float(np.median(a - b)) == 0.0)

    for low, high, delta, expected in [
        (-1.0, 1.0, 2.0, "EQUIVALENT"),
        (-2.0, 2.0, 2.0, "EQUIVALENT"),
        (-3.0, 1.0, 2.0, "INCONCLUSIVE"),
        (1.0, 3.0, 2.0, "INCONCLUSIVE"),
        (2.5, 4.0, 2.0, "NOT EQUIVALENT"),
        (-4.0, -2.5, 2.0, "NOT EQUIVALENT"),
    ]:
        check(f"decide({low}, {high}, {delta}) == {expected}", decide(low, high, delta) == expected)

    for rounds, expected in [(1, 1), (8, 2), (9, 3), (27, 3), (28, 4), (200, 6), (1000, 10), (1001, 11), (4000, 16)]:
        check(f"block_length({rounds}) == {expected}", block_length(rounds) == expected)

    rng = np.random.default_rng(7)
    indices = block_indices(rng, 10, 3, 50)
    check("block indices shape", indices.shape == (50, 10))
    check("block indices in range", int(indices.min()) >= 0 and int(indices.max()) <= 9)
    check("blocks are consecutive rounds", bool(np.all(np.diff(indices[:, 0:3], axis=1) == 1)))

    base = np.arange(100, dtype=float)
    low, high = bootstrap_interval(base + 7.0, base, 5, 400, 3)
    check("an exact shift gives a degenerate interval at the shift", low == 7.0 and high == 7.0)
    check("shift 7 inside bound 10 is EQUIVALENT", decide(low, high, 10.0) == "EQUIVALENT")
    check("shift 7 outside bound 5 is NOT EQUIVALENT", decide(low, high, 5.0) == "NOT EQUIVALENT")

    noisy_rng = np.random.default_rng(11)
    na, nb = noisy_rng.normal(100, 5, 300), noisy_rng.normal(100, 5, 300)
    check("same seed, same interval", bootstrap_interval(na, nb, 7, 300, 5) == bootstrap_interval(na, nb, 7, 300, 5))
    check("different seed, different interval", bootstrap_interval(na, nb, 7, 300, 5) != bootstrap_interval(na, nb, 7, 300, 6))

    with tempfile.TemporaryDirectory() as directory:
        good = os.path.join(directory, "good.csv")
        with open(good, "w", newline="", encoding="utf-8") as handle:
            handle.write("round,arm,position,elapsed_us\n0,x,0,1.0\n0,y,1,2.0\n1,y,0,3.0\n1,x,1,4.0\n")
        loaded = load_rounds(good)
        check("load orders samples by round", list(loaded["x"]) == [1.0, 4.0] and list(loaded["y"]) == [2.0, 3.0])

        marked = os.path.join(directory, "marked.csv")
        with open(marked, "w", newline="", encoding="utf-8-sig") as handle:
            handle.write("round,arm,position,elapsed_us\n0,x,0,1.0\n0,y,1,2.0\n")
        check("a byte order mark does not hide the first column", list(load_rounds(marked)["x"]) == [1.0])

        bad = os.path.join(directory, "bad.csv")
        with open(bad, "w", newline="", encoding="utf-8") as handle:
            handle.write("round,arm,position,elapsed_us\n0,x,0,1.0\n0,y,1,2.0\n1,x,0,3.0\n")
        try:
            load_rounds(bad)
            check("a round missing an arm is rejected", False)
        except ValueError:
            check("a round missing an arm is rejected", True)

    smoke = simulate(200, 10.0, math.log(1000.0), 0.1, 0.5, 5.0, 10, 100, 9, (0.0, 1.0, 3.0))
    check("simulate runs and returns rates", all(abs(sum(r["rates"].values()) - 1.0) < 1e-9 for r in smoke["rows"]))
    shifted = synthetic(np.random.default_rng(1), 100000, 25.0, math.log(1000.0), 0.0, 0.0, 0.0)
    check("synthetic effect is exact by construction", abs(estimate(*shifted) - 25.0) < 1e-6)

    return {"method_version": METHOD_VERSION, "checks_passed": len(checks), "checks": checks}


# --------------------------------------------------------------------------------------------
# CLI


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)

    commands.add_parser("selftest")

    p = commands.add_parser("analyze")
    p.add_argument("--csv", required=True)
    p.add_argument("--delta", type=float, required=True, help="bound in microseconds")
    p.add_argument("--arm-a", default="absent")
    p.add_argument("--arm-b", default="wrong_secret")
    p.add_argument("--resamples", type=int, default=DEFAULT_RESAMPLES)
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)

    p = commands.add_parser("plan")
    p.add_argument("--csv", required=True)
    p.add_argument("--delta", type=float, required=True)
    p.add_argument("--arm-a", default="absent")
    p.add_argument("--arm-b", default="wrong_secret")
    p.add_argument("--candidates", type=int, nargs="+", default=[500, 1000, 2000, 4000])
    p.add_argument("--resamples", type=int, default=DEFAULT_RESAMPLES)
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)

    p = commands.add_parser("fit")
    p.add_argument("--csv", required=True)
    p.add_argument("--arm-a", default="absent")
    p.add_argument("--arm-b", default="wrong_secret")

    p = commands.add_parser("simulate")
    p.add_argument("--rounds", type=int, required=True)
    p.add_argument("--delta", type=float, required=True)
    p.add_argument("--mu", type=float, required=True)
    p.add_argument("--sigma", type=float, required=True)
    p.add_argument("--rho", type=float, default=0.0)
    p.add_argument("--drift-sd", type=float, default=0.0)
    p.add_argument("--replicates", type=int, default=500)
    p.add_argument("--resamples", type=int, default=DEFAULT_RESAMPLES)
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)
    p.add_argument("--multiples", type=float, nargs="+", default=[0.0, 0.5, 1.0, 2.0, 3.0])

    p = commands.add_parser("observer")
    p.add_argument("--csv", required=True)
    p.add_argument("--arm-a", default="absent")
    p.add_argument("--arm-b", default="wrong_secret")
    p.add_argument("--q", type=int, nargs="+", default=[10, 100, 1000, 10000])
    p.add_argument("--draws", type=int, default=2000)
    p.add_argument("--seed", type=int, default=DEFAULT_SEED)

    args = parser.parse_args(argv)
    if args.command == "selftest":
        result = selftest()
    elif args.command == "analyze":
        result = analyze(args.csv, args.arm_a, args.arm_b, args.delta, args.resamples, args.seed)
    elif args.command == "plan":
        result = plan(args.csv, args.arm_a, args.arm_b, args.delta, args.candidates, args.resamples, args.seed)
    elif args.command == "fit":
        result = fit(args.csv, args.arm_b, args.arm_a)
    elif args.command == "simulate":
        result = simulate(
            args.rounds,
            args.delta,
            args.mu,
            args.sigma,
            args.rho,
            args.drift_sd,
            args.replicates,
            args.resamples,
            args.seed,
            args.multiples,
        )
    else:
        result = observer(args.csv, args.arm_a, args.arm_b, args.q, args.draws, args.seed)

    json.dump(result, sys.stdout, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
