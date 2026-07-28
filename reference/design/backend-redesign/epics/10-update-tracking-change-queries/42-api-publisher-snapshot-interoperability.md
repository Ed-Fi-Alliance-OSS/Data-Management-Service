---
jira: TBD
jira_url: TBD
---

# Story: Validate API Publisher Snapshot Interoperability

## Description

Add an automated or repeatable interoperability environment proving that an Ed-Fi API Publisher extraction can use DMS snapshot isolation without `--ignoreIsolation=true`.

The validation must distinguish primary and snapshot data, exercise every Publisher source-read surface that participates in an extraction, and document the operator-owned snapshot and read-replica lifecycle and upgrade implications from DMS-1190.

## Acceptance Criteria

- The validation records the DMS, Ed-Fi API, and API Publisher versions and all required configuration so the result is reproducible.
- With a configured snapshot, Publisher runs without `--ignoreIsolation=true` and its `Use-Snapshot: true` source requests are served only from that snapshot.
- A write committed to the primary after snapshot creation does not appear during the Publisher extraction.
- GET-many, GET-by-id, `/deletes`, `/keyChanges`, and `/availableChangeVersions` all use the same snapshot target within one extraction.
- A resource or descriptor mutation carrying `Use-Snapshot: true` returns the snapshot `405` ProblemDetails and `Allow: GET`.
- No configured snapshot returns the expected Snapshot Not Found `404`.
- Retiring or making the configured snapshot unreachable returns the same Snapshot Not Found `404`.
- `Use-Snapshot: false` does not select the snapshot.
- Where the environment also configures a read replica, `Use-Snapshot: true` proves snapshot precedence and a normal eligible read proves automatic read-replica selection.
- The operator workflow documents creation of a SQL Server database snapshot, PostgreSQL point-in-time clone, restored backup, or equivalent read-only source as an external responsibility rather than a DMS or CMS feature.
- The workflow documents engine and `dms.EffectiveSchema` compatibility, read-only credentials, CMS derivative registration, the DMS data-store cache interval, derivative replacement/removal, and retirement of obsolete databases.
- The workflow explains that read replicas may be eventually consistent and that operators requiring a fixed extraction boundary should use a prepared snapshot.
- Release notes call out that `Use-Snapshot: true` changes from ignored in DMS v1.0 to selecting a configured snapshot or returning `404`.
- Release notes explain that Publisher operators must configure a snapshot or continue to opt out explicitly with `--ignoreIsolation=true`.
- Release notes call out that valid existing CMS `ReadReplica` rows become active routing configuration, and advise operators to verify or remove stale derivative configuration before upgrading.
- The validation is included in an appropriate automated suite or has a documented repeatable command, expected results, and troubleshooting guidance suitable for release validation.

## Dependencies

- `38-cms-data-store-derivative-invariants.md`
- `39-snapshot-read-replica-runtime-routing.md`
- `40-snapshot-problem-details.md`
- `41-snapshot-openapi-surface.md`

## Out of Scope

- Database-engine-specific snapshot creation or teardown tooling in DMS or CMS.
- Replica-lag measurement or a read-after-write consistency guarantee for read replicas.
