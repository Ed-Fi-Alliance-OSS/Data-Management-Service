# Plugin Infrastructure (DMS-1462)

## Overview

Spike [DMS-1462](https://edfi.atlassian.net/browse/DMS-1462) designs the plugin infrastructure for the Data Management Service and the Configuration Service.
An implementer publishes a plugin as a directory of assemblies compiled against published Ed-Fi contract packages; an operator gets that directory into a **published stock image** at deploy time and names it in an allowlist; the host loads it into an isolated assembly load context and invokes its contribution hooks.
No image is derived and neither party rebuilds DMS.

The argument, the decisions, and the evidence are in [design.md](./design.md).
This file is the manifest.

## Provenance

The three plugin types are not speculative.
Each traces to an epic that already presumes a plugin mechanism that does not exist yet.

| Type | Epic | What it presumes |
| --- | --- | --- |
| Custom Validation | [DMS-1345](https://edfi.atlassian.net/browse/DMS-1345) | DMS-1434: "the deployment adds one call to that extension at DMS's composition root" |
| Identity Validation | [DMS-1412](https://edfi.atlassian.net/browse/DMS-1412), [DMS-1414](https://edfi.atlassian.net/browse/DMS-1414) | "the clients actually create the implementation via plugin" |
| Secrets Manager | none yet; **its own spike** | The ODS answer is [a plugin](https://docs.ed-fi.org/reference/ed-fi-api/platform-dev-guide/configuration/external-configuration-of-ods-connection-strings/), though only its loading seam transfers, not its data shape. See design.md, "The Secrets Spike" |

## Documents

| Document | Status | Covers |
| --- | --- | --- |
| [design.md](./design.md) | **Approved 2026-08-27**, four review revisions folded in (see its Status) | The shared mechanism: delivery, the two acquisition recipes, discovery, load isolation, the plugin contract, the two composition phases, cardinality, trust model, failure semantics, configuration, and what the stock image must ship. Carries the divergence ledger against the custom validation epic |
| [ods-precedent.md](./ods-precedent.md) | Approved with design.md | The Ed-Fi ODS/API survey design.md draws on and departs from, pinned to a commit |
| Secrets Manager **spike** | Not started; **starts when this spike merges**, runs in parallel with the foundation stories, and its own tickets depend on the foundations being complete. Its first foundation story builds Phase A, which design.md decides but this spike's stories do not implement | The whole type: contracts, runtime resolution, multi-tenancy, `IClientSecretHasher` relocation, CMS. A separate spike, not a companion to this one. design.md's "The Secrets Spike" carries its inherited findings |
| Identity companion | Not started | The Identity API contract behind DMS's own endpoints, and its relationship to UniqueId validation. DMS-1412, DMS-1414 |
| Custom Validation delta | **Done 2026-08-27**, applied in place | The three open CV stories that asserted compiled-in delivery were edited rather than given a companion document: DMS-1434 narrowed to the guard, DMS-1435 and DMS-1436 re-pointed at the plugin path, and a delta note added to `reference/design/custom-validation-DMS-1345/design.md`. DMS-1433 needed no change |

## Ticket Drafts

Seven drafts, in dependency order.
Drafts 01 through 06 are the foundation stories this spike files; 07 is post-release and is drafted now so its shape is settled, but it is filed only once the first published image carries the loader.

| Draft | Title | Depends on | Status |
| --- | --- | --- | --- |
| [01](./01-add-plugin-contract-package.md) | Add the Plugin Contract Package and `src/plugins/` | none | Draft |
| [02](./02-add-plugin-loader-and-load-isolation.md) | Add the Plugin Loader, Discovery, and Load Isolation | 01 | Draft |
| [03](./03-add-recording-wrapper-and-cardinality-guard.md) | Add the Recording Service Collection and Cardinality Guard | 02, DMS-1434 | Draft |
| [04](./04-integrate-plugin-loading-into-dms-startup.md) | Integrate Plugin Loading into DMS Startup | 03 | Draft |
| [05](./05-document-plugins-and-publish-host-manifest.md) | Document Plugins for Operators and Implementers and Publish the Host Assembly Manifest | 04 | Draft |
| [06](./06-publish-plugin-contract-packages.md) | Publish `EdFi.Api.Plugins` and `EdFi.Api.CustomValidation` | 01-05, DMS-1433, DMS-1435, DMS-1436 | Draft, release-gated |
| [07](./07-prove-plugin-loading-against-pulled-stock-image.md) | Prove Plugin Loading Against a Pulled Stock Image | first release carrying 04 | Draft, post-release |

Not drafted, by decision recorded in design.md: Phase A (`ContributeConfiguration`) belongs to the secrets spike's first foundation story; CMS host integration has no consuming epic; the DMS-1433 through DMS-1436 amendments are edits to filed tickets, not new ones.

**DMS ships no fetcher.**
Acquisition is a deploy-time step documented as two recipes, a read-only bind mount and a one-shot fetch-verify-extract step ahead of DMS, so the plugin root is read-only to the runtime in every deployment and no foundation ticket touches the shipped `ApiSchemaDownloader`.
A DMS-owned fetcher is fully designed and deferred; design.md, "Rejected Alternatives", records its shape and what would bring it back.
The pulled-stock-image end-to-end proof is its own post-release ticket, because it cannot run until the first published image carries the loader.
See design.md, "Acquisition" and "Level of Effort".

**Publishing is the last ticket, not the first.**
`EdFi.Api.CustomValidation` and `EdFi.Api.Plugins` are packed and consumer-verified on every pull request from a local folder feed, so every story in this spike proves the packaged path without a feed and without burning a package id.
Publishing is what an external implementer needs, it is blocked by every other ticket, and it blocks none of them.

## Filing Gate

**Open for drafts 01 through 06** as of 2026-08-27: design.md is approved and the Custom Validation delta is applied.
Draft 07 stays behind the first release that carries the loader.
Identity companion stories stay behind their own companion document.

The gate existed because the spine reverses a prior decision, the custom validation delivery model, and amends open stories that depend on it.
Both halves are now settled: the reversal is approved and the dependent stories are edited.
