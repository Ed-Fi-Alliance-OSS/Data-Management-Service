---
jira: TBD
jira_url: TBD
epic: TBD
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
It is drafted now so its shape is settled, and filed when that release exists.
Its payload is DMS-1436's fixture validator, so it depends on DMS-1436 as well as on the release; DMS-1436 in turn depends on draft 04, and none of the three is blocked by this one.
It also depends on draft 05, which writes the `docs/OPERATIONS.md` chapter this story's claim is about and which asserts that chapter equal to the overlay files this story runs.

**The release it waits for has to carry two things, not one.**
This story asserts a custom-validation 400 over HTTP against a pulled image, and a 400 requires the fan-in pipeline step that DMS-1433 adds, not only the loader that draft 04 adds.
An image carrying draft 04 but not DMS-1433 loads the validator, resolves it, and never calls it, so the assertion would fail on a mechanism that is working.
The gate is therefore the first release carrying **both** draft 04 and DMS-1433.

## Acceptance Criteria

- A test under `eng/` or the E2E suite pins an `edfialliance/ed-fi-api` tag at or after the first release carrying **both** `LoadPlugins` and DMS-1433's fan-in pipeline step, pulls it, and never invokes `docker build`. The pinned tag is a variable with the first qualifying release as its default, and the story records which release that is and why both are required.
- Both acquisition recipes run as the committed overlay files under `eng/docker-compose/`, unedited, in **two** deployments rather than one. Unedited means env-driven, as in draft 04: Recipe 1 supplies `DMS_PLUGINS_MOUNT_SOURCE` and Recipe 2 supplies `PLUGIN_PACKAGE_URL`, `PLUGIN_PACKAGE_SHA256`, and `PLUGIN_NAME`, all of which the committed files declare with `:?`. Those files are what `docs/OPERATIONS.md` publishes, and draft 05's document-versus-file equality assertion is what keeps that true, which is why this story depends on draft 05 as well as on draft 04's release. The two deployments are: Recipe 1's overlay in one, with the plugin pre-placed under a bind-mounted read-only root, and Recipe 2's overlay in a second, with the plugin delivered by the `fetch-plugins` one-shot service, digest-verified and extracted. They cannot share a deployment because both end at the single `/app/plugins` mount target, and merging them would mean running something neither document publishes, which is the one thing this tier exists not to do.
- Recipe 2's package is served to `fetch-plugins` over **HTTP**, from a test-owned Compose overlay added with its own `-f`, and the published overlay is not edited to accommodate the harness. The rest of this spike consumes nupkgs from a local folder feed and this step cannot: `fetch-plugins` mounts only `plugins:/out`, its BusyBox `wget` speaks http, https, and ftp, and `PLUGIN_PACKAGE_URL` is a `PackageBaseAddress` address rather than a path. The harness lays the packed nupkg out as `<id>/<version>/<id>.<version>.nupkg` in lower case, serves that directory from a static-file container pinned by digest, sets `PLUGIN_PACKAGE_URL` to it, and exports the SHA-256 it computed over the nupkg it packed. This reuses draft 04's arrangement unchanged, which is the point: the same harness proves the same recipe against a locally built image there and a pulled one here.
- The DMS-1436 fixture validator is the payload for both, packed asset-only, and a POST failing its check returns the custom-validation 400 over HTTP against the pulled image.
- A third run exports a deliberately wrong `PLUGIN_PACKAGE_SHA256` for Recipe 2, leaving the committed file and the served package alone, and asserts `fetch-plugins` exits non-zero and the DMS container never starts.
- A fourth run with one allowlisted name misspelled asserts DMS exits with a failed `LoadPlugins` phase in the startup status file, naming the expected path.
- The test asserts the container's `/app/plugins` mount is read-only from inside the container, since Control 1 of the trust model is the one DMS cannot verify itself.
- The test is added to the scheduled or on-release lane rather than the per-PR lane, because it depends on a published artifact that a pull request cannot change.

## Tasks

1. Parameterize draft 04's local-image end-to-end test on the image reference, so the same compose files and assertions run against a pulled tag.
2. Add the four runs: Recipe 1 happy path, Recipe 2 happy path, wrong digest, and misspelled allowlist entry.
3. Add the read-only mount assertion.
4. Wire the test into the scheduled or on-release lane.
5. Record the first green run against the first qualifying published tag on the ticket.
