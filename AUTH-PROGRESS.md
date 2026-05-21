# Authorization Implementation Progress

Audience: DMS team, technical leads, QA, and product owners  
Window: April 30-May 21, 2026  
Scope: Relational primary store authorization under `reference/design/backend-redesign/epics/14-authorization`

---

## Slide 1: From Design To Enforcement

Over the past three weeks, authorization moved from relational design and generated database support into runtime behavior. The clearest delivered path is EdOrg relationship-based authorization across GET-many, GET-by-id, DELETE, and POST-create.

---

## Slide 2: The Work Landed In Layers

The team first strengthened the generated relational surface, then added query filtering, then split single-record CRUD authorization into smaller slices. That sequencing reduced risk because each runtime operation now builds on shared planning, SQL, and failure metadata.

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

`DMS-1096` is in the verification/QA lane for emitted auth database objects. `DMS-1163` is in progress for PUT and POST-as-update, and it is the remaining slice needed to close EdOrg-only relationship CRUD coverage.

---

## Slide 6: What Remains

The remaining non-stretch work broadens authorization beyond the EdOrg-only path:

- `DMS-1057`: Namespace-based authorization.
- `DMS-1095`: People-involved relationship authorization for GET-many.
- `DMS-1158` and `DMS-1164`: People-involved relationship authorization for CRUD and its core.
- `DMS-1099` and `DMS-1165`: final security configuration and relationship auth ProblemDetails hardening.

---

## Slide 7: Why This Matters

The implementation now connects claims metadata, generated schema, query planning, SQL execution, and handler result mapping. Authorization failures carry enough structure for later ProblemDetails hardening, and provider coverage is being built for both PostgreSQL and SQL Server.

---

## Slide 8: Near-Term Focus

The next milestone is finishing `DMS-1163` so EdOrg-only relationship CRUD includes PUT and POST-as-update. After that, the work shifts from completing the EdOrg vertical slice to expanding strategy coverage for People and Namespace authorization, then tightening final error responses.
