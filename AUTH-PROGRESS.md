# Authorization Implementation Progress

Audience: DMS team, technical leads, QA, and product owners  
Window: April 30-May 21, 2026  
Scope: Relational primary store authorization under `reference/design/backend-redesign/epics/14-authorization`

---

## Slide 1: From Design To Enforcement

Over the past three weeks, authorization moved from relational design and generated database support into runtime behavior. The clearest delivered path is EdOrg relationship-based authorization across GET-many, GET-by-id, DELETE, and POST-create.

**Speaker notes:**  
The work is no longer just metadata, schema, or planning. DMS now applies authorization decisions in the relational backend for several real API paths.

---

## Slide 2: The Work Landed In Layers

The team first strengthened the generated relational surface, then added query filtering, then split single-record CRUD authorization into smaller slices. That sequencing reduced risk because each runtime operation now builds on shared planning, SQL, and failure metadata.

**Speaker notes:**  
The important narrative is dependency order: database objects and indexes first, GET-many filtering next, then reusable CRUD planning and endpoint-specific enforcement.

---

## Slide 3: Completed Foundation Work

Completed foundation work made authorization reliable enough to execute:

- `DMS-1054`: emitted relationship and namespace authorization indexes.
- `DMS-1094`: emitted People join indexes for Person traversal paths.
- `DMS-1091`: moved auth startup into `IDmsStartupTask`.
- `DMS-1090`: verified `NoFurtherAuthorizationRequired` as a no-op.
- `DMS-1096`: added verification harness coverage for emitted auth DB objects.

**Speaker notes:**  
These are the pieces that make runtime enforcement practical: generated schema support, startup ordering, no-op strategy behavior, and database-object verification.

---

## Slide 4: Completed Runtime Behavior

EdOrg relationship authorization is now implemented for key relational API paths:

- `DMS-1055`: GET-many filters unauthorized rows and keeps pagination/count behavior correct.
- `DMS-1160`: shared operation-neutral CRUD authorization core.
- `DMS-1161`: GET-by-id and DELETE enforce stored-value authorization.
- `DMS-1162`: POST-create enforces proposed-value authorization before insert.

**Speaker notes:**  
This is the main delivery signal. EdOrg relationship authorization now protects collection reads, single-record reads, deletes, and creates in the relational backend.

---

## Slide 5: In QA And In Progress

`DMS-1096` is in the verification/QA lane for emitted auth database objects. `DMS-1163` is in progress for PUT and POST-as-update, and it is the remaining slice needed to close EdOrg-only relationship CRUD coverage.

**Speaker notes:**  
Update flows are harder than create or delete because they must authorize both the stored record and the proposed final state before mutation, no-op success, or stale precondition responses can leak.

---

## Slide 6: What Remains

The remaining non-stretch work broadens authorization beyond the EdOrg-only path:

- `DMS-1057`: Namespace-based authorization.
- `DMS-1095`: People-involved relationship authorization for GET-many.
- `DMS-1158` and `DMS-1164`: People-involved relationship authorization for CRUD and its core.
- `DMS-1099` and `DMS-1165`: final security configuration and relationship auth ProblemDetails hardening.

**Speaker notes:**  
We should describe current progress as strong but bounded. People, Namespace, and final response-shaping work are still open and should not be represented as complete.

---

## Slide 7: Why This Matters

The implementation now connects claims metadata, generated schema, query planning, SQL execution, and handler result mapping. Authorization failures carry enough structure for later ProblemDetails hardening, and provider coverage is being built for both PostgreSQL and SQL Server.

**Speaker notes:**  
This gives engineering and QA concrete behavior to validate instead of only design intent. It also preserves the diagnostic data product needs for understandable failure responses.

---

## Slide 8: Near-Term Focus

The next milestone is finishing `DMS-1163` so EdOrg-only relationship CRUD includes PUT and POST-as-update. After that, the work shifts from completing the EdOrg vertical slice to expanding strategy coverage for People and Namespace authorization, then tightening final error responses.

**Speaker notes:**  
The practical message for stakeholders is: EdOrg relationship authorization is substantially implemented, update authorization is the active risk area, and the remaining non-stretch work is clearly scoped.
