# Plan Compilation Epic

## Plan Compilation

- The relational model is the database-shaped version of an Ed-Fi resource schema.
- It defines the tables, columns, keys, and relationships needed to store and read that resource.
- Plan compilation uses it to generate deterministic read and write plans.

---

## Plan Compilation

- Removes runtime guesswork and SQL parsing
- Compile executor-ready plans once (caching)
- Reuse the same plan shape across runtime and future AOT work

---

## What The Epic Delivered

- Canonical SQL generation
- Stable read, write, and projection plan contracts
- Runtime mapping set compilation and cache

---

## The Core Output

- Compile target: `MappingSet`
- Cache key: schema hash + dialect + mapping version
- Result: immutable per-resource read and write plans

---

## 4. Determinism Foundations

- Stable SQL formatting and quoting
- Stable parameter and alias naming
- Stable batching and projection ordering

---

## 5. Write Plan Compilation

- `InsertSql` for every table
- `UpdateSql` for applicable 1:1 tables
- `DeleteByParentSql` for replace semantics

---

## 6. Write Path Details

- Ordered column bindings drive execution
- Child and extension tables use delete + bulk insert
- Key unification is compiled as explicit precompute metadata

---

## 7. Read Plan Compilation

- Hydration SQL compiled per table
- Keyset-driven reads by `DocumentId`
- Deterministic `ORDER BY` aligned to table keys

---

## 8. Runtime Integration

- Compile active mapping set from effective schema
- Cache once per process
- Log `Compiled`, `Joined in-flight`, or `Reused completed`

---

## 9. Evidence

- Golden tests for SQL and manifest output
- Authoritative DS 5.2 fixture coverage
- Determinism tests for repeatability
- Cache tests for compile-once concurrency

---

## 10. Scope Boundaries

- This epic builds the compilation layer, not the full executor story
- PostgreSQL is the runtime path wired here
- AOT mapping-pack compilation is not implemented yet

---

## Close

- Deterministic plans now exist for reads and writes
- The runtime can compile and cache them safely
- The output is testable, reusable, and ready for executor implementation
