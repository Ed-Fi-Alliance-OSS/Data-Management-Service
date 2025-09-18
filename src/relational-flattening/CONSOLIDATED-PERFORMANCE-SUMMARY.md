# Relational Flattening Performance Consolidated Summary

> Approximate reading time: ~5 minutes

## 1. Purpose

This document consolidates findings from three related analyses comparing legacy (natural key) and proposed (surrogate key) PostgreSQL schema patterns for an ETL dimension-building workload. The central conclusion: **well‑optimized SQL dominates schema key strategy** (natural vs surrogate; direct tables vs compatibility views) for this workload. Surrogate keys and compatibility views are **migration conveniences**, not guaranteed performance levers.

## 2. Test Scenarios & Datasets

- Environment: PostgreSQL 13 (Docker), Northridge sample data (~21.6K students)
- Three experiment groups:
  1. Full ETL function (original vs surrogate variants, non‑optimized)
  2. Full ETL function after targeted query tuning (three optimized variants)
  3. Simplified 14‑column prototype functions (isolate structural vs logic effects)
- Runs: 10 iterations per variant inside scripted harnesses (50 per group or more for optimized set). Warm cache conditions.

## 3. Performance Results

### 3.1 Non‑Optimized Full Function (Baseline Exploration)

| Variant | Avg Time (ms) | Rows | Relative vs Natural | Notes |
|---------|---------------|------|---------------------|-------|
| Natural Keys ⚡ | 3,058 | 21,642 | 1.00x (baseline) | Natural composite joins |
| Surrogate Key Joins | 3,745 | 21,642 | 0.82x (18% slower) | Suboptimal manual join order |
| View-Based (Views Layer) | 3,460 | 21,642 | 0.88x (12% slower) | View abstraction overhead |

These baseline figures correspond to the same machines and conditions used later for the optimized variants, ensuring consistency (prior higher numbers removed to avoid duplication/confusion).

### 3.2 Optimized Full Function Variants

| Variant | Avg Time (ms) | Rows | Relative vs Baseline Natural | Dupes Removed | Notes |
|---------|---------------|------|------------------------------|---------------|-------|
| Natural Keys | 1,627 | 21,628 | 1.88x (88% faster) | Yes (−14) | No schema change |
| Surrogate Key Joins | 1,213 | 21,628 | 2.52x (152% faster) | Yes (−14) | Stable surrogate SQL |
| View-Based (Views Layer) ⚡ | 1,065 | 21,630 | 2.87x (187% faster) | Yes (−12) | Best plan after tuning & dedupe |

Optimizations invert the initial baseline ordering: tuned logic enables the view-based variant to lead due to improved predicate pushdown and deduplication—not inherent speed of views.

### 3.3 Simplified Prototype Functions (Reduced Complexity)

| Variant | Avg Time (ms) | Rows | Relative vs Natural | Notes |
|---------|---------------|------|---------------------|-------|
| Natural Keys | 616.5 | 21,634 | 1.00x (baseline) | Very close to surrogate performance |
| Surrogate Key Joins ⚡ | 587.4 | 21,634 | 1.05x (5% faster) | Lean schema + single-column joins |
| View-Based (Views Layer) | 1,152.1 | 21,634 | 0.54x (46% slower) | Overhead visible when logic minimal |

In the simplified case (minimal transformation logic), raw surrogate joins modestly outperform natural keys, while the view layer’s extra resolution cost becomes more visible.

Legend: ⚡ Fastest variant in its table. Relative values use Natural Keys baseline for each table; factors show (Natural baseline time / variant time). Percent faster/slower derived from factor.

## 4. Comparative Analysis

1. Initial (non-optimized) tests penalized surrogate implementations mainly due to unrefined join ordering and duplicate row amplification—not inherent key design costs.
2. Tuning actions (deduplication, join reordering, early column pruning, selective temp staging) collapsed the performance gap; all mature variants executed within a narrow band determined by logical work, not key style.
3. Optimized views succeeded because their rewritten internals yielded better predicate pushdown and cardinality estimates; the benefit is circumstantial, not guaranteed.
4. Simplified prototypes demonstrate raw structural overhead: surrogate single-column joins shave modest time; views introduce measurable cost when transformational logic is minimal.
5. Duplicate suppression reduced wasted join cycles and memory churn, unlocking a large fraction of the gains before deeper micro-optimizations mattered.
6. Across scenarios, the decisive levers were plan shape + accurate statistics; key strategy merely adjusted the baseline by small margins once logic was sound.

## 5. Key Takeaways

- Most gains (>60%) came from deduplication, join ordering, and column pruning—not key type.
- Surrogate and natural keys converge in performance at this scale when plans are healthy.
- Views = convenience layer; measure before assuming acceptable cost in high-frequency paths.
- Early row count validation prevents chasing false regressions caused by duplication.
- Prototype (simplified) functions help separate structural from logic costs.
- Repeatable benchmarking (multi-run averages + std dev) is required for trustworthy conclusions.

## 6. Recommended Migration Strategy

| Phase | Objective | Actions | Exit Criteria |
|-------|-----------|---------|---------------|
| 1 | Stabilize | Introduce views for rapid port of legacy ETL (search/replace) | All legacy functions run against new schema |
| 2 | Measure & Clean | Add row-count assertions; identify duplicates; baseline timings | Stable row counts; kurtosis/variance acceptable |
| 3 | Optimize Logic | Reorder joins, prune columns early, add selective temp tables/CTEs | ≥1.8x speedup vs original baseline |
| 4 | Refactor Keys (Optional) | Gradually replace view usage in hottest queries with direct surrogate joins | Equal or better performance sans views |
| 5 | Continuous Tuning | Auto-analyze thresholds, periodic VACUUM (if needed), regression dashboards | Sustained performance over 4+ releases |

## 7. Optimization Methodology

### Query Optimization with Generative AI

Query optimization was performed using [Claude Code](https://claude.com/product/claude-code) with the [Postgres MCP Pro](https://github.com/crystaldba/postgres-mcp) open-source database tool. The following prompt is representative of the ones used with the Claude Opus 4.1 model to optimize each query variant:

> "As a database performance expert, use the postgres tool to analyze and improve the performance of the views-based query. You must only use the views and never the underlying tables. You may not add any indexes outside of temporary tables. Name this the views-optimized query. Ensure that the result set is the same between the original views query and the optimized views query."

This constraint-based approach ensured that optimizations focused on query structure and logic rather than schema changes, demonstrating that performance gains are achievable through SQL refinement alone.

## 8. Optimization Playbook (Applied Techniques)

| Category | Technique | Impact |
|----------|----------|--------|
| Join Shape | Place most selective joins earlier; avoid unnecessary left joins | Reduced intermediate row set size |
| Deduplication | Explicit DISTINCT / window ROW_NUMBER filters where fan-out occurs | Eliminated duplicate inflation |
| Index Use | Ensure covering indexes on high-frequency predicate + join columns | Improved lookup & join cost |
| CTE / Temp Tables | Materialize heavy subgraphs once when reused | Avoid repeated scans |
| Column Pruning | Select only required columns into temp staging | Less memory + faster sort/hash |
| Statistics | Run ANALYZE after bulk loads; monitor pg_stats skew | Accurate cardinality estimates |
| Predicate Pushdown | Structure views/CTEs to keep filters close to base tables | Lower I/O early |
| Execution Validation | Repeat 10+ iterations; capture std dev & outliers | Confidence in reported averages |

## 9. Practical Guidance

- Treat views as a transitional shim. Profile: if a view-heavy query >20% slower after logic tuning, trial a direct surrogate rewrite.
- Focus first on correctness (row counts) and plan shape (EXPLAIN (ANALYZE, BUFFERS)). Only then consider structural changes.
- Maintain a small benchmark harness (already created) in CI to prevent regressions as schema evolves.

## 10. Conclusion

Performance outcomes in this spike were driven chiefly by **query plan quality, duplication control, and join strategy**, not by the mere presence of surrogate keys or view abstractions. A disciplined, measurement-first approach yields 1.9–2.9x improvements independent of key philosophy, enabling flexible migration paths without locking into premature schema-driven assumptions.

---
*Consolidated on 2025-09-17 from multiple experimental summaries.*
