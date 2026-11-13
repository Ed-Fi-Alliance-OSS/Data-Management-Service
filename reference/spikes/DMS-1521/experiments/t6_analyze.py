"""DMS-1521 task 6: SQL Server B vs I across interleaved sessions. Usage: python -I t6_analyze.py t6s1 t6s2 t6s3"""
import glob, json, os, re, statistics as st, sys
import xml.etree.ElementTree as ET
from collections import defaultdict

ROOT = r"C:\perf\dms-1521"
SESSIONS = sys.argv[1:] or ["t6s1", "t6s2", "t6s3"]
FIX = {"resource": "resource", "descriptor": "descriptor", "ra": "read-acceleration"}
NS = "{http://schemas.microsoft.com/sqlserver/2004/07/showplan}"
CHILD = ("StudentIdentificationDocument", "StudentOtherName", "StudentPersonalIdentificationDocument", "StudentVisa")


def rundir(session, fixture, variant):
    m = glob.glob(os.path.join(ROOT, f"{session}-mssql-{FIX[fixture]}-{variant}[0-9]-2*"))
    assert len(m) == 1, (session, fixture, variant, m)
    return m[0]


def load(p):
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def agg(vals):
    vals = [v for v in vals if v is not None]
    if not vals:
        return None
    mean = st.mean(vals)
    sd = st.stdev(vals) if len(vals) > 1 else 0.0
    return dict(mean=mean, sd=sd, cv=(sd / mean if mean else 0.0))


def fmt(x, nd=1):
    if x is None:
        return "-"
    return f"{x:,.0f}" if abs(x) >= 1000 else f"{x:.{nd}f}"


def line(name, b, i, nd=1):
    if b is None or i is None:
        return f"| {name} | {fmt(b and b['mean'], nd)} | {fmt(i and i['mean'], nd)} | - | - |"
    r = i["mean"] / b["mean"] if b["mean"] else float("nan")
    m = max(2 * b["cv"], 0.05)
    return (f"| {name} | {fmt(b['mean'],nd)} ± {fmt(b['sd'],nd)} ({b['cv']*100:.1f}%) | "
            f"{fmt(i['mean'],nd)} ± {fmt(i['sd'],nd)} ({i['cv']*100:.1f}%) | {r:.3f} | {m*100:.1f}% |")


H = "| Metric | B mean ± SD (CV) | I mean ± SD (CV) | I / B | m |\n|---|---|---|---|---|"
out = []

# ---------------- API cells ----------------
out.append("## API latency (cells.json)\n")
out.append(H)
cmds, sha, wall = {}, defaultdict(set), defaultdict(list)
for fixture in FIX:
    per = defaultdict(lambda: defaultdict(list))
    for v in "BI":
        for s in SESSIONS:
            d = rundir(s, fixture, v)
            for c in load(os.path.join(d, "cells.json")):
                per[c["cell"]][(v, "p50")].append(c["latencyMs"]["p50"])
                per[c["cell"]][(v, "p95")].append(c["latencyMs"]["p95"])
                cmds.setdefault((c["cell"], v), set()).add(json.dumps(c["commandsPerRequest"]))
                sha[(c["cell"], v)].add(c["batchSha256"])
    for cell, dd in per.items():
        for q in ("p50", "p95"):
            out.append(line(f"{cell} {q}", agg(dd[("B", q)]), agg(dd[("I", q)])))
out.append("\nCommands per request / distinct batch SHA across sessions:\n")
for (cell, v), s in sorted(cmds.items()):
    out.append(f"- {cell} {v}: {sorted(s)}; SHA distinct {len(sha[(cell, v)])}")

# ---------------- Replays and counters ----------------
out.append("\n## Replays\n")
out.append(H)
rep = defaultdict(lambda: defaultdict(list))
errs = defaultdict(int)
cnt = defaultdict(lambda: defaultdict(list))
causes = defaultdict(lambda: defaultdict(float))
raw_tt = defaultdict(list)
for fixture in FIX:
    for v in "BI":
        for s in SESSIONS:
            for x in load(os.path.join(rundir(s, fixture, v), "replays.json")):
                kind = x["kind"].replace("resource.traditional-offset-zero", "resource")
                r = x["replay"]
                key = (kind, x["pageSize"], r["concurrency"])
                rep[key][(v, "p50")].append(r["p50Ms"])
                rep[key][(v, "p95")].append(r["p95Ms"])
                rep[key][(v, "p99")].append(r["p99Ms"])
                rep[key][(v, "tput")].append(r["throughputPerSecond"])
                errs[(key, v)] += r["errors"] + r["warmupErrors"]
                c = x.get("counters")
                if not c:
                    continue
                n = c["netPerRequest"]
                g = lambda k: n.get(k, 0.0)
                cnt[key][(v, "xe temp tables/req")].append(g("xe.temp_tables_created"))
                cnt[key][(v, "perf Temp Tables Creation Rate/req")].append(g("perf.Temp Tables Creation Rate"))
                cnt[key][(v, "compilations/req")].append(g("perf.SQL Compilations/sec"))
                cnt[key][(v, "recompilations/req (perf)")].append(g("perf.SQL Re-Compilations/sec"))
                cnt[key][(v, "XE recompiles/req")].append(g("recompile.total"))
                cnt[key][(v, "PAGELATCH tasks/req")].append(sum(val for k, val in n.items() if k.startswith("wait.PAGELATCH") and k.endswith(".tasks")))
                cnt[key][(v, "PAGELATCH ms/req")].append(sum(val for k, val in n.items() if k.startswith("wait.PAGELATCH") and k.endswith(".ms")))
                cnt[key][(v, "querystats logical reads/req")].append(g("querystats.logical_reads"))
                cnt[key][(v, "querystats executions/req")].append(g("querystats.executions"))
                cnt[key][(v, "plan generation sum (net)")].append(c["net"].get("querystats.plan_generation_sum", 0.0))
                raw_tt[key].append((v, s, c["net"].get("xe.temp_tables_created"), c["net"].get("perf.Temp Tables Creation Rate"), c["requests"]))
                for k, val in c["net"].items():
                    if k.startswith("recompile.cause.") and val:
                        causes[(key, v)][k.split(".")[-1]] += val
for key in sorted(rep):
    d = rep[key]
    nm = f"{key[0]} {key[1]} c={key[2]}"
    for q, lab, nd in (("p50", "p50 ms", 2), ("p95", "p95 ms", 2), ("p99", "p99 ms", 2), ("tput", "throughput/s", 1)):
        out.append(line(f"{nm} {lab}", agg(d[("B", q)]), agg(d[("I", q)]), nd))
out.append("\nReplay errors: " + (", ".join(f"{k[0]} {v}={e}" for (k, v), e in sorted(errs.items()) if e) or "none"))

out.append("\n## Counter windows (net per request)\n")
out.append(H)
for key in sorted(cnt):
    for metric in sorted({k[1] for k in cnt[key]}):
        out.append(line(f"{key[0]} {key[1]} c={key[2]} {metric}", agg(cnt[key][("B", metric)]), agg(cnt[key][("I", metric)]), 3))
out.append("\nTemp-table windows (variant, session, XE net, perf-counter net, requests):\n")
for key in sorted(raw_tt):
    out.append(f"- {key}: " + "; ".join(f"{v}/{s} xe={a} perf={b} n={r}" for v, s, a, b, r in raw_tt[key]))
out.append("\nRecompile causes (net, summed over sessions):\n")
for k in sorted(causes):
    out.append(f"- {k}: {dict(causes[k])}")


# ---------------- Spike plans (SET STATISTICS XML/IO/TIME) ----------------
def plan_ops(path):
    """Per statement: (text, [(op, table, estimate*executions, actual)])."""
    stmts = []
    root = ET.parse(path).getroot()
    for stmt in root.iter(f"{NS}StmtSimple"):
        ops = []
        for r in stmt.iter(f"{NS}RelOp"):
            o = r.find(f".//{NS}Object")
            tbl = (o.get("Table") or "").strip("[]") if o is not None else ""
            if r.get("PhysicalOp") in ("Table-valued function", "Clustered Index Scan", "Clustered Index Seek", "Index Seek", "Index Scan", "Table Scan"):
                rti = r.find(f"{NS}RunTimeInformation")
                act = sum(int(c.get("ActualRows")) for c in rti) if rti is not None else None
                est = float(r.get("EstimateRows")) * (1 + float(r.get("EstimateRewinds") or 0) + float(r.get("EstimateRebinds") or 0))
                ops.append((r.get("PhysicalOp"), tbl, est, act))
        stmts.append(((stmt.get("StatementText") or "")[:60], ops))
    return stmts


def stats_reads(path):
    """Logical reads per table and elapsed ms from the STATISTICS IO/TIME text."""
    reads = defaultdict(int)
    elapsed = 0
    with open(path, encoding="utf-8", errors="replace") as f:
        for ln in f:
            m = re.match(r"Table '([^']+)'\. Scan count \d+, logical reads (\d+)", ln)
            if m:
                reads[m.group(1)] += int(m.group(2))
            m = re.search(r"Execution Times:", ln)
            m2 = re.search(r"CPU time = \d+ ms,\s+elapsed time = (\d+) ms", ln)
            if m2:
                elapsed += int(m2.group(1))
    return reads, elapsed


out.append("\n## Spike plans\n")
out.append(H)
plans = defaultdict(lambda: defaultdict(list))
nodes = defaultdict(lambda: defaultdict(int))
keyset_est = defaultdict(lambda: defaultdict(int))
for fixture, files in [("resource", ["resource.traditional-offset-zero.25", "resource.traditional-offset-zero.500",
                                     "resource.traditional-offset-deep.25", "resource.traditional-offset-deep.500"]),
                       ("ra", ["fallback.25", "fallback.500", "candidate.25", "candidate.500"]),
                       ("descriptor", ["descriptor.25", "descriptor.500"])]:
    for v in "BI":
        for s in SESSIONS:
            d = rundir(s, fixture, v)
            for fn in files:
                pj = os.path.join(d, "plans", fn + ".plans.json")
                if not os.path.exists(pj):
                    continue
                meta = load(pj)
                name = fn.replace("resource.traditional-offset-", "resource.")
                t = meta["totals"]
                plans[name][(v, "batch logical reads")].append(t["logicalReads"])
                plans[name][(v, "batch elapsed ms")].append(t["elapsedMs"])
                plans[name][(v, "batch cpu ms")].append(t["cpuMs"])
                reads, _ = stats_reads(os.path.join(d, meta["statisticsFile"]))
                plans[name][(v, "child logical reads")].append(sum(reads.get(c, 0) for c in CHILD))
                plans[name][(v, "tempdb #page/worktable reads")].append(sum(val for k, val in reads.items() if k.startswith("#") or k == "Worktable"))
                for pf in meta["planFiles"]:
                    for text, ops in plan_ops(os.path.join(d, pf)):
                        child = [op for op in ops if op[1] in CHILD]
                        if child:
                            for op in child:
                                nodes[(name, v)][f"{op[1]}:{op[0]}"] += 1
                        for op in ops:
                            if op[1].startswith("#") or op[0] == "Table-valued function":
                                keyset_est[(name, v)][(round(op[2]), op[3])] += 1
for name in sorted(plans):
    for k in sorted({k[1] for k in plans[name]}):
        out.append(line(f"{name} {k}", agg(plans[name][("B", k)]), agg(plans[name][("I", k)]), 1))
out.append("\nChild-table operators (counts over sessions):\n")
for k in sorted(nodes):
    out.append(f"- {k[0]} {k[1]}: {dict(nodes[k])}")
out.append("\nKeyset operator estimate (x executions) / actual -> count:\n")
for k in sorted(keyset_est):
    out.append(f"- {k[0]} {k[1]}: " + ", ".join(f"{e}/{a}: {c}" for (e, a), c in sorted(keyset_est[k].items(), key=lambda kv: -kv[1])[:6]))

# ---------------- calibration, wall time ----------------
out.append("\n## Calibration\n")
for s in SESSIONS:
    for v in "BI":
        p = os.path.join(rundir(s, "resource", v), "calibration.json")
        if os.path.exists(p):
            c = load(p)
            out.append(f"- {s} {v}: calibrated={c['calibrated']} gate={c['gateCounters']} sum={c['gateSum']}")
print("\n".join(out))
