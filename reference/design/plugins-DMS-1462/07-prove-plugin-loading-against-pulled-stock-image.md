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

## Acceptance Criteria

- A test under `eng/` or the E2E suite pins an `edfialliance/ed-fi-api` tag at or after the first release carrying `LoadPlugins`, pulls it, and never invokes `docker build`. The pinned tag is a variable with the first qualifying release as its default.
- Both acquisition recipes run in one Compose deployment exactly as `docs/OPERATIONS.md` publishes them: one plugin pre-placed under a bind-mounted read-only root, and one delivered by the `fetch-plugins` one-shot service from a local folder feed, digest-verified and extracted.
- The DMS-1436 fixture validator is the payload for both, packed asset-only, and a POST failing its check returns the custom-validation 400 over HTTP against the pulled image.
- A second run with the Recipe 2 digest deliberately wrong asserts `fetch-plugins` exits non-zero and the DMS container never starts.
- A third run with one allowlisted name misspelled asserts DMS exits with a failed `LoadPlugins` phase in the startup status file, naming the expected path.
- The test asserts the container's `/app/plugins` mount is read-only from inside the container, since Control 1 of the trust model is the one DMS cannot verify itself.
- The test is added to the scheduled or on-release lane rather than the per-PR lane, because it depends on a published artifact that a pull request cannot change.

## Tasks

1. Parameterize draft 04's local-image end-to-end test on the image reference, so the same compose files and assertions run against a pulled tag.
2. Add the three runs: happy path, wrong digest, misspelled allowlist entry.
3. Add the read-only mount assertion.
4. Wire the test into the scheduled or on-release lane.
5. Record the first green run against the first qualifying published tag on the ticket.
