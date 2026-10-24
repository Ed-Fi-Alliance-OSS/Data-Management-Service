"""Exploratory P3.0 diagnostic: does the committed C2 generator reproduce each pilot's own interval width?
Uses analyze_timing's functions unchanged; not part of the pre-registered method."""
import json, sys, time
import numpy as np
sys.path.insert(0, sys.argv[1])
import analyze_timing as t

evidence = sys.argv[2]
cases = [
    ("in-process", "pilot-in-process-20261023T232755Z.csv", 25.0),
    ("endpoint-postgresql", "pilot-endpoint-postgresql-20261023T233152Z.csv", 250.0),
    ("endpoint-mssql", "pilot-endpoint-mssql-20261023T233351Z.csv", 250.0),
]
replicates, resamples, seed = 200, 5000, 7
out = {"purpose": "exploratory calibration of the C2 generator at the pilot size; not pre-registered",
       "replicates": replicates, "resamples": resamples, "seed": seed, "rows": []}
for name, csv, delta in cases:
    started = time.time()
    arms = t.load_rounds(f"{evidence}/{csv}")
    a, b = t.comparison(arms, "absent", "wrong_secret")
    fit = json.load(open(f"{evidence}/analysis/{name}-fit.json"))
    analysis = json.load(open(f"{evidence}/analysis/{name}-analyze-absent-vs-wrong_secret.json"))
    low, high = analysis["interval_us"]
    rounds = len(b)
    rng = np.random.default_rng(seed)
    widths, core = [], []
    for _ in range(replicates):
        sa, sb = t.synthetic(rng, rounds, 0.0, fit["mu"], fit["sigma"], fit["rho"], fit["drift_sd"])
        lo, hi = t.bootstrap_interval(sa, sb, t.block_length(rounds), resamples, int(rng.integers(0, 2**31 - 1)))
        widths.append((hi - lo) / 2.0)
        core.append(float(np.percentile(sb, 75) - np.percentile(sb, 25)))
    out["rows"].append({
        "boundary": name,
        "rounds": rounds,
        "observed_half_width_us": (high - low) / 2.0,
        "model_half_width_us_median": float(np.median(widths)),
        "model_half_width_us_p10_p90": [float(np.percentile(widths, 10)), float(np.percentile(widths, 90))],
        "observed_b_iqr_us": float(np.percentile(b, 75) - np.percentile(b, 25)),
        "model_b_iqr_us_median": float(np.median(core)),
        "observed_b_sd_us": float(np.std(b, ddof=1)),
        "runtime_s": round(time.time() - started, 1),
    })
print(json.dumps(out, indent=2, allow_nan=False))
