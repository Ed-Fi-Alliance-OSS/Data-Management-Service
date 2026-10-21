# DMS-1617 rollout record

## Publication status

Public documentation is prepared locally, **not deployed**. No stable tool
publication, feed installation, or live redirect verification is claimed here.
Initial publication and public-docs deployment are separate post-merge follow-ups,
not DMS-1617 completion criteria. Initial publication must use the real normal app
8.1 release; docs deployment must follow successful stable feed installation and
representative conversion verification. Hosted independent-workflow verification
also remains outstanding until default-branch registration.

## Scope decision — 2026-10-21

Brad approved completing DMS-1617 on pre-merge implementation and verification.
The story retains conversion, packaging, release workflow implementation,
versioning/publication/recovery behavioral tests, local installed-package checks,
applicable CI, internal documentation, and prepared/build-verified public docs.
It does not require merging to main, executing the hosted dispatch workflow,
publishing packages, deploying public docs, or identifying a future patch before
completion.

Task DMS-1617-13 is complete under the revised criteria using its recorded local
verification (93/93 standalone and 195/195 combined tests, stable package smoke,
and read-only published Secrets contract comparison). Its prior HTTP 404 and the
missing hosted evidence remain historical facts. No hosted success is claimed.
The workflow remains dispatch-only, with its implementation safeguards intact.

The active tasks.json now contains tasks 01–15. Former tasks 16–19 are preserved
below as outstanding follow-ups; their IDs are historical references, not active
DMS-1617 completion gates. Responsible roles below route future work and do not
claim that a new Jira issue or individual assignment has been created. The first
real patch should have its own issue when a useful correction is identified.

## Public documentation preparation — DMS-1617-15

- Owning repository: https://github.com/Ed-Fi-Alliance-OSS/ed-fi-alliance-oss.github.io
- Base branch: `ed-fi-api-v81`, commit
  `7e898bece72236f5f8ff7ddcbd197bcc74cb37ac`.
- Local checkout: `/home/brad/work/ed-fi-docs-DMS-1617`; prepared branch:
  `DMS-1617` (not pushed).
- Prepared commit: `01843596e3f22e834af719ee9ec2224350101324`.
- The base includes DMS-1616's removal of Transform. The existing fragment and
  loading sections of the security metadata how-to are preserved byte-for-byte.

Prepared changes:

- Replaced `platform-dev-guide/utilities/cms-hierarchy-tool.md` with
  `security-metadata-tools.md` under `odsApi_versioned_docs/version-8`.
  Documents .NET 10/feed installation, conversion, exact export provenance,
  name/URI rules, authorization preservation, validation failures, output
  protection, editable imports/reserved-set retention, and unchanged-file upload
  with the current configuration and administrative-token prerequisites.
- Updated the Utilities link and the how-to's Building a Security Policy section
  to describe complete `claimSets`/`claimsHierarchy` output.
- Added the full client redirect from
  `/reference/ed-fi-api/8/platform-dev-guide/utilities/cms-hierarchy-tool/` to
  `/reference/ed-fi-api/8/platform-dev-guide/utilities/security-metadata-tools/`.
  An explicit page slug retains the required trailing slash without changing
  other site routes.
- Fixed 59 existing lint findings in nine blog/draft files encountered by the
  repository's required `npm run lint` command: heading levels, list/blank-line
  formatting, descriptive links, code-fence language and table spacing. No
  release facts changed. Markdownlint's angle-bracket URL auto-fixes were
  replaced with Markdown links compatible with Docusaurus MDX.

## Preparation verification

Verified on 2026-10-21 with Node 26.12.0, npm 11.20.0 and the locked Docusaurus
3.10.2 dependencies installed by `npm ci`:

- `npm run lint`: exit 0. Also ran explicit markdownlint on both changed utility
  pages and ESLint on the changed how-to; all passed.
- `npm run clear`, then `npm run build`: exit 0, static site generated. Broken
  Markdown links are configured to throw; Docusaurus's `onBrokenLinks` default
  remains `throw`. No link-validation settings were weakened.
- Inspected the generated new page's title, install/conversion commands,
  provenance, mappings, unsupported attributes, reserved-set behavior and
  unchanged-file upload. Both incoming links resolve to its exact new route;
  all of its security how-to anchors exist.
- Executed the generated redirect JavaScript in an isolated Node VM with a
  controlled location. It targets the exact new route and retains the incoming
  query string and fragment. Inspected the matching meta-refresh fallback.
- Confirmed the how-to from `### Adding New Claims` onward is byte-for-byte
  identical to the base, preserving DMS-1616's fragment and loading guidance.
- Compared the documented contract with the converter, export validator, CLI
  help, name/URI/validation regression tests, and fixture provenance. Ran the
  documented conversion using the existing Release executable on a temporary
  `export.xml` copied from the representative fixture: exit 0, empty stdout and
  stderr, and complete structural equality with the approved expected JSON.
  This is local executable verification, not a stable feed-installation claim.
- `git diff --check` passed. The docs checkout is committed and clean; no
  dependency/lock-file changes or publication actions.

Local evidence is retained under `TestResults/DMS-1617-15-docs/` in the DMS
checkout: install/lint/build logs, the inspection script and its passing log,
and copies of the generated page, redirect, Utilities and how-to HTML. These
ignored build artifacts supplement this committed preparation record. The clean
build had only non-failing update-date/Node localStorage warnings.

## Post-merge follow-ups

These follow-ups remain **outstanding** and do not gate DMS-1617 completion.
Record run URLs, selected source commits, exact package versions/hashes, durable
SBOM/provenance evidence, verification results, and deployed docs revisions here
as the corresponding work actually occurs.

### Hosted independent-workflow verification — deferred from DMS-1617-13

- Responsible role: Configuration Service release maintainer.
- Prerequisite: normal merge registers the dispatch-only workflow on the default
  branch. Complete this hosted verification before enabling independent publication.
- Dispatch `Pkg EdFi.Api.SecurityMetadataTools.yml` on the intended ref with
  `publish_package=false` and full version-source history.
- Record the exact commit, calculated stable version, package hash, successful
  installed-package comparison with the approved representative fixture,
  published Secrets dependency check, SBOM and SLSA provenance for that artifact,
  and skipped publish/promote jobs. A local run does not satisfy this follow-up.
- Keep `publish_package=false`; this verification does not publish or promote a
  package and does not require an app release tag.

### Initial stable publication — former DMS-1617-16

Responsible role: Configuration Service release maintainer. Status: **outstanding**.

After implementation and required CI checks are complete, publish tool 8.1.0 with
committed tool patch zero through the normal app 8.1 Configuration Service release
process. Verify the actual feed package through a clean installation and conversion of
the approved representative export, and record release workflow/artifact/installation
evidence in a story-specific rollout record. This separate follow-up depends on the real
normal 8.1 release and remains outstanding until that release occurs; it does not gate
DMS-1617 completion.

Required outcomes:

- The real normal app 8.1 release builds/publishes EdFi.Api.SecurityMetadataTools 8.1.0
  through the cs- chain and promotes that exact version through the normal release path.
- The published package has corresponding SBOM and provenance tied to its actual package
  bytes, with workflow run URLs, source ref, exact version, and durable artifact
  evidence recorded.
- A clean feed installation of exact version 8.1.0 reports the expected version and
  converts representative.xml to the approved expected JSON; the documented floating
  8.1.* installation command is also verified at this initial rollout.
- The rollout record distinguishes actual publication/installation results from earlier
  local package smoke tests and dry runs and supplies the prerequisite evidence for
  public-docs publication.
- Initial stable publication and feed verification supply the prerequisite evidence for
  public-docs deployment. Successful local builds or prerelease packages do not complete
  this follow-up; DMS-1617 completion does not depend on it.

Verification:

1. Inspect the actual normal 8.1 release workflow runs for tool pack/push, SBOM,
   provenance, release attachments, and promotion of 8.1.0.
2. Using fresh tool/cache directories, run `dotnet tool install
   EdFi.Api.SecurityMetadataTools --tool-path <fresh-tool-directory> --version 8.1.0
   --add-source
   https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json`,
   then run the installed --version and representative conversion.
3. Verify the public documented --global installation command in a disposable clean
   environment and compare generated JSON with the approved fixture.
4. Record exact workflow runs, package version/hash, SBOM/provenance locations,
   installation commands/results, and conversion comparison in the rollout record.

### Public documentation rollout — former DMS-1617-17

Responsible role: Public-documentation maintainer. Status: **outstanding**.

After the initial stable publication follow-up (former DMS-1617-16) confirms the first
stable feed installation and conversion, publish the prepared public-docs change from
DMS-1617-15 through the documentation site's normal process. Verify the live new page,
installation/conversion/loading guidance, updated links, and old URL redirect, and
append the deployed docs revision and checks to the story rollout record.

Required outcomes:

- The prepared new page, Utilities/how-to links, and client redirect are deployed after
  the recorded successful 8.1.0 feed verification.
- The live old /reference/ed-fi-api/8/platform-dev-guide/utilities/cms-hierarchy-tool/
  route redirects to
  /reference/ed-fi-api/8/platform-dev-guide/utilities/security-metadata-tools/, and the
  new route and incoming links resolve on the published site.
- The deployed instructions match the released tool and complete Claims.json upload
  contract, with export provenance and authorization compatibility limitations present.
- The rollout record includes the docs change/deployment revision, live URLs, build
  result, and redirect/link verification.

Verification:

1. Rebase/revalidate the prepared docs change against its target branch, re-run the
   owning documentation repository's build, and publish through its normal process after
   confirming the initial feed-installation evidence.
2. Open the published
   /reference/ed-fi-api/8/platform-dev-guide/utilities/cms-hierarchy-tool/ route in a
   browser and verify the client redirect reaches
   /reference/ed-fi-api/8/platform-dev-guide/utilities/security-metadata-tools/; inspect
   the new page and Utilities/security metadata how-to links.
3. Follow the published install and conversion instructions in a clean environment and
   confirm the loading prerequisites and unedited-file procedure agree with the upload
   integration test.
4. Append the deployed docs ref, URLs, and live checks to the story rollout record.

### Legitimate independent patch — former DMS-1617-18

Responsible role: Future patch issue owner and Configuration Service release maintainer.
Status: **outstanding**.

After the initial stable publication, implement a real identified improvement or
correction to the released tool, add its focused regression coverage, and advance the
committed tool patch from zero to one on the intended release ref. Follow VERSIONING.md
to prepare and dry-run the 8.1.1 independent package. This task is contingent on an
actual useful correction and does not authorize inventing a meaningless change solely to
exercise publication. Track the actual correction in a separate future issue. The
example patch 1/version 8.1.1 applies only if it is the next unused version; otherwise
use the next committed patch under VERSIONING.md consistently in preparation and
publication.

Required outcomes:

- A concrete useful issue/improvement is identified and documented, implemented within
  the released conversion/tooling contract, and covered by a regression test or
  appropriate verification that demonstrates the change.
- The intended source ref contains the legitimate change and committed tool patch 1, so
  the shared stable-release mode calculates 8.1.1 independently of the app's
  patch/prerelease version.
- The tool unit tests, relevant upload/package checks, and versioning checks pass, and
  the independent dispatch dry run builds/validates 8.1.1 with SBOM/provenance and no
  publication/promotion.
- The rollout record links the actual correction and selected source ref and records
  dry-run evidence; this preparation does not count as the second stable publication.

Verification:

1. Review and record the real correction's trigger, user benefit, source change, and
   targeted regression proof before advancing the patch.
2. Run the tool unit tests and eng/ci/tests/SecurityMetadataToolsVersioning.Tests.ps1;
   rerun the package smoke and any affected upload scenario.
3. Dispatch Pkg EdFi.Api.SecurityMetadataTools.yml on the intended patch ref with
   publish_package=false and confirm calculated version 8.1.1, successful installed
   conversion, SBOM/provenance, and absence of push/promotion.
4. Verify the selected ref contains patch 1 and record its commit and dry-run URL for
   the publication task.

### Independent stable publication — former DMS-1617-19

Responsible role: Future patch issue owner and Configuration Service release maintainer.
Status: **outstanding**.

After the legitimate patch preparation follow-up (former DMS-1617-18), publish 8.1.1
from the same selected ref through Pkg EdFi.Api.SecurityMetadataTools.yml with
publish_package enabled. Verify its exact-version feed installation, representative
conversion, SBOM/provenance, and release-view promotion, then record this future patch
issue's rollout evidence separately from DMS-1617 completion. Use the actual next
committed patch version if 8.1.1 is no longer the next unused version.

Required outcomes:

- The actual dispatch publishes EdFi.Api.SecurityMetadataTools 8.1.1 and promotes
  exactly 8.1.1 to the release view without running the app release pipeline or changing
  the version from the dry run.
- Workflow run/source-ref evidence, exact package version/hash, SBOM/provenance for the
  published artifact, and promotion result are recorded durably.
- A clean installation of exact feed version 8.1.1 runs --version and converts the
  approved representative export successfully; the patch's focused regression check also
  passes with the installed package where applicable.
- Publication refusal for an existing version directs maintainers to a new committed
  patch instead of an overwrite or silent skip.
- The rollout record documents the actual independent stable publication and its
  feed-installation/conversion, artifact, and promotion evidence. These checks belong to
  the future patch issue and do not gate DMS-1617 completion.
- The later normal-app-release/tool-8.1.2 scenario remains covered automatically by
  DMS-1617-12; no actual publication is required for DMS-1617 closure.

Verification:

1. Dispatch Pkg EdFi.Api.SecurityMetadataTools.yml on the verified patch ref with
   publish_package=true and inspect its push, exact-version promotion, SBOM, and
   provenance results.
2. Using fresh tool/cache directories, install EdFi.Api.SecurityMetadataTools version
   8.1.1 from the Ed-Fi feed; run the installed --version, representative conversion,
   and applicable patch regression check.
3. Compare generated JSON with the approved expected fixture and verify the feed's
   release view contains exact version 8.1.1.
4. Review the future patch issue's publication evidence and confirm its required checks
   pass before completing that issue; DMS-1617 does not remain open for this
   publication.
