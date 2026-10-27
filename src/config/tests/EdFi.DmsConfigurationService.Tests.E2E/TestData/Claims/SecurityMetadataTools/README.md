# Isolated policy fixtures

The isolated CMS launcher copies each scenario directory recursively into a fresh temporary
claims mount, preserving bytes and relative paths. The same directory is exposed to NUnit
through `SECURITY_METADATA_TOOLS_FIXTURE_DIRECTORY`; default E2E claims are not staged over it.
The [Hybrid scenario](Hybrid/README.md) covers both bundled Data Standards with reviewed
permissions and full API/offline comparison. Only its `fragments/` subdirectory is mounted.

The Filesystem 5.2 scenario runs the staged CLI against those same Hybrid fragments before
startup, then copies the unmodified merge output to `Claims.json` in an otherwise empty
mount. It starts fresh CMS with `ClaimsSource=Filesystem` and unrestricted loading disabled,
reuses the Hybrid permission/catalog oracles, and verifies the mounted document against
current API metadata. SHA-256 evidence records the input before startup and after verification;
source fragments and retained merge output are also checked for changes. Run it with:

```powershell
pwsh ./eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -Configuration Release -EnvironmentFile ./.env.config.e2e -SecurityMetadataToolsScenario Filesystem -DataStandardVersion 5.2
```

The Upload 5.2 scenario starts a fresh Hybrid instance with an empty mount and unrestricted
claims management enabled. It checks the initial full stored catalog and permissions against
the bundled base, merges the same Hybrid fragments outside the mount, and posts the generated
byte array directly to `/management/upload-claims` with `CmsTestRequests`. No catalog edits
or JSON/text round trips occur. It reuses the reviewed Hybrid oracles and full API/offline
comparison, then requires verify to match the unchanged merged file. Retained evidence includes
initial and final API metadata, the uploaded body, its SHA-256, and unchanged source hashes.
Among the named scenarios, only Upload enables unrestricted management. Run it with:

```powershell
pwsh ./eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -Configuration Release -EnvironmentFile ./.env.config.e2e -SecurityMetadataToolsScenario Upload -DataStandardVersion 5.2
```

Service cases belong to `Given_SecurityMetadataToolsService`, carry both
`SecurityMetadataToolsService` and their scenario category, and require
`SECURITY_METADATA_TOOLS_SERVICE_RUNNER=true`. The launcher must select and validate the exact
case names through `Invoke-IsolatedCmsTestRun -ExpectedTestNames`. Authentication uses
`CmsTestRequests` directly, without Reqnroll contexts. Do not log tokens or token responses.

Write sanitized comparison evidence to `SECURITY_METADATA_TOOLS_EVIDENCE_DIRECTORY`.
The launcher retains results under `TestResults/cms-isolation/<backend>/<scenario>/<version>/<run>`
before teardown, even on failure. Temporary fixture cleanup never removes these artifacts.
