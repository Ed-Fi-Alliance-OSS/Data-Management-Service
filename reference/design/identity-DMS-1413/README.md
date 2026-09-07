# Identity Management Companion (DMS-1413)

## Overview

Spike [DMS-1413](https://edfi.atlassian.net/browse/DMS-1413) defines the scope and story breakdown for adding the Ed-Fi Identities API surface to DMS.

The result is an optional, pluggable pass-through API under `/identity/v2`.
DMS owns the routes, Discovery entry, OpenAPI document, authorization, feature toggle, pipeline, and response mapping.
An implementer-owned plugin supplies the identity backend through the plugin architecture designed by DMS-1462.
DMS ships no concrete identity backend.

The feature is off by default.
When disabled, the routes and identity metadata are absent.
When enabled without a replacing plugin, DMS starts cleanly if normal host startup prerequisites are satisfied, and the host default reports every identity operation unsupported.

## Provenance

The Jira ticket is a design-only spike under the Identity Management work.
It depends on the plugin infrastructure design in `reference/design/plugins-DMS-1462/`, especially the replace-cardinality registry and startup loading stories.
The implementation stories below deliberately avoid claiming deploy-time plugin replacement until the DMS-1498 and DMS-1499 plugin foundation stories exist.

## Design Document

[design.md](./design.md) is the companion design.
It covers:

- the DMS-owned API surface and the implementer-owned plugin backend;
- the `EdFi.Api.Identity` contract package shape;
- the use of DMS-1462 replace-cardinality plugin contracts;
- feature-toggle, Discovery, and OpenAPI behavior;
- service-claim authorization, the authorization-strategy policy, client-to-tenant binding, and tenant/route-qualifier boundaries;
- explicit provider-owned namespace grants, tenant-wide access only by configured grant, and documented client/permission revocation windows and existing invalidation procedures;
- request parsing, duplicate-property rejection, async token handling and length limits, validation-error projection, and response mapping;
- provider lifetime and resolution, async job obligations, and UniqueId issuance constraints;
- case/order rules for context equality, mandatory job ownership, terminal job failure versus failed polling, and `no-store` on identity operation responses;
- sanitization of request-time provider activation, capability evaluation, and invocation, with compatibility rules for HTTP clients and provider implementations;
- identifier exposure in request logs and cross-origin access to the async `Location` header;
- an implementation story graph and test strategy;
- the divergence ledger against ODS/API 7.3 and remaining risks.

## Ticket Drafts

The spike breaks the work into two prerequisite drafts and four implementation drafts.
The DMS-owned API surface is kept together so route, pipeline, metadata, and Discovery behavior are reviewed as one feature.
The first deployable plugin replacement work stays behind the plugin loader and registry stories; earlier work can be implemented and tested with the DMS host default or in-repo test doubles.

Draft 00 is a pre-existing cross-tenant defect on the claim-set path rather than identity work.
Identity service-claim authorization adds a caller to that path, so it is corrected first and filed on its own.
Draft 00a makes empty datastore assignments valid through the CMS API-client lifecycle, matching application creation. Story 02 then proves provisioning and administration through real CMS endpoints, token issuance, and identity calls.

Datastore independence applies to requests on an initialized running host; startup and readiness are unchanged. Story 02 also owns complete tenant snapshots, coordinated refresh, and cancellation through DMS CMS lookup providers. Provider contracts require client-bound async jobs and complete synchronous results, define provider-owned identifier namespaces, and document concrete recovery from an unknown create outcome. Story 04 gates host wire-schema changes as well as package compatibility. DMS-1414 remains documentation/example work through custom validation, with no person-write implementation assigned by this spike.

| Draft | Title | Depends on |
| --- | --- | --- |
| [00](./00-send-tenant-header-per-request-in-cms-claim-set-provider.md) | Send the Tenant Header Per Request in the CMS Claim-Set Provider | this design |
| [00a](./00a-support-empty-datastore-assignments-in-cms-api-clients.md) | Support Empty Datastore Assignments in CMS API Clients | this design |
| [01](./01-add-identity-contract-package-and-host-default.md) | Add the Identity Contract Package and Host Default | this design |
| [02](./02-add-identity-api-surface-pipeline-toggle-openapi-discovery.md) | Add the Identity API Surface, Pipeline, Toggle, OpenAPI, and Discovery | 01, 00, 00a |
| [03](./03-register-identity-plugin-contract-and-prove-fixture.md) | Register the Identity Plugin Contract and Prove a Fixture Plugin | 02, DMS-1498, DMS-1499 |
| [04](./04-document-and-publish-identity-contract-package.md) | Document and Publish `EdFi.Api.Identity` | 03, DMS-1500, DMS-1501 |

## Filing Gate

Open after this spike is reviewed and approved.
File the downstream stories in the order above, preserving the dependency split between the DMS-owned API surface and the plugin-registry work.
