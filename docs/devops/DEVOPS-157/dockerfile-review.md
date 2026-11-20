# Data-Management-Service: Dockerfile review and build improvements

**Audience:** senior DevOps engineer, new to this repo, coming from GitLab CI
**Status:** DRAFT for team review. Proposals only. Nothing in the repo was changed.
**Context:** Jira DEVOPS-157 (epic DEVOPS-154). Companion of [ci-workflow-review.md](./ci-workflow-review.md) and [workflow-structure-review.md](./workflow-structure-review.md). Baselines: the ODS-Admin-API and Ed-Fi-AdminApp Dockerfile reviews (DEVOPS-155, DEVOPS-156).
**Evidence:** a read of the four Dockerfiles, the two `.dockerignore` files, the 29 Compose files and 19 `.env*` files under `eng/docker-compose`, `docs/DOCKER.md`, `docs/NUGET-LOCK-FILES.md`, the image-building jobs of the workflows, the Actions cache listing, and the Docker Hub tag list (public API) on 2026-11-12.

---

## 1. How images are built today

| Group | Files | What it does |
|---|---|---|
| **App, built from source** (PR CI, E2E, local, nightly, templates) | `src/dms/Dockerfile` (167 lines), `src/config/Dockerfile` (140 lines) | Four stages. `pluginsource`: copies `src/plugins` from a named context and prunes `bin/`, `obj/` and `results.sarif`. `build`: copies `Directory.Packages.props`, `nuget.config`, the props and targets, then **only the `.csproj` and `packages.lock.json` of every project**, runs `dotnet restore --locked-mode` (frontend, CLI, plugin host), then copies the sources and runs `dotnet publish --no-restore`. `runtimebase`: `aspnet:10.0.13-alpine3.24` by digest plus `apk add` (openssl, bash, postgresql18-client, jq, icu). `setup`: copies the publish output and `run.sh`. |
| **App, published** (Docker Hub) | `src/dms/Nuget.Dockerfile`, `src/config/Nuget.Dockerfile` (33 lines each) | No compile. `wget` of the released NuGet package from the Azure Artifacts feed, `unzip` onto the same Alpine runtime base, `run.sh` as entrypoint. Built by `docker-publish-dms` / `docker-publish-cs` in `on-prerelease.yml` with `cache-from: type=registry,ref=<image>:pre` and `cache-to: type=inline`. |
| **Test helpers** | `eng/docker-compose/tests/oauth-token-contract/Dockerfile` | Token contract E2E client. |
| **Infrastructure** | Compose only: PostgreSQL 18.6 Alpine, SQL Server 2025, Keycloak 26.8.1, Kafka 3.9.0, Kafka UI, Kafka Connect (`ed-fi-kafka-connect`), nginx, dotnet-monitor, Swagger UI | All third-party images are pinned by digest except `mssql/server:2025-latest` and `ed-fi-kafka-connect:pre` (the CDC lane pins the Connect image by digest through `CDC_CONNECT_IMAGE`). Dependabot `docker-compose` refreshes the pins weekly. |

Build contexts: the source Dockerfiles take `src/dms` (or `src/config`) as the default context and `src/` as a **named context** (`parentdir`), so `Directory.Packages.props`, `nuget.config` and `src/plugins` are reachable without a `src/.dockerignore` (the file comments explain why none was added). In CI the same two images are built **once per PR** by `build-ci-docker-images` and pushed to GHCR by commit SHA (`data-management-service-ed-fi-api-ci:<sha>`), with a registry cache tag `buildcache` written only by non-PR runs; the E2E and start-up jobs pull them (`pull-ci-images` with jittered retry). A daily job keeps the newest 100 versions and protects the cache tag.

Good practices already in place: multi-stage builds; SDK and runtime base images pinned by **tag and digest**; restore layer isolated from source changes (csproj and lock files copied first); `--locked-mode` restore inside the image (the lock graph is enforced where the artifact is produced); Alpine runtime (86 MB published image); `apk --no-cache` with version constraints; `.dockerignore` excludes `bin/`, `obj/`, tests and the solution file; Hadolint plus Docker Scout on every Dockerfile change and a weekly scan; a Pester test (`LockFileParity.Tests.ps1`) that keeps the Dockerfile COPY lists in step with the project list; build once per PR and reuse through GHCR.

---

## 2. Findings

Priority: **H** high value, **M** medium, **L** low. Evidence is the file and line it was read from.

### 2.1 Build speed and cache

| # | Finding | Evidence | Why it matters | Pri |
|---|---|---|---|---|
| D1 | **The BuildKit cache written by PR runs is the largest consumer of the 10 GB Actions cache.** Seven E2E-family jobs (`run-e2e-tests-*`, `run-instance-management-*`) and `build-and-start-dms` still carry a fallback path that builds both images with `actions/cache` (`Linux-buildx-<hash>`, `cache-to: type=local`) when `use_prebuilt_images != 'true'` (fork PRs, or the `REBUILD_DOWNSTREAM_IMAGES` variable). On 2026-11-12 the cache held 110 entries, **8.96 GB of 10 GB**; 7.12 GB belonged to one PR ref (`refs/pull/1452/merge`: three `Linux-buildx` entries of 2 GB each plus 99 `buildkit-blob` entries), and `main` held 1.4 GB (one 1.1 GB NuGet entry and three small buildx entries). | `gh api repos/.../actions/caches`; `on-dms-pullrequest.yml` lines 2099-2170 (and the sibling jobs) | A PR that takes the fallback path evicts `main`'s NuGet cache (1.1 GB, used by every job); the next runs restore nothing and pay the full download. The fallback is also the slow path that the GHCR design was built to avoid. | H |
| D2 | **The published image is not the image that was tested.** PR CI tests the source-built image (`src/dms/Dockerfile`); the release publishes `Nuget.Dockerfile`, which downloads the NuGet package built by `pack-dms` (a third build of the same commit) and unpacks it on the same runtime base. The two runtime stages are maintained by hand in two files each (DMS: `openssl bash postgresql18-client jq icu-libs` vs `postgresql18-client jq icu-libs`; CMS adds `icu-data-full` only in some). | the four Dockerfiles | Different package set and different build path between what CI exercised and what customers pull. Already partially mitigated (same digest-pinned base), but drift is by hand. | M |
| D3 | **`wget` of the package in `Nuget.Dockerfile` has no retry and no checksum, and the publish job sleeps 20 s before building.** The job runs after `publish-package-dms` pushed the package to Azure Artifacts. | `Nuget.Dockerfile` line 25; `on-prerelease.yml` `docker-publish-dms` "Wait 20s" | Same timing race the ODS-Admin-API review found (there, 3 of 17 prereleases). Here the last 30 days show 19 failed prerelease runs; the failure classification in `ci-workflow-review.md` section 4 says how many were this. The SHA-256 computed by `pack-dms` is never checked by the image build. | M |
| D4 | **No NuGet cache mount in the source Dockerfiles.** The restore layer is cached by layer identity only. | `src/dms/Dockerfile` line 89 | A change to any `.csproj` or lock file re-downloads every package inside the builder. `RUN --mount=type=cache,target=/root/.nuget/packages` keeps packages across builds on the same builder; with the registry cache the benefit is mostly local and in the fresh-rebuild job. | L |
| D5 | **`fresh-rebuild-docker-images` runs `docker compose build --no-cache` (about 3 min) at the end of the PR when `fresh_build_required` is true**, in addition to the GHCR build at the start. | `on-dms-pullrequest.yml` lines 4117-4136 | It proves the Dockerfile builds from scratch after a dependency change, which is useful, but it builds the images a second time in the same run and is in the `dms-ci-gate` needs list, so it extends the PR by 3 min after `build-and-start-dms`. Could run in parallel from the start of the run instead of after start-up. | L |
| D6 | **`build-ci-docker-images` writes the registry cache only on non-PR events**, and the cache tag is verified by the cleanup job. PR builds take about 6 s each (cache hit) and the fresh rebuild about 3 min. | job step timings (medians over 44 runs) | Works as designed; noted so nobody "fixes" it. A PR that changes `Directory.Packages.props` pays the full restore once (no PR write), which is the intended trade-off. | n/a |

### 2.2 Image size and layers

| # | Finding | Evidence | Why it matters | Pri |
|---|---|---|---|---|
| D7 | **Published images are small (86 MB for `ed-fi-api`, 94 MB for `ed-fi-api-configuration-service`, compressed) and amd64 only.** Docker Hub lists one architecture for every tag of both images (86 and 17 tags); there is no `platforms:` in the publish jobs. | Docker Hub tags API; `on-prerelease.yml` `docker-publish-*` | Fine for size. arm64 developers (Apple Silicon, Graviton) run under emulation or build locally from source. The AdminApp review found a 30-minute QEMU publish; here the decision is still open, so native `ubuntu-24.04-arm` runners would be the cheap way if arm64 is wanted. | L |
| D8 | **Runtime helpers installed in the app image:** `bash`, `jq`, `postgresql18-client` (and `openssl` on the 3.5 line) in the source image; `postgresql18-client jq icu-libs` in the published one. `run.sh` (194 lines) parses the admin connection string in `awk` and waits for PostgreSQL with the client. | `src/dms/Dockerfile` line 142; `src/dms/run.sh` | Each package is a CVE surface that Scout reports against the image (the ODS-Admin-API review's 26 Scout alerts were base-image and apk packages). The readiness wait could be done in .NET or by Compose `depends_on: condition: service_healthy`, which the stack already uses for the database. | M |
| D9 | **`.pdb` files are copied into the runtime image.** | `src/dms/Dockerfile` line 157; `src/config/Dockerfile` line 128 | Small size cost; useful for stack traces. A conscious choice is fine; note it in `docs/DOCKER.md`. | L |
| D10 | **The plugin tree is copied whole, then pruned, in a dedicated stage** (`pluginsource`) because `src/` has no `.dockerignore` and named contexts are not filtered by the default context's file. | `src/dms/Dockerfile` lines 7-30 | Correct and well documented, but it is a workaround: a `src/.dockerignore` (`**/bin`, `**/obj`, `**/results.sarif`, `**/Tests*`) would let both Dockerfiles copy from `parentdir` directly and remove one stage from each. The comment says this was avoided to not change the other image's context; both images would change the same way, so it is a shared decision, not a blocker. | L |

### 2.3 Security and supply chain

| # | Finding | Evidence | Why it matters | Pri |
|---|---|---|---|---|
| D11 | **Containers run as root.** No `USER` instruction in any of the four Dockerfiles, no `adduser`, and the Compose files set no `user:`. | `grep -n USER src/*/Dockerfile src/*/Nuget.Dockerfile` (no match) | The ODS-Admin-API images run as `edfi`. A compromised process in the API container has root inside the container; on Kubernetes, `runAsNonRoot` policies will refuse the image. Port 8080 needs no privilege. | H |
| D12 | **Base images are refreshed by hand.** The digests of `sdk:10.0.103-alpine3.23` and `aspnet:10.0.13-alpine3.24` were bumped in DMS-1608 (2026-10-14 and the follow-up #1397) after NuGet audit failures and "critical ASP.NET Core CVEs"; before that, in February and June 2026. Dependabot has `nuget` and `docker-compose` ecosystems but **no `docker` ecosystem** for the Dockerfiles. | `git log -L` on the `FROM` lines; `.github/dependabot.yml` | Compose images get weekly bump PRs, the application base images do not. Scout findings against the runtime layer will keep reappearing between manual bumps. Same gap as S11 in both sibling reviews. | H |
| D13 | **`apk` version constraints use `~` ranges** (`openssl=~3.5`, `bash=~5`, `jq=~1.8`, `icu-libs=~78`) and `Nuget.Dockerfile` for CMS pins only two of three packages. | `src/dms/Dockerfile` line 142; `src/config/Nuget.Dockerfile` line 12 | Reproducible enough for Alpine (the index is tied to the base digest), and Hadolint `DL3018` is satisfied. The inconsistency between the four files is the real issue (D2). | L |
| D14 | **No image signing or attestation for the published images.** SBOM and SLSA provenance cover the NuGet packages; the Docker images get neither (`docker/build-push-action` without `sbom: true` / `provenance: mode=max`, no `cosign`, no `actions/attest-build-provenance`). | `on-prerelease.yml` `docker-publish-*` | Consumers of the Docker Hub image cannot verify what built it. DEVOPS-158 already prototypes `actions/attest` for ODS-Admin-API; the same step would cover images here. | M |
| D15 | **The Docker Scout PR check uses a dedicated read-only Docker Hub identity** (`DOCKER_SCOUT_USERNAME` / `DOCKER_SCOUT_TOKEN`) and the publish token lives in the `publishing` environment; the weekly `analyze-docker-images` scan builds the two source images and fails on critical or high findings. | `docs/CI-PUBLISHING-CREDENTIALS.md`; `analyze-docker-images.yml` | Good. The weekly scan does not scan the **published** `:pre` image (the one customers pull); it rebuilds from source on `ubuntu-24.04`. | M |

### 2.4 Maintainability

| # | Finding | Evidence | Why it matters | Pri |
|---|---|---|---|---|
| D16 | **The COPY list of projects is duplicated: once for `.csproj` + lock file and once for sources, in each Dockerfile (13 + 13 lines for DMS, 9 + 9 for CMS), and mirrored by the Pester parity test.** | `src/dms/Dockerfile` lines 60-108 | Every new project needs three edits (two COPY lines and the test fixture). The doc calls it a "maintenance hotspot". A `COPY --parents` (BuildKit 1.7 labs) or a restore-only stage fed by `find`-generated lists would remove the hand-kept lists; both need a BuildKit frontend decision. | M |
| D17 | **Twenty-nine Compose files and nineteen `.env*` files**, composed by PowerShell (`start-local-dms.ps1`, `start-published-dms.ps1`, `bootstrap-*.psm1`) and documented in a 1,752-line README. Diagnostics, tmpfs, headroom, thread and pool overlays (`local-control-e3-certificates.yml`, `local-control-e4-threads.yml`, `local-control-e5-pool16.yml`, `local-postgresql-headroom.yml`, `local-resource-runner-approx.yml`) look like artifacts of specific investigations. | `ls eng/docker-compose` | The layering is powerful but a newcomer cannot tell which overlays are products (published, local, keycloak, mssql, kafka) and which are experiments. A short "overlay catalog" with owner and purpose, and retiring the one-off control overlays, lowers the review cost of every change that touches compose paths (which also trigger the four template workflows and the smoke test). | M |
| D18 | **`run.sh` does both readiness waiting and application start**; the connection-string parser is 60 lines of `awk`. | `src/dms/run.sh` | Logic that is hard to test (there is a Pester test for the wait) and duplicated in the CMS `run.sh`. Moving the wait to the host app (`WaitForPostgres` on startup, already the pattern for `ValidateStartupInstancesTask`) or to Compose health conditions would shrink the image and the scripts. | L |

---

## 3. Proposed improvements

IDs continue the DF numbering used in the ODS-Admin-API review so the three repos can be summarized together. Effort: **S** hours to a day, **M** days, **L** weeks.

### 3.1 Short term (hours to days, low risk, Dockerfile-only)

| ID | Item | Findings | Expected effect | Effort |
|---|---|---|---|---|
| DF1 | **Add a Dependabot `docker` ecosystem** for `src/dms`, `src/config` (both Dockerfiles) with a weekly schedule and a group; keep the `docker-compose` one. | D12 | Base-image digests refreshed by PR instead of by incident; Scout runtime findings clear on a cadence. Review cost: one grouped PR per week that runs the Dockerfile scan. | S |
| DF2 | **Run as a non-root user** in all four Dockerfiles (`adduser -D edfi`, `USER edfi`, `COPY --chown`), port 8080 unchanged; verify `run.sh` needs no root (it writes nothing). | D11 | Removes the most common hardening finding; Kubernetes `runAsNonRoot` compatible. Needs one E2E pass per image. | S |
| DF3 | **Make the four runtime stages identical** (one list of `apk` packages, one `ENV` block) and note in `docs/DOCKER.md` which packages exist for which reason; drop `bash` and `jq` if `run.sh` can live on `ash` (it already runs under `/bin/ash` in the published image). | D2, D8, D13 | Fewer packages to patch; the tested and published runtime layers match. | S |
| DF4 | **Verify the package the published image downloads**: pass the SHA-256 that `pack-dms` already computes as a build arg and check it after `wget`; add a bounded retry (or reuse the `pull-ci-images` backoff idea). Replace the fixed 20 s sleep with a poll of the feed. | D3 | Prerelease stops failing on feed timing; the image provably contains the attested package. | S |
| DF5 | **Decide on `.pdb` in the runtime image** and document it. | D9 | Clarity only. | S |
| DF6 | **Scan the published `:pre` image in the weekly job** (`docker scout cves edfialliance/ed-fi-api:pre`) in addition to, or instead of, rebuilding from source. | D15 | The scan matches what customers run. | S |

### 3.2 Mid term (days to weeks)

| ID | Item | Findings | Expected effect | Effort |
|---|---|---|---|---|
| DF7 | **Remove the per-job image-build fallback from the E2E family** (or restrict it to fork PRs with `cache-to` disabled): fork PRs already cannot push to GHCR; a single `build-ci-docker-images` fallback that uploads the images as an artifact (`docker save`) would serve them. Stop writing `Linux-buildx-*` entries from PR refs. | D1 | Protects `main`'s NuGet cache from eviction; removes about 120 lines repeated in 8 jobs. Needs one fork-PR trial. | M |
| DF8 | **Attest the images**: `provenance: mode=max` and `sbom: true` on `docker/build-push-action`, plus `actions/attest-build-provenance` (keyless) on the pushed digest; reuse the DEVOPS-158 PoC. | D14 | Image provenance alongside the package provenance; prepares the replacement of the SLSA generator fork. | M |
| DF9 | **Overlay catalog and retirement pass** for `eng/docker-compose`: a table (file, purpose, owner, used by which workflow or script), then delete or move the investigation overlays to `eng/performance`. | D17 | Lower review cost; fewer template and smoke workflow triggers from incidental compose edits. | M |
| DF10 | **Remove the hand-kept COPY lists** with a generated restore manifest (a small script that emits the `.csproj` + lock-file list, checked by the existing parity test) or `COPY --parents` once the team accepts a BuildKit labs frontend. | D16 | New projects need one edit instead of three. | M |

### 3.3 Long term (quarter and beyond)

| ID | Item | Findings | Expected effect | Effort |
|---|---|---|---|---|
| DF11 | **Build once, publish the tested image**: let the prerelease retag the GHCR CI image built and tested for the merge commit (or build the published image from the CI-built publish output) instead of a third build through `Nuget.Dockerfile`. | D2 | The artifact customers pull is the artifact CI tested; removes two Dockerfiles. Needs a decision on how the package feed and the image stay linked (the image would carry the package SHA as a label). | L |
| DF12 | **Multi-arch publish** (amd64 + arm64) on native `ubuntu-24.04-arm` runners if arm64 users exist; never under QEMU for a .NET publish of this size. | D7 | Native images for Apple Silicon and Graviton. | M-L |
| DF13 | **Move the readiness wait out of `run.sh`** into the host or Compose health conditions; shrink `run.sh` to `exec dotnet ...`. | D8, D18 | Smaller image, one less shell parser to test. | M |

---

## 4. Sketch for DF4 (verified download in the published image)

```dockerfile
ARG VERSION=0.0.0
ARG PACKAGE_SHA256=""
# Poll the feed with a bound, then verify the package before unpacking it.
RUN for i in 1 2 3 4 5 6; do \
        wget -q -O /tmp/pkg.zip "https://pkgs.dev.azure.com/.../EdFi.Api/versions/${VERSION}/content" && break; \
        echo "attempt $i failed, waiting"; sleep 20; \
    done && \
    if [ -n "$PACKAGE_SHA256" ]; then echo "$PACKAGE_SHA256  /tmp/pkg.zip" | sha256sum -c -; fi && \
    unzip -q /tmp/pkg.zip -d /app/ && rm /tmp/pkg.zip
```

The SHA comes from `needs.pack-dms.outputs.hash-code` (already base64-encoded for the SLSA subject; decode it in the job). This keeps the current "image from the published package" model while removing the timing race and the unverified download.

---

## 5. Questions for the development team

Each question has a read-only command and what it returned.

```bash
R=Ed-Fi-Alliance-OSS/Data-Management-Service
# Q1: who refreshes the base-image digests, and was a Dependabot docker ecosystem tried?
git log --format='%h %cs %an %s' -L '/^FROM mcr.microsoft.com\/dotnet\/aspnet/,+1:src/dms/Dockerfile' | grep -E '^[0-9a-f]{9} ' | head
grep -n 'package-ecosystem' .github/dependabot.yml
# Q2: is running as root a known decision (Kubernetes deployments, file permissions)?
grep -n 'USER\|adduser' src/dms/Dockerfile src/config/Dockerfile src/dms/Nuget.Dockerfile src/config/Nuget.Dockerfile
# Q3: how often does the fallback image build run on PRs (fork PRs, REBUILD_DOWNSTREAM_IMAGES)?
gh api "repos/$R/actions/caches?per_page=100" --paginate -q '.actions_caches[] | "\(.size_in_bytes/1048576|floor) MB \(.ref) \(.key[:40])"' | sort -rn | head
gh api repos/$R/actions/cache/usage
# Q4: is amd64-only publishing intentional? Are there arm64 users?
curl -s "https://hub.docker.com/v2/repositories/edfialliance/ed-fi-api/tags?page_size=3" | jq '.results[] | {name, arch: [.images[].architecture]}'
# Q5: which compose overlays are still used by anything?
for f in eng/docker-compose/*.yml; do echo "$(grep -rl "$(basename $f)" .github eng/*.ps1 eng/docker-compose/*.ps1 eng/docker-compose/*.psm1 build-*.ps1 2>/dev/null | wc -l) $(basename $f)"; done | sort -n | head -12
# Q6: did the prerelease ever fail on the package download or the 20 s wait?
gh run list -R $R --workflow on-prerelease.yml --status failure --limit 20 --json databaseId,createdAt -q '.[] | "\(.databaseId) \(.createdAt[:10])"'
```

**Found:** Q1: five manual bumps since 2024, the last two in DMS-1608 (October 2026); Dependabot has `nuget` and `docker-compose` only. Q2: no `USER` in any Dockerfile. Q3: 110 cache entries, 8.96 GB of 10 GB, 7.12 GB on `refs/pull/1452/merge` (three 2 GB `Linux-buildx` entries); so the fallback path ran on at least one recent PR. Q4: every tag is `amd64` only. Q5: see the catalog proposal (DF9); the answer needs the script run by someone who knows the overlays. Q6: 19 failed prerelease runs in 30 days; their causes are classified in `ci-workflow-review.md` section 4.

---

## 6. Limits of this review

- Scout alert counts and CVE IDs could not be read (`code-scanning/alerts` returns 403 for this account). Findings about CVEs come from the workflow design, not from the alert list.
- Image sizes are the compressed sizes Docker Hub reports on 2026-11-12 for `edfialliance/ed-fi-api` and `edfialliance/ed-fi-api-configuration-service`.
- No image was built or scanned locally; build-time figures come from the CI step timings in `ci-workflow-review-detailed.md`.
- No repository file was changed.
