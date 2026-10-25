#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

"""Offline warning diagnostics for one endpoint timing run (spec section 18.3). Reads samples; never measures.

Input is the measured samples CSV an endpoint collector writes. The analysis needs the columns
round, arm and elapsed_us, with exactly the three endpoint arms (absent, unapproved_correct_secret,
wrong_secret) once per round, rounds numbered 0..N-1, and N >= 20; anything else is refused. Other
columns (position, and in timing-evidence/2 round_started_qpc and round_started_utc) are permitted
and ignored. The warm-up file is never an input.

Warning flags. The constants are prospective and conservative rejection flags, not validated
stationarity or coverage criteria, and they were chosen after the historical runs were seen:

    D1 level        max / min of the 10 consecutive decile medians of the round level > 1.10
    D2 dependence   |autocorrelation of the round level at lag 2L| > 0.20, with L = ceil(N^(1/3))
    D-undefined     a flag cannot be computed: zero variance, a zero denominator, or no pairs

The round level is the median of a round's three samples. Autocorrelation is the committed
centred dot-product convention, r_k = sum (x_t - m)(x_{t+k} - m) / sum (x_t - m)^2. A value equal
to a limit does not raise its flag.

Any flag stops the stage for review. No flag means only that no flag was raised: it is not a
validity, admissibility or AC2 finding, and it says nothing of the power source or sampler
coverage (R3), which come from the lane's own records. Stated limits:

    - arm-specific changes can be invisible: when two arms share base[r], the round level is
      base[r] whatever the third arm does;
    - decile medians can hide within-decile transitions;
    - one lag cannot show that all dependence decays within L.

Mandatory diagnostics, reported for every accepted run whatever its flags: per-arm decile, quarter
and half medians; absent - wrong_secret and unapproved_correct_secret - wrong_secret by decile,
quarter and half; per-arm and per-round-difference autocorrelation at lags 1 to max(20, 2L),
bounded by N - 1, with a zero-variance series reported as undefined, never as 0; the round level
by decile; and the L and 2L intervals and decisions from analyze_timing.py (unchanged, method 1).

Commands (numpy is the only dependency):

    selftest                          deterministic checks of this script; exits nonzero on failure
    diagnose --csv F --delta D        flags and mandatory diagnostics for one run

Exit codes: 0 accepted, no flag; 3 accepted, a flag raised (stop for review); 2 refused input, with
no flags. Output is JSON without NaN or Infinity, and names the input's SHA-256.
"""

import argparse
import contextlib
import csv
import io
import json
import math
import os
import random
import sys
import tempfile

import numpy as np

# The numerical method is the committed analyze_timing.py beside this script, also under python -I.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_timing  # noqa: E402

DIAGNOSTICS_VERSION = "1"
ENDPOINT_ARMS = ("absent", "unapproved_correct_secret", "wrong_secret")
REFERENCE_ARM = "wrong_secret"
COMPARED_ARMS = ("absent", "unapproved_correct_secret")
REQUIRED_COLUMNS = ("round", "arm", "elapsed_us")
MIN_ROUNDS = analyze_timing.MIN_INTERVAL_ROUNDS
D1_LIMIT = 1.10
D2_LIMIT = 0.20
MIN_REPORTED_LAGS = 20
SEGMENTS = {"deciles": 10, "quarters": 4, "halves": 2}
STATEMENT = (
    "Warning flags only. No flag is not a validity, admissibility or AC2 finding; power and sampler "
    "coverage (R3) are judged from the lane's records, and every final still needs the independent "
    "validity review."
)
LIMITS = [
    "arm-specific changes can be invisible: when two arms share base[r], the round level is base[r]",
    "decile medians can hide within-decile transitions",
    "one lag cannot show that all dependence decays within L",
    "D1 and D2 constants were chosen after the historical runs were seen",
]


# --------------------------------------------------------------------------------------------
# Input


def load_endpoint_rounds(path):
    """The three endpoint arms, by round, from a complete and valid samples file; otherwise ValueError."""
    with open(path, newline="", encoding="utf-8-sig") as handle:
        header = next(csv.reader(handle), None)
    if header is None:
        raise ValueError("the input is empty")
    missing = [column for column in REQUIRED_COLUMNS if column not in header]
    if missing:
        raise ValueError(f"the input lacks the column(s) {missing}")
    try:
        arms = analyze_timing.load_rounds(path)
    except (KeyError, TypeError) as error:
        raise ValueError(f"the input has a malformed row ({type(error).__name__})") from error
    if tuple(sorted(arms)) != ENDPOINT_ARMS:
        raise ValueError(f"the input has arms {sorted(arms)}, not exactly {list(ENDPOINT_ARMS)}")
    rounds = len(arms[REFERENCE_ARM])
    if rounds < MIN_ROUNDS:
        raise ValueError(f"{rounds} rounds is too few; at least {MIN_ROUNDS} are required")
    return arms


# --------------------------------------------------------------------------------------------
# Building blocks


def exceeds(value, limit):
    """A flag's comparison: strictly above the limit. A value equal to the limit does not raise it."""
    return value > limit


def round_level(arms):
    """The median of each round's three samples."""
    return np.median(np.vstack([arms[arm] for arm in ENDPOINT_ARMS]), axis=0)


def segment_medians(values, parts):
    return [float(np.median(segment)) for segment in np.array_split(values, parts)]


def segment_differences(a, b, parts):
    """The estimand within each consecutive segment: median(A) - median(B)."""
    return [float(np.median(x) - np.median(y)) for x, y in zip(np.array_split(a, parts), np.array_split(b, parts))]


def autocorrelations(series, max_lag):
    """Lags 1..min(max_lag, N - 1) by the centred dot-product convention; undefined for zero variance."""
    lags = min(max_lag, len(series) - 1)
    centered = series - np.mean(series)
    variance = float(np.dot(centered, centered))
    if variance == 0.0:
        return {"lags": lags, "values": None, "undefined": "zero_variance"}
    values = [float(np.dot(centered[:-lag], centered[lag:]) / variance) for lag in range(1, lags + 1)]
    return {"lags": lags, "values": values, "undefined": None}


def flags(levels):
    """D1, D2 and D-undefined for a round-level series, with the values and limits behind them."""
    rounds = len(levels)
    length = analyze_timing.block_length(rounds)
    lag = 2 * length
    deciles = segment_medians(levels, 10)
    reasons = []

    d1 = {"decile_medians": deciles, "ratio": None, "limit": D1_LIMIT, "raised": None, "undefined": None}
    if min(deciles) == 0.0:
        d1["undefined"] = "zero_denominator"
        reasons.append("D1: zero_denominator")
    else:
        d1["ratio"] = max(deciles) / min(deciles)
        d1["raised"] = exceeds(d1["ratio"], D1_LIMIT)

    d2 = {"lag": lag, "block_length": length, "autocorrelation": None, "absolute": None, "limit": D2_LIMIT, "raised": None, "undefined": None}
    centered = levels - np.mean(levels)
    variance = float(np.dot(centered, centered))
    if lag > rounds - 1:
        d2["undefined"] = "no_pairs_at_lag"
    elif variance == 0.0:
        d2["undefined"] = "zero_variance"
    else:
        d2["autocorrelation"] = float(np.dot(centered[:-lag], centered[lag:]) / variance)
        d2["absolute"] = abs(d2["autocorrelation"])
        d2["raised"] = exceeds(d2["absolute"], D2_LIMIT)
    if d2["undefined"] is not None:
        reasons.append(f"D2: {d2['undefined']}")

    undefined = {"raised": bool(reasons), "reasons": reasons}
    return {
        "D1": d1,
        "D2": d2,
        "D_undefined": undefined,
        "stop_for_review": bool(d1["raised"]) or bool(d2["raised"]) or undefined["raised"],
    }


# --------------------------------------------------------------------------------------------
# Command


def diagnose(path, delta, resamples, seed):
    arms = load_endpoint_rounds(path)
    rounds = len(arms[REFERENCE_ARM])
    length = analyze_timing.block_length(rounds)
    max_lag = max(MIN_REPORTED_LAGS, 2 * length)
    levels = round_level(arms)
    reference = arms[REFERENCE_ARM]

    mandatory = {
        "arm_medians": {arm: {name: segment_medians(arms[arm], parts) for name, parts in SEGMENTS.items()} for arm in ENDPOINT_ARMS},
        "differences": {
            f"{arm}-minus-{REFERENCE_ARM}": {name: segment_differences(arms[arm], reference, parts) for name, parts in SEGMENTS.items()}
            for arm in COMPARED_ARMS
        },
        "autocorrelation": {
            **{arm: autocorrelations(arms[arm], max_lag) for arm in ENDPOINT_ARMS},
            **{f"{arm}-minus-{REFERENCE_ARM} per round": autocorrelations(arms[arm] - reference, max_lag) for arm in COMPARED_ARMS},
        },
        "round_level_decile_medians": segment_medians(levels, 10),
    }

    # The intervals come from the unchanged numerical method, not from a second implementation.
    intervals = {}
    for arm in COMPARED_ARMS:
        result = analyze_timing.analyze(path, arm, REFERENCE_ARM, delta, resamples, seed)
        intervals[f"{arm}-minus-{REFERENCE_ARM}"] = {
            key: result[key]
            for key in (
                "estimate_us",
                "block_length",
                "interval_us",
                "decision",
                "sensitivity_block_length",
                "sensitivity_interval_us",
                "sensitivity_decision",
            )
        }

    return {
        "diagnostics_version": DIAGNOSTICS_VERSION,
        "method_version": analyze_timing.METHOD_VERSION,
        "input": os.path.basename(path),
        "input_sha256": analyze_timing.sha256_of(path),
        "rounds": rounds,
        "block_length": length,
        "reported_lags": min(max_lag, rounds - 1),
        "delta_us": float(delta),
        "resamples": resamples,
        "seed": seed,
        "flags": flags(levels),
        "statement": STATEMENT,
        "limits": LIMITS,
        "mandatory": mandatory,
        "intervals": intervals,
    }


# --------------------------------------------------------------------------------------------
# Self test


def raises(action):
    try:
        action()
    except ValueError:
        return True
    return False


def refusal(action):
    """The message of the ValueError an action raises, or None."""
    try:
        action()
    except ValueError as error:
        return str(error)
    return None


def write_samples(path, arms):
    """A samples file in the collectors' format from {arm: per-round samples}."""
    names = sorted(arms)
    with open(path, "w", newline="", encoding="utf-8") as handle:
        handle.write("round,arm,position,elapsed_us\n")
        for round_index in range(len(arms[names[0]])):
            for position, arm in enumerate(names):
                handle.write(f"{round_index},{arm},{position},{float(arms[arm][round_index])!r}\n")


def blind_spot_arms():
    """The #X26 example (synthetic): 500 bases from random.Random(32).uniform(-20, 20) + 20000;
    wrong_secret and unapproved share the base, absent is 600 us below it for the first 250 rounds
    and 600 us above it for the rest."""
    generator = random.Random(32)
    base = np.array([generator.uniform(-20, 20) + 20000 for _ in range(500)])
    absent = base + np.where(np.arange(500) < 250, -600.0, 600.0)
    return {"absent": absent, "unapproved_correct_secret": base.copy(), "wrong_secret": base.copy()}


def run_main(argv):
    """main() with its output captured: (exit code, parsed JSON or None)."""
    out = io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
        code = main(argv)
    text = out.getvalue()
    return code, (json.loads(text) if text.strip() else None)


def selftest():
    checks = []

    def check(name, condition):
        if not condition:
            raise AssertionError(f"selftest failed: {name}")
        checks.append(name)

    here = os.path.dirname(os.path.abspath(__file__))
    resamples = 200

    # Comparison boundaries: equal to a limit does not raise.
    check("D1 boundary: 1.10 does not exceed 1.10", not exceeds(1.10, D1_LIMIT))
    check("D1 boundary: just above 1.10 exceeds it", exceeds(math.nextafter(1.10, 2.0), D1_LIMIT))
    check("D2 boundary: 0.20 does not exceed 0.20", not exceeds(0.20, D2_LIMIT))
    check("D2 boundary: just above 0.20 exceeds it", exceeds(math.nextafter(0.20, 1.0), D2_LIMIT))
    exact = np.array([100.0] * 18 + [110.0] * 2)
    check("a decile ratio of exactly 1.10 is computed and does not raise D1", flags(exact)["D1"]["ratio"] == 1.10 and flags(exact)["D1"]["raised"] is False)
    above = np.array([100.0] * 18 + [111.0] * 2)
    check("a decile ratio of 1.11 raises D1", flags(above)["D1"]["raised"] is True)

    # Lag range and convention.
    rng = np.random.default_rng(5)
    for rounds, expected_lags in ((20, 19), (200, 20), (500, 20), (4000, 32)):
        series = rng.normal(0.0, 1.0, rounds)
        got = autocorrelations(series, max(MIN_REPORTED_LAGS, 2 * analyze_timing.block_length(rounds)))
        check(f"{rounds} rounds report {expected_lags} lags", got["lags"] == expected_lags and len(got["values"]) == expected_lags)
    series = rng.normal(0.0, 1.0, 300)
    check(
        "the autocorrelation convention is analyze_timing's for a non-constant series",
        autocorrelations(series, 20)["values"] == analyze_timing.autocorrelation(series, 20),
    )
    check("a constant series is undefined, not zero", autocorrelations(np.full(30, 7.0), 20) == {"lags": 20, "values": None, "undefined": "zero_variance"})

    with tempfile.TemporaryDirectory() as directory:
        # Synthetic: the #X26 blind spot passes D1/D2 while its half differences show the shift.
        blind = os.path.join(directory, "synthetic-blind-spot.csv")
        write_samples(blind, blind_spot_arms())
        result = diagnose(blind, 250.0, resamples, 1)
        d1, d2 = result["flags"]["D1"], result["flags"]["D2"]
        check("synthetic blind spot: D1 is 1.000542 and not raised", round(d1["ratio"], 6) == 1.000542 and d1["raised"] is False)
        check("synthetic blind spot: |D2| at lag 16 is 0.038999 and not raised", d2["lag"] == 16 and round(d2["absolute"], 6) == 0.038999 and d2["raised"] is False)
        check("synthetic blind spot: no flag, no stop", result["flags"]["stop_for_review"] is False and result["flags"]["D_undefined"]["raised"] is False)
        halves = result["mandatory"]["differences"]["absent-minus-wrong_secret"]["halves"]
        check("synthetic blind spot: the half differences are -600 and +600 us", abs(halves[0] + 600.0) < 1e-6 and abs(halves[1] - 600.0) < 1e-6)
        check(
            "synthetic blind spot: 500 rounds report 20 lags (the minimum of 20 over 2L = 16)",
            result["reported_lags"] == 20 and len(result["mandatory"]["autocorrelation"]["absent"]["values"]) == 20,
        )
        check(
            "synthetic blind spot: unapproved - wrong_secret is 0 in both halves",
            result["mandatory"]["differences"]["unapproved_correct_secret-minus-wrong_secret"]["halves"] == [0.0, 0.0],
        )

        # Synthetic: the intervals are analyze_timing's own.
        for arm in COMPARED_ARMS:
            reference = analyze_timing.analyze(blind, arm, REFERENCE_ARM, 250.0, resamples, 1)
            got = result["intervals"][f"{arm}-minus-{REFERENCE_ARM}"]
            check(
                f"synthetic: the {arm} intervals are analyze_timing's",
                got["interval_us"] == reference["interval_us"] and got["sensitivity_interval_us"] == reference["sensitivity_interval_us"] and got["decision"] == reference["decision"],
            )

        # Synthetic: a constant run is undefined, stops for review, and is valid JSON through main.
        constant = os.path.join(directory, "synthetic-constant.csv")
        write_samples(constant, {arm: np.full(40, 1000.0) for arm in ENDPOINT_ARMS})
        code, output = run_main(["diagnose", "--csv", constant, "--delta", "250", "--resamples", str(resamples)])
        check("synthetic constant: exit 3 with JSON", code == 3 and output is not None)
        check("synthetic constant: D1 is 1.0 and not raised", output["flags"]["D1"]["ratio"] == 1.0 and output["flags"]["D1"]["raised"] is False)
        check("synthetic constant: D2 is undefined (zero variance)", output["flags"]["D2"]["undefined"] == "zero_variance" and output["flags"]["D2"]["raised"] is None)
        check("synthetic constant: D-undefined is raised", output["flags"]["D_undefined"] == {"raised": True, "reasons": ["D2: zero_variance"]})
        check("synthetic constant: arm autocorrelation is undefined, not zeros", output["mandatory"]["autocorrelation"]["absent"]["values"] is None)

        # Synthetic: a square wave of half-period 2L (200 rounds, L = 6) has strongly negative
        # dependence at lag 2L; D2 compares the absolute value, so it raises.
        wave = np.where((np.arange(200) // 12) % 2 == 0, 1010.0, 990.0)
        negative = os.path.join(directory, "synthetic-negative-dependence.csv")
        write_samples(negative, {arm: wave.copy() for arm in ENDPOINT_ARMS})
        negative_flags = diagnose(negative, 250.0, resamples, 1)["flags"]
        check(
            "synthetic negative dependence: autocorrelation at lag 12 is below -0.20 and raises D2",
            negative_flags["D2"]["lag"] == 12 and negative_flags["D2"]["autocorrelation"] < -D2_LIMIT and negative_flags["D2"]["raised"] is True,
        )
        check("synthetic negative dependence: D1 is not raised", negative_flags["D1"]["raised"] is False)

        zero = os.path.join(directory, "synthetic-zero.csv")
        write_samples(zero, {arm: np.zeros(40) for arm in ENDPOINT_ARMS})
        check("synthetic zero level: D1 is undefined (zero denominator)", diagnose(zero, 250.0, resamples, 1)["flags"]["D1"]["undefined"] == "zero_denominator")

        # Synthetic: refused inputs give exit 2 and no flags.
        twenty = {arm: np.arange(20, dtype=float) + 100.0 + offset for offset, arm in enumerate(ENDPOINT_ARMS)}
        refused = {
            "a missing elapsed_us column": "round,arm,position\n0,absent,0\n",
            "a header only": "round,arm,position,elapsed_us\n",
            "an empty file": "",
            "a short row": "round,arm,position,elapsed_us\n0,absent,0\n",
            "a non-integer round": "round,arm,position,elapsed_us\nx,absent,0,1.0\n",
        }
        missing_column = os.path.join(directory, "synthetic-missing-column.csv")
        with open(missing_column, "w", newline="", encoding="utf-8") as handle:
            handle.write("round,arm,position\n0,absent,0\n")
        message = refusal(lambda: diagnose(missing_column, 250.0, resamples, 1))
        check("synthetic: a missing column is refused by name", message is not None and "lacks the column(s) ['elapsed_us']" in message)
        for label, text in refused.items():
            bad = os.path.join(directory, "refused.csv")
            with open(bad, "w", newline="", encoding="utf-8") as handle:
                handle.write(text)
            code, output = run_main(["diagnose", "--csv", bad, "--delta", "250"])
            check(f"synthetic: {label} is refused with exit 2 and no output", code == 2 and output is None)

        def variant(label, change):
            arms = {arm: values.copy() for arm, values in twenty.items()}
            change(arms)
            path = os.path.join(directory, f"synthetic-{label}.csv")
            write_samples(path, arms)
            return path

        for label, change in (
            ("nan", lambda a: a["absent"].__setitem__(3, float("nan"))),
            ("infinite", lambda a: a["absent"].__setitem__(3, float("inf"))),
            ("negative", lambda a: a["absent"].__setitem__(3, -1.0)),
            ("19-rounds", lambda a: [a.__setitem__(arm, a[arm][:19]) for arm in list(a)]),
            ("extra-arm", lambda a: a.__setitem__("other", a["absent"].copy())),
            ("missing-arm", lambda a: a.pop("unapproved_correct_secret")),
        ):
            path = variant(label, change)
            check(f"synthetic: {label} input is refused", raises(lambda p=path: diagnose(p, 250.0, resamples, 1)))

        good = variant("valid", lambda a: None)
        with open(good, encoding="utf-8") as handle:
            lines = handle.read().splitlines()
        for label, edited in (
            ("a duplicate sample", lines + [lines[1]]),
            ("a round missing an arm", lines[:-1]),
            ("a gap in the rounds", [lines[0]] + [line for line in lines[1:] if not line.startswith("5,")]),
        ):
            path = os.path.join(directory, "edited.csv")
            with open(path, "w", newline="", encoding="utf-8") as handle:
                handle.write("\n".join(edited) + "\n")
            check(f"synthetic: {label} is refused", raises(lambda p=path: diagnose(p, 250.0, resamples, 1)))

        # Synthetic: the timing-evidence/2 columns are ignored; the same samples give the same flags
        # and diagnostics.
        with open(good, encoding="utf-8") as handle:
            plain = handle.read().splitlines()
        wide = os.path.join(directory, "synthetic-timing-evidence-2.csv")
        with open(wide, "w", newline="", encoding="utf-8") as handle:
            handle.write(plain[0] + ",round_started_qpc,round_started_utc\n")
            for line in plain[1:]:
                handle.write(f"{line},{1000 + int(line.split(',')[0])},2026-10-24T12:00:00.0000000Z\n")
        narrow_result, wide_result = diagnose(good, 250.0, resamples, 1), diagnose(wide, 250.0, resamples, 1)
        check(
            "synthetic: a timing-evidence/2 file's extra columns change nothing",
            narrow_result["flags"] == wide_result["flags"] and narrow_result["mandatory"] == wide_result["mandatory"] and narrow_result["intervals"] == wide_result["intervals"],
        )

        accepted = diagnose(good, 250.0, resamples, 1)
        check("no output key claims validity or admissibility", not any(key in json.dumps(accepted) for key in ('"valid"', '"admissible"', '"accepted"')))
        check("the statement says no flag is not a validity finding", "not a validity" in accepted["statement"])

    # Observed (read only): the preserved A5 finals.
    sql = os.path.join(here, "a5-final", "final-a5-endpoint-mssql-20261024T163740Z.csv")
    postgres = os.path.join(here, "a5-final", "final-a5-endpoint-postgresql-20261024T162905Z.csv")
    sql_flags = diagnose(sql, 250.0, resamples, 1)["flags"]
    check("observed SQL Server A5 final: D1 3.019 raised", abs(sql_flags["D1"]["ratio"] - 3.0186) < 5e-4 and sql_flags["D1"]["raised"] is True)
    check("observed SQL Server A5 final: |D2| 0.833 raised", abs(sql_flags["D2"]["absolute"] - 0.8335) < 5e-4 and sql_flags["D2"]["raised"] is True)
    postgres_flags = diagnose(postgres, 250.0, resamples, 1)["flags"]
    check("observed PostgreSQL A5 final: D1 1.025 not raised", abs(postgres_flags["D1"]["ratio"] - 1.0253) < 5e-4 and postgres_flags["D1"]["raised"] is False)
    check("observed PostgreSQL A5 final: |D2| 0.016 not raised", abs(postgres_flags["D2"]["absolute"] - 0.0159) < 5e-4 and postgres_flags["D2"]["raised"] is False)
    check("observed PostgreSQL A5 final: no stop", postgres_flags["stop_for_review"] is False)

    return {"diagnostics_version": DIAGNOSTICS_VERSION, "checks_passed": len(checks), "checks": checks}


# --------------------------------------------------------------------------------------------
# CLI


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("selftest")
    p = commands.add_parser("diagnose")
    p.add_argument("--csv", required=True)
    p.add_argument("--delta", type=float, required=True, help="bound in microseconds, passed to analyze_timing")
    p.add_argument("--resamples", type=int, default=analyze_timing.DEFAULT_RESAMPLES)
    p.add_argument("--seed", type=int, default=analyze_timing.DEFAULT_SEED)
    args = parser.parse_args(argv)

    try:
        result = selftest() if args.command == "selftest" else diagnose(args.csv, args.delta, args.resamples, args.seed)
        text = json.dumps(result, indent=2, allow_nan=False)
    except ValueError as error:
        sys.stderr.write(f"error: {error}\n")
        return 2

    sys.stdout.write(text + "\n")
    if args.command == "diagnose" and result["flags"]["stop_for_review"]:
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
