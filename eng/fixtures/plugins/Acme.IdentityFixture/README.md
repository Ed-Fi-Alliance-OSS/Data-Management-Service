# Acme.IdentityFixture

The reference identity provider the DMS identity contract describes, built as a plugin against the packed `EdFi.Api.Identity` and `EdFi.Api.Plugins` contracts.
It is a test fixture: an in-memory provider that implements every `IIdentityService` obligation so the DMS-owned identity HTTP surface can be proven through the real plugin path.
It logs nothing itself, because identifiers and person data are all it handles.

## Authority and namespace mapping

A namespace is this fixture's identity authority: the unit within which a UniqueId is unique.
Each namespace is configured with one or more contexts, and a context is a tenant plus the complete set of route qualifiers (for example `districtId` and `schoolYear`).
Every operation selects the namespace from the request context before it does anything else.

- Context equality follows the contract: tenant, qualifier names and qualifier values compare `OrdinalIgnoreCase`, a missing tenant equals only a missing tenant, and the qualifier set is order-independent.
  A context with a missing or extra qualifier selects nothing.
  The fixture copies the qualifiers into its own storage and never relies on the comparer of the dictionary the host passes in.
- A context that no namespace maps selects no namespace, and the operation answers `NotFound`.
- Several contexts may map to one namespace.
  A UniqueId issued through any of them resolves through all of them to the same person, which is how a shared namespace resolves an id consistently.
- Independent namespaces may reuse an id.
  Each namespace keeps its own person store, so an id in one namespace never returns another namespace's person.
  The fixture issues random ids, so a reused value is arranged with `Namespaces:{n}:SeedPersons`, which places a person under an explicit id in that namespace at first use.
- Namespace names compare `Ordinal`.
  One context may not map to two namespaces; the fixture throws on first use if configuration does.

## Grant policy

Every operation, results included, checks the client's grant on the selected namespace before any identity work.
An unknown namespace and a missing grant both answer `NotFound` with no lookup, no issuance and no job creation.
A grant never waives job ownership: `ResultsAsync` additionally requires the job to have been issued under the same tenant, qualifiers and client (see Jobs).

- **Policy source.**
  Configuration (`IdentityFixture:Grants`) by default.
  When `IdentityFixture:ControlBaseAddress` is set, the control channel is the policy source instead, and `Grants` is ignored.
- **Administration.**
  Configuration grants are administered by editing configuration and restarting the host; they are read at startup and never change at runtime.
  Control-channel grants are administered by whatever answers the control channel; in-process tests use `IdentityFixtureControlStub`.
- **Grants.**
  A grant is `{ ClientId, Tenant, Namespace }`.
  The client id compares `Ordinal`; the tenant compares `OrdinalIgnoreCase` with a missing tenant equal only to a missing tenant.
  `ClientId` of `*` is an explicit tenant-wide grant: it grants every client the host has already authorized, on that namespace and tenant.
  It must be configured; it is never inferred from a tenant existing or from the absence of client-specific grants.
- **Permitted operations.**
  A grant permits all five operations on its namespace.
  The fixture has no per-operation grants.
- **Cache.**
  Configuration answers are static and uncached.
  Control-channel answers are uncached too: every grant check, and so every operation on a mapped namespace, asks the control channel.
- **Revocation.**
  Revoking a grant on the control channel takes effect on the next operation.
  A revoked client's poll of a job it owns then answers `NotFound`.
  Revoking a configuration grant takes a restart.
- **Policy-source failure.**
  A control-channel failure (a non-success status, a malformed answer, a connection error or a timeout) throws.
  It is never treated as a grant, so DMS answers its sanitized `502` upstream-failure problem.

## Persons and the operations

- **Create** issues a 32-character hyphen-free ASCII alphanumeric id (a GUID in `N` form), stores the posted object in the namespace and answers the id as a bare JSON string.
  Every create issues a new id; a repeated create for the same identifying data issues a distinct duplicate.
- **Get-by-id** answers an `IdentityResponse`: `UniqueId`, every standard identifying attribute, `BirthLocation` and `Score`.
  An attribute the stored person does not have is `null`, `BirthLocation` is always an object with its four children (null when absent), `Score` is `null`, and every custom property the person was created with passes through.
  Ids compare `OrdinalIgnoreCase` within the namespace.
- **Find** answers positional `SearchResponses`, one entry per submitted id, each with `Responses` holding the matched person or nothing.
- **Search** answers positional `SearchResponses`, one entry per submitted object.
  A stored person matches when it agrees with at least half of the attributes the object supplies (standard and custom, `null` values and reserved names ignored); strings compare `OrdinalIgnoreCase`, `BirthLocation` agrees when every supplied child agrees.
  Each match carries `Score`, the agreeing share as a percentage from 0 to 100.
  Matches are ordered by score descending, then UniqueId.
  A search object whose only supplied attribute is `upstreamKey` is not a scored match; it is the exact reconciliation lookup (see Lost-create reconciliation).
  No match is an entry with an empty `Responses`, never `NotFound`.
- A successful synchronous find or search payload carries wire `Status: "Complete"`.

### Payload examples

Create, `200`:

```json
"3f2a9c1e5b7d4e0a8c6d1f9b2a4e7c03"
```

Get-by-id, `200` (the three `Fixture*` properties are the lifetime probe described below):

```json
{
  "UniqueId": "3f2a9c1e5b7d4e0a8c6d1f9b2a4e7c03",
  "LastSurname": "Rivera",
  "FirstName": "Ana",
  "MiddleName": null,
  "GenerationCodeSuffix": null,
  "SexType": null,
  "BirthDate": null,
  "BirthOrder": null,
  "BirthLocation": { "City": null, "StateAbbreviation": null, "InternationalProvince": null, "Country": null },
  "Score": null,
  "Favorite": "blue",
  "FixtureProviderId": "0b0a2e0e-6a7b-4a53-9a43-0d8e7c7b1d11",
  "FixtureScopeId": "5e1f6a3c-8d0a-4b8a-9d5e-6f1b0c2a7e44",
  "FixtureCapabilitiesReadCount": 1
}
```

Find or search, synchronous `200`:

```json
{ "Status": "Complete", "SearchResponses": [ { "Responses": [ { "UniqueId": "3f2a...", "Score": 100.0, "...": "..." } ] }, { "Responses": [] } ] }
```

Async find or search, `202` with `Location` to the results route; results poll, `200`:

```json
{ "Status": "Incomplete", "SearchResponses": [] }
```

and, once complete, the same payload a synchronous call would have carried.

## Lifetime probe

`FixtureIdentityService` is registered scoped and takes a scoped `FixtureRequestScope`.
Get-by-id echoes three custom properties: `FixtureProviderId` (the provider instance), `FixtureScopeId` (the scoped dependency instance) and `FixtureCapabilitiesReadCount` (how many times that provider instance's `Capabilities` getter has been read).
Two requests show different ids; one request shows the gate and the invocation sharing the same instance, so the read count is `1`.
The getter reads options only: it is inexpensive and does no I/O.

## Jobs

An async find or search accepts a job and answers a `32`-character hyphen-free alphanumeric token, which satisfies every usability rule of `IdentityAsyncResult.RequestToken`.

- **Ownership.**
  A job is bound to the complete issuing context: `Tenant`, `RouteQualifiers` and `ClientId`, under the equality rules above (`ClientId` is `Ordinal`, `TraceId` is excluded).
  A poll under any non-equivalent context, including a different client in the same tenant and qualifiers, answers `NotFound`.
- **Polling.**
  The first `PollsUntilComplete` polls of a job answer wire `Status: "Incomplete"` with an empty `SearchResponses`; every later poll answers the complete payload, and every repeated poll of a complete job answers the same payload.
  The count is polls, never time.
- **Retention.**
  A job is retained for the life of the process.
  The only other way a job ends is an explicit expiry on the control channel, after which the token answers `NotFound`.
- **In-memory only.**
  Persons and jobs live in process memory.
  A restart of the DMS container loses every person and every job, so a token issued before the restart answers `NotFound`.
  The fixture is single-replica only: a second replica has its own empty store.

## Lost-create reconciliation

The contract adds no idempotency key, so this fixture documents its own: a create that carries the custom property `upstreamKey` stores that key with the person, and a search object carrying only `upstreamKey` is the fixture's exact reconciliation lookup within the same namespace.
This is the fixture's own mechanism, not a host idempotency contract.
A scored demographic match, or a no-match that cannot establish authoritative absence, is not safe recovery.

- **Simulated lost response.**
  A create carrying `"~FixtureReturn": "lost-create"` records the issuance and then throws, as if the upstream acted and its answer never arrived.
  DMS answers its sanitized `502` upstream-failure problem, and the caller cannot know the id.
- **Reconciliation lookup.**
  A search object whose only non-null, non-reserved property is `upstreamKey` compares the value `Ordinal` against the `upstreamKey` of every person in the selected namespace.
  It answers the original issuance (the earliest person with that key) with `Score` `100`, and nothing else.
  A key that matches no person answers an entry with an empty `Responses`, which is authoritative absence only while the lookup is enabled.
  A search object that carries `upstreamKey` together with any other attribute is an ordinary scored match, not the lookup.
- **No reliable reconciliation.**
  With `ReconciliationLookup` set to `false` the lookup is disabled: an `upstreamKey`-only search answers `200` with an entry whose `Responses` is empty, whether or not a person with that key was issued.
  That empty answer cannot establish absence, so the documented client behavior is: no reliable reconciliation, stop for operator reconciliation, never retry the create.
- **Duplicates.**
  A retried create always issues a new id, so a retry after a lost response issues a duplicate.

## Configuration

Section `IdentityFixture`.
Environment variables use `__` for the section separator and a zero-based index for lists.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Capabilities` | comma-separated flags | all five (`Create,GetById,Find,Search,Results`) | The operations the fixture advertises. An unadvertised operation answers operation-unsupported `404` before the fixture is called. |
| `Namespaces:{n}:Name` | string | none | The namespace name. |
| `Namespaces:{n}:Contexts:{m}:Tenant` | string | unset | The tenant of the context; unset for single-tenant mode. |
| `Namespaces:{n}:Contexts:{m}:Qualifiers:{name}` | string | none | One route qualifier of the context. The set must be complete. |
| `Namespaces:{n}:SeedPersons:{k}:UniqueId` | string | none | A person that exists in the namespace from the start, under this exact id. The same id may be seeded in independent namespaces; twice in one namespace throws on first use. |
| `Namespaces:{n}:SeedPersons:{k}:Attributes:{name}` | string | none | One attribute of the seeded person, for example `LastSurname`. |
| `Grants:{n}:ClientId` | string | none | The client id, or `*` for an explicit tenant-wide grant. |
| `Grants:{n}:Tenant` | string | unset | The tenant the grant applies to. |
| `Grants:{n}:Namespace` | string | none | The namespace the grant applies to. |
| `PollsUntilComplete` | integer | `1` | How many polls answer incomplete before a job completes. |
| `ControlBaseAddress` | absolute http URI | unset | When set, the control channel is the policy source, the job-state source (expiry and failure) and the destination of the provider's event reports. |
| `ThrowAt` | `None`, `Factory`, `Constructor`, `Capabilities` or `Operation` | `None` | Makes the provider throw a nested exception whose outer and inner messages both carry the person-data sentinel `SENTINEL-Jane-Doe-1999-01-01`. See the trigger table. |
| `ReconciliationLookup` | boolean | `true` | Whether an `upstreamKey`-only search is the exact reconciliation lookup. `false` means no reliable reconciliation. |

Environment form, for a Docker deployment:

```
IdentityFixture__Namespaces__0__Name=ns-a
IdentityFixture__Namespaces__0__Contexts__0__Tenant=tenant-one
IdentityFixture__Namespaces__0__Contexts__0__Qualifiers__districtId=255901
IdentityFixture__Namespaces__0__Contexts__0__Qualifiers__schoolYear=2026
IdentityFixture__Grants__0__ClientId=*
IdentityFixture__Grants__0__Tenant=tenant-one
IdentityFixture__Grants__0__Namespace=ns-a
IdentityFixture__PollsUntilComplete=2
```

Configuration alone is enough to run every behavior in the table below that does not name the control channel.

### Control channel

Used only when `ControlBaseAddress` is set.
Plain HTTP, JSON bodies, no authentication; it is a test seam and must never be exposed.
The fixture's HTTP client has its default loggers removed, so request URLs never reach host logs.

| Request | Answer |
| --- | --- |
| `GET policy?clientId={client}&namespace={namespace}[&tenant={tenant}]` | `200` with `{ "granted": true }` or `{ "granted": false }`. Any other status, or a body without a boolean `granted`, is a policy-source failure. |
| `GET jobs/expiry?token={token}` | `200` with `{ "expired": true }` or `{ "expired": false }`. `true` ends the job and the poll answers `NotFound`. |
| `GET jobs/state?token={token}` | `200` with `{ "failed": bool, "failNextPoll": bool }`. `failNextPoll` is consumed by the read, so only the next poll sees it. Any other status, or a body without both booleans, is a failure to obtain the job state and throws. |
| `POST events` with `{ "operation": "...", "kind": "...", "token": "..." }` | Any `2xx`. A report from the provider, never read back. `operation` is `create`, `getById`, `find`, `search` or `results`. `kind` is `invocation` (sent first, before the grant is checked), `lookup` (sent only after the namespace and grant checks passed, so never for a denied call), `issuance` (a UniqueId was issued), `job` (an async job was created) or `awaiting-cancellation` (the `cancel-person` variant is about to wait for its cancellation token; sent without that token, so cancelling the request cannot interrupt the report). `token` is present only on a `results` invocation or lookup, and is the request token exactly as the provider received it. A report carries no person data and is never logged. |

`IdentityFixtureControlStub` implements them for in-process tests: `Grant`, `Revoke`, `FailPolicySource`, `ExpireJob`, `FailJob`, `FailNextPoll`, `PolicyLookupCount`, `RequestPaths` (every request except an event report), `Events`, `InvocationCount`, `LookupCount`, `IssuanceCount`, `JobCount`, `ReceivedResultTokens`, `WaitForEventAsync` (completes once a matching report has arrived) and `Reset`.

## Fixture triggers

A trigger is a reserved value or property that selects a behavior over ordinary HTTP.
Every reserved name starts with `~`, which no real identifying attribute or issued UniqueId starts with, so none can collide with real data; reserved properties never appear in a response, and no trigger is ever echoed.
A trigger takes effect only for an authorized call, after the namespace and grant checks, so a denied call still answers `NotFound` with no lookup, issuance or job creation.
The HTTP statuses below are what the DMS host is expected to map the fixture's result to; the fixture itself only returns statuses or throws.

A `<variant>` names a deliberate result and is compared `OrdinalIgnoreCase`.
An unknown variant makes the fixture throw, which DMS answers as the `502` upstream-failure problem.
Where a variant does not apply to an operation (`success-both` has no token to carry on create, get-by-id or results, and `lost-create` and `cancel-person` apply to create only) it is unknown there.

### Variants

| Variant | Result the fixture returns | Expected from create, get-by-id, find, search | Expected from results (job variant) |
| --- | --- | --- | --- |
| `incomplete` | `Incomplete` with an incomplete payload | `502` provider-contract-violation | `200` incomplete payload with a `Location` to the poll URL (same as the normal first poll) |
| `jobfailed` | `JobFailed`, no payload | `502` provider-contract-violation | `502` job-failed |
| `jobfailed-payload` | `JobFailed` with a payload and errors supplied (find and search also supply a token) | `502` provider-contract-violation | `502` job-failed, payload and errors ignored |
| `success-nopayload` | `Success`, no payload and no token | `502` provider-contract-violation | `502` provider-contract-violation |
| `success-neither` | Same as `success-nopayload`, named for the find/search rule | `502` provider-contract-violation | `502` provider-contract-violation |
| `success-both` | `Success` with both a payload and a token (find and search only) | find, search: `502` provider-contract-violation | unknown, so `502` upstream-failure |
| `throw-person` | Throws an exception whose message contains person-shaped text (`Jane Doe born 1999-01-01` and the sentinel) | `502` upstream-failure | `502` upstream-failure |
| `invalid-createFieldError` | `InvalidProperties`, one error at `$.firstName`: `First name is required.` | `400` data-validation-failed with `validationErrors` `$.firstName` | `400`, same |
| `invalid-searchItemError` | `InvalidProperties`, one error at `$[2].firstName`: `First name is required for item 2.` | `400` data-validation-failed with `validationErrors` `$[2].firstName` | `400`, same |
| `invalid-pathlessError` | `InvalidProperties`, one error with a blank (`""`) path: `The request could not be evaluated.` | `400` bad-request with that message in `errors` | `400`, same |
| `invalid-pathlessNullError` | The same with a null path; projects identically to the blank path | `400` bad-request with that message in `errors` | `400`, same |
| `invalid-twoMessagesOneKey` | `InvalidProperties`, two errors at `$.firstName`: `First name is required.` and `First name must not exceed 75 characters.` | `400` data-validation-failed, both messages under one `$.firstName` key | `400`, same |
| `lost-create` | Create only: see Lost-create reconciliation | `502` upstream-failure after the issuance is recorded | not applicable |
| `cancel-person` | Create only: reports `awaiting-cancellation` on the control channel, waits until the operation's cancellation token is cancelled, then throws an `OperationCanceledException` of that token whose message and inner exception both contain person-shaped text (`Jane Doe born 1999-01-01` and the sentinel). A token that is never cancelled waits forever, so it is meant for an HTTP request the client cancels. | No response, because the client cancelled the request; DMS logs the exception at Debug only and rethrows a cancellation that carries none of its text | not applicable |

### Triggers

| Trigger | Where | Effect |
| --- | --- | --- |
| `~fixture:async` | An element of a find array | The find becomes an async job: `202` with `Location` to the results route. The trigger element itself yields an empty `Responses` group, so the groups stay positional. |
| `"~FixtureAsync": true` | A property of a search object | The search becomes an async job, as above. |
| `"~FixtureReturn": "<variant>"` | A property of a create body | The create returns the variant instead of issuing a person. `lost-create` issues, then throws. `cancel-person` issues nothing, waits for the request's cancellation, then throws. |
| `~fixture-return-<variant>` | A get-by-id path id | The get-by-id returns the variant. |
| `~fixture:return:<variant>` | An element of a find array | The whole find returns the variant (the first such element wins). The element yields an empty `Responses` group. |
| `"~FixtureReturn": "<variant>"` | A property of a search object | The whole search returns the variant (the first such object wins). |
| `~fixture:results:<variant>` | An element of a find array | The find becomes an async job whose every authorized, unexpired, unfailed poll answers the variant instead of incomplete then complete. Ownership is still checked first. |
| `"~FixtureResults": "<variant>"` | A property of a search object | The same for a search. |
| `~fixture:token:<value>` | An element of a find array | The find becomes an async job whose `RequestToken` is exactly `<value>`, byte for byte, with nothing trimmed or escaped. Any earlier job under the same token is replaced. `<value>` may be any text, so a test can request a token that needs escaping (for example `a%b c` plus a non-ASCII letter), exactly `.` or `..`, an escaped form over 1024 characters, or one under 1024 that overflows a composed poll path. An unusable token is `502` provider-contract-violation with no `Location`; a usable one is `202`, and a later poll receives the same string (the stub's `ReceivedResultTokens` records it). |
| `"~FixtureToken": "<value>"` | A property of a search object | The same for a search. |
| `ThrowAt=Factory` | Configuration | Registers the provider through a factory that goes through the activation gate. The gate permits only the first activation, which is the host's startup probe, and throws the nested sentinel exception on every later one. Startup reaches `Ready`; every request-time activation fails as `500` provider-configuration. Registered scoped. |
| `ThrowAt=Constructor` | Configuration | The same through the provider's constructor. |
| `ThrowAt=Capabilities` | Configuration | The `Capabilities` getter throws the nested sentinel exception. The startup probe never reads it, so startup reaches `Ready`; every request fails as `500` provider-configuration. |
| `ThrowAt=Operation` | Configuration | Every authorized operation throws the nested sentinel exception: `502` upstream-failure. A denied call still answers `404`. |
| `ReconciliationLookup=false` | Configuration | Disables the reconciliation lookup: no reliable reconciliation (see above). |
| `PollsUntilComplete` | Configuration | Polls that answer incomplete before the job completes. |
| `ControlBaseAddress` | Configuration | Switches the policy and job-state sources to the control channel and enables event reports. |
| `FailNextPoll(token)` | Control stub | The next poll of the token throws, after ownership and expiry checks. DMS answers `502` upstream-failure with no terminal conclusion; every later poll answers normally. |
| `FailJob(token)` | Control stub | The job failed terminally: every authorized poll while the job is retained answers `JobFailed` with no payload, which DMS maps to `502` job-failed. Ownership is checked first, so another client still gets `404`. |
| `ExpireJob(token)` | Control stub | After this the token answers `NotFound` (`404`) and the job is dropped. |
| `Revoke(client, tenant, namespace)` | Control stub | The client's next operation on that namespace, a poll of a job it owns included, answers `NotFound` (`404`), because every grant check asks the control channel. |
