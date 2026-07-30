---
jira: TBD
jira_url: TBD
---

# Spike: Define Ownership-token Operational Lifecycle and Administration

## Description

The initial ownership-token contract defines CMS maintenance APIs and a bounded-staleness DMS
cache, but intentionally excludes the broader operational lifecycle needed to administer
ownership safely over time.

Investigate the customer-facing and operational value of faster revocation, Admin App workflows,
token retirement, API-client replacement, diagnostics, and identifier-capacity safeguards. Produce
only the follow-on work justified by concrete security, support, or administration needs.

Refer to the
[DMS-1058 ownership-token decision record](../../design-docs/ownership-token-maintenance.md) for
the initial contract and exclusions.

## Acceptance Criteria

- Establish the required ownership-access revocation SLA for assignment removal, compromised API
  clients, creator-token changes, and API-client replacement.
- Compare the approved configurable cache lifetime with explicit reload and push-invalidation
  options. Document tenant behavior, dependency-failure behavior, operational complexity, and the
  recommended approach.
- Define the Admin App workflows that provide sufficient customer value for token creation,
  assignment, reassignment, revocation, and visibility. Identify any CMS API changes beyond the
  approved maintenance contract.
- Decide whether ownership tokens need retirement or deactivation. Preserve the ability to
  interpret historical `dms.Document.CreatedByOwnershipTokenId` values and define assignment,
  listing, reactivation, and audit behavior.
- Determine whether assigning an existing token to a replacement API client covers supported
  hand-off scenarios. Propose bulk transfer of existing document ownership only if a validated
  customer workflow requires it.
- Evaluate whether exposing ownership diagnostics through `/oauth/token_info` provides enough
  support value to justify disclosure of authorization identifiers. Define authorization and
  response-shape constraints if recommended.
- Quantify global ownership-token ID consumption, define monitoring and alert thresholds, and
  provide a migration strategy for widening the CMS and DMS identifier types before the positive
  `SMALLINT` range is exhausted. Token IDs must not be reused.
- Recommend prioritized implementation stories for the adopted capabilities, or record why no
  additional work is justified. Link any approved follow-on stories back to this spike.

## Boundaries

- This spike does not implement product changes.
- It does not redesign the initial CMS maintenance endpoints or direct application-context delivery
  unless the revocation SLA cannot be met by the approved model.
- It does not block DMS-1060 unless product or security rejects the approved bounded-staleness
  contract.
