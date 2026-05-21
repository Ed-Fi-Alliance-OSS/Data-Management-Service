# Authorization Implementation Progress Notes

Audience: DMS team, technical leads, QA, and product owners  
Window: April 30-May 21, 2026  
Scope: Relational primary store authorization under `reference/design/backend-redesign/epics/14-authorization`

---

## Slide 1: From Design To Enforcement

The work is no longer just metadata, schema, or planning. DMS now applies authorization decisions in the relational backend for several real API paths.

---

## Slide 2: The Work Landed In Layers

The important narrative is dependency order: database objects and indexes first, GET-many filtering next, then reusable CRUD planning and endpoint-specific enforcement.

---

## Slide 3: Completed Foundation Work

These are the pieces that make runtime enforcement practical: generated schema support, startup ordering, no-op strategy behavior, and database-object verification.

---

## Slide 4: Completed Runtime Behavior

This is the main delivery signal. EdOrg relationship authorization now protects collection reads, single-record reads, deletes, and creates in the relational backend.

---

## Slide 5: In QA And In Progress

Update flows are harder than create or delete because they must authorize both the stored record and the proposed final state before mutation, no-op success, or stale precondition responses can leak.

---

## Slide 6: What Remains

We should describe current progress as strong but bounded. People, Namespace, and final response-shaping work are still open and should not be represented as complete.

---

## Slide 7: Why This Matters

This gives engineering and QA concrete behavior to validate instead of only design intent. It also preserves the diagnostic data product needs for understandable failure responses.

---

## Slide 8: Near-Term Focus

The practical message for stakeholders is: EdOrg relationship authorization is substantially implemented, update authorization is the active risk area, and the remaining non-stretch work is clearly scoped.
