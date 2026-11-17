"""DMS-1521 task 8: SQL Server keyset form screening. Usage: python -I t8_analyze.py [run-dir]"""
import glob, json, os, statistics as st, sys
from collections import defaultdict

ROOT = r"C:\perf\dms-1521"
run = sys.argv[1] if len(sys.argv) > 1 else sorted(glob.glob(os.path.join(ROOT, "t8-mssql-forms-*")))[-1]
FORMS = ["baseline", "inline", "tablevar", "values", "tvp"]


def load(name):
    with open(os.path.join(run, name), encoding="utf-8") as f:
        return json.load(f)


def agg(vals):
    vals = [v for v in vals if v is not None]
    mean = st.mean(vals)
    sd = st.stdev(vals) if len(vals) > 1 else 0.0
    return mean, sd, (sd / mean if mean else 0.0)


out = [f"# Task 8 screening: {os.path.basename(run)}\n"]
manifest = load("run-manifest.json")
FORMS = manifest.get("forms") or FORMS
out.append(f"forms {FORMS}; memory-optimized provisioning {manifest.get('memoryOptimizedProvisioningSeconds')} s, XTP clerk KB {manifest.get('xtpMemoryClerkKbAfterReplays')}")
out.append(f"source {manifest.get('formsSource')}, rounds {manifest.get('formsRounds')}, load {manifest.get('fixtureLoadSeconds', 0):.0f} s, commit {manifest.get('subjectCommit')}\n")

out.append("## Forms (one execution each)\n")
out.append("| Batch | Rows | Form | Phases | SQL length | Parameters | First execution ms (plan cache cleared) | Same result sets as baseline |")
out.append("|---|---|---|---|---|---|---|---|")
for x in load("forms.json"):
    out.append(f"| {x['kind'].split('.')[0]} | {x['pageSize']} | {x['form']} | {x['phases']} | {x['sqlLength']:,} | {x['parameterCount']} | {x['firstExecutionMs']:.1f} | {x['sameResultSetsAsBaseline']} |")

rep = defaultdict(list)
cnt = {}
errors = 0
for r in load("replays.json"):
    kind = r["kind"].split(".")[0]
    key = (kind, r["pageSize"], r["form"], r["replay"]["concurrency"])
    rep[key].append(r["replay"])
    errors += r["replay"]["errors"] + r["replay"]["warmupErrors"]
    if r.get("counters"):
        n = r["counters"]["netPerRequest"]
        cnt[key] = dict(
            xe=n.get("xe.temp_tables_created", 0.0),
            perf=n.get("perf.Temp Tables Creation Rate", 0.0),
            latch=sum(v for k, v in n.items() if k.startswith("wait.PAGELATCH") and k.endswith(".tasks")),
            latch_ms=sum(v for k, v in n.items() if k.startswith("wait.PAGELATCH") and k.endswith(".ms")),
            recompiles=n.get("recompile.total", 0.0),
            reads=n.get("querystats.logical_reads", 0.0),
        )

out.append(f"\nReplay errors (all rounds, warmup and measured): {errors}\n")
out.append("## Replays: mean of the per-round statistic (3 rounds), ratio to baseline in the same run\n")
out.append("`m = max(2 x CV(baseline across rounds), 5%)`. Gate (SQL Server, task 8): p50 and p95 at c=1 <= 1+m, p95 at c=32 <= 1+m, throughput at c=32 >= 1-m.\n")
out.append("| Batch | Rows | Form | c | p50 ms | p95 ms | tput/s | p50 ratio | p95 ratio | tput ratio | m (p50/p95/tput) |")
out.append("|---|---|---|---|---|---|---|---|---|---|---|")
verdict = defaultdict(list)
for kind in ("resource", "fallback"):
    for size in (25, 500):
        for c in (1, 8, 32):
            b = rep[(kind, size, "baseline", c)]
            bp50, bp95, bt = (agg([x[k] for x in b]) for k in ("p50Ms", "p95Ms", "throughputPerSecond"))
            m = [max(2 * s[2], 0.05) for s in (bp50, bp95, bt)]
            for form in FORMS:
                f = rep[(kind, size, form, c)]
                p50, p95, t = (agg([x[k] for x in f]) for k in ("p50Ms", "p95Ms", "throughputPerSecond"))
                r50, r95, rt = p50[0] / bp50[0], p95[0] / bp95[0], t[0] / bt[0]
                out.append(f"| {kind} | {size} | {form} | {c} | {p50[0]:.2f} | {p95[0]:.2f} | {t[0]:.1f} | {r50:.3f} | {r95:.3f} | {rt:.3f} | {m[0]*100:.0f}/{m[1]*100:.0f}/{m[2]*100:.0f}% |")
                if form == "baseline":
                    continue
                if c == 1:
                    verdict[form].append((f"{kind} {size} c=1 p50", r50, 1 + m[0], r50 <= 1 + m[0]))
                    verdict[form].append((f"{kind} {size} c=1 p95", r95, 1 + m[1], r95 <= 1 + m[1]))
                if c == 32:
                    verdict[form].append((f"{kind} {size} c=32 p95", r95, 1 + m[1], r95 <= 1 + m[1]))
                    verdict[form].append((f"{kind} {size} c=32 tput", rt, 1 - m[2], rt >= 1 - m[2]))

out.append("\n## Counters (round 1, net per request)\n")
out.append("| Batch | Rows | Form | c | XE temp tables | Temp Tables Creation Rate | PAGELATCH waits | PAGELATCH ms | recompiles | logical reads |")
out.append("|---|---|---|---|---|---|---|---|---|---|")
for key in sorted(cnt, key=lambda k: (k[0], k[1], k[3], FORMS.index(k[2]))):
    v = cnt[key]
    out.append(f"| {key[0]} | {key[1]} | {key[2]} | {key[3]} | {v['xe']:.2f} | {v['perf']:.2f} | {v['latch']:.2f} | {v['latch_ms']:.2f} | {v['recompiles']:.3f} | {v['reads']:,.0f} |")

out.append("\n## Gate verdict per form (latency/throughput criteria only)\n")
for form in FORMS[1:]:
    fails = [v for v in verdict[form] if not v[3]]
    out.append(f"- **{form}**: {len(verdict[form]) - len(fails)}/{len(verdict[form])} pass" + ("; fails: " + "; ".join(f"{n} {r:.2f} vs {b:.2f}" for n, r, b, _ in fails) if fails else ""))

for name in ("calibration.json", "calibration-tablevar.json"):
    try:
        c = load(name)
        out.append(f"\n{name}: " + json.dumps(c.get("gateSum") if "gateSum" in c else c))
    except FileNotFoundError:
        pass
print("\n".join(out))
