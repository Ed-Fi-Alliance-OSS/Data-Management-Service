# DMS-843 Change Queries Spike Package

## Purpose

This folder is the review-ready spike package for `DMS-843`.

It consolidates the Change Queries design for the current DMS architecture into one package that can be used for:

- architecture and design review
- implementation planning
- Jira story creation
- later implementation traceability

The package is intended to reduce review round trips by making the public contract, execution model, data model, authorization behavior, rollout constraints, and validation expectations explicit in one place.

## Canonical Status

The numbered documents in this folder are the canonical design for `DMS-843`.

Use these as the source of truth:

1. `01-Feature-Summary-and-Decisions.md`
2. `02-API-Contract-and-Synchronization.md`
3. `03-Architecture-and-Execution.md`
4. `04-Data-Model-and-DDL.md`
5. `05-Authorization-and-Delete-Semantics.md`
6. `06-Validation-Rollout-and-Operations.md`

If older spike notes, local planning files, draft comments, or historical review trails are referenced during review, treat them as context only. They are not approval sources.

## What The Package Decides

The canonical package defines these key DMS-843 decisions:

- existing APIs remain non-breaking
- DMS follows the Ed-Fi default-on posture for Change Queries when `AppSettings.EnableChangeQueries` is absent
- DMS-843 v1 does not expose a client-selectable snapshot or consistent-read mode
- changed-resource execution uses required `journal + verify` behavior through `dms.DocumentChangeEvent`
- the canonical live-row change token lives on `dms.Document.ChangeVersion`
- `dms.Document.IdentityVersion` is required for identity-tracking responsibility and redesign alignment, but it is not the public `/keyChanges` token
- deletes are tracked in `dms.DocumentDeleteTracking`
- key changes are tracked in `dms.DocumentKeyChangeTracking`
- delete tracking and key-change tracking stay in separate artifacts
- descriptor endpoints are included in DMS-843 v1 unless a later explicit product decision says otherwise
- key-change authorization uses the stored pre-update authorization projection for transition visibility

## Normative vs Informative Content

The normative design in this package is defined by:

- the public API contract
- the required behavior and synchronization rules
- the data model and tracking artifacts
- the authorization and consistency rules
- the rollout and validation constraints

Project names, component names, code paths, SQL sketches, and implementation touchpoints are informative planning aids unless a numbered document explicitly makes a behavioral requirement out of them.

## Recommended Review Path

For a focused spike review with the least noise:

1. Read `01` through `06` in order.
2. Use `07` and `08` only after the canonical design is understood.
3. Use `Appendix-A-Feature-DDL-Sketch.sql` only as an implementation-oriented sketch, not as a source of truth.

## Required Review Set

The minimum review set for approving the spike is:

1. `01-Feature-Summary-and-Decisions.md`
2. `02-API-Contract-and-Synchronization.md`
3. `03-Architecture-and-Execution.md`
4. `04-Data-Model-and-DDL.md`
5. `05-Authorization-and-Delete-Semantics.md`
6. `06-Validation-Rollout-and-Operations.md`

Together, these define:

- feature scope and decisions
- public API behavior
- synchronization rules
- architecture and execution model
- data model and DDL requirements
- authorization semantics
- rollout, validation, and operational constraints

## Supporting Artifacts

The following files are useful, but they are supporting artifacts rather than approval sources:

1. `07-Jira-Story-Input.md`
2. `08-Requirements-Traceability.md`
3. `Appendix-A-Feature-DDL-Sketch.sql`

Supporting artifact roles:

- `07-Jira-Story-Input.md`: ticket-ready story decomposition using `Description`, `Acceptance Criteria`, `Tasks`, `Dependencies`, `Design References`, and `Likely Implementation Areas`
- `08-Requirements-Traceability.md`: coverage matrix from spike constraints to package documents and stories
- `Appendix-A-Feature-DDL-Sketch.sql`: PostgreSQL-flavored implementation sketch only

## How To Use The Package

### For Review

- Start with the numbered documents.
- Evaluate support files only after the numbered package is coherent.

### For Jira Story Creation

- Use `07-Jira-Story-Input.md` as the primary ticket-decomposition input.
- Use `08-Requirements-Traceability.md` to verify that each story still maps back to the spike obligations.
- Keep `04-Data-Model-and-DDL.md` as the normative source whenever a ticket touches schema or storage semantics.

### For Implementation Planning

- Use the numbered package for behavior and contract.
- Use `07-Jira-Story-Input.md` to identify story boundaries, dependencies, and concrete work items.
- Use `Appendix-A-Feature-DDL-Sketch.sql` only when a SQL sketch helps discussion; do not let it override the numbered documents.

## Output Coverage

This package covers the spike outputs directly:

- architecture design
- DDL proposal
- API specification
- synchronization algorithm
- delete tracking strategy
- key-change tracking strategy
- migration and backfill strategy
- performance considerations
- validation scenarios

## Relationship To Existing References

This package is aligned to:

- Ed-Fi ODS/API platform guide, Changed Record Queries: <https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/>
- Ed-Fi ODS/API client guide, Using the Changed Record Queries: <https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/>

The alignment target is behavioral and contract parity where DMS-843 follows Ed-Fi guidance, with explicit documentation where DMS makes a product-specific choice.
