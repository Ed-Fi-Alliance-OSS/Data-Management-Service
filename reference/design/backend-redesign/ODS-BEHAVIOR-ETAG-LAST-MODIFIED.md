# ODS/API Behavior: `ETag` and `LastModifiedDate`

## Purpose

This document describes how the Ed-Fi ODS/API implements `ETag` and `LastModifiedDate`, with a focus on the frequently surprising behavior (to Brad) that **a resource’s serialized representation can change (due to joins/lookups) without the resource’s `ETag`/`LastModifiedDate` changing**.

## Summary (what to expect)

- In ODS/API, `ETag` is effectively a **row/aggregate version token**, derived from the entity’s `LastModifiedDate`.
- `LastModifiedDate` advances when the resource’s own aggregate is written (insert/update), or in a few special cases when a child-table update triggers an update of the root’s `LastModifiedDate`.
- ODS/API does **not** attempt to make `ETag` / `LastModifiedDate` **representation-sensitive** to changes occurring in *other* rows (e.g., a referenced person’s UniqueId changing), unless those changes cause an actual update of the resource’s own tables.

## How ODS/API generates `ETag`

ODS/API’s default `ETag` implementation derives the `ETag` from the entity’s `LastModifiedDate` (converted to UTC and serialized via `DateTime.ToBinary()`).

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Api/Providers/ETagProvider.cs`

```csharp
// Handle entities
if (entity is IDateVersionedEntity versionEntity)
{
    var dateToGenerateEtagFrom = versionEntity.LastModifiedDate;

    if (dateToGenerateEtagFrom == default(DateTime))
    {
        return null;
    }

    var standardizedEtagDateTime = dateToGenerateEtagFrom.ToUniversalTime();

    return standardizedEtagDateTime.ToBinary()
                                   .ToString(CultureInfo.InvariantCulture);
}
```

The reverse operation (parsing the incoming `If-Match` value back into a `DateTime`) is also implemented here:

```csharp
public DateTime GetDateTime(string etag)
{
    if (!string.IsNullOrWhiteSpace(etag) && long.TryParse(etag, out long result))
    {
        return DateTime.FromBinary(result);
    }

    return default(DateTime);
}
```

## What ODS/API means by “`LastModifiedDate`”

The ODS common interface makes the intended meaning explicit: `LastModifiedDate` is used to implement `ETag` support and to expose per-resource “last modified” metadata.

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Common/IDateVersionedEntity.cs`

```csharp
/// <remarks>This value is used to implement ETag support in the API, as well as to expose the
/// last modified date of each resource item as metadata in the responses.</remarks>
DateTime LastModifiedDate { get; set; }
```

## Optimistic concurrency is `LastModifiedDate` equality

ODS/API’s optimistic concurrency checks compare the entity’s current `LastModifiedDate` to the value supplied by the client (via `If-Match`, decoded by `ETagProvider.GetDateTime`).

### Update (PUT/POST upsert)

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Common/Infrastructure/Repositories/UpsertEntity.cs`

```csharp
// Update the entity
if (enforceOptimisticLock)
{
    if (!persistedEntity.LastModifiedDate.Equals(entity.LastModifiedDate))
    {
        throw new OptimisticLockException();
    }
}
```

### Delete

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Common/Infrastructure/Repositories/NHibernateRepositoryDeleteOperationBase.cs`

```csharp
// only check last modified data
if (!string.IsNullOrEmpty(etag))
{
    var lastModifiedDate = _eTagProvider.GetDateTime(etag);

    if (!persistedEntity.LastModifiedDate.Equals(lastModifiedDate))
    {
        throw new OptimisticLockException();
    }
}
```

## How `LastModifiedDate` is stored and advanced

### It is a persisted column on ODS tables

For most Ed-Fi resource tables, the physical table includes `CreateDate`, `LastModifiedDate`, and `Id` columns.

Example (PostgreSQL DDL excerpt for `edfi.AcademicWeek`):

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/0020-Tables.sql`

```sql
CREATE TABLE edfi.AcademicWeek (
    ...
    CreateDate TIMESTAMP NOT NULL,
    LastModifiedDate TIMESTAMP NOT NULL,
    Id UUID NOT NULL,
    CONSTRAINT AcademicWeek_PK PRIMARY KEY (SchoolId, WeekIdentifier)
);
ALTER TABLE edfi.AcademicWeek ALTER COLUMN CreateDate SET DEFAULT current_timestamp AT TIME ZONE 'UTC';
ALTER TABLE edfi.AcademicWeek ALTER COLUMN LastModifiedDate SET DEFAULT current_timestamp AT TIME ZONE 'UTC';
```

### Updates advance it when (and only when) the aggregate is modified

ODS’s update pipeline performs a generated, field-by-field `Synchronize(...)` between the incoming entity and the persisted entity. If the aggregate is modified, ODS “touches” the root so the update will advance the stored `LastModifiedDate` (and therefore the `ETag`).

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Common/Infrastructure/Repositories/UpsertEntity.cs`

```csharp
// Synchronize using strongly-typed generated code
isModified = entity.Synchronize(persistedEntity);

// Force aggregate root to be touched with an updated date if aggregate has been modified
if (isModified)
{
    // Make root dirty, NHibernate will override the value during insert (through a hook)
    persistedEntity.LastModifiedDate = persistedEntity.LastModifiedDate.AddSeconds(1);
}
```

### `LastModifiedDate` is treated as “assigned by NHibernate”

ODS code refers to `LastModifiedDate` as being assigned by NHibernate (this is used to ensure serialized data reflects the stored `LastModifiedDate` value).

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Common/Infrastructure/Listeners/EdFiOdsPreInsertListener.cs`

```csharp
// Get the LastModifiedDate assigned by NHibernate
DateTime originalLastModifiedDate = aggregateRoot.LastModifiedDate;

// Set additional properties on entity so that they're reflected correctly in serialized data
aggregateRoot.CreateDate = currentDateTime;
aggregateRoot.LastModifiedDate = persister.Get<DateTime>(@event.State, ColumnNames.LastModifiedDate);
```

## Indirect aggregate updates (child-table updates can bump the root)

ODS includes database triggers that bump a root table’s `LastModifiedDate` when a related child table row changes *volatile foreign key values* that effectively change the aggregate’s identity/shape.

This still reflects **changes within the resource’s own aggregate tables** (root + its children). It is not a general mechanism for tracking upstream changes in unrelated tables.

Example (PostgreSQL):

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/Changes/0230-CreateIndirectUpdateCascadeTriggers.sql`

```sql
CREATE OR REPLACE FUNCTION edfi.update_Assessment_lastmodifieddate()
RETURNS TRIGGER AS $$
BEGIN
    -- Check if any volatile foreign key values have changed
    IF NEW.LocalCourseCode IS DISTINCT FROM OLD.LocalCourseCode
       OR NEW.SchoolId IS DISTINCT FROM OLD.SchoolId
       OR NEW.SchoolYear IS DISTINCT FROM OLD.SchoolYear
       OR NEW.SectionIdentifier IS DISTINCT FROM OLD.SectionIdentifier
       OR NEW.SessionName IS DISTINCT FROM OLD.SessionName
       THEN
       -- Update the LastModifiedDate in the root table to the current UTC time
       UPDATE edfi.Assessment rt
       SET LastModifiedDate = NOW()
       WHERE rt.AssessmentIdentifier = NEW.AssessmentIdentifier
         AND rt.Namespace = NEW.Namespace;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_edfi_AssessmentSection_afterupdate
AFTER UPDATE ON edfi.AssessmentSection
FOR EACH ROW
EXECUTE FUNCTION edfi.update_Assessment_lastmodifieddate();
```

There is an analogous SQL Server script:

- `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/MsSql/Structure/Ods/Changes/0230-CreateIndirectUpdateCascadeTriggers.sql`

## Cascading natural-key updates (“fan-out”) and `ETag`/`LastModifiedDate`

ODS supports *cascading natural key updates* for some resources. This is the closest analog to a DMS “fan-out”, because updating one resource’s key can cause the database to update key/foreign-key columns in other tables to preserve referential integrity.

### How ODS performs key-update cascades

At the application layer, ODS uses a post-update event listener to issue a **direct SQL `UPDATE`** to apply new identifier values (and optionally `AggregateData`) to the aggregate root table by `Id`:

Source: `Ed-Fi-ODS/Application/EdFi.Ods.Common/Infrastructure/Listeners/EdFiOdsPostUpdateEventListener.cs`

```csharp
// Build the UPDATE sql query
string sql = $@"UPDATE {tableName} SET {setClause} WHERE Id = :id";
...
// Build the SET clause (identifier columns + optional AggregateData)
string setClause = GetSetClause(updateTargetColumnNames, valueSourceColumnNames, aggregateData != null);
```

Notably, this SQL does **not** assign `LastModifiedDate` (and therefore does not itself “touch” the row for `ETag` purposes). For the aggregate root being updated, `LastModifiedDate` may already have been advanced by the NHibernate-managed update that triggered this listener; the concern is primarily about rows updated indirectly via `ON UPDATE CASCADE`, which are not NHibernate-managed updates.

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

### Implication: no general mechanism to bump `LastModifiedDate` for cascaded updates

Because `ETag` is derived from `LastModifiedDate`, the critical question for these cascades is:

- when a row is **updated indirectly** by `ON UPDATE CASCADE` (i.e., only its key/FK columns change due to a parent key change), does ODS also bump the row’s `LastModifiedDate`?

Based on code and artifact inspection:

- The key-update SQL executed by `EdFiOdsPostUpdateEventListener` does not set `LastModifiedDate` (see above).
- In the generated database artifacts for standard ODS, the only explicit “bump `LastModifiedDate`” logic we found is the small set of *indirect update cascade* triggers in:
  - `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/PgSql/Structure/Ods/Changes/0230-CreateIndirectUpdateCascadeTriggers.sql` (PostgreSQL)
  - `Ed-Fi-ODS/Application/EdFi.Ods.Standard/Standard/5.2.0/Artifacts/MsSql/Structure/Ods/Changes/0230-CreateIndirectUpdateCascadeTriggers.sql` (SQL Server)

These triggers cover a limited set of *within-aggregate* child-table updates (often volatile FK values), and are not a general “any update bumps `LastModifiedDate`” mechanism.

Taken together, this indicates ODS makes **no general best-effort** to ensure that *indirectly cascaded updates* (e.g., key/FK changes caused by an upstream key update) always update `_lastModifiedDate`/`ETag` for the indirectly affected resource.

### Test coverage note (ODS)

In ODS source we did **not** find automated tests that assert `_etag` or `_lastModifiedDate` behavior specifically for cascading natural-key updates on *dependent* resources.

What we did find:

- Unit tests that validate the `ETagProvider` is a pure function of `LastModifiedDate`:
  - `Ed-Fi-ODS/Application/EdFi.Ods.Tests/EdFi.Ods.Api/Providers/ETagProviderTests.cs`
- Postman collections that validate an `ETag` header changes for direct update operations, but do not validate fan-out/cascade behavior to other resources:
  - `Ed-Fi-ODS/Postman Test Suite/Ed-Fi ODS-API Integration Test Suite ResponseTests.postman_collection.json`

## What this means for “representation sensitivity”

ODS/API frequently reconstitutes responses by joining or translating values from other tables (e.g., resolving `studentUniqueId` from a stored `StudentUSI`).

Because `ETag` and `LastModifiedDate` are based on the **resource’s own stored row timestamp**, the following behavior is expected in ODS:

- If an upstream referenced row changes in a way that affects the referencing resource’s serialized representation (e.g., a person’s UniqueId changes), but no rows in the referencing resource’s aggregate are updated, then:
  - the referencing resource’s `LastModifiedDate` does **not** change, and
  - the referencing resource’s `ETag` does **not** change,
  - even though a subsequent GET may return a different JSON representation.

ODS’s design choice is consistent with its concurrency model: `If-Match` protects against **concurrent writes to the resource itself**, not against representation differences caused by changes in joined lookup tables.

## Practical implications (for consumers and for DMS)

- **Client caching**: an ODS consumer can observe the representation changing without seeing a new `ETag` for the referencing resource. This is a consequence of row-based versioning.
- **Concurrency**: ODS `ETag` is reliable for preventing lost updates on the resource being written, but it does not provide “representation-sensitive” concurrency across referenced resources.
- **DMS redesign context**: if DMS requires `_etag/_lastModifiedDate` to change when *representation* changes (including reference-identity/descriptor changes), DMS must implement additional semantics beyond ODS (e.g., derived-token/representation-sensitive `_etag`), and should treat ODS behavior as a different (weaker) contract rather than an exact precedent.
