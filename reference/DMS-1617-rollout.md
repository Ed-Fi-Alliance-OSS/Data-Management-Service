# DMS-1617 rollout record

## Publication status

Public documentation is prepared locally, **not deployed**. No stable tool
publication, feed installation, or live redirect verification is claimed here.
Initial publication remains DMS-1617-16 and must use the real normal app 8.1
release. Public-docs rollout remains DMS-1617-17 and must follow successful
8.1.0 feed installation and representative conversion verification.

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

## Publication handoff

Keep this docs branch unpublished until DMS-1617-16 records the normal release
run, exact 8.1.0 package/hash, SBOM and provenance, clean exact and floating feed
installations, and representative conversion equality. Then rebase/revalidate
the prepared docs change against its target branch and publish through the docs
repository's normal process. Record the deployed revision and live old/new URL
checks here under DMS-1617-17. Local generated-page checks are not live-site
verification.

DMS-1617-13 separately still needs its hosted dry run after the dispatch-only
workflow is registered on the DMS default branch. This preparation neither
satisfies that gate nor publishes/promotes packages. Later legitimate patch and
independent stable publication evidence remains DMS-1617-18/-19.
