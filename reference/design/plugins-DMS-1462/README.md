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
| [design.md](./design.md) | **Approved 2026-08-27** by the spike's ticket owner; four approval revisions plus four rounds of panel review folded in (see its Status) | The shared mechanism: delivery, the two acquisition recipes, discovery, load isolation, the plugin contract, the two composition phases, cardinality, trust model, failure semantics, configuration, and what the stock image must ship. Carries the divergence ledger against the custom validation epic |
| [ods-precedent.md](./ods-precedent.md) | Approved with design.md | The Ed-Fi ODS/API survey design.md draws on and departs from, pinned to a commit in each of the two repositories it cites |
| Secrets Manager **spike** | Not started; **starts when this spike merges**, runs in parallel with the foundation stories, and its own tickets depend on the foundations being complete. Its first foundation story builds Phase A, which design.md decides but this spike's stories do not implement | The whole type: contracts, runtime resolution, multi-tenancy, `IClientSecretHasher` relocation, CMS. A separate spike, not a companion to this one. design.md's "The Secrets Spike" carries its inherited findings |
| [Identity companion](../identity-DMS-1413/README.md) | **Drafted 2026-09-03** as [identity-DMS-1413](../identity-DMS-1413/design.md), the design output of DMS-1413; its own filing gate opens when that spike is reviewed and approved, and its eight downstream drafts are filed from there | The Identity API contract behind DMS's own endpoints, and its relationship to UniqueId validation. DMS-1412, DMS-1414. Inherits this design's `Replace` cardinality for the identity service and its deferral of plugin-owned HTTP routes |
| Custom Validation delta | **Done 2026-08-27**, applied in place | The three open CV stories that asserted compiled-in delivery were edited rather than given a companion document: DMS-1434 narrowed to the guard, DMS-1435 and DMS-1436 re-pointed at the plugin path, and a delta note added to `reference/design/custom-validation-DMS-1345/design.md`. DMS-1433 needed no change |

## Ticket Drafts

Seven drafts, in dependency order.
Drafts 01 through 06 are the foundation stories this spike files; 07 is post-release and is drafted now so its shape is settled, but it is filed only once the first published image carries the loader.

| Draft | Title | Depends on | Status |
| --- | --- | --- | --- |
| [01](./01-add-plugin-contract-package.md) | Add the Plugin Contract Package and `src/plugins/` | none | [DMS-1496](https://edfi.atlassian.net/browse/DMS-1496) |
| [02](./02-add-plugin-loader-and-load-isolation.md) | Add the Plugin Loader, Discovery, and Load Isolation | 01 | [DMS-1497](https://edfi.atlassian.net/browse/DMS-1497) |
| [03](./03-add-recording-wrapper-and-cardinality-guard.md) | Add the Recording Service Collection and Cardinality Guard | 02, DMS-1434 | [DMS-1498](https://edfi.atlassian.net/browse/DMS-1498) |
| [04](./04-integrate-plugin-loading-into-dms-startup.md) | Integrate Plugin Loading into DMS Startup | 03, and the merged DMS-1432 contract package | [DMS-1499](https://edfi.atlassian.net/browse/DMS-1499) |
| [05](./05-document-plugins-and-publish-host-manifest.md) | Document Plugins for Operators and Implementers and Publish the Host Assembly Manifest | 04 | [DMS-1500](https://edfi.atlassian.net/browse/DMS-1500) |
| [06](./06-publish-plugin-contract-packages.md) | Publish `EdFi.Api.Plugins` and `EdFi.Api.CustomValidation` | 01-05, DMS-1433, DMS-1435, DMS-1436 | [DMS-1501](https://edfi.atlassian.net/browse/DMS-1501), release-gated |
| [07](./07-prove-plugin-loading-against-pulled-stock-image.md) | Prove Plugin Loading Against a Pulled Stock Image | 05, DMS-1436, and the first release carrying **both** 04 and DMS-1433 | [DMS-1502](https://edfi.atlassian.net/browse/DMS-1502), post-release |

**Where the two `src/dms/` build-lane changes sit, and why they are split.**
The frontend does not reference `EdFi.Api.Plugins.Hosting` today, and `src/dms/Dockerfile`'s build stage cannot reach `src/plugins/`.
Draft 03 adds the `ProjectReference`, because its plugin guard is a frontend-owned startup task and it is the first story the frontend has to see.
Draft 01 makes the build-stage change that lets an image build with that reference in it, because it is the story that creates the tree and because `.github/workflows/on-dms-pullrequest.yml` builds `src/dms/Dockerfile` in nine places on a relevant pull request, so any later assignment leaves a merge order that breaks CI.
Draft 04 keeps the other build-lane change, dropping `/p:AssemblyVersion` and `/p:FileVersion` from the publish command line and the `ASSEMBLY_VERSION` build-arg pair from `build-dms.ps1`'s `DockerBuild`, because it is the story whose skew preflight one contract assembly carrying two `AssemblyVersion`s would break.
Nothing replaces that stamping: `DockerBuild` is deliberately **not** taught to run `SetDMSAssemblyInfo`, which rewrites a tracked props file on every run of the `E2ETest` or `StartEnvironment` command and drops the build height besides, so a locally built image's DMS assemblies carry the committed `src/dms/Directory.Build.props` version and the release lane is untouched.

**Which story asserts which contract's version, and where.**
Draft 04's Docker-lane test asserts `EdFi.Api.Plugins.dll` only, and draft 05's host-assembly-manifest assertion covers `EdFi.Api.Plugins` only.
`EdFi.DataManagementService.CustomValidation`'s csproj declares no version of its own until draft 06, so neither earlier story has anything to assert against for it, and draft 06 extends **both** assertions in the pass that adds the declaration.

**Which story owns the acquisition recipes, and in which direction.**
Draft 04 creates the two overlay compose files and its end-to-end tiers run them as committed, supplying every deployment-specific value through the `:?`-required environment variables the files declare; draft 05 writes the `docs/OPERATIONS.md` chapter and asserts that the chapter's Compose blocks equal those files.
The files are the artifact and the document is pinned to them, which is why no tier drives a recipe parsed out of Markdown and why draft 07, which makes the published claim, depends on draft 05.

**Draft 07 waits for DMS-1433 as well as for a release carrying draft 04.**
Its assertion is a custom-validation 400 over HTTP, which needs the fan-in pipeline step to be in the released image and not only the loader.
An image carrying the loader alone would load the validator, resolve it, never call it, and fail the assertion on a mechanism that is working.

**Draft 04 builds its own fixture plugin, and that is what keeps the graph acyclic.**
An earlier revision had draft 04 assert the custom-validation 400 using DMS-1436's fixture validator, while DMS-1436 depended on draft 04 for the loader that would load it.
Draft 04 now ships a minimal fixture plugin of its own.
That fixture implements `ICustomResourceValidator`, because draft 04 runs DMS's production contract registry and a plugin registering no declared contract is fatal, and `ICustomResourceValidator` is the only contract that registry declares.
The dependency this creates is on **merged** work, the DMS-1432 contract package, not on any open custom validation story: the fixture is a trivially passing validator, draft 04 asserts on the load inventory and on resolving the registration rather than on a 400, and it needs neither DMS-1433's pipeline step nor DMS-1435's guide.
Draft 04 does depend on DMS-1434 transitively, through draft 03, whose plugin guard runs beside it.
The graph is acyclic because nothing in the custom validation epic depends on draft 04 except DMS-1435 and DMS-1436, both downstream of it.
DMS-1436 depends on draft 04, builds the validator plugin, and owns the custom-validation 400 over HTTP; draft 07 reuses DMS-1436's fixture and therefore depends on it.

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
Draft 07 stays behind DMS-1436 and behind the first release that carries the loader.
Identity companion stories stay behind their own companion document.

Filing these drafts includes one step that is not a new ticket: **DMS-1434, DMS-1435, and DMS-1436's Jira descriptions are updated to mirror their edited drafts**, in the same pass that files 01 through 06.
Their local drafts already carry the scope-change notes; the filed tickets do not, and a reader of Jira would otherwise still see compiled-in delivery.
It is named here so it has an owner rather than sitting as an untracked intention.

The gate existed because the spine reverses a prior decision, the custom validation delivery model, and amends open stories that depend on it.
Both halves are now settled: the reversal is approved and the dependent stories are edited.
