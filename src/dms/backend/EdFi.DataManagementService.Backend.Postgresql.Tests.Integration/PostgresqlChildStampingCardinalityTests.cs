// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Proves that PostgreSQL non-root statement-level stamping (DMS-1208) allocates one
/// <c>dms.ChangeVersionSequence</c> value per distinct affected root document rather than one per
/// affected source row. Provisions the focused stable-key fixture (root <c>edfi.School</c>, child
/// <c>edfi.SchoolAddress</c>, collection-aligned <c>_ext</c> <c>sample.SchoolExtensionAddress</c>) and
/// exercises multi-row DML against a real PostgreSQL server.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Provisioned_Postgresql_Database_With_NonRoot_Statement_Level_Stamping
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    // Whole microseconds, so the value round-trips exactly through timestamp with time zone.
    private static readonly DateTime CreatedAtSentinel = new DateTime(
        2000,
        2,
        3,
        4,
        5,
        6,
        DateTimeKind.Utc
    ).AddTicks(1_234_560);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        await _database.ExecuteNonQueryAsync(
            """UPDATE "dms"."DocumentCacheState" SET "ProjectionLifecycleState" = 'Tracking';"""
        );
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_provisions_only_authoritative_owner_timestamps_and_retains_document_versions()
    {
        var columns = await _database.QueryRowsAsync(
            """
            SELECT table_schema, table_name, column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE (table_schema, table_name) IN (('dms', 'Document'), ('dms', 'Descriptor'), ('edfi', 'School'))
              AND column_name IN ('ContentVersion', 'ContentLastModifiedAt', 'ContentCreatedAt')
            ORDER BY table_schema, table_name, column_name;
            """
        );
        columns
            .Select(row =>
                $"{row["table_schema"]}.{row["table_name"]}.{row["column_name"]}:{row["data_type"]}:{row["is_nullable"]}"
            )
            .Should()
            .Equal(
                "dms.Descriptor.ContentCreatedAt:timestamp with time zone:NO",
                "dms.Descriptor.ContentLastModifiedAt:timestamp with time zone:NO",
                "dms.Descriptor.ContentVersion:bigint:NO",
                "dms.Document.ContentVersion:bigint:NO",
                "edfi.School.ContentCreatedAt:timestamp with time zone:NO",
                "edfi.School.ContentLastModifiedAt:timestamp with time zone:NO",
                "edfi.School.ContentVersion:bigint:NO"
            );
    }

    [Test]
    public async Task It_initializes_the_root_timestamp_without_allocating_an_extra_document_version()
    {
        var documentId = await InsertDocumentAsync("Ed-Fi", "School");
        var version = await ReadDocumentContentVersionAsync(documentId);
        var watermark = await ReadMaxChangeVersionAsync();
        var insertTimestamp = await _database.ExecuteScalarAsync<DateTime>(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "SchoolId", "ContentLastModifiedAt", "ContentCreatedAt")
            VALUES (@documentId, 900, '2000-01-01T00:00:00Z', '2000-01-01T00:00:00Z')
            RETURNING now();
            """,
            new NpgsqlParameter("documentId", documentId)
        );
        var after = await ReadStampPairAsync(documentId);
        after.DocumentVersion.Should().Be(version);
        // The trigger overrides both supplied timestamps with the inserting transaction's now().
        after.Owner.Should().Be(new StampValues(version, insertTimestamp, insertTimestamp));
        (await ReadMaxChangeVersionAsync()).Should().Be(watermark);
    }

    [TestCase("edfi", "School", "SchoolId", "DocumentId", "\"SchoolId\" + 1")]
    [TestCase(
        "edfi",
        "SchoolAddressPeriod",
        "PeriodName",
        "School_DocumentId",
        "\"PeriodName\" || '-changed'"
    )]
    [TestCase("sample", "SchoolExtension", "CampusCode", "DocumentId", "\"CampusCode\" || '-changed'")]
    public async Task It_stamps_root_nested_child_and_root_extension_changes_once(
        string schema,
        string table,
        string column,
        string ownerColumn,
        string value
    )
    {
        var documentId = await SeedSchoolWithFullDescendantGraphAsync(800, 3);
        await BackdateOwnerTimestampAsync(documentId);
        await SetOwnerCreatedAtSentinelAsync(documentId);
        var before = await ReadStampPairAsync(documentId);
        var watermark = await ReadMaxChangeVersionAsync();
        await _database.ExecuteNonQueryAsync(
            $"""
            UPDATE "{schema}"."{table}" SET "{column}" = {value} WHERE "{ownerColumn}" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );
        var after = await ReadStampPairAsync(documentId);
        after.DocumentVersion.Should().Be(watermark + 1);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(before.Owner.ContentLastModifiedAt);
        after.Owner.ContentCreatedAt.Should().Be(CreatedAtSentinel);
        (await ReadMaxChangeVersionAsync()).Should().Be(watermark + 1);
    }

    [TestCase("edfi", "School", "SchoolId", "DocumentId")]
    [TestCase("edfi", "SchoolAddress", "City", "School_DocumentId")]
    [TestCase("edfi", "SchoolAddressPeriod", "PeriodName", "School_DocumentId")]
    [TestCase("sample", "SchoolExtension", "CampusCode", "DocumentId")]
    [TestCase("sample", "SchoolExtensionAddress", "Zone", "School_DocumentId")]
    public async Task It_preserves_exact_stamps_and_projection_work_on_no_op_updates(
        string schema,
        string table,
        string column,
        string ownerColumn
    )
    {
        var documentId = await SeedSchoolWithFullDescendantGraphAsync(700, 3);
        await BackdateOwnerTimestampAsync(documentId);
        await SetOwnerCreatedAtSentinelAsync(documentId);
        var before = await ReadStampPairAsync(documentId);
        var watermark = await ReadMaxChangeVersionAsync();
        var beforeWork = await _database.QueryRowsAsync(
            """SELECT * FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
        await _database.ExecuteNonQueryAsync(
            $"""
            UPDATE "{schema}"."{table}" SET "{column}" = "{column}" WHERE "{ownerColumn}" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );
        (await ReadStampPairAsync(documentId)).Should().Be(before);
        (await ReadMaxChangeVersionAsync()).Should().Be(watermark);
        var afterWork = await _database.QueryRowsAsync(
            """SELECT * FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
        afterWork.Should().BeEquivalentTo(beforeWork);
    }

    [Test]
    public async Task It_should_allocate_one_content_version_for_a_multi_row_single_statement_child_insert()
    {
        var schoolDocumentId = await InsertDocumentAsync("Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, schoolId: 170);
        await BackdateOwnerTimestampAsync(schoolDocumentId);
        await SetOwnerCreatedAtSentinelAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        // Multiple child rows for the same root in one INSERT statement exercise the ins-trigger
        // SELECT DISTINCT dedupe — the seed helpers insert one row per statement, so this is the only
        // coverage of a multi-row single-statement insert.
        var insertedRows = await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolAddress" ("Ordinal", "School_DocumentId", "City")
            VALUES (1, @schoolDocumentId, 'City-170-1'),
                   (2, @schoolDocumentId, 'City-170-2'),
                   (3, @schoolDocumentId, 'City-170-3');
            """,
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var after = await ReadStampPairAsync(schoolDocumentId);

        insertedRows.Should().Be(3, "the single statement must insert all three child rows");
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(
                1L,
                "a multi-row single-statement child insert touching one root must allocate exactly one content version, not one per inserted row"
            );
        after.DocumentVersion.Should().BeGreaterThan(before.DocumentVersion);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(before.Owner.ContentLastModifiedAt);
        after
            .Owner.ContentVersion.Should()
            .Be(after.DocumentVersion, "the root mirror ContentVersion must equal dms.Document");
        after
            .Owner.ContentCreatedAt.Should()
            .Be(CreatedAtSentinel, "a child insert stamps an existing root and never restarts its lifetime");
    }

    [Test]
    public async Task It_should_preserve_the_root_creation_timestamp_on_a_root_extension_insert()
    {
        var schoolDocumentId = await InsertDocumentAsync("Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, schoolId: 175);
        await BackdateOwnerTimestampAsync(schoolDocumentId);
        await SetOwnerCreatedAtSentinelAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var watermark = await ReadMaxChangeVersionAsync();

        await InsertSchoolExtensionAsync(schoolDocumentId, "Campus-175");

        var after = await ReadStampPairAsync(schoolDocumentId);
        after.DocumentVersion.Should().Be(watermark + 1);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(before.Owner.ContentLastModifiedAt);
        after.Owner.ContentCreatedAt.Should().Be(CreatedAtSentinel);
    }

    [Test]
    public async Task It_should_allocate_one_content_version_for_a_multi_row_child_update()
    {
        var schoolDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 100, addressCount: 3);
        await BackdateOwnerTimestampAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        var changedRows = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."SchoolAddress"
            SET "City" = "City" || '-changed'
            WHERE "School_DocumentId" = @schoolDocumentId;
            """,
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var after = await ReadStampPairAsync(schoolDocumentId);

        changedRows.Should().Be(3, "the single statement must change all three child rows");
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(
                1L,
                "a multi-row child update touching one root must allocate exactly one content version, not one per row"
            );
        after.DocumentVersion.Should().BeGreaterThan(before.DocumentVersion);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(before.Owner.ContentLastModifiedAt);
        after
            .Owner.ContentVersion.Should()
            .Be(after.DocumentVersion, "the root mirror ContentVersion must equal dms.Document");
    }

    [Test]
    public async Task It_should_allocate_one_content_version_for_a_multi_row_collection_aligned_extension_update()
    {
        var schoolDocumentId = await SeedSchoolWithExtensionAddressesAsync(schoolId: 110, addressCount: 3);
        await BackdateOwnerTimestampAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        var changedRows = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "sample"."SchoolExtensionAddress"
            SET "Zone" = "Zone" || '-changed'
            WHERE "School_DocumentId" = @schoolDocumentId;
            """,
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var after = await ReadStampPairAsync(schoolDocumentId);

        changedRows.Should().Be(3, "the single statement must change all three collection-aligned _ext rows");
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(
                1L,
                "a multi-row collection-aligned _ext update touching one root must allocate exactly one content version"
            );
        after.DocumentVersion.Should().BeGreaterThan(before.DocumentVersion);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(before.Owner.ContentLastModifiedAt);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
    }

    [Test]
    public async Task It_should_not_allocate_a_content_version_for_a_no_op_child_update()
    {
        var schoolDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 120, addressCount: 3);
        await BackdateOwnerTimestampAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."SchoolAddress"
            SET "City" = "City"
            WHERE "School_DocumentId" = @schoolDocumentId;
            """,
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var after = await ReadStampPairAsync(schoolDocumentId);

        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(0L, "a no-op update whose stored values do not change must not allocate a content version");
        after.DocumentVersion.Should().Be(before.DocumentVersion);
        after.Owner.ContentLastModifiedAt.Should().Be(before.Owner.ContentLastModifiedAt);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
    }

    [Test]
    public async Task It_should_stamp_both_old_and_new_roots_on_a_child_locator_change()
    {
        var oldRootDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 130, addressCount: 2);
        var newRootDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 131, addressCount: 0);
        await BackdateOwnerTimestampAsync(oldRootDocumentId);
        var beforeOld = await ReadStampPairAsync(oldRootDocumentId);
        await BackdateOwnerTimestampAsync(newRootDocumentId);
        var beforeNew = await ReadStampPairAsync(newRootDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        // Reparent both child rows from the old root to the new root in one statement.
        var changedRows = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."SchoolAddress"
            SET "School_DocumentId" = @newRootDocumentId
            WHERE "School_DocumentId" = @oldRootDocumentId;
            """,
            new NpgsqlParameter("newRootDocumentId", newRootDocumentId),
            new NpgsqlParameter("oldRootDocumentId", oldRootDocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var afterOld = await ReadStampPairAsync(oldRootDocumentId);
        var afterNew = await ReadStampPairAsync(newRootDocumentId);

        changedRows.Should().Be(2, "both child rows must be reparented by the single statement");
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(2L, "a locator change must stamp the old and the new owning root exactly once each");
        afterOld.DocumentVersion.Should().BeGreaterThan(beforeOld.DocumentVersion);
        afterNew.DocumentVersion.Should().BeGreaterThan(beforeNew.DocumentVersion);
        afterOld.Owner.ContentLastModifiedAt.Should().BeAfter(beforeOld.Owner.ContentLastModifiedAt);
        afterNew.Owner.ContentLastModifiedAt.Should().BeAfter(beforeNew.Owner.ContentLastModifiedAt);
        afterOld.Owner.ContentVersion.Should().Be(afterOld.DocumentVersion);
        afterNew.Owner.ContentVersion.Should().Be(afterNew.DocumentVersion);
        afterOld
            .DocumentVersion.Should()
            .NotBe(afterNew.DocumentVersion, "each distinct root must receive a distinct content version");
    }

    [Test]
    public async Task It_should_allocate_one_distinct_version_per_root_for_a_multi_root_child_update()
    {
        var firstRootDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 140, addressCount: 2);
        var secondRootDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 141, addressCount: 2);
        await BackdateOwnerTimestampAsync(firstRootDocumentId);
        await BackdateOwnerTimestampAsync(secondRootDocumentId);
        var beforeFirst = await ReadStampPairAsync(firstRootDocumentId);
        var beforeSecond = await ReadStampPairAsync(secondRootDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        // One statement changes child rows belonging to two different roots.
        var changedRows = await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."SchoolAddress"
            SET "City" = "City" || '-changed'
            WHERE "School_DocumentId" IN (@firstRootDocumentId, @secondRootDocumentId);
            """,
            new NpgsqlParameter("firstRootDocumentId", firstRootDocumentId),
            new NpgsqlParameter("secondRootDocumentId", secondRootDocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var afterFirst = await ReadStampPairAsync(firstRootDocumentId);
        var afterSecond = await ReadStampPairAsync(secondRootDocumentId);

        changedRows.Should().Be(4, "the statement changes two child rows for each of the two roots");
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(2L, "two distinct affected roots must each receive exactly one content version");
        afterFirst.Owner.ContentLastModifiedAt.Should().BeAfter(beforeFirst.Owner.ContentLastModifiedAt);
        afterSecond.Owner.ContentLastModifiedAt.Should().BeAfter(beforeSecond.Owner.ContentLastModifiedAt);
        afterFirst.Owner.ContentVersion.Should().Be(afterFirst.DocumentVersion);
        afterSecond.Owner.ContentVersion.Should().Be(afterSecond.DocumentVersion);
        afterFirst
            .DocumentVersion.Should()
            .NotBe(
                afterSecond.DocumentVersion,
                "each root must pull a distinct nextval — a per-statement cache would collide"
            );
    }

    [Test]
    public async Task It_should_allocate_one_content_version_for_a_multi_row_child_delete()
    {
        var schoolDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 150, addressCount: 3);
        await BackdateOwnerTimestampAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        var deletedRows = await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "edfi"."SchoolAddress"
            WHERE "School_DocumentId" = @schoolDocumentId;
            """,
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var after = await ReadStampPairAsync(schoolDocumentId);

        deletedRows.Should().Be(3, "the single statement must delete all three child rows");
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(
                1L,
                "a multi-row child delete against one surviving root must allocate exactly one content version, not one per deleted row"
            );
        after.DocumentVersion.Should().BeGreaterThan(before.DocumentVersion);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(before.Owner.ContentLastModifiedAt);
        after
            .Owner.ContentVersion.Should()
            .Be(after.DocumentVersion, "the root mirror ContentVersion must equal dms.Document");
    }

    [Test]
    public async Task It_should_not_advance_the_root_watermark_past_the_tombstone_on_a_cascaded_root_delete()
    {
        // Seed the full descendant graph (child, nested-child, root _ext, collection-aligned _ext) so the
        // cascading root delete fires every non-root statement-level DELETE trigger under its
        // root-existence guard.
        var schoolDocumentId = await SeedSchoolWithFullDescendantGraphAsync(schoolId: 160, addressCount: 3);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        // Delete the owning root. The row-level BEFORE-DELETE root trigger stamps the tombstone
        // ChangeVersion onto dms.Document; the row deletion then cascades to every descendant, and each
        // descendant AFTER-STATEMENT stamp trigger must observe the already-removed root and skip its stamp.
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM "edfi"."School"
            WHERE "DocumentId" = @schoolDocumentId;
            """,
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var documentContentVersion = await ReadDocumentContentVersionAsync(schoolDocumentId);
        var tombstoneChangeVersion = await ReadDeleteTombstoneChangeVersionAsync(schoolDocumentId);

        (await ReadSchoolRowCountAsync(schoolDocumentId))
            .Should()
            .Be(0L, "the cascading root delete must remove the root row");
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(
                1L,
                "only the root tombstone may allocate a content version; the cascaded child/nested-child/_ext deletes must not"
            );
        documentContentVersion
            .Should()
            .Be(
                tombstoneChangeVersion,
                "the root ContentVersion must remain at the tombstone's ChangeVersion after the cascade"
            );
        documentContentVersion
            .Should()
            .Be(
                afterMaxChangeVersion,
                "no descendant delete may advance the visible watermark past the root tombstone's ChangeVersion"
            );
    }

    private async Task<long> SeedSchoolWithAddressesAsync(int schoolId, int addressCount)
    {
        var schoolDocumentId = await InsertDocumentAsync("Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, schoolId);
        for (var ordinal = 1; ordinal <= addressCount; ordinal++)
        {
            await InsertSchoolAddressAsync(schoolDocumentId, ordinal, $"City-{schoolId}-{ordinal}");
        }
        return schoolDocumentId;
    }

    private async Task<long> SeedSchoolWithExtensionAddressesAsync(int schoolId, int addressCount)
    {
        var schoolDocumentId = await InsertDocumentAsync("Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, schoolId);
        await InsertSchoolExtensionAsync(schoolDocumentId, $"Campus-{schoolId}");
        for (var ordinal = 1; ordinal <= addressCount; ordinal++)
        {
            var addressCollectionItemId = await InsertSchoolAddressAsync(
                schoolDocumentId,
                ordinal,
                $"City-{schoolId}-{ordinal}"
            );
            await InsertSchoolExtensionAddressAsync(
                addressCollectionItemId,
                schoolDocumentId,
                $"Zone-{schoolId}-{ordinal}"
            );
        }
        return schoolDocumentId;
    }

    private async Task<long> SeedSchoolWithFullDescendantGraphAsync(int schoolId, int addressCount)
    {
        var schoolDocumentId = await InsertDocumentAsync("Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, schoolId);
        await InsertSchoolExtensionAsync(schoolDocumentId, $"Campus-{schoolId}");
        for (var ordinal = 1; ordinal <= addressCount; ordinal++)
        {
            var addressCollectionItemId = await InsertSchoolAddressAsync(
                schoolDocumentId,
                ordinal,
                $"City-{schoolId}-{ordinal}"
            );
            await InsertSchoolAddressPeriodAsync(
                addressCollectionItemId,
                schoolDocumentId,
                ordinal,
                $"Period-{schoolId}-{ordinal}"
            );
            await InsertSchoolExtensionAddressAsync(
                addressCollectionItemId,
                schoolDocumentId,
                $"Zone-{schoolId}-{ordinal}"
            );
        }
        return schoolDocumentId;
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

    private async Task InsertSchoolAsync(long documentId, int schoolId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentId", "SchoolId")
            VALUES (@documentId, @schoolId);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("schoolId", schoolId)
        );
    }

    private async Task InsertSchoolExtensionAsync(long documentId, string campusCode)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "sample"."SchoolExtension" ("DocumentId", "CampusCode")
            VALUES (@documentId, @campusCode);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("campusCode", campusCode)
        );
    }

    private async Task<long> InsertSchoolAddressAsync(long schoolDocumentId, int ordinal, string city)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO "edfi"."SchoolAddress" ("Ordinal", "School_DocumentId", "City")
            VALUES (@ordinal, @schoolDocumentId, @city)
            RETURNING "CollectionItemId";
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("city", city)
        );
    }

    private async Task InsertSchoolAddressPeriodAsync(
        long parentCollectionItemId,
        long schoolDocumentId,
        int ordinal,
        string periodName
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "edfi"."SchoolAddressPeriod" ("Ordinal", "ParentCollectionItemId", "School_DocumentId", "PeriodName")
            VALUES (@ordinal, @parentCollectionItemId, @schoolDocumentId, @periodName);
            """,
            new NpgsqlParameter("ordinal", ordinal),
            new NpgsqlParameter("parentCollectionItemId", parentCollectionItemId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("periodName", periodName)
        );
    }

    private async Task InsertSchoolExtensionAddressAsync(
        long baseCollectionItemId,
        long schoolDocumentId,
        string zone
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO "sample"."SchoolExtensionAddress" ("BaseCollectionItemId", "School_DocumentId", "Zone")
            VALUES (@baseCollectionItemId, @schoolDocumentId, @zone);
            """,
            new NpgsqlParameter("baseCollectionItemId", baseCollectionItemId),
            new NpgsqlParameter("schoolDocumentId", schoolDocumentId),
            new NpgsqlParameter("zone", zone)
        );
    }

    private async Task<long> ReadMaxChangeVersionAsync()
    {
        return await _database.ExecuteScalarAsync<long>("""SELECT "dms"."GetMaxChangeVersion"();""");
    }

    private async Task<long> ReadDocumentContentVersionAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """SELECT "ContentVersion" FROM "dms"."Document" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task<long> ReadDeleteTombstoneChangeVersionAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT tc."ChangeVersion"
            FROM "tracked_changes_edfi"."School" tc
            INNER JOIN "dms"."Document" d ON d."DocumentUuid" = tc."Id"
            WHERE d."DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task<long> ReadSchoolRowCountAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """SELECT COUNT(*) FROM "edfi"."School" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task<StampPair> ReadStampPairAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                doc."ContentVersion" AS "DocumentContentVersion",
                school."ContentVersion" AS "OwnerContentVersion",
                school."ContentLastModifiedAt" AS "OwnerContentLastModifiedAt",
                school."ContentCreatedAt" AS "OwnerContentCreatedAt"
            FROM "dms"."Document" doc
            INNER JOIN "edfi"."School" school ON school."DocumentId" = doc."DocumentId"
            WHERE doc."DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );
        var row = rows.Single();
        var requiredVersion = await _database.ExecuteScalarAsync<long>(
            """SELECT "RequiredContentVersion" FROM "dms"."DocumentProjectionWork" WHERE "DocumentId" = @documentId;""",
            new NpgsqlParameter("documentId", documentId)
        );
        requiredVersion.Should().Be(Convert.ToInt64(row["DocumentContentVersion"]));
        return new StampPair(
            Convert.ToInt64(row["DocumentContentVersion"]),
            new StampValues(
                Convert.ToInt64(row["OwnerContentVersion"]),
                Convert.ToDateTime(row["OwnerContentLastModifiedAt"]),
                Convert.ToDateTime(row["OwnerContentCreatedAt"])
            )
        );
    }

    private async Task SetOwnerCreatedAtSentinelAsync(long documentId)
    {
        // Stamp-only maintenance is excluded from representation stamping.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "ContentCreatedAt" = @createdAt
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("createdAt", CreatedAtSentinel),
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private async Task BackdateOwnerTimestampAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE "edfi"."School"
            SET "ContentLastModifiedAt" = '2000-01-01T00:00:00Z'
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );
    }

    private sealed record StampPair(long DocumentVersion, StampValues Owner);

    private sealed record StampValues(
        long ContentVersion,
        DateTime ContentLastModifiedAt,
        DateTime ContentCreatedAt
    );
}
