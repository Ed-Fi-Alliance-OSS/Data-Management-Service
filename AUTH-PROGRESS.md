# Authorization Implementation Progress

## Slide 1: From Design To Enforcement

Over the past three weeks, authorization moved from relational design and generated database support into runtime behavior. The clearest delivered path is EdOrg relationship-based authorization across GET-many, GET-by-id, DELETE, and POST-create.

---

## Slide 2: The Work Landed In Layers

The team first strengthened the generated relational surface, then added query filtering, then split single-record CRUD authorization into smaller slices.

---

## Slide 3: Completed Foundation Work

Completed foundation work made authorization reliable enough to execute:

- `DMS-1054`: emitted relationship and namespace authorization indexes.
- `DMS-1094`: emitted People join indexes for Person traversal paths.
- `DMS-1091`: moved auth startup into `IDmsStartupTask`.
- `DMS-1090`: verified `NoFurtherAuthorizationRequired` as a no-op.
- `DMS-1096`: added verification harness coverage for emitted auth DB objects.

---

## Slide 4: Completed Runtime Behavior

EdOrg relationship authorization is now implemented for key relational API paths:

- `DMS-1055`: GET-many filters unauthorized rows and keeps pagination/count behavior correct.
- `DMS-1160`: shared operation-neutral CRUD authorization core.
- `DMS-1161`: GET-by-id and DELETE enforce stored-value authorization.
- `DMS-1162`: POST-create enforces proposed-value authorization before insert.

---

## Slide 5: In QA And In Progress

`DMS-1163` is in progress for PUT and POST-as-update, and it is the remaining slice needed to close EdOrg-only relationship CRUD coverage.

---

## Slide 6: What Remains

The remaining v1.0 work broadens authorization beyond the EdOrg-only path:

- `DMS-1057`: Namespace-based authorization.
- `DMS-1095`: People-involved relationship authorization for GET-many.
- `DMS-1158` and `DMS-1164`: People-involved relationship authorization for CRUD and its core.
- `DMS-1099` and `DMS-1165`: final security configuration and relationship auth ProblemDetails hardening.

