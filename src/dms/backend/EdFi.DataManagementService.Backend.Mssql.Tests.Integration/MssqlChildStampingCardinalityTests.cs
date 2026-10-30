// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// SQL Server counterpart of
/// <c>Given_A_Provisioned_Postgresql_Database_With_NonRoot_Statement_Level_Stamping</c>. Runs the same
/// logical multi-row child and collection-aligned <c>_ext</c> updates against a real SQL Server and
/// asserts the identical allocation cardinality, proving PostgreSQL and SQL Server advance
/// <c>ContentVersion</c> consistently after the DMS-1208 PostgreSQL dedupe change. SQL Server stamping
/// was already statement-level; this locks in cross-engine parity and guards against regression.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Provisioned_Mssql_Database_With_NonRoot_Statement_Level_Stamping
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private static readonly DateTime RewoundContentLastModifiedAt = new(
        2000,
        1,
        1,
        0,
        0,
        0,
        DateTimeKind.Utc
    );

    private MssqlGeneratedDdlTestDatabase _database = null!;

    // Whole microseconds, so the value round-trips exactly through datetime2(7).
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
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        await _database.ExecuteNonQueryAsync(
            "UPDATE [dms].[DocumentCacheState] SET [ProjectionLifecycleState] = 'Tracking';"
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
            SELECT s.name AS [SchemaName], t.name AS [TableName], c.name AS [ColumnName],
                   TYPE_NAME(c.user_type_id) AS [TypeName], c.is_nullable AS [IsNullable], c.scale AS [Scale]
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE ((s.name = 'dms' AND t.name IN ('Document', 'Descriptor'))
                OR (s.name = 'edfi' AND t.name = 'School'))
              AND c.name IN ('ContentVersion', 'ContentLastModifiedAt', 'ContentCreatedAt')
            ORDER BY s.name, t.name, c.name;
            """
        );
        columns
            .Select(row =>
                $"{row["SchemaName"]}.{row["TableName"]}.{row["ColumnName"]}:{row["TypeName"]}:{row["IsNullable"]}:{row["Scale"]}"
            )
            .Should()
            .Equal(
                "dms.Descriptor.ContentCreatedAt:datetime2:False:7",
                "dms.Descriptor.ContentLastModifiedAt:datetime2:False:7",
                "dms.Descriptor.ContentVersion:bigint:False:0",
                "dms.Document.ContentVersion:bigint:False:0",
                "edfi.School.ContentCreatedAt:datetime2:False:7",
                "edfi.School.ContentLastModifiedAt:datetime2:False:7",
                "edfi.School.ContentVersion:bigint:False:0"
            );
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task It_stamps_distinct_owners_concurrently_while_both_hold_root_and_work_locks(
        bool firstWriterChangesRoot
    )
    {
        var first = await SeedSchoolWithAddressesAsync(100, 2);
        var second = await SeedSchoolWithAddressesAsync(200, 2);
        var neighbor = await SeedSchoolWithAddressesAsync(300, 2);
        await RewindContentLastModifiedAtAsync(first);
        await RewindContentLastModifiedAtAsync(second);
        const string childWrite = """
            UPDATE [edfi].[SchoolAddress] SET [City] = [City] + '-changed'
            WHERE [School_DocumentId] = @documentId;
            """;
        await MssqlStampingConcurrencyScenario.RunAsync(
            _database,
            "[edfi].[School]",
            first,
            second,
            neighbor,
            firstWriterChangesRoot
                ? "UPDATE [edfi].[School] SET [SchoolId] = [SchoolId] + 1 WHERE [DocumentId] = @documentId;"
                : childWrite,
            childWrite
        );

        var roots = await _database.QueryRowsAsync(
            "SELECT [SchoolId] FROM [edfi].[School] ORDER BY [DocumentId];"
        );
        roots
            .Select(row => Convert.ToInt32(row["SchoolId"]))
            .Should()
            .Equal(firstWriterChangesRoot ? 101 : 100, 200, 300);
        var children = await _database.QueryRowsAsync(
            "SELECT [City] FROM [edfi].[SchoolAddress] ORDER BY [School_DocumentId], [Ordinal];"
        );
        children
            .Select(row => row["City"])
            .Should()
            .Equal(
                firstWriterChangesRoot ? "City-100-1" : "City-100-1-changed",
                firstWriterChangesRoot ? "City-100-2" : "City-100-2-changed",
                "City-200-1-changed",
                "City-200-2-changed",
                "City-300-1",
                "City-300-2"
            );
    }

    [Test]
    public async Task It_initializes_the_root_timestamp_without_allocating_an_extra_document_version()
    {
        var documentId = await InsertDocumentAsync("Ed-Fi", "School");
        var version = await ReadDocumentContentVersionAsync(documentId);
        var watermark = await ReadMaxChangeVersionAsync();
        var beforeInsert = await _database.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [SchoolId], [ContentLastModifiedAt], [ContentCreatedAt])
            VALUES (@documentId, 900, '2000-01-01', '2000-01-01');
            """,
            new SqlParameter("@documentId", documentId)
        );
        var afterInsert = await _database.ExecuteScalarAsync<DateTime>("SELECT SYSUTCDATETIME();");
        var after = await ReadStampPairAsync(documentId);
        after.DocumentVersion.Should().Be(version);
        after.Owner.ContentVersion.Should().Be(version);
        after.Owner.ContentLastModifiedAt.Should().BeOnOrAfter(beforeInsert).And.BeOnOrBefore(afterInsert);
        // The insert firing overrides both supplied timestamps from one captured value.
        after.Owner.ContentCreatedAt.Should().Be(after.Owner.ContentLastModifiedAt);
        (await ReadMaxChangeVersionAsync()).Should().Be(watermark);
    }

    [Test]
    public async Task It_terminates_stamp_maintenance_with_recursive_triggers_enabled()
    {
        await _database.ExecuteNonQueryAsync("ALTER DATABASE CURRENT SET RECURSIVE_TRIGGERS ON;");
        try
        {
            // The insert firing's own owner stamp re-fires the trigger as an UPDATE. A supplied creation
            // value makes that stamp change ContentCreatedAt, so the re-fire would allocate a version if
            // the creation column were in the no-op diff or the UPDATE() pre-filter.
            var insertedDocumentId = await InsertDocumentAsync("Ed-Fi", "School");
            var insertedVersion = await ReadDocumentContentVersionAsync(insertedDocumentId);
            var insertWatermark = await ReadMaxChangeVersionAsync();
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [edfi].[School] ([DocumentId], [SchoolId], [ContentCreatedAt])
                VALUES (@documentId, 905, '2000-01-01');
                """,
                new SqlParameter("@documentId", insertedDocumentId)
            );
            var inserted = await ReadStampPairAsync(insertedDocumentId);
            inserted.DocumentVersion.Should().Be(insertedVersion);
            inserted.Owner.ContentVersion.Should().Be(insertedVersion);
            inserted.Owner.ContentCreatedAt.Should().Be(inserted.Owner.ContentLastModifiedAt);
            (await ReadMaxChangeVersionAsync()).Should().Be(insertWatermark);

            var documentId = await SeedSchoolWithAddressesAsync(910, 3);
            var watermark = await ReadMaxChangeVersionAsync();
            await RewindContentLastModifiedAtAsync(documentId);
            await SetOwnerCreatedAtSentinelAsync(documentId);
            var before = await ReadStampPairAsync(documentId);
            before.Owner.ContentLastModifiedAt.Should().Be(RewoundContentLastModifiedAt);
            (await ReadMaxChangeVersionAsync()).Should().Be(watermark);
            await _database.ExecuteNonQueryAsync(
                "UPDATE [edfi].[SchoolAddress] SET [City] = [City] + '-changed' WHERE [School_DocumentId] = @documentId;",
                new SqlParameter("@documentId", documentId)
            );
            var after = await ReadStampPairAsync(documentId);
            after.DocumentVersion.Should().Be(watermark + 1);
            after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
            after.Owner.ContentLastModifiedAt.Should().BeAfter(before.Owner.ContentLastModifiedAt);
            after.Owner.ContentCreatedAt.Should().Be(CreatedAtSentinel);
            (await ReadMaxChangeVersionAsync()).Should().Be(watermark + 1);
        }
        finally
        {
            await _database.ExecuteNonQueryAsync("ALTER DATABASE CURRENT SET RECURSIVE_TRIGGERS OFF;");
        }
    }

    [TestCase("edfi", "School", "SchoolId", "DocumentId", "[SchoolId] + 1")]
    [TestCase("edfi", "SchoolAddressPeriod", "PeriodName", "School_DocumentId", "[PeriodName] + '-changed'")]
    [TestCase("sample", "SchoolExtension", "CampusCode", "DocumentId", "[CampusCode] + '-changed'")]
    public async Task It_stamps_root_nested_child_and_root_extension_changes_once(
        string schema,
        string table,
        string column,
        string ownerColumn,
        string value
    )
    {
        var documentId = await SeedSchoolWithFullDescendantGraphAsync(800, 3);
        await RewindContentLastModifiedAtAsync(documentId);
        await SetOwnerCreatedAtSentinelAsync(documentId);
        var before = await ReadStampPairAsync(documentId);
        var watermark = await ReadMaxChangeVersionAsync();
        await _database.ExecuteNonQueryAsync(
            $"""
            UPDATE [{schema}].[{table}] SET [{column}] = {value} WHERE [{ownerColumn}] = @documentId;
            """,
            new SqlParameter("documentId", documentId)
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
        await RewindContentLastModifiedAtAsync(documentId);
        await SetOwnerCreatedAtSentinelAsync(documentId);
        var before = await ReadStampPairAsync(documentId);
        var watermark = await ReadMaxChangeVersionAsync();
        var beforeWork = await _database.QueryRowsAsync(
            """SELECT * FROM [dms].[DocumentProjectionWork] WHERE [DocumentId] = @documentId;""",
            new SqlParameter("documentId", documentId)
        );
        await _database.ExecuteNonQueryAsync(
            $"""
            UPDATE [{schema}].[{table}] SET [{column}] = [{column}] WHERE [{ownerColumn}] = @documentId;
            """,
            new SqlParameter("documentId", documentId)
        );
        (await ReadStampPairAsync(documentId)).Should().Be(before);
        (await ReadMaxChangeVersionAsync()).Should().Be(watermark);
        var afterWork = await _database.QueryRowsAsync(
            """SELECT * FROM [dms].[DocumentProjectionWork] WHERE [DocumentId] = @documentId;""",
            new SqlParameter("documentId", documentId)
        );
        afterWork.Should().BeEquivalentTo(beforeWork);
    }

    [Test]
    public async Task It_should_allocate_one_content_version_for_a_multi_row_single_statement_child_insert()
    {
        var schoolDocumentId = await InsertDocumentAsync("Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, schoolId: 170);
        await RewindContentLastModifiedAtAsync(schoolDocumentId);
        await SetOwnerCreatedAtSentinelAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        // Multiple child rows for the same root in one INSERT statement exercise the statement-level
        // dedupe — the seed helpers insert one row per statement, so this is the only coverage of a
        // multi-row single-statement insert.
        var insertedRows = await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[SchoolAddress] ([Ordinal], [School_DocumentId], [City])
            VALUES (1, @schoolDocumentId, 'City-170-1'),
                   (2, @schoolDocumentId, 'City-170-2'),
                   (3, @schoolDocumentId, 'City-170-3');
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId)
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
            .Be(
                CreatedAtSentinel,
                "a child INSERT firing has an empty deleted set but must never take the root insert branch"
            );
    }

    [Test]
    public async Task It_should_preserve_the_root_creation_timestamp_on_a_root_extension_insert()
    {
        var schoolDocumentId = await InsertDocumentAsync("Ed-Fi", "School");
        await InsertSchoolAsync(schoolDocumentId, schoolId: 175);
        await RewindContentLastModifiedAtAsync(schoolDocumentId);
        await SetOwnerCreatedAtSentinelAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var watermark = await ReadMaxChangeVersionAsync();

        await InsertSchoolExtensionAsync(schoolDocumentId, "Campus-175");

        var after = await ReadStampPairAsync(schoolDocumentId);
        after.DocumentVersion.Should().Be(watermark + 1);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(before.Owner.ContentLastModifiedAt);
        after
            .Owner.ContentCreatedAt.Should()
            .Be(
                CreatedAtSentinel,
                "an _ext INSERT firing has an empty deleted set but must never take the root insert branch"
            );
    }

    [Test]
    public async Task It_should_allocate_one_content_version_for_a_multi_row_child_update()
    {
        var schoolDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 100, addressCount: 3);
        await RewindContentLastModifiedAtAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        var changedRows = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[SchoolAddress]
            SET [City] = [City] + '-changed'
            WHERE [School_DocumentId] = @schoolDocumentId;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId)
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
        await RewindContentLastModifiedAtAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        var changedRows = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [sample].[SchoolExtensionAddress]
            SET [Zone] = [Zone] + '-changed'
            WHERE [School_DocumentId] = @schoolDocumentId;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId)
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
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[SchoolAddress]
            SET [City] = [City]
            WHERE [School_DocumentId] = @schoolDocumentId;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId)
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
        await RewindContentLastModifiedAtAsync(oldRootDocumentId);
        await RewindContentLastModifiedAtAsync(newRootDocumentId);
        var beforeOld = await ReadStampPairAsync(oldRootDocumentId);
        var beforeNew = await ReadStampPairAsync(newRootDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        // Reparent both child rows from the old root to the new root in one statement.
        var changedRows = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[SchoolAddress]
            SET [School_DocumentId] = @newRootDocumentId
            WHERE [School_DocumentId] = @oldRootDocumentId;
            """,
            new SqlParameter("@newRootDocumentId", newRootDocumentId),
            new SqlParameter("@oldRootDocumentId", oldRootDocumentId)
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
        await RewindContentLastModifiedAtAsync(firstRootDocumentId);
        await RewindContentLastModifiedAtAsync(secondRootDocumentId);
        var beforeFirst = await ReadStampPairAsync(firstRootDocumentId);
        var beforeSecond = await ReadStampPairAsync(secondRootDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        // One statement changes child rows belonging to two different roots.
        var changedRows = await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[SchoolAddress]
            SET [City] = [City] + '-changed'
            WHERE [School_DocumentId] IN (@firstRootDocumentId, @secondRootDocumentId);
            """,
            new SqlParameter("@firstRootDocumentId", firstRootDocumentId),
            new SqlParameter("@secondRootDocumentId", secondRootDocumentId)
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
                "each root must pull a distinct NEXT VALUE — a per-statement cache would collide"
            );
    }

    [Test]
    public async Task It_should_allocate_one_content_version_for_a_multi_row_child_delete()
    {
        var schoolDocumentId = await SeedSchoolWithAddressesAsync(schoolId: 150, addressCount: 3);
        await RewindContentLastModifiedAtAsync(schoolDocumentId);
        var before = await ReadStampPairAsync(schoolDocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        var deletedRows = await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [edfi].[SchoolAddress]
            WHERE [School_DocumentId] = @schoolDocumentId;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId)
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

        // Delete the owning root. The root stamping trigger records the tombstone ChangeVersion onto
        // dms.Document; the row deletion then cascades to every descendant, and each descendant stamp
        // trigger must observe the already-removed root and skip its stamp.
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [edfi].[School]
            WHERE [DocumentId] = @schoolDocumentId;
            """,
            new SqlParameter("@schoolDocumentId", schoolDocumentId)
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
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", projectName),
            new SqlParameter("@resourceName", resourceName)
        );
    }

    private async Task<long> InsertDocumentAsync(string projectName, string resourceName)
    {
        var resourceKeyId = await GetResourceKeyIdAsync(projectName, resourceName);

        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([DocumentId] bigint);
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, @resourceKeyId);
            SELECT TOP (1) [DocumentId] FROM @Inserted;
            """,
            new SqlParameter("@documentUuid", Guid.NewGuid()),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    private async Task InsertSchoolAsync(long documentId, int schoolId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[School] ([DocumentId], [SchoolId])
            VALUES (@documentId, @schoolId);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@schoolId", schoolId)
        );
    }

    private async Task InsertSchoolExtensionAsync(long documentId, string campusCode)
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[SchoolExtension] ([DocumentId], [CampusCode])
            VALUES (@documentId, @campusCode);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@campusCode", campusCode)
        );
    }

    private async Task<long> InsertSchoolAddressAsync(long schoolDocumentId, int ordinal, string city)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            DECLARE @Inserted TABLE ([CollectionItemId] bigint);
            INSERT INTO [edfi].[SchoolAddress] ([Ordinal], [School_DocumentId], [City])
            OUTPUT INSERTED.[CollectionItemId] INTO @Inserted ([CollectionItemId])
            VALUES (@ordinal, @schoolDocumentId, @city);
            SELECT TOP (1) [CollectionItemId] FROM @Inserted;
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@city", city)
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
            INSERT INTO [edfi].[SchoolAddressPeriod] ([Ordinal], [ParentCollectionItemId], [School_DocumentId], [PeriodName])
            VALUES (@ordinal, @parentCollectionItemId, @schoolDocumentId, @periodName);
            """,
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@parentCollectionItemId", parentCollectionItemId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@periodName", periodName)
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
            INSERT INTO [sample].[SchoolExtensionAddress] ([BaseCollectionItemId], [School_DocumentId], [Zone])
            VALUES (@baseCollectionItemId, @schoolDocumentId, @zone);
            """,
            new SqlParameter("@baseCollectionItemId", baseCollectionItemId),
            new SqlParameter("@schoolDocumentId", schoolDocumentId),
            new SqlParameter("@zone", zone)
        );
    }

    private async Task<long> ReadMaxChangeVersionAsync()
    {
        return await _database.ExecuteScalarAsync<long>("SELECT [dms].[GetMaxChangeVersion]();");
    }

    private async Task<long> ReadDocumentContentVersionAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            "SELECT [ContentVersion] FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<long> ReadDeleteTombstoneChangeVersionAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT tc.[ChangeVersion]
            FROM [tracked_changes_edfi].[School] tc
            INNER JOIN [dms].[Document] d ON d.[DocumentUuid] = tc.[Id]
            WHERE d.[DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<long> ReadSchoolRowCountAsync(long documentId)
    {
        return await _database.ExecuteScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM [edfi].[School] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
    }

    private async Task<StampPair> ReadStampPairAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                doc.[ContentVersion] AS [DocumentContentVersion],
                school.[ContentVersion] AS [OwnerContentVersion],
                school.[ContentLastModifiedAt] AS [OwnerContentLastModifiedAt],
                school.[ContentCreatedAt] AS [OwnerContentCreatedAt]
            FROM [dms].[Document] doc
            INNER JOIN [edfi].[School] school ON school.[DocumentId] = doc.[DocumentId]
            WHERE doc.[DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
        var row = rows.Single();
        var requiredVersion = await _database.ExecuteScalarAsync<long>(
            "SELECT [RequiredContentVersion] FROM [dms].[DocumentProjectionWork] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
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

    private async Task RewindContentLastModifiedAtAsync(long documentId)
    {
        // Use a deterministic pre-operation stamp instead of depending on hosted-runner clock ticks
        // between the seed writes and the write being tested.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[School]
            SET [ContentLastModifiedAt] = @contentLastModifiedAt
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@contentLastModifiedAt", RewoundContentLastModifiedAt),
            new SqlParameter("@documentId", documentId)
        );
    }

    private sealed record StampPair(long DocumentVersion, StampValues Owner);

    private async Task SetOwnerCreatedAtSentinelAsync(long documentId)
    {
        // Stamp-only maintenance is excluded from representation stamping.
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [edfi].[School]
            SET [ContentCreatedAt] = @createdAt
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = CreatedAtSentinel },
            new SqlParameter("@documentId", documentId)
        );
    }

    private sealed record StampValues(
        long ContentVersion,
        DateTime ContentLastModifiedAt,
        DateTime ContentCreatedAt
    );
}
