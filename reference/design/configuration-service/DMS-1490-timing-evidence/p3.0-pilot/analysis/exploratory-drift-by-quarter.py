"""Exploratory P3.0 diagnostic: per-quarter medians per arm and of the A-B location effect."""
import json, sys
import numpy as np
sys.path.insert(0, sys.argv[1])
import analyze_timing as t
evidence = sys.argv[2]
cases = [("in-process", "pilot-in-process-20261023T232755Z.csv"),
         ("endpoint-postgresql", "pilot-endpoint-postgresql-20261023T233152Z.csv"),
         ("endpoint-mssql", "pilot-endpoint-mssql-20261023T233351Z.csv")]
out = {"purpose": "exploratory drift shape; quarters of 50 consecutive rounds; not pre-registered", "rows": []}
for name, csv in cases:
    arms = t.load_rounds(f"{evidence}/{csv}")
    row = {"boundary": name, "arm_quarter_medians_us": {}, "absent_minus_wrong_secret_by_quarter_us": []}
    for arm in ("absent", "wrong_secret", "unapproved_correct_secret"):
        if arm in arms:
            v = np.asarray(arms[arm])
            row["arm_quarter_medians_us"][arm] = [round(float(np.median(q)), 1) for q in np.array_split(v, 4)]
    a, b = np.asarray(arms["absent"]), np.asarray(arms["wrong_secret"])
    row["absent_minus_wrong_secret_by_quarter_us"] = [round(float(np.median(x) - np.median(y)), 1)
                                                      for x, y in zip(np.array_split(a, 4), np.array_split(b, 4))]
    row["absent_minus_wrong_secret_by_half_us"] = [round(float(np.median(x) - np.median(y)), 1)
                                                   for x, y in zip(np.array_split(a, 2), np.array_split(b, 2))]
    out["rows"].append(row)
print(json.dumps(out, indent=2, allow_nan=False))
