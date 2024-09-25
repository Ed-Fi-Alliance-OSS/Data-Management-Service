# September 2024 Deadlock Analysis

Bulk upload of a large set of sample XML files has been consistently failing
with deadlocks when it hits the `StudentSectionAttendanceEvent` files. What can
we do to reduce these deadlocks? The following sequence diagram is for the
Upsert process.

```mermaid
sequenceDiagram
    repository ->> npgsql: OpenConnectionAsync()
    npgsql -->> repository: connection
    repository ->> connection: BeginTransactionAsync()
    connection -->> repository: trans

    repository ->> upsertDocument: upsert(request, connection, trans)

    upsertDocument ->> sqlAction: FindDocumentByReferentialId(..., LockOption.BlockUpdateDelete)
    sqlAction -->> upsertDocument: documentFromDb

    alt documentFromDb is null: INSERT
        upsertDocument ->> sqlAction: InsertDocument(...)
        sqlAction -->> upsertDocument: newDocumentId
        upsertDocument ->> sqlAction: InsertAlias(..., newDocumentid)

        alt isSubClass
            upsertDocument ->> sqlAction: InsertAlias(...)
        end

        upsertDocument ->> sqlAction: InsertReferences(...)
    else UPDATE
        upsertDocument ->> sqlAction: UpdateDocumentEdfiDoc(...)
        upsertDocument ->> sqlAction: DeleteREferencesByDocumentUuid(...)
        upsertDocument ->> sqlAction: InsertReferences(...)
    end

    upsertDocument -->> repository: result

    alt SUCCESS
        repository ->> trans: CommitAsync()
    else FAILED
        repository ->> trans: RollbackAsync()
    end
```

After we have locked the existing record, we have up to three different SQL
statements. All of these statements take some time, and each one is separately
issued from the DMS. That means there is a small amount of overhead with
multiple calls to the database. We may be able to tune this through batching
some commands (that is, send multiple SQL statements to the server at the same
time) or by switching to a stored procedure.

Furthermore, there may be a problem with the way we are handling database
retries: that is, we might not be properly retrying the transaction after the
initial deadlock.
