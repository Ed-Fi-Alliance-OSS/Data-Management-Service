# DMS-1642 — Measurement: claim set change applied to every client of one Application

| Field | Value |
|---|---|
| Jira | [DMS-1642](https://edfi.atlassian.net/browse/DMS-1642), scope item "First, measure" |
| Recorded | 2026-10-27, branch `DMS-1642` at `765c002185cd10315ada284c2418b9b7bb11a9a0` (DMS-1640 base) plus the probe |
| Probe | `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Measurement/ApplicationUpdateFanoutMeasurementTests.cs` |
| Status | Evidence for the synchronous-loop decision, then for the finished endpoint (see [Finished endpoint](#finished-endpoint-task-4a)). |

## Question

DMS-1642 replaces the Application PUT's single-client provider update with an update of
every affected client. The Jira scope asks, before that loop is built, how long a claim set
change takes across about 200 clients on Keycloak and on OpenIddict, to confirm that a
synchronous loop inside the request is acceptable.

## Method

The probe is an opt-in NUnit fixture (category `ApplicationUpdateFanoutMeasurement`). It is
ignored unless `CONFIGURATION_SERVICE_FANOUT_MEASUREMENT=true`, and refuses to run unless the
suite targets an isolated deployment (`CONFIGURATION_SERVICE_E2E_API_URL` set and not the stock
stack's 8081) whose container reports `keycloak` or `self-contained`.

Setup (untimed), through the Configuration Service API: one vendor, two data stores, and one
Application with 200 education organizations, then 199 `POST /v3/apiClients`, giving 200 clients:

- 190 narrowed to one education organization each; 10 (every twentieth, including the
  Application's first client) inherit all 200, so their claim carries 200 ids.
- 4 unapproved (every fiftieth from 25); 196 approved.
- About a third hold two data stores; the rest one.

Timed: for each change, one `IIdentityProviderRepository` instance, as one PUT request would
resolve, calls the real `UpdateClientAsync` for every client in turn with the client's own UUID,
name, the new claim set, its effective education organization claim, its data stores and its
approval. Only that call is inside the per-client stopwatch; the run total is the loop's wall
time. Three changes, each to a different claim set:

1. Cold: the first change after setup, to a claim set with no provider scope yet.
2. Repeat to another new claim set (first client again creates the scope).
3. Repeat back to the original claim set, whose scope already exists.

Verification (untimed), for every client, before the first change and after every change:

- The Configuration Service still returns the client's UUID, key, approval, education
  organizations and data stores unchanged.
- The provider's stored client holds the new claim set scope and not the previous one, the
  expected `educationOrganizationIds` and `dataStoreIds` values, Keycloak `enabled` equal to
  approval, and (OpenIddict) the same stored secret hash as before the first change.
- Each approved client obtains a new token with its original secret from the provider's own
  token endpoint (Keycloak realm endpoint; Configuration Service `/connect/token` for
  OpenIddict). The token names the client, carries the new claim set and otherwise exactly the
  scopes it carried at baseline, the effective education organizations, the data stores and the
  vendor namespace prefixes.
- Each unapproved client, presenting its valid secret, is refused with exactly `401
  invalid_client`, classified from the status and OAuth error code alone. Keycloak refuses a
  disabled client that way; the Configuration Service's OpenIddict endpoint answers an unapproved
  client's valid secret with `invalid_client` and a wrong secret with `unauthorized_client`. A
  server error or any other refusal fails verification rather than counting as one.

Any update result other than `Success` with the same UUID, or any verification mismatch, fails
the fixture. No elapsed-time assertion is made. The JSON report holds only identifiers, timings
and outcomes; it is refused if any secret or token issued during the run would appear in it.

### Negative control

To show that a skipped or incomplete update cannot pass, the probe was run once (OpenIddict on
PostgreSQL, `CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_CLIENTS=20`, isolation
`dms-1642-probe-neg`) with a temporary, uncommitted edit that sent client 7 its previous claim
set during the cold change while still counting the call's `Success`. The fixture failed:
`It_verifies_every_client_after_every_run` reported client 7 three times, for the provider's
stored scopes, the token holding a scope other than the expected claim set, and the token lacking
the expected claim set. Every other test passed, including `It_updates_every_client_in_every_run`,
so verification, not the result count, caught it. The edit was then reverted.

The 200-client runs below predate the exact-refusal check; before it, any non-200 answer counted
as an unapproved client's refusal. The timed loop is unchanged by it. The strengthened check was
then run with `CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_CLIENTS=30` (which includes one
unapproved client) on Keycloak (isolation `dms-1642-probe-rkc`) and on OpenIddict with PostgreSQL
(`dms-1642-probe-roi`): every client verified at baseline and after all three changes, and the
unapproved client was refused with `401 invalid_client` each time.

## Environment

- Apple M5 Max (18 cores, 64 GB), macOS 27.0.1, Docker Desktop 29.8.0 (18 CPUs, 16 GB to the VM),
  .NET SDK 10.0.401.
- Stacks started by `eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1`, one at a
  time, under ticket-owned isolation names (`dms-1642-probe-kc`, `dms-1642-probe-oi-pg`,
  `dms-1642-probe-oi-ms`), each removed by the launcher afterwards. Configuration Service image
  built from this tree; Keycloak `quay.io/keycloak/keycloak:26.1`; PostgreSQL `postgres:18.6-alpine`;
  SQL Server `mcr.microsoft.com/mssql/server:2025-latest`.
- The probe runs in the test process on the host. It reaches Keycloak and the database through
  the stacks' published loopback ports (Keycloak 8145, PostgreSQL 5536, SQL Server 1536), where the
  Configuration Service would reach them over the Docker network.

Commands (from the repository root; output directory is any writable path):

```shell
CONFIGURATION_SERVICE_FANOUT_MEASUREMENT=true CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_OUTPUT=<dir> \
  pwsh eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -IdentityProvider keycloak \
  -IsolationName dms-1642-probe-kc -E2ETestFilter "TestCategory=ApplicationUpdateFanoutMeasurement"

CONFIGURATION_SERVICE_FANOUT_MEASUREMENT=true CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_OUTPUT=<dir> \
  pwsh eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -IdentityProvider self-contained \
  -IsolationName dms-1642-probe-oi-pg -E2ETestFilter "TestCategory=ApplicationUpdateFanoutMeasurement"

CONFIGURATION_SERVICE_FANOUT_MEASUREMENT=true CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_OUTPUT=<dir> \
  pwsh eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -EnvironmentFile ./.env.config.mssql.e2e \
  -IdentityProvider self-contained -IsolationName dms-1642-probe-oi-ms \
  -E2ETestFilter "TestCategory=ApplicationUpdateFanoutMeasurement"
```

`CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_CLIENTS` changes the client count (default 200).

## Results

200 clients per run. Every run: 200 attempted, 200 succeeded, 0 failed, 0 verification
mismatches; baseline verification also clean. Times in milliseconds; "total" is the loop's
wall time for all 200 clients.

### Keycloak 26.1 (PostgreSQL datastore, which the Keycloak update does not touch)

| Change | Total | First client | Min | Mean | p50 | p90 | p95 | p99 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Cold, new scope | 4,356 | 75.3 | 14.8 | 21.8 | 19.8 | 29.6 | 34.1 | 38.9 | 75.3 |
| Repeat, new scope | 3,325 | 24.9 | 13.1 | 16.6 | 14.9 | 18.4 | 21.0 | 29.0 | 182.6 |
| Repeat, existing scope | 2,902 | 19.2 | 12.6 | 14.5 | 14.1 | 16.2 | 18.2 | 20.5 | 23.3 |

Setup (create 200 clients and read them back) took 65 s.

### OpenIddict on PostgreSQL 18.6

| Change | Total | First client | Min | Mean | p50 | p90 | p95 | p99 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Cold, new scope | 422 | 14.1 | 1.7 | 2.1 | 2.0 | 2.2 | 2.4 | 4.8 | 14.1 |
| Repeat, new scope | 360 | 2.3 | 1.6 | 1.8 | 1.8 | 1.9 | 2.0 | 2.2 | 2.4 |
| Repeat, existing scope | 401 | 2.6 | 1.8 | 2.0 | 2.0 | 2.1 | 2.4 | 2.6 | 4.3 |

Setup took 61 s.

### OpenIddict on SQL Server 2025

| Change | Total | First client | Min | Mean | p50 | p90 | p95 | p99 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Cold, new scope | 739 | 19.0 | 3.1 | 3.7 | 3.5 | 4.0 | 4.4 | 6.7 | 19.0 |
| Repeat, new scope | 663 | 5.1 | 2.9 | 3.3 | 3.3 | 3.5 | 3.8 | 5.0 | 5.4 |
| Repeat, existing scope | 641 | 4.4 | 2.8 | 3.2 | 3.0 | 3.3 | 3.7 | 5.1 | 12.4 |

Setup took 112 s.

The first client of a change to a new claim set includes the provider's scope creation
(Keycloak `CheckAndCreateClientScopeAsync`; an OpenIddict scope row), and the cold run's first
client also includes connection and JIT warm-up. Neither adds more than about 60 ms to a run.

## Conclusions

- A sequential loop over 200 clients took at most 4.4 s (Keycloak, cold) and under 0.75 s
  (OpenIddict on either database) on this machine. Per-client cost is steady, so the loop scales
  linearly: Keycloak is roughly 15–22 ms per client, OpenIddict 2–4 ms.
- At these rates a synchronous loop inside the PUT request appears feasible for the ~200-client
  case the design names, well inside common client and proxy timeouts (for example the 100 s
  `HttpClient` default). Extrapolated linearly, 1,000 Keycloak clients would take roughly
  15–22 s, which is the range where a deployment's proxy timeout could start to matter.
- The decision on the synchronous contract stays with the design owners; this records the
  evidence only.

## Limits

- One run per configuration, on one developer machine; CI runners and production deployments
  will differ. The probe reaches Keycloak and the database through Docker Desktop's published
  ports rather than the Docker network, which adds a hop the Configuration Service does not pay.
- Only the provider calls are timed. The loop the ticket builds also marks and clears each
  client's sync-pending state in the database; that cost is measured separately, through the
  finished endpoint, in [Finished endpoint](#finished-endpoint-task-4a).
- Keycloak's update lists every realm client scope while resolving the target scope, so its cost
  grows with the number of client scopes in the realm. The isolated realm held only the stack's
  standard scopes plus those this run created (its count was not recorded); a realm with many
  claim sets would be slower per client.
- The four unapproved clients cannot obtain tokens, so their verification is the provider's
  stored state (scope, claims, Keycloak `enabled=false`) and the Configuration Service's record.
  On Keycloak, credential retention is shown by approved clients' tokens; Keycloak exposes no
  credential digest to compare for the unapproved ones.
- The negative control ran only on OpenIddict with PostgreSQL. The token checks are the same for
  both providers, but Keycloak's stored-scope read was not separately shown to flag a stale client.
- Only newly issued tokens are checked. Tokens issued before a change keep their claims until
  they expire.

## Finished endpoint (Task 4a)

Recorded 2026-10-27 on the same machine, at `7121db50` plus the probe change, after the endpoint
work landed (`a71892b3` atomic save and marking, `3ec358fb` sequential synchronization, `4b6f3990`
marker completion on client PUT).

The probe now has two fixtures in the same opt-in category, sharing the setup and verification above:

- `Given_claim_set_changes_applied_to_every_client_of_one_application` (provider mode) repeats the
  direct-provider baseline.
- `Given_claim_set_changes_saved_through_the_application_endpoint` (endpoint mode) sends each change
  as one `PUT /v3/applications/{id}` to the running Configuration Service. The stopwatch covers that
  request alone: the atomic save and marking, the provider update of every client and the clear of
  every marker. A run counts a client as updated only when the request answered 204 and, read from the
  Configuration Service database afterwards, no client of the Application is still pending. The
  endpoint gives no per-client timing, so only totals are reported for it.

The setup also narrows the second client to the Application's whole list, so every run covers the
first client, secondary clients, ten inheriting clients, one client narrowed to the whole list and 189
narrowed to one education organization, four of them unapproved. After every change each of the 200
clients is verified as above: stored state, the provider's scopes without the previous claim set, and a
newly issued token (or an exact `401 invalid_client` refusal) carrying exactly the new claim set and the
client's own education organizations, data stores and namespace, with its original key and secret.

Lanes, run one at a time by `Invoke-IsolatedConfigE2E.ps1`, each removed afterwards:

| Lane | Isolation name | Environment file |
|---|---|---|
| Keycloak 26.1, PostgreSQL 18.6 | `dms-1642-4a-kc-pg` | `.env.config.e2e` |
| OpenIddict, PostgreSQL 18.6 | `dms-1642-4a-oi-pg` | `.env.config.e2e` |
| Keycloak 26.1, SQL Server 2025 | `dms-1642-4a-kc-ms` | `.env.config.mssql.e2e` |
| OpenIddict, SQL Server 2025 | `dms-1642-4a-oi-ms` | `.env.config.mssql.e2e` |

```shell
CONFIGURATION_SERVICE_FANOUT_MEASUREMENT=true CONFIGURATION_SERVICE_FANOUT_MEASUREMENT_OUTPUT=<dir> \
  pwsh eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -EnvironmentFile <file> \
  -IdentityProvider <keycloak|self-contained> -IsolationName <name> \
  -E2ETestFilter "TestCategory=ApplicationUpdateFanoutMeasurement"
```

Every lane passed the launcher's 2-test target preflight and then 8 of 8 measurement tests with no
skip (4 per fixture). Every run in every lane: 200 attempted,
200 updated, 0 failures, 0 verification mismatches, and a clean baseline.

Total time for the 200 clients, in milliseconds (cold, then the two repeats):

| Lane | Endpoint PUT | Direct provider loop |
|---|---:|---:|
| Keycloak, PostgreSQL | 2,748 / 2,213 / 2,304 | 4,322 / 3,596 / 3,394 |
| Keycloak, SQL Server | 2,698 / 2,663 / 2,517 | 4,206 / 3,687 / 3,584 |
| OpenIddict, PostgreSQL | 307 / 283 / 310 | 461 / 396 / 400 |
| OpenIddict, SQL Server | 661 / 499 / 525 | 862 / 718 / 715 |

The direct loop's per-client p50 was 17–21 ms on Keycloak, 1.9–2.2 ms on OpenIddict with PostgreSQL
and 3.5–4.0 ms with SQL Server, as in the baseline above.

The endpoint is faster than the direct loop even though it also writes and clears 200 markers. The
endpoint runs inside the stack and reaches Keycloak and the database over the Docker network; the
direct loop runs in the test process and goes through Docker Desktop's published ports, which the
Limits above already note. The two columns are measurements over different network paths and do not
isolate the marker writes' cost. The finished request stays within a few seconds for 200 clients on Keycloak and
well under one second on OpenIddict.

Limits of this section: one run per lane on one developer machine, no elapsed-time assertion, and
the provider failures that leave clients pending are injected through a fake provider in the
Configuration Service's own HTTP tests (`ApplicationUpdateSyncApiTests`, on both databases) rather
than against these real providers. The tokens and stored state checked here are real.
