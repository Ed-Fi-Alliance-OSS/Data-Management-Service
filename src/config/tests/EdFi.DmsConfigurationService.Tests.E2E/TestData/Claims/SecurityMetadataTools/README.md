# Isolated policy fixtures

The isolated CMS launcher copies each scenario directory recursively into a fresh temporary
claims mount, preserving bytes and relative paths. The same directory is exposed to NUnit
through `SECURITY_METADATA_TOOLS_FIXTURE_DIRECTORY`; default E2E claims are not staged over it.
Scenario selectors and reviewed policy fixtures are added with their service tests.

Service cases belong to `Given_SecurityMetadataToolsService`, carry both
`SecurityMetadataToolsService` and their scenario category, and require
`SECURITY_METADATA_TOOLS_SERVICE_RUNNER=true`. The launcher must select and validate the exact
case names through `Invoke-IsolatedCmsTestRun -ExpectedTestNames`. Authentication uses
`CmsTestRequests` directly, without Reqnroll contexts. Do not log tokens or token responses.

Write sanitized comparison evidence to `SECURITY_METADATA_TOOLS_EVIDENCE_DIRECTORY`.
The launcher retains results under `TestResults/cms-isolation/<backend>/<scenario>/<version>/<run>`
before teardown, even on failure. Temporary fixture cleanup never removes these artifacts.
