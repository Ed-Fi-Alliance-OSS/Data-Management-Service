// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// PostgreSQL parity counterpart to the MSSQL
/// <c>Given_A_Provisioned_Mssql_Database_With_A_ClassPeriod_To_BellSchedule_Child_Binding</c> fixture. Proves that a
/// rename of an upstream resource's identity (<c>ClassPeriod</c>) reaches a stored child-collection
/// binding through the native <c>ON UPDATE CASCADE</c> foreign key and preserves update tracking for
/// the owning root, using the same acyclic authoritative <c>ds-5.2</c> scenario
/// (<c>ClassPeriod → BellScheduleClassPeriod → BellSchedule</c>) as the SQL Server test. The owning
/// root <c>BellSchedule</c> advances its content stamps and root-table mirror while its identity
/// stamps stay frozen and it emits no key-change row; the upstream <c>ClassPeriod</c> advances all four
/// stamps and emits exactly one key-change row carrying the full composite identity with the three-way
/// <c>ChangeVersion</c> linkage. This delivers the cross-engine equivalent-outcomes acceptance
/// criterion.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Provisioned_Postgresql_Database_With_A_ClassPeriod_To_BellSchedule_Child_Binding
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Authoritative tests must opt into the production-equivalent pipeline (the loader defaults
        // strict to false for the synthetic fixtures used elsewhere).
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task SetUp()
    {
        await _database.ResetAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        // This fixture provisions a dedicated database; disposing drops it so runs do not leak databases.
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_should_propagate_a_ClassPeriod_identity_rename_through_the_BellSchedule_child_binding_and_preserve_update_tracking()
    {
        // Arrange — seed the dependency chain
        //   School(id=100)
        //   ClassPeriod(School=100, Name="Period 1")
        //   BellSchedule(School=100, Name="BS1")
        //   BellScheduleClassPeriod child binding referencing ClassPeriod and BellSchedule
        const int SchoolId = 100;
        const string OldClassPeriodName = "Period 1";
        const string NewClassPeriodName = "Period 1A";
        const string BellScheduleName = "BS1";

        var schoolDocumentId = await InsertSchoolDocumentAsync(SchoolId);
        var classPeriodDocumentId = await InsertClassPeriodAsync(
            schoolDocumentId,
            SchoolId,
            OldClassPeriodName
        );
        var bellScheduleDocumentId = await InsertBellScheduleAsync(
            schoolDocumentId,
            SchoolId,
            BellScheduleName
        );
        await InsertBellScheduleClassPeriodAsync(
            bellScheduleDocumentId,
            ordinal: 1,
            classPeriodDocumentId: classPeriodDocumentId,
            classPeriodName: OldClassPeriodName,
            classPeriodSchoolId: SchoolId
        );

        var classPeriodDocumentUuid = await QueryDocumentUuidAsync(classPeriodDocumentId);
        var bellScheduleDocumentUuid = await QueryDocumentUuidAsync(bellScheduleDocumentId);

        // Native FK contract — the defining requirement. Identity propagation into this binding is
        // carried by the child FK's OWN native ON UPDATE CASCADE over the full composite reference key,
        // not by any propagation trigger. Scoped to THIS binding's FK.
        var bindingForeignKeys = await _database.GetForeignKeyMetadataAsync(
            "edfi",
            "BellScheduleClassPeriod"
        );
        var classPeriodReferenceKey = bindingForeignKeys.Single(foreignKey =>
            foreignKey.ConstraintName == "FK_BellScheduleClassPeriod_ClassPeriod_RefKey"
        );
        classPeriodReferenceKey
            .Columns.Should()
            .Equal("ClassPeriod_ClassPeriodName", "ClassPeriod_SchoolId", "ClassPeriod_DocumentId");
        classPeriodReferenceKey.ReferencedSchema.Should().Be("edfi");
        classPeriodReferenceKey.ReferencedTable.Should().Be("ClassPeriod");
        classPeriodReferenceKey
            .ReferencedColumns.Should()
            .Equal("ClassPeriodName", "School_SchoolId", "DocumentId");
        classPeriodReferenceKey.DeleteAction.Should().Be("NO ACTION");
        classPeriodReferenceKey
            .UpdateAction.Should()
            .Be("CASCADE", "identity propagation is carried by the native FK cascade, not a trigger");

        var childRowsBefore = await QueryBellScheduleClassPeriodRowCountAsync(bellScheduleDocumentId);
        var childAnchorBefore = await QueryBellScheduleClassPeriodAnchorAsync(bellScheduleDocumentId);
        childRowsBefore.Should().Be(1);
        var childCollectionItemIdBefore = Convert.ToInt64(
            (await QuerySingleBellScheduleClassPeriodAsync(bellScheduleDocumentId))["CollectionItemId"],
            CultureInfo.InvariantCulture
        );

        var beforeBellSchedule = await GetDocumentStampStateAsync(bellScheduleDocumentId);
        var beforeClassPeriod = await GetDocumentStampStateAsync(classPeriodDocumentId);
        var bellScheduleKeyChangesBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "BellSchedule",
            bellScheduleDocumentUuid
        );
        var classPeriodKeyChangesBefore = await CountTrackedChangeRowsAsync(
            "tracked_changes_edfi",
            "ClassPeriod",
            classPeriodDocumentUuid
        );

        // Server-side delay so ContentLastModifiedAt (now(), transaction-start time) advances between
        // the seed transaction and the rename transaction, letting assertions use BeAfter.
        await DelayForDistinctTimestampsAsync();

        // Act — update the upstream ClassPeriod identity column.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."ClassPeriod"
            SET "ClassPeriodName" = @newName
            WHERE "DocumentId" = @classPeriodDocumentId;
            """,
            new NpgsqlParameter("newName", NewClassPeriodName),
            new NpgsqlParameter("classPeriodDocumentId", classPeriodDocumentId)
        );

        // Assert — the cascade updated the projected identity column on the child row.
        var childRow = await QuerySingleBellScheduleClassPeriodAsync(bellScheduleDocumentId);
        Convert
            .ToString(childRow["ClassPeriod_ClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewClassPeriodName);

        // The unchanged composite identity half (School) must NOT cascade — only the renamed
        // ClassPeriodName component may move on the projected child row.
        Convert
            .ToInt64(childRow["ClassPeriod_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(SchoolId, "the unchanged composite identity half must not cascade");

        // Row count must be unchanged — the cascade is an UPDATE, not an INSERT/DELETE.
        var childRowsAfter = await QueryBellScheduleClassPeriodRowCountAsync(bellScheduleDocumentId);
        childRowsAfter
            .Should()
            .Be(
                childRowsBefore,
                "the cascade must update projected identity columns, not insert/delete rows"
            );

        // FK anchor (ClassPeriod_DocumentId) must NOT change — propagation should only touch the
        // projected non-key identity columns, never the reference link itself.
        var childAnchorAfter = await QueryBellScheduleClassPeriodAnchorAsync(bellScheduleDocumentId);
        childAnchorAfter
            .Should()
            .Be(
                childAnchorBefore,
                "ClassPeriod_DocumentId is the reference anchor and must not change during an identity cascade"
            );

        // CollectionItemId must survive the cascade — row-count and anchor stability alone would
        // also pass a DELETE + re-INSERT with identical values; the sequence-assigned value cannot.
        Convert
            .ToInt64(childRow["CollectionItemId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(
                childCollectionItemIdBefore,
                "the cascade must update the child row in place, not delete and re-insert it"
            );

        var afterBellSchedule = await GetDocumentStampStateAsync(bellScheduleDocumentId);
        var afterClassPeriod = await GetDocumentStampStateAsync(classPeriodDocumentId);
        var afterBellScheduleMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "BellSchedule",
            bellScheduleDocumentId
        );
        var afterClassPeriodMirror = await GetRootMirrorStampStateAsync(
            "edfi",
            "ClassPeriod",
            classPeriodDocumentId
        );

        // Owning root BellSchedule: its representation changed (a child binding's projected identity was
        // cascade-updated) but its OWN identity did not. Its content stamps advance and its root-table
        // mirror tracks the document row, while its identity stamps stay frozen (a paired contract) and
        // it emits no key-change row because its identity is unchanged.
        afterBellSchedule
            .ContentVersion.Should()
            .BeGreaterThan(
                beforeBellSchedule.ContentVersion,
                "child stamp trigger must fire from the cascade UPDATE and bump the owning root ContentVersion"
            );
        afterBellSchedule.ContentLastModifiedAt.Should().BeAfter(beforeBellSchedule.ContentLastModifiedAt);
        afterBellSchedule
            .IdentityVersion.Should()
            .Be(
                beforeBellSchedule.IdentityVersion,
                "BellSchedule's own identity did not change, so IdentityVersion must be frozen"
            );
        afterBellSchedule
            .IdentityLastModifiedAt.Should()
            .Be(
                beforeBellSchedule.IdentityLastModifiedAt,
                "identity stamps are a paired contract and must both be frozen"
            );
        AssertMirrorContentMatchesDocument(afterBellScheduleMirror, afterBellSchedule);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "BellSchedule", bellScheduleDocumentUuid))
            .Should()
            .Be(
                bellScheduleKeyChangesBefore,
                "the referrer's identity did not change, so it must emit no key-change row"
            );

        // Upstream ClassPeriod: its own identity changed. All four stamps advance, and it emits exactly
        // one key-change row.
        afterClassPeriod.ContentVersion.Should().BeGreaterThan(beforeClassPeriod.ContentVersion);
        afterClassPeriod.ContentLastModifiedAt.Should().BeAfter(beforeClassPeriod.ContentLastModifiedAt);
        afterClassPeriod.IdentityVersion.Should().BeGreaterThan(beforeClassPeriod.IdentityVersion);
        afterClassPeriod.IdentityLastModifiedAt.Should().BeAfter(beforeClassPeriod.IdentityLastModifiedAt);
        AssertMirrorContentMatchesDocument(afterClassPeriodMirror, afterClassPeriod);
        (await CountTrackedChangeRowsAsync("tracked_changes_edfi", "ClassPeriod", classPeriodDocumentUuid))
            .Should()
            .Be(
                classPeriodKeyChangesBefore + 1,
                "the resource whose identity changed emits exactly one key-change row"
            );

        var classPeriodKeyChange = await GetLatestTrackedChangeRowAsync(
            "tracked_changes_edfi",
            "ClassPeriod",
            classPeriodDocumentUuid
        );
        Convert
            .ToString(classPeriodKeyChange["OldClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(OldClassPeriodName);
        Convert
            .ToString(classPeriodKeyChange["NewClassPeriodName"], CultureInfo.InvariantCulture)
            .Should()
            .Be(NewClassPeriodName);
        // ClassPeriod identity is School + ClassPeriodName. The School half did not change, so both the
        // old and new sides must carry SchoolId 100 — a row that mangled the unchanged component would be
        // a malformed key-change and must fail here.
        Convert
            .ToInt64(classPeriodKeyChange["OldSchool_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(SchoolId);
        Convert
            .ToInt64(classPeriodKeyChange["NewSchool_SchoolId"], CultureInfo.InvariantCulture)
            .Should()
            .Be(SchoolId);
        // Three-way linkage: the key-change ChangeVersion equals dms.Document.ContentVersion equals the
        // edfi.ClassPeriod root-table mirror ContentVersion (16-tracked-change-trigger-rendering §29/§33).
        Convert
            .ToInt64(classPeriodKeyChange["ChangeVersion"], CultureInfo.InvariantCulture)
            .Should()
            .Be(afterClassPeriod.ContentVersion);
        afterClassPeriodMirror.ContentVersion.Should().Be(afterClassPeriod.ContentVersion);
    }

    private async Task<long> InsertSchoolDocumentAsync(int schoolId)
    {
        var schoolDocumentId = await InsertDocumentAsync("Ed-Fi", "School");

        // The DS 5.2 School insert triggers maintain the EducationOrganizationIdentity alias row. The
        // BellSchedule/ClassPeriod FKs only need edfi.School populated with matching
        // (DocumentId, SchoolId), so user triggers are disabled temporarily to bypass the alias
        // side-effects. FK enforcement (internal constraint triggers) is left active.
        await _database.ExecuteNonQueryAsync("""ALTER TABLE "edfi"."School" DISABLE TRIGGER USER;""");

        try
        {
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO "edfi"."School" ("DocumentId", "NameOfInstitution", "SchoolId")
                VALUES (@documentId, @nameOfInstitution, @schoolId);
                """,
                new NpgsqlParameter("documentId", schoolDocumentId),
                new NpgsqlParameter("nameOfInstitution", "Test School"),
                new NpgsqlParameter("schoolId", schoolId)
            );
        }
        finally
        {
            await _database.ExecuteNonQueryAsync("""ALTER TABLE "edfi"."School" ENABLE TRIGGER USER;""");
        }

        return schoolDocumentId;
    }

    private async Task<long> InsertClassPeriodAsync(
        long schoolDocumentId,
        int schoolId,
        string classPeriodName
    )
    {
        var classPeriodDocumentId = await InsertDocumentAsync("Ed-Fi", "ClassPeriod");

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."ClassPeriod" (
                "DocumentId",
                "School_DocumentId",
                "School_SchoolId",
                "ClassPeriodName"
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @schoolId,
                @classPeriodName
            );
            """,
            new NpgsqlParameter("documentId", classPeriodDocumentId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("classPeriodName", classPeriodName)
        );

        return classPeriodDocumentId;
    }

    private async Task<long> InsertBellScheduleAsync(
        long schoolDocumentId,
        int schoolId,
        string bellScheduleName
    )
    {
        var bellScheduleDocumentId = await InsertDocumentAsync("Ed-Fi", "BellSchedule");

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."BellSchedule" (
                "DocumentId",
                "School_DocumentId",
                "School_SchoolId",
                "BellScheduleName"
            )
            VALUES (
                @documentId,
                @schoolDocumentId,
                @schoolId,
                @bellScheduleName
            );
            """,
            new NpgsqlParameter("documentId", bellScheduleDocumentId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("schoolId", schoolId),
            new NpgsqlParameter("bellScheduleName", bellScheduleName)
        );

        return bellScheduleDocumentId;
    }

    private async Task InsertBellScheduleClassPeriodAsync(
        long bellScheduleDocumentId,
        int ordinal,
        long classPeriodDocumentId,
        string classPeriodName,
        int classPeriodSchoolId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."BellScheduleClassPeriod" (
                "BellSchedule_DocumentId",
                "Ordinal",
                "ClassPeriod_DocumentId",
                "ClassPeriod_ClassPeriodName",
                "ClassPeriod_SchoolId"
            )
            VALUES (
                @bellScheduleDocumentId,
                @ordinal,
                @classPeriodDocumentId,
                @classPeriodName,
                @classPeriodSchoolId
            );
            """,
            new NpgsqlParameter("bellScheduleDocumentId", bellScheduleDocumentId),
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("classPeriodDocumentId", classPeriodDocumentId),
            new NpgsqlParameter("classPeriodName", classPeriodName),
            new NpgsqlParameter("classPeriodSchoolId", classPeriodSchoolId)
        );
    }

    private async Task<long> InsertDocumentAsync(string projectName, string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(projectName, resourceName);

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dms"."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@documentUuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("documentUuid", Guid.NewGuid()),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task<short> GetResourceKeyIdAsync(string projectName, string resourceName)
    {
        return await _database.ExecuteScalarAsync<short>(
            """
            SELECT "ResourceKeyId"
            FROM "dms"."ResourceKey"
            WHERE "ProjectName" = @projectName
              AND "ResourceName" = @resourceName;
            """,
            new NpgsqlParameter("projectName", projectName),
            new NpgsqlParameter("resourceName", resourceName)
        );
    }

    private async Task<Guid> QueryDocumentUuidAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<Guid>(
            """SELECT "DocumentUuid" FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task<long> QueryBellScheduleClassPeriodRowCountAsync(long bellScheduleDocumentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT COUNT(*)
            FROM "edfi"."BellScheduleClassPeriod"
            WHERE "BellSchedule_DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", bellScheduleDocumentId)
        );
    }

    private async Task<long> QueryBellScheduleClassPeriodAnchorAsync(long bellScheduleDocumentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT "ClassPeriod_DocumentId"
            FROM "edfi"."BellScheduleClassPeriod"
            WHERE "BellSchedule_DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", bellScheduleDocumentId)
        );
    }

    private async Task<IReadOnlyDictionary<string, object?>> QuerySingleBellScheduleClassPeriodAsync(
        long bellScheduleDocumentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                "CollectionItemId",
                "BellSchedule_DocumentId",
                "Ordinal",
                "ClassPeriod_DocumentId",
                "ClassPeriod_ClassPeriodName",
                "ClassPeriod_SchoolId"
            FROM "edfi"."BellScheduleClassPeriod"
            WHERE "BellSchedule_DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", bellScheduleDocumentId)
        );

        return rows.Single();
    }

    private async Task<DocumentStampState> GetDocumentStampStateAsync(long documentId)
    {
        var row = (
            await _database.QueryRowsAsync(
                """
                SELECT
                    "ContentVersion",
                    "IdentityVersion",
                    "ContentLastModifiedAt",
                    "IdentityLastModifiedAt"
                FROM "dms"."Document"
                WHERE "DocumentId" = @documentId;
                """,
                new NpgsqlParameter("documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            Convert.ToInt64(row["IdentityVersion"], CultureInfo.InvariantCulture),
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            ReadDateTimeOffset(row["IdentityLastModifiedAt"])
        );
    }

    // Root tables carry ContentVersion/ContentLastModifiedAt mirror columns that resource change-version
    // queries filter on; this reads the mirror pair for the owning root row.
    private async Task<DocumentStampState> GetRootMirrorStampStateAsync(
        string schemaName,
        string tableName,
        long documentId
    )
    {
        var row = (
            await _database.QueryRowsAsync(
                $"""
                SELECT
                    "ContentVersion",
                    "ContentLastModifiedAt"
                FROM "{schemaName}"."{tableName}"
                WHERE "DocumentId" = @documentId;
                """,
                new NpgsqlParameter("documentId", documentId)
            )
        ).Single();

        return new(
            Convert.ToInt64(row["ContentVersion"], CultureInfo.InvariantCulture),
            IdentityVersion: 0,
            ReadDateTimeOffset(row["ContentLastModifiedAt"]),
            IdentityLastModifiedAt: DateTimeOffset.UnixEpoch
        );
    }

    private static void AssertMirrorContentMatchesDocument(
        DocumentStampState mirror,
        DocumentStampState document
    )
    {
        mirror.ContentVersion.Should().Be(document.ContentVersion);
        mirror.ContentLastModifiedAt.Should().Be(document.ContentLastModifiedAt);
    }

    // tracked_changes_* rows are keyed by "Id", which holds the DocumentUuid (not DocumentId).
    private async Task<long> CountTrackedChangeRowsAsync(
        string schemaName,
        string tableName,
        Guid documentUuid
    )
    {
        return await _database.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(*)
            FROM "{schemaName}"."{tableName}"
            WHERE "Id" = @documentUuid;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );
    }

    private async Task<IReadOnlyDictionary<string, object?>> GetLatestTrackedChangeRowAsync(
        string schemaName,
        string tableName,
        Guid documentUuid
    )
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT *
            FROM "{schemaName}"."{tableName}"
            WHERE "Id" = @documentUuid
            ORDER BY "ChangeVersion" DESC
            LIMIT 1;
            """,
            new NpgsqlParameter("documentUuid", documentUuid)
        );

        return rows.Single();
    }

    private async Task DelayForDistinctTimestampsAsync()
    {
        await _database.ExecuteNonQueryAsync("SELECT pg_sleep(0.05);");
    }

    private static DateTimeOffset ReadDateTimeOffset(object? value)
    {
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Unsupported timestamp value type '{value?.GetType().FullName ?? "<null>"}'."
            ),
        };
    }
}
