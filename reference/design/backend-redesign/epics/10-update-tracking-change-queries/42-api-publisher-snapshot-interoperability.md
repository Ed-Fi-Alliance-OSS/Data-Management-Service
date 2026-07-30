---
jira: TBD
jira_url: TBD
---

# Story: Validate API Publisher Snapshot Interoperability

## Description

Add an automated or repeatable interoperability environment proving that an Ed-Fi API Publisher extraction can use DMS snapshot isolation without `--ignoreIsolation=true`.

The validation must distinguish primary and snapshot data, exercise every Publisher source-read surface that participates in an extraction, and document the operator-owned snapshot and read-replica lifecycle and upgrade implications from DMS-1190.

This is a release-validation, environment, and documentation ticket rather than DMS product code. It changes no DMS runtime behavior: the behavior it exercises is delivered by stories 38 through 40, and this story proves the composed result against an external Publisher build and writes the operator workflow and release notes. It depends on an external tool it does not own — the Ed-Fi API Publisher is a separate product whose isolation behavior is an input to this validation, not something this story may change. If Publisher's behavior turns out to differ from what `29-snapshot-support.md` § Verified ODS and Publisher Behavior records, that is a finding to report against the design, not a defect to fix here.

This story deliberately does **not** depend on `41-snapshot-openapi-surface.md`. No acceptance criterion below concerns the served OpenAPI surface, and Publisher keys its isolation on the advertised API major version rather than on the served document, so the OpenAPI work is not an input to any of this validation. Taking that dependency would transitively block this story on an upstream MetaEd package publication — and because this story also owns the release notes, including the extraction-window stability warning, that block would allow the runtime behavior change from stories 39 and 40 to ship without the operator guidance that makes it safe. Story 41 is expected in the same release, and the validation should be re-confirmed once it lands, but it is not a prerequisite for scheduling or closing this story.

## Acceptance Criteria

- The validation records the DMS, Ed-Fi API, and API Publisher versions and all required configuration so the result is reproducible.
- With a configured snapshot, Publisher runs without `--ignoreIsolation=true` and its `Use-Snapshot: true` source requests are served only from that snapshot.
- A write committed to the primary after snapshot creation does not appear during the Publisher extraction.
- GET-many, GET-by-id, `/deletes`, `/keyChanges`, and `/availableChangeVersions` all use the same snapshot target within one extraction, given that the derivative configuration and the underlying snapshot are held unchanged for the extraction's duration.
- The operator workflow states that a `Snapshot` derivative must not be replaced, re-pointed, removed, or recreated at the same connection string while an extraction is reading from it, and distinguishes the outcomes: re-pointing the derivative row silently serves later pages from the replacement image once the configuration cache refreshes; recreating the database at the unchanged connection string does the same once a later connection reaches it, with no configuration change to detect; removing the row or making the database unreachable instead interrupts the extraction with Snapshot Not Found `404`.
- A resource or descriptor mutation carrying `Use-Snapshot: true` returns the snapshot `405` ProblemDetails and `Allow: GET`.
- When no snapshot is configured for the data store, a snapshot-eligible read carrying `Use-Snapshot: true` returns the expected Snapshot Not Found `404`.
- Retiring the configured snapshot or making it unreachable returns the same Snapshot Not Found `404`.
- `Use-Snapshot: false` does not select the snapshot.
- Where the environment also configures a read replica, `Use-Snapshot: true` proves snapshot precedence and a normal eligible read proves automatic read-replica selection.
- The operator workflow documents creation of a SQL Server database snapshot, PostgreSQL point-in-time clone, restored backup, or equivalent read-only source as an external responsibility rather than a DMS or CMS feature.
- The workflow documents engine and `dms.EffectiveSchema` compatibility, read-only credentials, CMS derivative registration, the DMS data-store cache interval, derivative replacement/removal, and retirement of obsolete databases.
- The workflow explains that read replicas may be eventually consistent and that operators requiring a fixed extraction boundary should use a prepared snapshot.
- Release notes call out that `Use-Snapshot: true` changes from ignored in DMS v1.0 to selecting a configured snapshot or returning `404`.
- Release notes explain that Publisher operators must configure a snapshot or continue to opt out explicitly with `--ignoreIsolation=true`.
- Release notes warn that a `Snapshot` derivative must not be replaced, re-pointed, removed, or recreated at the same connection string while an extraction is reading from it, and state that DMS selects the target per request, so re-pointing the row or recreating the database at the unchanged connection string silently serves later pages from the replacement image, while removal or unreachability instead interrupts the extraction with Snapshot Not Found `404`.
- Release notes call out that valid existing CMS `ReadReplica` rows become active routing configuration, and advise operators to verify or remove stale derivative configuration before upgrading.
- The validation is included in an appropriate automated suite or has a documented repeatable command, expected results, and troubleshooting guidance suitable for release validation.

## Dependencies

- `38-cms-data-store-derivative-invariants.md`
- `39-snapshot-read-replica-runtime-routing.md`
- `40-snapshot-problem-details.md`
- An external Ed-Fi API Publisher build, whose recorded version is part of the validation result.

Not a dependency: `41-snapshot-openapi-surface.md`, and therefore not the upstream MetaEd/ApiSchema ticket and published packages that story depends on. See § Description for why.

## Out of Scope

- Database-engine-specific snapshot creation or teardown tooling in DMS or CMS.
- Replica-lag measurement or a read-after-write consistency guarantee for read replicas.
