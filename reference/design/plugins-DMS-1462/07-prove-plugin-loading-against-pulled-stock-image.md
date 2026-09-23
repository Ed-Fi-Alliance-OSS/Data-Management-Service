---
jira: DMS-1502
jira_url: https://edfi.atlassian.net/browse/DMS-1502
epic: none
source_spike: DMS-1462
---

# Story: Prove Plugin Loading Against a Pulled Stock Image

## Description

Draft 04 proves the mechanism against a locally built image, which proves everything except the one word the design exists for: **stock**.
This story runs the same proof against an `edfialliance/ed-fi-api` image that the test pulls and never builds, per:

- `reference/design/plugins-DMS-1462/design.md` ("## Testing Strategy", "End-to-end, the load-bearing one"; "### What the Stock Image Must Ship")

The assertion is that a published stock image runs third-party code with no image derived and no DMS rebuilt.
It is the one assertion that fails if the design's central claim is wrong.

It cannot run before the first published image carries the loader, which is why it is its own post-release ticket rather than a note on draft 04.
Its payload is DMS-1436's fixture validator, so it depends on DMS-1436 as well as on the release; DMS-1436 in turn depends on draft 04, and none of the three is blocked by this one.
It also depends on draft 05, which writes the `docs/OPERATIONS.md` chapter this story's claim is about and which asserts that chapter equal to the overlay files this story runs.

**The release it waits for has to carry two things, not one.**
This story asserts a custom-validation 400 over HTTP against a pulled image, and a 400 requires the fan-in pipeline step that DMS-1433 adds, not only the loader that draft 04 adds.
An image carrying draft 04 but not DMS-1433 loads the validator, resolves it, and never calls it, so the assertion would fail on a mechanism that is working.
The gate is therefore the first release carrying **both** draft 04 and DMS-1433.

## What Is Built, and What Is Still Waiting

The harness, its gating, its evidence and its lane are implemented and tested.
No qualifying image has been published, the pin is still `pending`, and **the proof has never run against a real image**.
Everything below describes what will happen when it does, except where it says otherwise.

### The pin, and what completing it requires

`eng/docker-compose/tests/plugin-deployment/stock-image-pin.json` names the published artifacts the proof runs against.
Its `status` is `pending` and every release-specific value is `null`; the two repository names are not release-specific and are recorded from the outset.

Moving it to `published` means filling all of:

| Field | What it records |
| --- | --- |
| `edFiApi.tag`, `edFiApi.digest` | The version-specific tag **and** the digest it resolved to. Both, because a tag alone moves |
| `configurationService.digest` | Digest alone. This ticket adds version-specific tags to the Ed-Fi API publication only, so the Configuration Service still carries nothing but the moving `pre` tag, and a bare digest is honest about that |
| `release.githubRelease`, `release.sourceCommit`, `release.publicationRunUrl` | The release that produced the tag, the commit it was cut from, and the run that published it, as `https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/<run id>` (an `/attempts/<n>` suffix is allowed; a query or fragment is refused) |
| `provisioning.schemaToolsPackageVersion`, `provisioning.schemaToolsFeedUrl`, `provisioning.dataStandardVersion`, `provisioning.schemaPackages` | The released SchemaTools version and the feed it installs from, the data-standard label, and the exact schema package identities with their feed. `schemaPackages` is the whole of what selects schema content; `dataStandardVersion` is only a label |
| `contracts.pluginsPackageVersion`, `contracts.customValidationPackageVersion`, `contracts.feedUrl` | The two published contract packages the fixture compiles against, restored rather than built, and the feed they restore from. Neither the tool nor the contracts borrow a schema package's feed |

A release qualifies when it carries DMS-1499's loader and DMS-1433's fan-in step, when it was cut after this ticket's version-specific image tagging merged, and when it follows DMS-1501's publication of `EdFi.Api.Plugins` and `EdFi.Api.CustomValidation` together with that story's external-consumer restore and compile evidence.

Nothing in the pin may be guessed.
A fabricated tag or digest would make the proof a statement about an artifact nobody published.
`eng/ci/Test-StockImagePinReadiness.ps1` refuses a half-filled pin in either direction: a `pending` pin carrying values and a `published` pin missing any of them are both invalid rather than pending.
It also refuses a pinned tag the pinned release does not produce, computed by `eng/ci/Get-DmsPrereleaseImageTag.ps1`, which is the same rule `on-prerelease.yml` runs.

### Pending on a schedule versus pending on request

These are different answers and the readiness script keeps them apart.

- **Scheduled**, pin pending: `ready=false`, exit zero, no proof job. The workflow writes a step summary saying the run proved nothing and that a skipped proof is not evidence.
- **Dispatched**, pin pending, malformed, or incomplete: the readiness job fails. Somebody asked for the proof, and silence would read as success.

### What the proof establishes about the artifact

- The pinned tag is asked of the registry with `docker buildx imagetools inspect` and must resolve to the pinned digest. Two local pulls cannot establish this; a `RepoDigests` list accumulates every digest an image id has carried.
- Every deployment runs the image by `repository:tag@digest`, and each container's image id is compared against `docker image inspect` of the pinned digest.
- The released SchemaTools CLI is installed at the pinned version into a run-owned tool path, and the installed version is read back and compared.
- The fixture is published from a **copy** of `eng/fixtures/plugins/Acme.CustomValidationProof` in the run workspace, with run-owned intermediate and output paths and a run-owned `nuget.config`, against the two pinned published contract versions. The restored versions are read out of `project.assets.json` and compared. The committed fixture tree is an input and is left byte-for-byte unchanged.
- Every deployment's staged `bootstrap-manifest.json` is compared against the pin's `schemaPackages`, per deployment rather than once per run, because each scenario tears the workspace down and restages it.
- No recorded command builds an image. The command log is read as commands rather than searched for a word, so `--no-build` and a path containing `build` are not builds, and `docker build`, `docker buildx build`, `docker compose build` and `docker compose up --build` are.

### The four deployments

Each is a separate deployment with its own composed environment file, torn down after the scenario body whether it held or not.
Nothing is torn down before a start: the first deployment begins clean because the host preflight refused to begin at all unless the compose project was unoccupied, and every later one begins clean because the preceding scenario's teardown is checked and a failure there fails the run.

The committed recipe files are run **unedited**.
Three test-owned overlays are added with their own `-f`: `stock-image-pin-dms.yml`, which pins the image and sets `restart: "no"`, and `plugins-allowed-dms.yml`, both in every deployment; and `plugins-feed-dms.yml`, which serves the packed nupkg over HTTP and is used only by the two fetch deployments.

| Evidence key | Composed from | What it asserts |
| --- | --- | --- |
| `recipe1` | `plugins-dms.yml` | The bind-mount recipe: DMS reaches `Ready`, `/app/plugins` is read-only observed from inside the container with a write probe that fails on a read-only file system, a control POST returns 201, and both fixture rejection arms return the fixture's exact message |
| `recipe2` | `plugins-fetch-dms.yml` plus the test-owned `plugins-feed-dms.yml` | The same, with the plugin delivered by the `fetch-plugins` one-shot service over HTTP from a digest-pinned static-file container serving the packed nupkg |
| `wrongDigest` | `plugins-fetch-dms.yml` with a deliberately wrong `PLUGIN_PACKAGE_SHA256` | The fetch fails **on the checksum comparison specifically**, not on transport, and DMS never started, established from a successful enumeration and a successful inspect rather than from an absence |
| `misspelledAllowlist` | `plugins-dms.yml` with one allowlisted name misspelled | DMS exits of its own accord under `restart: "no"`, recording `State=Failed` and `Phase=LoadPlugins` and naming `/app/plugins/Acme.CustomValidationProof1` |

A generic 400 is the failure mode the rejection assertions exist to rule out, as are a 401 or 403, which would mean the request never reached a validator.
A DMS write-path 400 carries both arms every time, so each arm is parsed and matched exactly rather than searched for a substring.

### The client the proof posts with

The proof authenticates to the Configuration Service with the bootstrap admin client the deployment was started with, resolved from that deployment's environment file.
It then requires **exactly one** route-unqualified data store with a whole positive id and passes that id explicitly to the real `Get-SmokeTestCredential`.
Zero stores, several, a route-qualified one, a missing or non-numeric id, and a malformed `dataStoreContexts` all refuse.
Without the explicit id the helper falls back to the first store the service lists, and a client bound to a route-qualified store reaches its data only under the qualifier, so the bare-route POST would fail for a reason that says nothing about plugin loading.

Credentials stay in memory. The bootstrap secret, the Configuration Service token, the client secret and the DMS access token are all redacted out of the evidence and the command log.

### What the run says it was

Evidence is written to `.ai-work/verification/stock-image-plugin-proof.json` after cleanup, so the record includes the teardown.

- `outcome` is `passed` only when the scenario work reached its end, the no-build verdict is verified, and everything the run claimed was given back. Anything else is `failed`.
- `primaryFailure` carries the first failure, sanitized. A later cleanup error joins it in `failure` rather than replacing it.
- `buildCommandAbsent` keeps the structured verdict, and an unverified one fails the run rather than being recorded and ignored.
- `cleanup` records whether the run owned anything, whether cleanup was attempted, whether it succeeded, and every sanitized error.
- Each scenario record carries the acquisition path it composed, the image reference, the schema packages that deployment staged, and the observations behind its conclusions rather than only the conclusions.

Every value is redacted before the document is serialized, not after. A rule run over serialized JSON could consume the closing quote and the members after the value it matched.

The workflow uploads that artifact **whenever the proof step ran**, passing or failing, and only from the job that ran the harness. A passing run's artifact is the record of the pass. A skipped proof produces no artifact.

### Cleanup, and its ownership boundary

The harness refuses to start when the host is already occupied: a fixed container name in use, a leftover container or volume of its compose project, a foreign container on the shared external network, or a wanted port already bound, by Docker or by anything else. A failed inventory refuses rather than reading as an empty one.

It removes only what it claimed, and it claims each resource before the operation that could create it.

For the scheduled lane's `always()` step there is a receipt.
`.ai-work/stock-image-proof-cleanup.json` is written after the host preflight and immediately before the first operation that can create the compose project, and it names the environment file that would tear that deployment down.
The harness's own cleanup removes it when cleanup succeeded; an abrupt exit or a failed teardown leaves it.
`eng/docker-compose/tests/plugin-deployment/Invoke-StockImageProofFallbackCleanup.ps1` runs **no Docker command at all** when there is no receipt, which is what stops a run that refused to touch somebody else's stack from having that stack removed by its own teardown step.

### Where it runs

`.github/workflows/scheduled-stock-plugin-proof.yml` runs weekly and on `workflow_dispatch`, which accepts an alternate pin file.
`permissions: read-all`.
The proof job runs only when readiness is true.

Per-PR validation stays at the Pester level, where the harness is driven against a fail-closed shim with no daemon, no registry and no stack: `StockImageProofEntryScript.Tests.ps1`, `StockImageProofOrchestration.Tests.ps1`, `StockImageProofDecisions.Tests.ps1`, `StockImagePinReadiness.Tests.ps1` and `StockImageProofWorkflow.Tests.ps1`.
Nothing that pulls an image is added to the pull-request checks.

### What remains external to this ticket

1. DMS-1501 merges and publishes `EdFi.Api.Plugins` and `EdFi.Api.CustomValidation`, with its external-consumer restore and compile evidence.
2. A qualifying `edfialliance/ed-fi-api` release is published, carrying DMS-1499's loader and DMS-1433's fan-in step and cut after this ticket's version-specific image tagging.
3. The pin is completed from that publication and its status moved to `published`.
4. The first green scheduled or dispatched proof run is recorded on the ticket.

None of the four has happened.

## Acceptance Criteria

- A test under `eng/` pins an `edfialliance/ed-fi-api` tag at or after the first release carrying **both** `LoadPlugins` and DMS-1433's fan-in pipeline step, pulls it, and never invokes `docker build`. The pin is a committed document with a `pending` state that gates the lane, and the story records which release qualifies and why both are required.
- Both acquisition recipes run as the committed overlay files under `eng/docker-compose/`, unedited, in **two** deployments rather than one. Unedited means env-driven, as in draft 04: Recipe 1 supplies `DMS_PLUGINS_MOUNT_SOURCE` and Recipe 2 supplies `PLUGIN_PACKAGE_URL`, `PLUGIN_PACKAGE_SHA256`, and `PLUGIN_NAME`, all of which the committed files declare with `:?`. They cannot share a deployment because both end at the single `/app/plugins` mount target, and merging them would mean running something neither document publishes.
- Recipe 2's package is served to `fetch-plugins` over **HTTP**, from a test-owned Compose overlay added with its own `-f`, and the published overlay is not edited to accommodate the harness.
- The DMS-1436 fixture validator is the payload for both, packed asset-only, and a POST failing its check returns the custom-validation 400 over HTTP against the pulled image, matched exactly and per arm.
- A third deployment exports a deliberately wrong `PLUGIN_PACKAGE_SHA256` for Recipe 2, leaving the committed file and the served package alone, and asserts `fetch-plugins` failed on the checksum comparison and the DMS container never started.
- A fourth deployment with one allowlisted name misspelled asserts DMS exits with a failed `LoadPlugins` phase in the startup status file, naming the expected path.
- The test asserts the container's `/app/plugins` mount is read-only from inside the container, since Control 1 of the trust model is the one DMS cannot verify itself.
- The test is added to a scheduled lane rather than the per-PR lane, because it depends on a published artifact that a pull request cannot change.

## Tasks

1. ~~Parameterize draft 04's local-image end-to-end test on the image reference.~~ Done as a separate stock-only harness, `Invoke-StockImagePluginProof.ps1`, driven by the committed pin. `Invoke-PluginDeploymentCheck.ps1` is unchanged.
2. ~~Add the four runs.~~ Done: `recipe1`, `recipe2`, `wrongDigest`, `misspelledAllowlist`, each its own deployment.
3. ~~Add the read-only mount assertion.~~ Done, from the mount table and a write probe inside the container.
4. ~~Wire the test into the scheduled lane.~~ Done: `.github/workflows/scheduled-stock-plugin-proof.yml`, gated on pin readiness.
5. Record the first green run against the first qualifying published tag on the ticket. **Outstanding**, and blocked on the four external items above.
