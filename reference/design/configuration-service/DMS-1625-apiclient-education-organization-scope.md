---
jira: DMS-1625
---

# DMS-1625 — Scope an API client to a subset of its Application's education organizations

| Field | Value |
|---|---|
| Jira | [DMS-1625](https://edfi.atlassian.net/browse/DMS-1625) (epic) and child stories [ADMINAPI-1540](https://edfi.atlassian.net/browse/ADMINAPI-1540) (created as DMS-1638), [DMS-1639](https://edfi.atlassian.net/browse/DMS-1639), [DMS-1640](https://edfi.atlassian.net/browse/DMS-1640), [DMS-1641](https://edfi.atlassian.net/browse/DMS-1641), [DMS-1642](https://edfi.atlassian.net/browse/DMS-1642) |
| Source design | [Management API APIClient EducationOrganizations](https://edfi.atlassian.net/wiki/spaces/BD/pages/2911076354) (Confluence, version 13, read 2026-10-26) |
| Status | Records the agreed behavior, the decisions the child stories made (section 6) and the issues still open. |
| Related designs | [DMS-1365](./DMS-1365-keycloak-client-provisioning-compensation.md) (provider client creation and compensation), [DMS-1529](./DMS-1529-apiclient-identifier-uniqueness.md) (client identifier uniqueness), [DMS-1586](./DMS-1586-management-api-problem-type-prefix.md) (problem type prefix) |

The Confluence design is the source of truth for the Management API contract. This
document restates the parts the Configuration Service implements, maps them to child
stories, and lists where the stories and the Confluence design don't yet agree.

## 1. Context

In ODS/API, `ApiClientApplicationEducationOrganizations` in EdFi_Admin let an API
client be limited to some of its Application's education organizations. SEAs rely on
this: they keep one Application per vendor product (for example, a SIS used by 200
LEAs) and issue one key/secret per LEA, often to the LEA's Security Admin rather than
the vendor.

Before 8.1, the Configuration Service couldn't express this:

- The Management API v3 client request schemas (`addApiClientRequest`,
  `editApiClientRequest`) had no education organization field.
- A client's education organization claim was a copy of the Application's full list,
  taken when the client was created or updated.
- `PUT /v3/applications/{id}` refreshed only the Application's first client, sending
  it the Application's list, and compensated in the provider if the database update
  then failed. Other clients kept whatever list they had at their own last update.
- `POST /v3/applications` always created a default client covering every education
  organization on the Application.

So a key issued for one LEA worked for every LEA on the vendor's Application. The
workaround, one Application per vendor-and-LEA pair, multiplied Application
management: changing a vendor's claim set meant updating every Application.

Goals: let a client be limited to specific education organizations of its
Application, let an Application be created without a default client, and keep both
additive so Admin App and Education Analytics behave as they did in 8.0.

## 2. Behavior

### 2.1 Client education organization rules (`POST` and `PUT /v3/apiClients`)

Each client has a stored flag, `inheritsApplicationEducationOrganizations`, and a
list. An inheriting client's effective list is its Application's current list and
follows later changes. A narrowed client's effective list is its own list. It loses
education organizations removed from the Application but never gains added ones.

"The Application's list" below means the current list of the client's Application;
on a PUT that moves the client, the destination Application's list.

| Request contains | Result |
|---|---|
| A list, no flag | Narrowed to that list, even if it equals the Application's list. |
| `false` with a list | Narrowed to that list. |
| `true`, no list | Inherits. |
| `true` with a list equal to the Application's list | Inherits; the list is ignored (a GET-then-PUT round trip of an inheriting client is a no-op). |
| `true` with any other list | 400 |
| `false` without a list | 400 |
| An empty list `[]` | 400 |
| A list containing an education organization not on the Application | 400, error on `educationOrganizationIds` |
| Neither field, POST | Inherits. |
| Neither field, PUT on the same Application | Current flag and list are kept, so older integrations can't wipe a client's narrowing. |
| Neither field, PUT that moves the client (new `applicationId`) | Inheriting: takes the destination list and keeps inheriting. Narrowed: keeps its list and its rows are repointed in the same transaction if every id is on the destination; otherwise 409 `conflict:education-organization-scope` and nothing changes. |

Further rules:

- Duplicate ids in a list are removed silently (Confluence; not restated in DMS-1640).
- A narrowed client sent `true` on PUT (no list, or a list equal to the
  Application's) switches back to inheriting.
- All 400s use `urn:ed-fi:management-api:bad-request:data` with the error on the field
  concerned.
- `GET /v3/apiClients` and `GET /v3/apiClients/{id}` return the flag and the effective
  `educationOrganizationIds`. The list is never `null`; `[]` means no education
  organization access. A narrowed client reaches `[]` only through Application
  removals; it stays enabled, so namespace-based access still works.
- Token claims are built from the effective list on client create and client update,
  for both Keycloak and OpenIddict. A credential reset changes only the secret: it
  keeps the client's list and the claims the provider holds, and doesn't complete a
  pending client (O-4).
- Changes take effect at the client's next token. Tokens already issued keep their
  claims until they expire. This feature does not revoke tokens.

### 2.2 Applications with zero clients and `createDefaultCredentials`

| Operation | Behavior |
|---|---|
| `POST /v3/applications` | Optional `createDefaultCredentials`, default `true` (the 8.0 behavior, unchanged). With `false`: no provider client is created, `key` and `secret` are `null`, and `enabled` or `dataStoreIds` in the request returns 400. |
| `GET /v3/applications[/{id}]` | `enabled` is `true` only with at least one client and every client approved; `false` with zero clients (8.0: `true`). `dataStoreIds` is the union of the clients' data stores, `[]` with zero clients. |
| `PUT /v3/applications/{id}/reset-credential` | 409 `conflict` with zero clients (8.0: 404) or more than one client. |
| `DELETE /v3/apiClients/{id}` | Deleting the last client leaves the Application, which can still be read, updated, deleted and given new clients. |
| `PUT /v3/applications/{id}` | Works with zero clients (8.0: 404). Owned by DMS-1642, not DMS-1641. |

### 2.3 Application updates

`PUT /v3/applications/{id}` removes deleted education organizations from every client
and adds new ones only to inheriting clients. The affected clients, which must be
updated in the identity provider, are:

| Change | Affected clients |
|---|---|
| Application name or claim set | Every client |
| Education organization removed | Every client that loses it (every inheriting client, plus narrowed clients that had it) |
| Education organization added | Every inheriting client |

## 3. Storage and migration (DMS-1639)

Both engines get the same objects:

- `dmscs.ApiClientEducationOrganization (ApiClientId, ApplicationId, EducationOrganizationId)`
  with a foreign key from `(ApplicationId, EducationOrganizationId)` to
  `ApplicationEducationOrganization` with `ON DELETE CASCADE`, so removing an
  education organization from an Application removes it from every client in the
  database.
- An index on the education organization column, so that cascade stays fast.
- `dmscs.ApiClient.InheritsApplicationEducationOrganizations`, not null, default
  `true`. The flag is stored rather than derived: a client deliberately narrowed to
  every current education organization must not pick up new ones.
- A nullable sync-pending timestamp on `dmscs.ApiClient`,
  `IdentityProviderSyncPendingSince`, set when an Application change commits (and by
  the upgrade, below) and cleared when that client's provider update succeeds, unless
  a claim set rename committed after the claim set name it was sent was read.

A client's rows are tied to its current Application by the second foreign key
Confluence proposes (O-1): `(ApiClientId, ApplicationId)` to a new unique
`ApiClient (Id, ApplicationId)`, with `NO ACTION`. SQL Server rejects two cascade
paths into one table (Application → education organization rows → client rows, and
Application → ApiClient → client rows), so that key can't cascade. With it:

| Operation | Database behavior |
|---|---|
| Remove an education organization from an Application | Cascade removes it from every client. |
| Delete an Application (directly, or through a vendor delete) | Cascade through the education organization rows removes all client rows. SQL Server Application and vendor delete tests cover this, since `ApiClient` is also cascade-deleted from `Application`. |
| Delete one client | Code deletes the client's rows, then the client, in one transaction. |
| Move a client | Code deletes its rows, changes `ApplicationId`, then inserts rows for the destination, in one transaction. Skipping the first step is rejected by the database. |

Application save (`ApplicationRepository.SaveApplication` in both backends) applies
only the differences to the Application's list, rather than deleting and reinserting
it. With the cascade, delete-and-reinsert would wipe every client's rows on each save.

**Upgrade.** Every existing client is set to inherit, and its rows are copied from its
Application's current list. No client could be narrowed before this feature, and this
matches Admin API, which links every client to every education organization. The
upgrade doesn't touch Keycloak or OpenIddict and can't read their claims, so it also
marks every existing client sync-pending (O-5). The mark is set only by the run that
adds the column, so a rerun never re-marks clients already synchronized.

**Access consequence of the upgrade.** Before 8.1 only the first client was refreshed
by an Application update, so a secondary client's token claim can differ from its
Application's list, holding education organizations since removed or missing ones
since added, or an earlier name or claim set. After the upgrade, `GET` reports the
Application's list for that client while its provider claim is unchanged. Because
every existing client is pending, the Application's first PUT after the upgrade, even
an unchanged one, sends every client its claims (section 4), and a client PUT
finishes a single client. A client whose provider client no longer exists stays
pending, and each update of its Application returns 502 naming it until the client is
deleted.

## 4. Database-first sync and recovery (DMS-1642)

Client claims live in Keycloak or OpenIddict, so an Application update must update
each affected client there in the same request. A claim set change on a 200-client SEA
Application updates 200 provider clients.

**Measurement gate.** Before building the loop, DMS-1642 times a claim set change
across about 200 clients on Keycloak and OpenIddict to confirm a synchronous loop is
acceptable. If it isn't, the design returns to review.

Flow for `PUT /v3/applications/{id}`, replacing the 8.0 single-client provider-first
update and compensation:

1. Validate everything that can be validated (references, duplicate name) before any
   provider call. A validation failure changes nothing.
2. In one transaction, save the Application and mark every affected client
   sync-pending.
3. Update each affected client in the identity provider; clear its marker on success,
   only while the Application still has the claim set name the provider was sent.
4. All succeed: 204. Some fail: 502 `urn:ed-fi:management-api:bad-gateway:partial-sync`,
   with `detail` stating that the Application change is saved and an `apiClientIds`
   extension member listing pending clients. Pending clients are logged as warnings.
5. Resending the request retries the pending clients and returns 204 once none remain.

Other paths that finish a pending client (O-4): any later Application PUT sends every
pending client, not only an identical resend, and a successful `PUT /v3/apiClients/{id}`
finishes the client even with no other change. A client credential reset does not.

Until a pending client is synced, its token can still hold a removed education
organization, and the client gets no signal. Recovery depends on a later operator
request, so the 502 body and warning logs must be clear. A scheduled background retry
is out of scope.

Other workflows keep their 8.0 models: client create and Application create stay
provider-first with compensation (DMS-1365); client update and reset are unchanged
apart from the claim content. Client create, update, delete and reset take the
Application's aggregate lock, which the Application PUT holds through its save and
provider updates, so a client created during an Application PUT is provisioned from
the saved Application rather than missing the save's marking.

**Application delete.** CMS already deletes each client in the provider before the
database rows, treats an already-absent client as success, and leaves the Application
and remaining clients in place on failure (`ApplicationModule.cs`, `Delete`), so a
resend converges. The spec documents this as 502 `bad-gateway` with a resend
guarantee. See open issue O-3 for ownership.

## 5. Child stories

| Story | Owner and scope | Depends on | Verification |
|---|---|---|---|
| [ADMINAPI-1540](https://edfi.atlassian.net/browse/ADMINAPI-1540) (was DMS-1638) | **External**: `management-api-3.0.0.yaml` in Ed-Fi-API-Specifications, reviewed and merged by the Admin API team. New request and response fields, the rules table, zero-client semantics, per-endpoint 409/502 codes, problem types `conflict:education-organization-scope`, `conflict:unresolved-reference`, `bad-gateway`, `bad-gateway:partial-sync`, and resend guarantees for PUT and DELETE `/v3/applications/{id}`. | — | Every design change maps to the YAML and nothing else changes; OpenAPI validation passes. |
| [DMS-1639](https://edfi.atlassian.net/browse/DMS-1639) | Deploy scripts and repositories for both engines (section 3). | — | Scripts run on new and upgraded databases; upgrade leaves every client inheriting with rows equal to its Application's list; an unchanged save leaves client rows untouched; a removal cascades and an addition changes no client rows; repository integration tests on PostgreSQL and SQL Server. |
| [DMS-1640](https://edfi.atlassian.net/browse/DMS-1640) | `/v3/apiClients` rules, moves, GET fields, claims from the effective list, security configuration docs and the SEA per-LEA workflow (section 2.1). | DMS-1639; spec content agreed | A test per rules-table row on both engines, with both providers where a token is involved; a client narrowed to one LEA gets only that LEA in its token and DMS returns 403 for another; a narrowed client never gains added ids; reset keeps the list; a fitting move repoints rows, a non-fitting one returns 409 and changes nothing. |
| [DMS-1641](https://edfi.atlassian.net/browse/DMS-1641) | `createDefaultCredentials` and zero-client behavior (section 2.2), docs and changelog for `enabled` becoming `false` with zero clients. | Spec content agreed; no storage dependency, so it can start now | `false` creates no client and returns null key and secret; omitting the flag behaves as today; each zero-client behavior tested on both engines. |
| [DMS-1642](https://edfi.atlassian.net/browse/DMS-1642) | Measurement gate, then the database-first Application update (sections 2.3 and 4), zero-client PUT, docs and changelog for secondary-client catch-up. | DMS-1639, DMS-1640; spec content agreed. Measurement and the claim set restructuring can start during DMS-1640. | A claim set change reaches every client's claims; a removal reaches every affected client, verified by DMS 403; an injected provider failure partway saves the database, lists pending clients and a resend finishes them; validation fails before any provider call; both engines and both providers. |

The endpoint stories (DMS-1640, DMS-1641 and DMS-1642) require the spec content to be
agreed before implementation; DMS-1639's storage work doesn't. The spec PR must be
merged before the feature is released. ODS Admin API, EdFi_Admin and Admin App changes
are tracked outside this epic.

## 6. Issues between the sources

These were raised where the stories and the Confluence design didn't agree. Decided
issues name where the decision is recorded; the rest are still open and must be
settled by the named story or by the maintainers.

- **O-1 Client-to-Application linkage (DMS-1639). Decided:** the `NO ACTION`
  composite key Confluence proposes, in both engines' 0038 scripts, with the
  Application and vendor delete cascades covered on SQL Server (section 3).
- **O-2 Partial-sync contract approval.** Confluence marks the 502
  `bad-gateway:partial-sync` status, type and body as "proposed, needs approval" and
  lists it under decisions needed. DMS-1642 treats it as settled. It needs approval
  through ADMINAPI-1540 before DMS-1642 implements it.
- **O-3 DELETE `/v3/applications/{id}` ownership.** ADMINAPI-1540 must document its
  502 and resend guarantee, but no CMS story owns verifying CMS against that contract
  (DMS-1642 scopes only PUT). The current code already matches the guarantee's shape,
  though some failures (a thrown provider exception, an unknown provider result)
  return 500 rather than 502. Decide whether that is in scope, and which story owns
  the test.
- **O-4 Other paths that finish pending clients. Decided:** as Confluence says, any
  later Application PUT and a client PUT finish a pending client, and a client
  `reset-credential` does not (sections 2.1 and 4). DMS-1642 implemented both paths.
- **O-5 Catch-up after upgrade. Decided:** the upgrade marks every existing client
  sync-pending, so the first Application PUT after the upgrade, even an unchanged
  one, refreshes every client (section 3).
- **O-6 Background retry release.** DMS-1642 says the scheduled retry is planned for
  8.2; Confluence lists the target release and whether the sweep ships with it as
  undecided. It isn't documented as behavior until it ships.
- **O-7 Admin API and EdFi_Admin.** Whether Admin API supports narrowing in this
  release depends on allowing one new EdFi_Admin column under the database freeze.
  Outside this epic, but the shared spec must not promise what Admin API won't
  implement.
