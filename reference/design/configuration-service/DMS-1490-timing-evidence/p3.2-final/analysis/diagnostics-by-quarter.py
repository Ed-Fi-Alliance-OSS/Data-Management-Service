"""P3.2 diagnostic (reported beside the pre-registered analysis; it decides nothing): per-quarter
medians per arm and the absent - wrong_secret difference of medians per quarter and half."""
import json, sys
import numpy as np
sys.path.insert(0, sys.argv[1])
import analyze_timing as t
out = {"purpose": "drift shape of each final/contrast run; quarters of consecutive rounds", "rows": []}
for csv in sys.argv[2:]:
    arms = t.load_rounds(csv)
    a, b = np.asarray(arms["absent"]), np.asarray(arms["wrong_secret"])
    out["rows"].append({
        "input": csv, "input_sha256": t.sha256_of(csv), "rounds": len(b),
        "arm_quarter_medians_us": {k: [round(float(np.median(q)), 1) for q in np.array_split(np.asarray(v), 4)] for k, v in arms.items()},
        "absent_minus_wrong_secret_by_quarter_us": [round(float(np.median(x) - np.median(y)), 1) for x, y in zip(np.array_split(a, 4), np.array_split(b, 4))],
        "absent_minus_wrong_secret_by_half_us": [round(float(np.median(x) - np.median(y)), 1) for x, y in zip(np.array_split(a, 2), np.array_split(b, 2))],
    })
print(json.dumps(out, indent=2, allow_nan=False))
