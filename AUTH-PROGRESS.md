# Authorization Implementation Progress

Audience: DMS team, technical leads, QA, and product owners  
Window: April 30-May 21, 2026  
Scope: Relational primary store authorization work under `reference/design/backend-redesign/epics/14-authorization`

---

## Slide 1: Where We Are

- Authorization moved from design and DDL groundwork into runtime enforcement.
- The strongest progress is in EdOrg relationship-based authorization.
- GET-many, GET-by-id, DELETE, and POST-create now have completed EdOrg-only coverage.
- PUT and POST-as-update are the active implementation front.

**Speaker notes:**  
The past three weeks were about turning the relational authorization design into executable behavior. The current shape is a vertical slice through EdOrg relationship authorization, with remaining work focused on update flows, People relationships, namespace authorization, and final error hardening.

---

## Slide 2: Three-Week Narrative

- First, the team strengthened the generated database surface: indexes and People join-path support.
- Then GET-many filtering landed for EdOrg relationship strategies.
- Next, CRUD authorization was split into focused slices to reduce review and QA risk.
- The first three CRUD slices are complete.
- QA now has concrete relational behavior to validate instead of only design contracts.

**Speaker notes:**  
The sequencing matters. Runtime authorization depends on predictable generated schema objects and reusable planning contracts. The team built those foundations first, then applied them to query and single-record write paths.

---

## Slide 3: Completed Foundation And Query Work

| Status | Ticket | Work |
| --- | --- | --- |
| Done | `DMS-1054` | Relationship and Namespace auth indexes emitted into generated DDL. |
| Done | `DMS-1055` | EdOrg-only relationship authorization for GET-many. |
| Done | `DMS-1091` | Auth startup moved into `IDmsStartupTask`. |
| Done | `DMS-1090` | `NoFurtherAuthorizationRequired` verified as a no-op strategy. |
| Done | `DMS-1094` | People join indexes emitted for Person traversal paths. |

**Speaker notes:**  
This work made authorization part of the generated relational model and the DMS startup lifecycle. GET-many now applies EdOrg relationship filters instead of treating the strategy as a future placeholder.

---

## Slide 4: Completed CRUD Authorization Slices

| Status | Ticket | Work |
| --- | --- | --- |
| Done | `DMS-1160` | Slice 1: operation-neutral relationship CRUD auth core. |
| Done | `DMS-1161` | Slice 2: EdOrg-only GET-by-id and DELETE authorization. |
| Done | `DMS-1162` | Slice 3: EdOrg-only POST-create authorization. |

**Speaker notes:**  
The CRUD work was intentionally sliced. Slice 1 created shared planning and classification contracts. Slice 2 applied those contracts to stored single-record checks. Slice 3 added proposed-value checks for create-new POST behavior.

---

## Slide 5: In QA And In Progress

| Status | Ticket | Work |
| --- | --- | --- |
| In QA | `DMS-1096` | Verification harness for emitted auth database objects. |
| In Progress | `DMS-1163` | Slice 4: EdOrg-only PUT and POST-as-update. |
| In Progress | `DMS-1056` | Parent story for relationship CRUD authorization. |

**Speaker notes:**  
`DMS-1096` is validating the generated auth database objects, especially trigger behavior across PostgreSQL and SQL Server. `DMS-1163` is the highest-risk active slice because updates must authorize both stored and proposed values before any mutation, no-op success, or stale precondition response escapes.

---

## Slide 6: Open Non-Stretch Work

| Status | Ticket | Work |
| --- | --- | --- |
| Open | `DMS-1057` | Namespace-based authorization strategy. |
| Open | `DMS-1095` | People-involved relationship authorization for GET-many. |
| Open | `DMS-1099` | Security configuration ProblemDetails. |
| Open | `DMS-1158` | People-involved relationship authorization for CRUD. |
| Open | `DMS-1164` | People relationship auth core. |
| Open | `DMS-1165` | Relationship auth ProblemDetails hardening. |

**Speaker notes:**  
The remaining non-stretch work expands beyond EdOrg-only behavior. People relationships and namespace authorization are still open, and the final ProblemDetails work will make failure responses consistent and product-ready.

---

## Slide 7: Why The Work Matters Technically

- Authorization is now planned and executed in the relational backend, not only represented in claims metadata.
- SQL generation is being tested for both PostgreSQL and SQL Server.
- GET-many now filters unauthorized rows.
- GET-by-id, DELETE, and POST-create now deny unauthorized single-record operations.
- Failure metadata is being preserved so later ProblemDetails hardening has the right inputs.

**Speaker notes:**  
This is a shift from structural readiness to enforceable behavior. The same authorization concepts now flow through planning, SQL compilation, parameter binding, execution, and handler result mapping.

---

## Slide 8: Product And QA Impact

- Product can describe EdOrg relationship authorization as partially implemented, with clear operation boundaries.
- QA can validate concrete relational behavior for GET-many, GET-by-id, DELETE, and POST-create.
- Update behavior is not complete until `DMS-1163` lands.
- People, Namespace, Ownership, and View-based behavior should not be represented as complete.
- Open stretch-goal items are intentionally out of the current delivery signal.

**Speaker notes:**  
The key message is progress with boundaries. EdOrg relationship authorization is real for several operations, but the team should avoid implying full authorization parity until the remaining non-stretch stories land and QA completes verification.

---

## Slide 9: Next Work To Watch

- Finish `DMS-1163` so EdOrg-only relationship CRUD covers PUT and POST-as-update.
- Complete QA on `DMS-1096` to lock down emitted auth object verification.
- Start People relationship core and GET-many work: `DMS-1164` and `DMS-1095`.
- Start Namespace authorization: `DMS-1057`.
- Harden final failure response shape: `DMS-1099` and `DMS-1165`.

**Speaker notes:**  
The next milestone is closing the EdOrg-only CRUD path. After that, the work broadens to People and Namespace strategies and then tightens the user-facing failure contract.
