---
jira:
epic: DMS-1418
source_spike: DMS-1419
---

# Story: Add Grant Polling and Freshness to DMS

## TL;DR

Makes every running DMS instance learn, within about a minute, when capture is turned on or off for a client.
If an instance loses contact with the Configuration Service, it stops capturing rather than keep capturing on old information.

## Description

Each DMS replica polls CMS for the active capture grants and keeps them in a local snapshot, so deciding whether to capture a request costs a lookup and no CMS call.
This story builds the poller, the snapshot, and the freshness rules that make enable and disable reach every replica within a known bound.

Per `reference/design/request-response-capture-DMS-1419/design.md` ("### How Every Replica Learns of a Change", "### Freshness" under D2).

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Core/` names `src/dms/core/EdFi.DataManagementService.Core/`.

## Acceptance Criteria

**The poller**

- A background service polls `GET /v3/requestCaptureGrants/active` for each tenant the replica has loaded (`Core/Configuration/ConfigurationServiceDataStoreProvider.cs`, `GetLoadedTenantKeys`), through the existing CMS client and token handler.
- Each tenant is polled on a fixed-rate schedule: its polls start every `I`, however long the previous poll took. Tenants are polled concurrently, each poll bounded by `T`. The `DocumentCacheProjectionSupervisor` loop, which starts the next poll `I` after the last one ends, is a hosting precedent only.
- In a single-tenant deployment the loaded tenant key is empty. The poller then sends no `Tenant` header, and the snapshot keys its grants by that empty key, which selection uses.
- It is registered only when `RequestResponseCapture:ConnectionString` is set.
- DMS binds `RequestResponseCapture:ConnectionString`, `PollIntervalSeconds` (`I`, default 30), and `PollTimeoutSeconds` (`T`, default 10). The `RequestResponseCapture` options type is shared with the capture-store story, so whichever of the two lands first creates it. A non-positive value, or a timeout not shorter than the interval, fails startup.
- A failed poll logs a rate-limited warning with the tenant and the failure, and nothing from a grant beyond its id.

**The snapshot**

- Each successful poll of a tenant replaces that tenant's entry in an immutable snapshot keyed by tenant and client key.
- Tenants compare with `StringComparer.OrdinalIgnoreCase`, as `ConfigurationServiceDataStoreProvider` keys loaded tenants, because DMS serves every spelling of a tenant route. Client keys compare ordinally (`reference/adr-client-id-casing.md`).
- A lookup by tenant, client key, and time returns the active grant, including its canonical tenant name, or none, with no I/O and no allocation.
- A tenant that has not been polled yet has no grants.

**Freshness**

- Freshness is per tenant: one tenant's failed or late polls never change another tenant's lookups.
- A tenant entry's age is counted from the start of the poll that produced it, on the monotonic clock.
- A response from an older poll of a tenant that completes after a newer poll of the same tenant is discarded.
- A tenant whose entry is older than `A`, which is `2I + T`, returns no grant.
- A grant past its `ExpiresAt` is not returned, even before the next poll.

**Tests**

- Units use FakeItEasy, fake time, and explicit signals, never delays, and cover:
    - a stale tenant entry returns no grant
    - age is counted from poll start, so a slow response does not refresh it
    - an out-of-order response is discarded
    - local expiry between polls
    - a tenant loaded after the last poll has no grants until the next one
    - one timed-out poll followed by a successful one never makes the snapshot stale
    - several tenants, each answering in just under `T`, all stay within `A`
    - tenant X's polls fail while tenant Y's succeed: X returns no grant once its entry is older than `A`, and Y keeps returning its grant
    - a single-tenant deployment polls without a `Tenant` header and returns the granted client's grant
    - a grant polled under `Tenant1` is returned for a lookup on `tenant1`, and a lookup with the client key in another case returns none
- Two-replica tests run two pollers against one fake CMS and cover:
    - healthy: enable and disable reach both within `A`
    - CMS unreachable during a disable: both stop capturing within `A`
    - a delayed response that crosses a disable is discarded
    - CMS unreachable during an enable: neither captures until a poll succeeds

## Dependencies

- CMS request-capture grants (01): the active-grants read.

## Estimate

2 SP.
