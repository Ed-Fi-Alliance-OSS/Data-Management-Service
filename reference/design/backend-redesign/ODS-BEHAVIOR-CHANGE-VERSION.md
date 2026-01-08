# ODS/API Behavior: `ChangeVersion`

## Purpose

This document describes how the Ed-Fi ODS/API implements `ChangeVersion`, with a focus on the behavior that **a resource’s serialized representation can change (due to joins/lookups) without the resource’s `ChangeVersion` changing**.

All code references below are **relative to `/home/brad/work/ods/`** (i.e., paths begin with `Ed-Fi-ODS/...`).

## Summary (what to expect)

- In ODS/API, `ChangeVersion` is a **global, monotonically increasing `bigint`**, sourced from a single database sequence.
- A resource’s `ChangeVersion` changes when (and only when) the resource’s own row is inserted/updated.
- Deletes and key changes are recorded as **separate change events** in `tracked_changes_*` tables, each assigned its own `ChangeVersion` from the same global sequence.
- ODS/API does **not** attempt to make `ChangeVersion` representation-sensitive to upstream changes in other tables (e.g., a referenced person’s UniqueId changing), unless those upstream changes cause an actual update of the resource’s own tables.

## Where `ChangeVersion` comes from: a global sequence

ODS defines a single sequence in the `changes` schema and uses it as the source for all change events.

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/Changes/0020-CreateChangeVersionSequence.sql`

```sql
CREATE SEQUENCE IF NOT EXISTS changes.ChangeVersionSequence START WITH 1;

CREATE OR REPLACE FUNCTION changes.updateChangeVersion()
    RETURNS trigger AS
$BODY$
BEGIN
    new.ChangeVersion := nextval('changes.ChangeVersionSequence');
    RETURN new;
END;
$BODY$ LANGUAGE plpgsql;
```

Key points:

- `ChangeVersion` values are **global** (comparable across all resources).
- Values can have **gaps** (e.g., due to rollbacks) because `nextval(...)` increments the sequence even if a transaction is not committed.
- ODS uses the **sequence value as the “newest” change indicator** (rather than `MAX(ChangeVersion)` across resource tables), which naturally covers change events written to tracked change tables (deletes/keyChanges) as well as resource-row updates.

## How resource tables get a `ChangeVersion` column

ODS adds a `ChangeVersion` column to most `edfi.*` tables and sets the default to allocate a new value from the global sequence for new rows.

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/Changes/0030-AddColumnChangeVersionForTables.sql`

```sql
-- For performance reasons on existing data sets, all existing records will start with ChangeVersion of 0.
...
ALTER TABLE edfi.AcademicWeek ADD ChangeVersion BIGINT DEFAULT (0) NOT NULL;
ALTER TABLE edfi.AcademicWeek ALTER ChangeVersion SET DEFAULT nextval('changes.ChangeVersionSequence');
```

This means:

- Existing data may initially have `ChangeVersion = 0` until it is updated.
- New inserts will default `ChangeVersion` using `nextval('changes.ChangeVersionSequence')`.

## When a resource’s `ChangeVersion` changes

### Inserts (new rows)

For tables that have `ALTER ... SET DEFAULT nextval('changes.ChangeVersionSequence')`, inserts obtain a new `ChangeVersion` from the sequence via the column default (unless explicitly overridden).

### Updates (existing rows)

ODS uses triggers to set a new `ChangeVersion` on every `UPDATE` of a resource row.

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/Changes/0210-CreateTriggersForChangeVersionAndKeyChanges.sql`

```sql
CREATE TRIGGER UpdateChangeVersion BEFORE UPDATE ON edfi.student
    FOR EACH ROW EXECUTE PROCEDURE changes.UpdateChangeVersion();
```

Combined with `changes.UpdateChangeVersion()` (see above), this makes the semantics very direct:

- If the database executes an `UPDATE` statement on a row, that row’s `ChangeVersion` becomes `nextval(changes.ChangeVersionSequence)`.
- If the resource is **not updated**, its `ChangeVersion` will **not change**, even if the *serialized representation* would look different due to upstream lookup/join behavior.

## How ODS exposes “available change versions”

ODS’s “available change versions” endpoint uses a database function that returns the sequence’s `last_value`.

### Database function

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/Changes/1010-CreateGetMaxChangeVersionFunction.sql`

```sql
CREATE OR REPLACE FUNCTION changes.GetMaxChangeVersion() RETURNS bigint AS
$$
DECLARE
    result bigint;
BEGIN
    SELECT last_value FROM changes.ChangeVersionSequence INTO result;
    RETURN result;
END
$$ language plpgsql;
```

### API/provider usage

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Features/ChangeQueries/Providers/AvailableChangeVersionProvider.cs`

```csharp
var cmdSql =
    $@"SELECT {ChangeQueriesDatabaseConstants.SchemaName}.GetMaxChangeVersion() as NewestChangeVersion";

var maxChangeVersion = await conn.ExecuteScalarAsync<long>(cmdSql);

return new AvailableChangeVersion { NewestChangeVersion = maxChangeVersion };
```

Notes:

- The `SchemaName` constant is `"changes"` (see `Ed-Fi-ODS/Application/EdFi.Ods.Features/ChangeQueries/ChangeQueriesDatabaseConstants.cs`).
- `OldestChangeVersion` is not populated by this provider and will default to `0` in the response object.

## How change query filtering works

### Resource change queries filter on the resource table’s `ChangeVersion`

ODS applies the `minChangeVersion`/`maxChangeVersion` parameters as inclusive predicates on the resource table’s `ChangeVersion` column.

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Common/Providers/Queries/QueryBuilderExtensions.cs`

```csharp
if (queryParameters.MinChangeVersion.HasValue)
{
    queryBuilder.Where(ColumnNames.ChangeVersion, ">=", queryParameters.MinChangeVersion.Value);
}

if (queryParameters.MaxChangeVersion.HasValue)
{
    queryBuilder.Where(ColumnNames.ChangeVersion, "<=", queryParameters.MaxChangeVersion.Value);
}
```

This reflects the core contract: **resources are “in the window” if their row’s latest `ChangeVersion` is in the window**.

### Deletes and key changes filter on tracked-change tables

ODS stores deletes and key changes in separate `tracked_changes_{schema}.{table}` tables and applies the same min/max filtering against those tracked-change rows.

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Features/ChangeQueries/Repositories/TrackedChangesQueryTemplatePreparerBase.cs`

```csharp
q2.Where($"{ChangeQueriesDatabaseConstants.TrackedChangesAlias}.{ChangeVersionColumnName}",
         ">=",
         new Parameter("@MinChangeVersion", queryParameters.MinChangeVersion));
...
q2.Where($"{ChangeQueriesDatabaseConstants.TrackedChangesAlias}.{ChangeVersionColumnName}",
         "<=",
         new Parameter("@MaxChangeVersion", queryParameters.MaxChangeVersion));
```

## Deletes: separate tracked change events

ODS uses triggers to insert a row into the appropriate tracked-change table after a delete, assigning a `ChangeVersion` from the global sequence.

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/Changes/0220-CreateTriggersForDeleteTracking.sql`

```sql
CREATE OR REPLACE FUNCTION tracked_changes_edfi.student_deleted()
    RETURNS trigger AS
$BODY$
BEGIN
    INSERT INTO tracked_changes_edfi.student(
        oldstudentusi, oldstudentuniqueid,
        id, discriminator, changeversion)
    VALUES (
        OLD.studentusi, OLD.studentuniqueid,
        OLD.id, OLD.discriminator, nextval('changes.changeversionsequence'));

    RETURN NULL;
END;
$BODY$ LANGUAGE plpgsql;

CREATE TRIGGER TrackDeletes AFTER DELETE ON edfi.student
    FOR EACH ROW EXECUTE PROCEDURE tracked_changes_edfi.student_deleted();
```

Implication: the delete itself is a first-class change event with its own `ChangeVersion`, but it does not (and cannot) “update” the deleted row’s `ChangeVersion` because the row is gone.

## Key changes: separate tracked change events

ODS tracks key changes (including `StudentUniqueId` changes) in tracked-change tables, again assigning a `ChangeVersion` from the global sequence.

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/Changes/0210-CreateTriggersForChangeVersionAndKeyChanges.sql`

```sql
CREATE OR REPLACE FUNCTION tracked_changes_edfi.student_keychg()
    RETURNS trigger AS
$BODY$
DECLARE
BEGIN
    -- Handle key changes
    INSERT INTO tracked_changes_edfi.student(
        oldstudentusi, oldstudentuniqueid,
        newstudentusi, newstudentuniqueid,
        id, changeversion)
    VALUES (
        old.studentusi, old.studentuniqueid,
        new.studentusi, new.studentuniqueid,
        old.id, (nextval('changes.changeversionsequence')));

    RETURN null;
END;
$BODY$ LANGUAGE plpgsql;

CREATE TRIGGER HandleKeyChanges AFTER UPDATE OF studentuniqueid ON edfi.student
    FOR EACH ROW EXECUTE PROCEDURE tracked_changes_edfi.student_keychg();
```

Key point: ODS records the **key change event** separately from the resource’s row `ChangeVersion` update, and it uses the same global sequence for both.

Practical note: because both the row update trigger and the key-change tracking trigger call `nextval(...)`, a single logical “key update” typically consumes **two** sequence values:

- one assigned to the updated resource row’s `ChangeVersion` (via `UpdateChangeVersion BEFORE UPDATE`), and
- a later one assigned to the tracked key change event row (via `HandleKeyChanges AFTER UPDATE OF ...`).

## Cascading natural-key updates (“fan-out”) and `ChangeVersion`

ODS supports *cascading natural key updates* for some resources. This is the closest analog to a DMS “fan-out”, because updating one resource’s key can cause the database to update key/foreign-key columns in other tables to preserve referential integrity.

### How ODS performs key-update cascades

At the application layer, ODS uses a post-update event listener to issue a **direct SQL `UPDATE`** to apply new identifier values (and optionally `AggregateData`) to the aggregate root table by `Id`:

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Common/Infrastructure/Listeners/EdFiOdsPostUpdateEventListener.cs`

```csharp
// Build the UPDATE sql query
string sql = $@"UPDATE {tableName} SET {setClause} WHERE Id = :id";
```

This SQL does not explicitly assign `ChangeVersion`; it relies on the database’s `UpdateChangeVersion` trigger (described above) to assign a new value whenever an `UPDATE` occurs.

At the database layer, ODS enables propagation of key changes using foreign keys with `ON UPDATE CASCADE`.

Example (Session key changes cascading into CourseOffering/Section):

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/1030-AddSessionCascadeSupport.sql`

```sql
ALTER TABLE edfi.CourseOffering
    ADD CONSTRAINT FK_CourseOffering_Session FOREIGN KEY (SchoolId, SchoolYear, SessionName)
    REFERENCES edfi.Session (SchoolId, SchoolYear, SessionName) ON UPDATE CASCADE;

ALTER TABLE edfi.Section
    ADD CONSTRAINT FK_Section_CourseOffering FOREIGN KEY (LocalCourseCode, SchoolId, SchoolYear, SessionName)
    REFERENCES edfi.CourseOffering (LocalCourseCode, SchoolId, SchoolYear, SessionName) ON UPDATE CASCADE;
```

### ChangeVersion behavior for cascades: only rows that are actually updated

ODS does **not** have a “derived dependency graph” that increments a resource’s `ChangeVersion` when only its *representation inputs* (joined/looked-up values) change.

Instead, ODS’s behavior is strictly row-update-driven:

- If a row is **not updated**, its `ChangeVersion` does **not** change (even if a GET could render differently due to joins/lookups).
- If a row **is updated** (including because a key/foreign-key update cascades into it), the `UpdateChangeVersion` trigger is intended to assign it a new `ChangeVersion` value (see “Updates” above).

This distinction matters for DMS alignment:

- DMS “fan-out” for representation-sensitive tokens is not an ODS concept.
- ODS “fan-out” exists primarily for referential integrity (key cascades), and it only affects resources whose own stored rows are physically updated.

### Test coverage note (ODS)

In ODS source we did **not** find automated unit/integration tests that assert how *dependent resources’* `ChangeVersion` behaves under cascading natural-key updates (e.g., “SessionName changed, therefore CourseOffering/Section rows were updated and have new ChangeVersion values”).

What we did find:

- Unit tests that validate SQL generation and filtering behavior for `ChangeVersion` query parameters (not cascade semantics):
  - `Ed-Fi-ODS/Application/EdFi.Ods.Tests/EdFi.Ods.Common/Database/Querying/QueryBuilderTests.cs`
- Postman collections that validate `changeVersion` behavior for change query endpoints and for the `keyChanges` stream, but do not validate fan-out/cascade behavior to other resources:
  - `Ed-Fi-ODS/Postman Test Suite/Ed-Fi ODS-API ChangeQueries Test Suite.postman_collection.json`
  - `Ed-Fi-ODS/Postman Test Suite/Ed-Fi ODS-API ChangeQueries Key Changes and Deletes Test Suite.postman_collection.json`

Example (Postman assertion that the *keyChanges* stream’s `changeVersion` increases across multiple key updates to the same resource):

Source: `Ed-Fi-ODS/Postman Test Suite/Ed-Fi ODS-API ChangeQueries Key Changes and Deletes Test Suite.postman_collection.json`

```javascript
pm.environment.set('known:'+scenarioId+':classPeriod:initialChangeVersion', item.changeVersion);
...
pm.expect(item.changeVersion).to.be.above(intermediateUpdateChangeVersion);
```

## What this means for “representation sensitivity”

In ODS, `ChangeVersion` is about **write events**, not about “the JSON representation would be different if reconstituted now”.

Example scenario (conceptual):

- A “Person identity” changes (e.g., `StudentUniqueId` changes).
- A different resource `G` references the student through a stable surrogate identifier (e.g., `StudentUSI`) and only resolves `studentUniqueId` at read time.

What happens in ODS:

- The `edfi.student` row is updated:
  - its `ChangeVersion` changes due to `UpdateChangeVersion` trigger, and
  - a key change event is inserted into `tracked_changes_edfi.student` (with its own `ChangeVersion`).
- The referencing resource `G` is **not updated** (no `UPDATE` statement against `G`’s tables), so:
  - `G.ChangeVersion` does **not** change.

Even if `G`’s serialized response would now show a different `studentReference.studentUniqueId`, ODS change queries do not treat that as a “change to `G`”.

## Practical implications (for consumers and for DMS)

- ODS change queries help consumers detect **what rows were written** (and explicit deletes/key changes), not “which other resources’ representations may render differently due to upstream identity/descriptor changes”.
- If DMS needs representation-sensitive `_etag/_lastModifiedDate`, it will necessarily be implementing a stronger contract than ODS, and it should not assume ODS `ChangeVersion` semantics are representation-sensitive.

## Concrete counterexample: UniqueId/descriptor changes can affect JSON without changing a resource’s `ChangeVersion`

ODS has multiple “representation inputs” that are not stored on the resource row itself:

1. **Person UniqueIds**, resolved from USIs at request time (see GET pipeline step `ResolveUniqueIds<...>` in `Ed-Fi-ODS/Application/EdFi.Ods.Api/Infrastructure/Pipelines/Factories/PipelineStepsProviders.cs` and `Ed-Fi-ODS/Application/EdFi.Ods.Api/Infrastructure/Pipelines/Steps/ResolveUniqueIds.cs`).
2. **Descriptor URIs**, derived from `edfi.Descriptor.Uri` (computed from `Namespace` + `CodeValue`) rather than stored on each referencing row (see `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/0020-Tables.sql`).

In these cases, a referenced value can change without the referencing resource row being updated, so the referencing resource’s `ChangeVersion` does not change.

Example (persons):

- `edfi.StudentSchoolAssociation` stores `StudentUSI` (surrogate) as part of its key, not `StudentUniqueId`.
- Updating `edfi.Student.StudentUniqueId` updates the `edfi.Student` row (and emits a key change event), but does not execute an `UPDATE` against `edfi.StudentSchoolAssociation`.
- Therefore `edfi.StudentSchoolAssociation.ChangeVersion` remains unchanged, even though its rendered `studentReference.studentUniqueId` would reflect the new UniqueId after resolution.
