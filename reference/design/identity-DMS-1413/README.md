# Identity Management Companion (DMS-1413)

## Overview

Spike [DMS-1413](https://edfi.atlassian.net/browse/DMS-1413) defines the scope and story breakdown for adding the Ed-Fi Identities API surface to DMS.

The result is an optional, pluggable pass-through API under `/identity/v2`.
DMS owns the routes, Discovery entry, OpenAPI document, authorization, feature toggle, pipeline, and response mapping.
An implementer-owned plugin supplies the identity backend through the plugin architecture designed by DMS-1462.
DMS ships no concrete identity backend.

The feature is off by default.
When disabled, the routes and identity metadata are absent.
When enabled without a replacing plugin, DMS starts cleanly and the host default reports every identity operation unsupported.

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
- service-claim authorization and tenant/route-qualifier boundaries;
- request parsing, duplicate-property rejection, async token handling, and response mapping;
- an implementation story graph and test strategy;
- the divergence ledger against ODS/API 7.3 and remaining risks.

## Ticket Drafts

The spike breaks the implementation into eight downstream drafts.
The first deployable plugin replacement work stays behind the plugin loader and registry stories; earlier stories can be implemented and tested with the DMS host default or in-repo test doubles.

| Draft | Title | Depends on |
| --- | --- | --- |
| [01](./01-add-identity-contract-package-and-host-default.md) | Add the Identity Contract Package and Host Default | this design |
| [02](./02-add-identity-core-pipeline.md) | Add the Identity Core Pipeline and Response Mapping | 01 |
| [03](./03-add-identity-endpoints-and-feature-toggle.md) | Add Identity Endpoints and Feature Toggle | 02 |
| [04](./04-publish-identity-openapi-and-discovery.md) | Publish Identity OpenAPI and Discovery Entries | 03 |
| [05](./05-declare-identity-plugin-contract-registry.md) | Declare the Identity Plugin Contract Registry Entry | 01, DMS-1498, DMS-1499 |
| [06](./06-prove-identity-api-against-fixture-plugin.md) | Prove Identity End-to-End Against a Fixture Plugin | 04, 05 |
| [07](./07-document-identity-api-and-plugin-contract.md) | Document the Identity API and Plugin Contract | 04, 05, DMS-1500 |
| [08](./08-publish-identity-contract-package.md) | Publish `EdFi.Api.Identity` | 01-07, DMS-1501 |

## Filing Gate

Open after this spike is reviewed and approved.
File the downstream stories in the order above, preserving the dependency split between the DMS-owned API surface and the plugin-registry work.
