import glob, json, os, re, statistics as st, sys
from collections import defaultdict

ROOT = r"C:\perf\dms-1521"
SESSIONS = ["t5s1", "t5s2", "t5s3"]
FIX = {"resource": "resource", "descriptor": "descriptor", "ra": "read-acceleration"}
CHILD_RE = re.compile(r'FROM\s+"edfi"\."(Student(?:IdentificationDocument|OtherName|PersonalIdentificationDocument|Visa))"\s+t\b')


def rundir(session, fixture, variant):
    lab = "B1" if variant == "B" else "I2"
    m = glob.glob(os.path.join(ROOT, f"{session}-postgresql-{FIX[fixture]}-{lab}-*"))
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
    return dict(mean=mean, sd=sd, cv=(sd / mean if mean else 0.0), vals=vals)


def fmt(x, nd=1):
    if x is None:
        return "-"
    if abs(x) >= 1000:
        return f"{x:,.0f}"
    return f"{x:.{nd}f}"


def ratio_line(name, b, i, nd=1, rate=False):
    if b is None or i is None:
        return f"| {name} | - | - | - | - |"
    r = i["mean"] / b["mean"] if b["mean"] else float("nan")
    m = max(2 * b["cv"], 0.05)
    return (f"| {name} | {fmt(b['mean'],nd)} ± {fmt(b['sd'],nd)} ({b['cv']*100:.1f}%) | "
            f"{fmt(i['mean'],nd)} ± {fmt(i['sd'],nd)} ({i['cv']*100:.1f}%) | {r:.3f} | {m*100:.1f}% |")


out = []
H = "| Metric | B mean ± SD (CV) | I mean ± SD (CV) | I / B | m |\n|---|---|---|---|---|"

# ---------------- API cells ----------------
out.append("## API latency (cells.json), per-run p50/p95 aggregated across 3 sessions\n")
out.append(H)
cmds = {}
sha = defaultdict(set)
for fixture in ["resource", "descriptor", "ra"]:
    per = defaultdict(lambda: defaultdict(list))
    for v in "BI":
        for s in SESSIONS:
            for c in load(os.path.join(rundir(s, fixture, v), "cells.json")):
                per[c["cell"]][(v, "p50")].append(c["latencyMs"]["p50"])
                per[c["cell"]][(v, "p95")].append(c["latencyMs"]["p95"])
                cpr = c["commandsPerRequest"]
                cmds.setdefault((c["cell"], v), set()).add(json.dumps(cpr))
                sha[(c["cell"], v)].add(c["batchSha256"])
    for cell, d in per.items():
        for q in ("p50", "p95"):
            out.append(ratio_line(f"{cell} {q}", agg(d[("B", q)]), agg(d[("I", q)])))
out.append("\nCommands per request and batch SHA distinctness:\n")
for (cell, v), s in sorted(cmds.items()):
    out.append(f"- {cell} {v}: commands {sorted(s)}; distinct batch SHA across sessions: {len(sha[(cell, v)])}")

# ---------------- Replays ----------------
out.append("\n## Replays (replays.json)\n")
out.append(H)
rep = defaultdict(lambda: defaultdict(list))
errs = defaultdict(int)
counters = defaultdict(lambda: defaultdict(list))
fpi = defaultdict(list)
for fixture in ["resource", "descriptor", "ra"]:
    for v in "BI":
        for s in SESSIONS:
            for x in load(os.path.join(rundir(s, fixture, v), "replays.json")):
                kind = x["kind"].replace("resource.traditional-offset-zero", "resource")
                r = x["replay"]
                key = (kind, x["pageSize"], r["concurrency"])
                rep[key][(v, "p50")].append(r["p50Ms"])
                rep[key][(v, "p95")].append(r["p95Ms"])
                rep[key][(v, "tput")].append(r["throughputPerSecond"])
                errs[(key, v)] += r["errors"] + r["warmupErrors"]
                c = x.get("counters")
                if c:
                    n = c["netPerRequest"]
                    ck = (kind, x["pageSize"])
                    cat = sum(val for k, val in n.items() if k.startswith("catalog."))
                    counters[ck][(v, "catalog")].append(cat)
                    counters[ck][(v, "pg_statistic")].append(n["catalog.pg_statistic.n_tup_ins"] + n["catalog.pg_statistic.n_tup_del"])
                    counters[ck][(v, "plans/call")].append(c["net"]["statements.plans"] / c["net"]["statements.calls"] if c["net"]["statements.calls"] else None)
                    counters[ck][(v, "calls/req")].append(n["statements.calls"])
                    counters[ck][(v, "plan ms/req")].append(n["statements.total_plan_ms"])
                    counters[ck][(v, "exec ms/req")].append(n["statements.total_exec_ms"])
                    clean = c["workloadDelta"]["wal.fpi"] == 0 and c["idleDelta"]["wal.bytes"] <= 200
                    fpi[ck].append((v, s, c["workloadDelta"]["wal.fpi"], c["idleDelta"]["wal.bytes"], n["wal.bytes"], n["wal.records"]))
                    counters[ck][(v, "wal records/req")].append(n["wal.records"])
                    if clean:
                        counters[ck][(v, "WAL bytes/req (clean)")].append(n["wal.bytes"])
for key in sorted(rep):
    d = rep[key]
    nm = f"{key[0]} {key[1]} c={key[2]}"
    out.append(ratio_line(nm + " p50 ms", agg(d[("B", "p50")]), agg(d[("I", "p50")]), 2))
    out.append(ratio_line(nm + " p95 ms", agg(d[("B", "p95")]), agg(d[("I", "p95")]), 2))
    out.append(ratio_line(nm + " throughput/s", agg(d[("B", "tput")]), agg(d[("I", "tput")]), 1))
out.append("\nReplay errors (warmup + measured, summed over sessions): " +
           ", ".join(f"{k[0][0]} {k[0][1]} c={k[0][2]} {k[1]}={e}" for k, e in sorted(errs.items()) if e) + (" none" if not any(errs.values()) else ""))

out.append("\n## Counters (concurrency-1 windows), per request\n")
out.append(H)
for ck in sorted(counters):
    for metric in ["catalog", "pg_statistic", "WAL bytes/req (clean)", "wal records/req", "calls/req", "plans/call", "plan ms/req", "exec ms/req"]:
        out.append(ratio_line(f"{ck[0]} {ck[1]} {metric}", agg(counters[ck][("B", metric)]), agg(counters[ck][("I", metric)]), 2))
out.append("\nWAL window detail (variant, session, workload FPI, idle WAL bytes, net WAL bytes/req, WAL records/req):\n")
for ck in sorted(fpi):
    out.append(f"- {ck}: " + "; ".join(f"{v}/{s} fpi={f} idle={ib} wal/req={w:.0f} rec/req={rr:.1f}" for v, s, f, ib, w, rr in fpi[ck]))

# ---------------- Spike plans ----------------
out.append("\n## Spike plans (EXPLAIN ANALYZE replay, one per batch)\n")
out.append(H)
plans = defaultdict(lambda: defaultdict(list))
for fixture, files in [("resource", ["resource.traditional-offset-zero.25", "resource.traditional-offset-zero.500"]),
                       ("ra", ["fallback.25", "fallback.500", "candidate.25", "candidate.500"]),
                       ("descriptor", ["descriptor.25", "descriptor.500"])]:
    for v in "BI":
        for s in SESSIONS:
            for fn in files:
                p = load(os.path.join(rundir(s, fixture, v), "plans", fn + ".explain.json"))
                name = fn.replace("resource.traditional-offset-zero", "resource")
                ct = cb = 0.0
                nchild = 0
                for stmt in p["statements"]:
                    if stmt["kind"] == "explained" and CHILD_RE.search(stmt["sql"]):
                        ct += stmt["executionMs"]
                        cb += stmt["sharedHitBlocks"] + stmt["sharedReadBlocks"]
                        nchild += 1
                t = p["totals"]
                plans[name][(v, "batch exec ms")].append(t["explainedExecutionMs"] + t["timedStatementMs"])
                plans[name][(v, "batch plan ms")].append(t["explainedPlanningMs"])
                plans[name][(v, "batch buffers")].append(t["sharedHitBlocks"] + t["sharedReadBlocks"])
                if nchild:
                    plans[name][(v, f"child exec ms ({nchild} stmts)")].append(ct)
                    plans[name][(v, "child buffers")].append(cb)
for name in sorted(plans):
    keys = sorted({k[1] for k in plans[name]})
    for k in keys:
        out.append(ratio_line(f"{name} {k}", agg(plans[name][("B", k)]), agg(plans[name][("I", k)]), 2))


# ---------------- auto_explain ----------------
def walk(node):
    yield node
    for ch in node.get("Plans", []) or []:
        yield from walk(ch)


def keyset_nodes(root):
    for n in walk(root):
        if n.get("Relation Name") in ("page", "descriptor_page") or n.get("Function Name") == "unnest":
            yield n


out.append("\n## auto_explain windows (driver path; 20 requests / replays per page size)\n")
out.append("Child cost per request = sum of the four child statements (median over requests in a window); "
           "aggregated as mean ± SD of the per-session medians.\n")
out.append(H)
ae = defaultdict(lambda: defaultdict(list))
nodes = defaultdict(lambda: defaultdict(int))
est = defaultdict(lambda: defaultdict(int))
for fixture, files in [("resource", ["resource.25", "resource.500"]), ("ra", ["fallback.25", "fallback.500", "candidate.25", "candidate.500"]),
                       ("descriptor", ["descriptor.25", "descriptor.500"])]:
    for v in "BI":
        for s in SESSIONS:
            for fn in files:
                path = os.path.join(rundir(s, fixture, v), "autoexplain", fn + ".plans.json")
                entries = load(path)
                size = int(fn.split(".")[1])
                child = []
                keyset_stmts = []
                for e in entries:
                    qt = e["plan"].get("Query Text", "")
                    root = e["plan"]["Plan"]
                    ks = list(keyset_nodes(root))
                    if ks and fn.startswith(("descriptor", "candidate")):
                        keyset_stmts.append(e)
                    m = CHILD_RE.search(qt)
                    if m:
                        bufs = root.get("Shared Hit Blocks", 0) + root.get("Shared Read Blocks", 0)
                        child.append((e["durationMs"], bufs))
                        for n in walk(root):
                            if n.get("Relation Name") == m.group(1):
                                nodes[(fn, v)][n["Node Type"]] += 1
                        for n in ks:
                            est[(fn, v)][(n.get("Plan Rows"), n.get("Actual Rows") if n.get("Actual Loops", 1) else None)] += 1
                    elif ks:
                        for n in ks:
                            est[(fn, v)][(n.get("Plan Rows"), n.get("Actual Rows"))] += 1
                if child:
                    assert len(child) % 4 == 0, (path, len(child))
                    per_req = [child[i:i + 4] for i in range(0, len(child), 4)]
                    ae[fn][(v, "child ms/req (median)")].append(st.median(sum(c[0] for c in r) for r in per_req))
                    ae[fn][(v, "child buffers/req (median)")].append(st.median(sum(c[1] for c in r) for r in per_req))
                    ae[fn][(v, "requests seen")].append(len(per_req))
                    # first 5 vs last 5 requests (custom -> generic transition)
                    ae[fn][(v, "child ms/req first 5")].append(st.median(sum(c[0] for c in r) for r in per_req[:5]))
                    ae[fn][(v, "child ms/req last 10")].append(st.median(sum(c[0] for c in r) for r in per_req[-10:]))
                if keyset_stmts:
                    ae[fn][(v, "keyset stmt ms total/window")].append(sum(e["durationMs"] for e in keyset_stmts))
for fn in sorted(ae):
    for k in sorted({k[1] for k in ae[fn]}):
        out.append(ratio_line(f"{fn} {k}", agg(ae[fn][("B", k)]), agg(ae[fn][("I", k)]), 2))
out.append("\nChild-table node types (counts over all sessions):\n")
for k in sorted(nodes):
    out.append(f"- {k[0]} {k[1]}: {dict(nodes[k])}")
out.append("\nKeyset node estimate (Plan Rows, Actual Rows) -> count, over all sessions:\n")
for k in sorted(est):
    top = sorted(est[k].items(), key=lambda kv: -kv[1])[:8]
    out.append(f"- {k[0]} {k[1]}: " + ", ".join(f"{pr}/{ar}: {c}" for (pr, ar), c in top))

# ---------------- calibration ----------------
out.append("\n## Calibration\n")
for s in SESSIONS:
    p = os.path.join(rundir(s, "resource", "B"), "calibration.json")
    if os.path.exists(p):
        out.append(f"- {s}: " + json.dumps(load(p))[:400])

print("\n".join(out))
