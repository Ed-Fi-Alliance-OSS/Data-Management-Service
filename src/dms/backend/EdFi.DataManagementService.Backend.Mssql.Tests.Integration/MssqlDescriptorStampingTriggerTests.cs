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

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard3)]
public class Given_A_Provisioned_Mssql_Database_With_Descriptor_Stamping_Trigger
{
    private static readonly DateTime RewoundContentLastModifiedAt = new(
        2000,
        1,
        1,
        0,
        0,
        0,
        DateTimeKind.Utc
    );

    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.IntegrationFixtures/descriptor-runtime";

    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
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

        var fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _databaseLease = await MssqlBackendBaselineCache.AcquireLeaseAsync(
            FixtureRelativePath,
            strict: false,
            fixture.GeneratedDdl
        );
        _database = _databaseLease.Database;
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[Descriptor];
            DELETE FROM [dms].[Document];
            DELETE FROM [tracked_changes_edfi].[Descriptor];
            UPDATE [dms].[DocumentCacheState] SET [ProjectionLifecycleState] = 'Tracking';
            DBCC CHECKIDENT ('[dms].[Document]', RESEED, 1000) WITH NO_INFOMSGS;
            DBCC CHECKIDENT ('[dms].[Descriptor]', RESEED, 1) WITH NO_INFOMSGS;
            """
        );
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_databaseLease is not null)
        {
            await _databaseLease.DisposeAsync();
        }
    }

    private async Task<(long DocumentId, long ContentVersion, DateTime ContentLastModifiedAt)> SeedAsync(
        string shortDescription = "Female",
        string codeValue = "Female"
    )
    {
        var documentId = await InsertDocumentAsync();
        await InsertDescriptorAsync(documentId, shortDescription, codeValue);
        var descriptorId = await _database.ExecuteScalarAsync<int>(
            "SELECT [DescriptorId] FROM [dms].[Descriptor] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
        descriptorId.Should().NotBe((int)documentId);
        await RewindContentLastModifiedAtAsync(documentId);
        await SetOwnerCreatedAtAsync(documentId, CreatedAtSentinel);

        var stamps = await ReadStampPairAsync(documentId);
        return (documentId, stamps.DocumentVersion, stamps.Owner.ContentLastModifiedAt);
    }

    private async Task<long> InsertDocumentAsync()
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            "SELECT [ResourceKeyId] FROM [dms].[ResourceKey] WHERE [ProjectName] = 'Ed-Fi' AND [ResourceName] = 'SchoolTypeDescriptor';"
        );

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO [dms].[Document] (DocumentUuid, ResourceKeyId)
            VALUES (@uuid, @resourceKeyId);
            SELECT SCOPE_IDENTITY();
            """,
            new SqlParameter("@uuid", Guid.NewGuid()),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );
    }

    private async Task InsertDescriptorAsync(
        long documentId,
        string shortDescription = "Female",
        string codeValue = "Female"
    )
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            "SELECT [ResourceKeyId] FROM [dms].[ResourceKey] WHERE [ProjectName] = 'Ed-Fi' AND [ResourceName] = 'SchoolTypeDescriptor';"
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Descriptor]
                ([DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription], [Description],
                 [EffectiveBeginDate], [EffectiveEndDate], [ContentLastModifiedAt], [ContentCreatedAt])
            VALUES (@documentId, @resourceKeyId, @namespace, @codeValue, @shortDescription, @description,
                    NULL, NULL, '2000-01-01', '2000-01-01');
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId),
            new SqlParameter("@namespace", "uri://ed-fi.org/SchoolTypeDescriptor"),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", codeValue)
        );
    }

    private async Task RewindContentLastModifiedAtAsync(long documentId)
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ContentLastModifiedAt] = @contentLastModifiedAt
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@contentLastModifiedAt", RewoundContentLastModifiedAt)
        );
    }

    private async Task<long> ReadDocumentVersionAsync(long documentId) =>
        await _database.ExecuteScalarAsync<long>(
            "SELECT [ContentVersion] FROM [dms].[Document] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );

    private async Task<StampPair> ReadStampPairAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                doc.ContentVersion AS DocumentContentVersion,
                descriptor.ContentVersion AS OwnerContentVersion,
                descriptor.ContentLastModifiedAt AS OwnerContentLastModifiedAt,
                descriptor.ContentCreatedAt AS OwnerContentCreatedAt
            FROM [dms].[Document] doc
            INNER JOIN [dms].[Descriptor] descriptor ON descriptor.DocumentId = doc.DocumentId
            WHERE doc.DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
        var row = rows.Single();
        var requiredVersion = await _database.ExecuteScalarAsync<long>(
            "SELECT [RequiredContentVersion] FROM [dms].[DocumentProjectionWork] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
        requiredVersion.Should().Be(Convert.ToInt64(row["DocumentContentVersion"]));
        return new(
            Convert.ToInt64(row["DocumentContentVersion"]),
            new(
                Convert.ToInt64(row["OwnerContentVersion"]),
                Convert.ToDateTime(row["OwnerContentLastModifiedAt"]),
                Convert.ToDateTime(row["OwnerContentCreatedAt"])
            )
        );
    }

    private async Task<long> ReadMaxChangeVersionAsync()
    {
        return await _database.ExecuteScalarAsync<long>("SELECT [dms].[GetMaxChangeVersion]();");
    }

    private sealed record StampPair(long DocumentVersion, StampValues Owner);

    private async Task SetOwnerCreatedAtAsync(long documentId, DateTime createdAt)
    {
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ContentCreatedAt] = @createdAt
            WHERE [DocumentId] = @documentId;
            """,
            new SqlParameter("@createdAt", SqlDbType.DateTime2) { Value = createdAt },
            new SqlParameter("@documentId", documentId)
        );
    }

    private sealed record StampValues(
        long ContentVersion,
        DateTime ContentLastModifiedAt,
        DateTime ContentCreatedAt
    );

    [Test]
    public async Task It_stamps_distinct_unequal_id_descriptors_concurrently_while_both_hold_owner_and_work_locks()
    {
        var first = await SeedAsync(codeValue: "First");
        var second = await SeedAsync(codeValue: "Second");
        var neighbor = await SeedAsync(codeValue: "Neighbor");
        const string write = """
            UPDATE [dms].[Descriptor] SET [ShortDescription] = [CodeValue] + '-changed'
            WHERE [DocumentId] = @documentId;
            """;
        await MssqlStampingConcurrencyScenario.RunAsync(
            _database,
            "[dms].[Descriptor]",
            first.DocumentId,
            second.DocumentId,
            neighbor.DocumentId,
            write,
            write
        );

        var descriptors = await _database.QueryRowsAsync(
            "SELECT [ShortDescription] FROM [dms].[Descriptor] ORDER BY [DocumentId];"
        );
        descriptors
            .Select(row => row["ShortDescription"])
            .Should()
            .Equal("First-changed", "Second-changed", "Female");
        (await _database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM [tracked_changes_edfi].[Descriptor];"))
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_initializes_owner_timestamp_and_copies_document_version_on_descriptor_insert()
    {
        var documentId = await InsertDocumentAsync();
        var beforeDocument = await ReadDocumentVersionAsync(documentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        await InsertDescriptorAsync(documentId);

        var after = await ReadStampPairAsync(documentId);
        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        after.DocumentVersion.Should().Be(beforeDocument);
        after.Owner.ContentVersion.Should().Be(beforeDocument);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(RewoundContentLastModifiedAt);
        // The insert firing overrides the supplied creation time from the same captured value.
        after.Owner.ContentCreatedAt.Should().Be(after.Owner.ContentLastModifiedAt);
        afterMaxChangeVersion.Should().Be(beforeMaxChangeVersion);
    }

    [Test]
    public async Task It_stamps_document_on_descriptor_delete()
    {
        var seed = await SeedAsync();
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[Descriptor]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var afterDocument = await ReadDocumentVersionAsync(seed.DocumentId);
        afterDocument.Should().BeGreaterThan(seed.ContentVersion);
        var tombstoneVersion = await _database.ExecuteScalarAsync<long>(
            "SELECT [ChangeVersion] FROM [tracked_changes_edfi].[Descriptor] WHERE [DocumentId] = @documentId;",
            new SqlParameter("@documentId", seed.DocumentId)
        );
        tombstoneVersion.Should().Be(afterDocument);
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(1L, "a descriptor delete must allocate exactly one content stamp");
    }

    [Test]
    public async Task It_stamps_document_on_descriptor_value_change()
    {
        var seed = await SeedAsync();
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var seedContentLastModifiedAt = seed.ContentLastModifiedAt;

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ShortDescription] = 'Changed Short Description'
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var after = await ReadStampPairAsync(seed.DocumentId);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
        after.DocumentVersion.Should().BeGreaterThan(seed.ContentVersion);
        after.Owner.ContentLastModifiedAt.Should().BeAfter(seedContentLastModifiedAt);
        after.Owner.ContentCreatedAt.Should().Be(CreatedAtSentinel);
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(1L, "a single descriptor value change must allocate exactly one content stamp");
    }

    [Test]
    public async Task It_does_not_stamp_document_on_descriptor_no_op_update()
    {
        var seed = await SeedAsync();
        var neighbor = await SeedAsync(codeValue: "Neighbor");
        var beforeNeighbor = await ReadStampPairAsync(neighbor.DocumentId);
        var watermark = await ReadMaxChangeVersionAsync();
        var beforeWork = await _database.QueryRowsAsync(
            "SELECT * FROM [dms].[DocumentProjectionWork] ORDER BY [DocumentId];"
        );

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ShortDescription] = [ShortDescription]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var after = await ReadStampPairAsync(seed.DocumentId);
        after.DocumentVersion.Should().Be(seed.ContentVersion);
        after.Owner.ContentLastModifiedAt.Should().Be(seed.ContentLastModifiedAt);
        after.Owner.ContentCreatedAt.Should().Be(CreatedAtSentinel);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
        (await ReadStampPairAsync(neighbor.DocumentId)).Should().Be(beforeNeighbor);
        (await ReadMaxChangeVersionAsync()).Should().Be(watermark);
        (
            await _database.QueryRowsAsync(
                "SELECT * FROM [dms].[DocumentProjectionWork] ORDER BY [DocumentId];"
            )
        )
            .Should()
            .BeEquivalentTo(beforeWork);
    }

    [Test]
    public async Task It_does_not_stamp_or_track_a_creation_timestamp_only_update()
    {
        var seed = await SeedAsync();
        var watermark = await ReadMaxChangeVersionAsync();
        var trackedRowsBefore = await _database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM [tracked_changes_edfi].[Descriptor];"
        );
        var beforeWork = await _database.QueryRowsAsync(
            "SELECT * FROM [dms].[DocumentProjectionWork] ORDER BY [DocumentId];"
        );
        var updatedCreatedAt = CreatedAtSentinel.AddDays(1);

        await SetOwnerCreatedAtAsync(seed.DocumentId, updatedCreatedAt);

        var after = await ReadStampPairAsync(seed.DocumentId);
        after.DocumentVersion.Should().Be(seed.ContentVersion);
        after.Owner.ContentVersion.Should().Be(seed.ContentVersion);
        after.Owner.ContentLastModifiedAt.Should().Be(seed.ContentLastModifiedAt);
        after.Owner.ContentCreatedAt.Should().Be(updatedCreatedAt);
        (await ReadMaxChangeVersionAsync()).Should().Be(watermark);
        (await _database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM [tracked_changes_edfi].[Descriptor];"))
            .Should()
            .Be(trackedRowsBefore);
        (
            await _database.QueryRowsAsync(
                "SELECT * FROM [dms].[DocumentProjectionWork] ORDER BY [DocumentId];"
            )
        )
            .Should()
            .BeEquivalentTo(beforeWork);
    }

    private async Task InsertBenchResourceKeyAsync()
    {
        await _database.ExecuteNonQueryAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 32000)
            INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
            VALUES (32000, 'Bench', 'BenchDescriptor', '1.0.0');
            """
        );
    }

    [Test]
    public async Task It_rejects_a_descriptor_update_whose_resource_key_id_diverges_from_the_document()
    {
        // List paging trusts dms.Descriptor.ResourceKeyId while GET-by-id trusts
        // dms.Document.ResourceKeyId, and no FK ties the two copies together. The trigger's
        // equality guard must reject the divergent write before the no-op diff (which
        // deliberately excludes ResourceKeyId) can short-circuit past it.
        var seed = await SeedAsync();
        await InsertBenchResourceKeyAsync();

        var act = async () =>
            await _database.ExecuteNonQueryAsync(
                """
                UPDATE [dms].[Descriptor]
                SET [ResourceKeyId] = 32000
                WHERE DocumentId = @documentId;
                """,
                new SqlParameter("@documentId", seed.DocumentId)
            );

        var exception = (await act.Should().ThrowAsync<SqlException>()).Which;
        exception.Message.Should().Contain("diverges from the owning dms.Document row");

        var storedResourceKeyId = await _database.ExecuteScalarAsync<short>(
            "SELECT ResourceKeyId FROM [dms].[Descriptor] WHERE DocumentId = @documentId;",
            new SqlParameter("@documentId", seed.DocumentId)
        );
        var documentResourceKeyId = await _database.ExecuteScalarAsync<short>(
            "SELECT ResourceKeyId FROM [dms].[Document] WHERE DocumentId = @documentId;",
            new SqlParameter("@documentId", seed.DocumentId)
        );
        storedResourceKeyId.Should().Be(documentResourceKeyId);
        var after = await ReadStampPairAsync(seed.DocumentId);
        after.DocumentVersion.Should().Be(seed.ContentVersion);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
    }

    [Test]
    public async Task It_rejects_a_descriptor_insert_whose_resource_key_id_diverges_from_the_document()
    {
        var documentId = await InsertDocumentAsync();
        await InsertBenchResourceKeyAsync();

        var act = async () =>
            await _database.ExecuteNonQueryAsync(
                """
                INSERT INTO [dms].[Descriptor]
                    ([DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription], [Description],
                     [EffectiveBeginDate], [EffectiveEndDate])
                VALUES (@documentId, 32000, 'uri://ed-fi.org/SchoolTypeDescriptor', 'Female', 'Female', 'Female',
                        NULL, NULL);
                """,
                new SqlParameter("@documentId", documentId)
            );

        var exception = (await act.Should().ThrowAsync<SqlException>()).Which;
        exception.Message.Should().Contain("diverges from the owning dms.Document row");

        var descriptorRowCount = await _database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM [dms].[Descriptor] WHERE DocumentId = @documentId;",
            new SqlParameter("@documentId", documentId)
        );
        descriptorRowCount.Should().Be(0);
    }

    [Test]
    public async Task It_does_not_stamp_document_when_a_resource_key_id_backfill_updates_both_tables()
    {
        // ResourceKeyId is immutable in production and deliberately excluded from the trigger's
        // no-op diff so a migration backfill UPDATE of the column cannot bump stamps. The
        // equality guard requires such a backfill to update dms.Document before dms.Descriptor.
        var seed = await SeedAsync();
        await InsertBenchResourceKeyAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Document]
            SET [ResourceKeyId] = 32000
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );
        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ResourceKeyId] = 32000
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var after = await ReadStampPairAsync(seed.DocumentId);
        after.DocumentVersion.Should().Be(seed.ContentVersion);
        after.Owner.ContentLastModifiedAt.Should().Be(seed.ContentLastModifiedAt);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
    }

    [Test]
    public async Task It_stamps_both_documents_on_multi_row_descriptor_update()
    {
        var seedA = await SeedAsync(codeValue: "Female");
        var seedB = await SeedAsync(codeValue: "Male");
        var neighbor = await SeedAsync(codeValue: "Neighbor");
        var beforeNeighbor = await ReadStampPairAsync(neighbor.DocumentId);
        var watermark = await ReadMaxChangeVersionAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ShortDescription] = 'Changed Short Description'
            WHERE DocumentId IN (@documentIdA, @documentIdB);
            """,
            new SqlParameter("@documentIdA", seedA.DocumentId),
            new SqlParameter("@documentIdB", seedB.DocumentId)
        );

        var afterA = await ReadStampPairAsync(seedA.DocumentId);
        var afterB = await ReadStampPairAsync(seedB.DocumentId);
        (await ReadStampPairAsync(neighbor.DocumentId)).Should().Be(beforeNeighbor);
        (await ReadMaxChangeVersionAsync()).Should().Be(watermark + 2);
        afterA.Owner.ContentLastModifiedAt.Should().BeAfter(seedA.ContentLastModifiedAt);
        afterB.Owner.ContentLastModifiedAt.Should().BeAfter(seedB.ContentLastModifiedAt);
        afterA.Owner.ContentVersion.Should().Be(afterA.DocumentVersion);
        afterB.Owner.ContentVersion.Should().Be(afterB.DocumentVersion);
        afterA.Owner.ContentCreatedAt.Should().Be(CreatedAtSentinel);
        afterB.Owner.ContentCreatedAt.Should().Be(CreatedAtSentinel);
        afterA.DocumentVersion.Should().BeGreaterThan(seedA.ContentVersion);
        afterB.DocumentVersion.Should().BeGreaterThan(seedB.ContentVersion);
        afterA
            .DocumentVersion.Should()
            .NotBe(
                afterB.DocumentVersion,
                "the affectedDocs CTE must allocate a distinct NEXT VALUE per joined Document row"
            );
    }

    [Test]
    public async Task It_stamps_document_on_case_or_trailing_space_change()
    {
        // Default MSSQL CI collation + ANSI_PADDING treat 'Female' and 'female ' as equal
        // for plain <>. The descriptor trigger wraps string columns in CAST(... AS varbinary(max))
        // so the trigger MUST detect this as a real change. Proves the byte-comparison path
        // in the emitted affectedDocs CTE is intact (a names-only helper would silently miss this).
        var seed = await SeedAsync(shortDescription: "Female");

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ShortDescription] = 'female '
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var after = await ReadStampPairAsync(seed.DocumentId);
        after.Owner.ContentVersion.Should().Be(after.DocumentVersion);
        after
            .DocumentVersion.Should()
            .BeGreaterThan(
                seed.ContentVersion,
                "byte-level CAST comparison must detect case-only + trailing-space-only change"
            );
    }
}
