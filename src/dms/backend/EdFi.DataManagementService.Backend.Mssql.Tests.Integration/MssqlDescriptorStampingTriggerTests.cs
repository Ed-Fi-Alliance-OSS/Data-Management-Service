// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    private static readonly string FixtureRelativePath = Path.Combine(
        "src",
        "dms",
        "backend",
        "EdFi.DataManagementService.Backend.Ddl.Tests.Unit",
        "Fixtures",
        "small",
        "minimal"
    );

    private IMssqlGeneratedDdlBaselineLease _databaseLease = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;

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

        var stamps = await ReadStampPairAsync(documentId);
        return (documentId, stamps.Document.ContentVersion, stamps.Document.ContentLastModifiedAt);
    }

    private async Task<long> InsertDocumentAsync()
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            "SELECT MIN(ResourceKeyId) FROM [dms].[ResourceKey];"
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
            "SELECT MIN(ResourceKeyId) FROM [dms].[ResourceKey];"
        );

        var uriOrDiscriminator = $"uri://ed-fi.org/SexDescriptor#{codeValue}";
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Descriptor]
                ([DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription], [Description],
                 [EffectiveBeginDate], [EffectiveEndDate], [Discriminator], [Uri])
            VALUES (@documentId, @resourceKeyId, @namespace, @codeValue, @shortDescription, @description,
                    NULL, NULL, @discriminator, @uri);
            """,
            new SqlParameter("@documentId", documentId),
            new SqlParameter("@resourceKeyId", resourceKeyId),
            new SqlParameter("@namespace", "uri://ed-fi.org/SexDescriptor"),
            new SqlParameter("@codeValue", codeValue),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", codeValue),
            new SqlParameter("@discriminator", uriOrDiscriminator),
            new SqlParameter("@uri", uriOrDiscriminator)
        );
    }

    private async Task<StampValues> ReadDocumentStampAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT ContentVersion, ContentLastModifiedAt
            FROM [dms].[Document]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
        var row = rows.Single();
        return new(Convert.ToInt64(row["ContentVersion"]), Convert.ToDateTime(row["ContentLastModifiedAt"]));
    }

    private async Task<StampPair> ReadStampPairAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                doc.ContentVersion AS DocumentContentVersion,
                doc.ContentLastModifiedAt AS DocumentContentLastModifiedAt,
                descriptor.ContentVersion AS MirrorContentVersion,
                descriptor.ContentLastModifiedAt AS MirrorContentLastModifiedAt
            FROM [dms].[Document] doc
            INNER JOIN [dms].[Descriptor] descriptor ON descriptor.DocumentId = doc.DocumentId
            WHERE doc.DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
        var row = rows.Single();
        return new(
            new(
                Convert.ToInt64(row["DocumentContentVersion"]),
                Convert.ToDateTime(row["DocumentContentLastModifiedAt"])
            ),
            new(
                Convert.ToInt64(row["MirrorContentVersion"]),
                Convert.ToDateTime(row["MirrorContentLastModifiedAt"])
            )
        );
    }

    private async Task<long> ReadMaxChangeVersionAsync()
    {
        return await _database.ExecuteScalarAsync<long>("SELECT [dms].[GetMaxChangeVersion]();");
    }

    private async Task DelayForDistinctTimestampsAsync()
    {
        // Server-side delay so the post-write ContentLastModifiedAt is strictly greater
        // than the seed stamp, letting assertions use BeAfter instead of the weaker
        // BeOnOrAfter (which cannot catch a stamp that never moved).
        await _database.ExecuteNonQueryAsync("WAITFOR DELAY '00:00:00.050';");
    }

    private sealed record StampPair(StampValues Document, StampValues Mirror);

    private sealed record StampValues(long ContentVersion, DateTime ContentLastModifiedAt);

    [Test]
    public async Task It_copies_existing_document_stamp_on_descriptor_insert_without_allocating_version()
    {
        var documentId = await InsertDocumentAsync();
        var beforeDocument = await ReadDocumentStampAsync(documentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        await InsertDescriptorAsync(documentId);

        var after = await ReadStampPairAsync(documentId);
        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        after.Document.Should().Be(beforeDocument);
        after.Mirror.Should().Be(beforeDocument);
        afterMaxChangeVersion.Should().Be(beforeMaxChangeVersion);
    }

    [Test]
    public async Task It_stamps_document_on_descriptor_delete()
    {
        var seed = await SeedAsync();
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();
        await DelayForDistinctTimestampsAsync();

        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[Descriptor]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();
        var afterDocument = await ReadDocumentStampAsync(seed.DocumentId);
        afterDocument.ContentVersion.Should().BeGreaterThan(seed.ContentVersion);
        afterDocument.ContentLastModifiedAt.Should().BeAfter(seed.ContentLastModifiedAt);
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(1L, "a descriptor delete must allocate exactly one content stamp");
    }

    [Test]
    public async Task It_stamps_document_on_descriptor_value_change()
    {
        var seed = await SeedAsync();
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();
        await DelayForDistinctTimestampsAsync();

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
        after.Mirror.Should().Be(after.Document);
        after.Document.ContentVersion.Should().BeGreaterThan(seed.ContentVersion);
        after.Document.ContentLastModifiedAt.Should().BeAfter(seed.ContentLastModifiedAt);
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(1L, "a single descriptor value change must allocate exactly one content stamp");
    }

    [Test]
    public async Task It_does_not_stamp_document_on_descriptor_no_op_update()
    {
        var seed = await SeedAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ShortDescription] = [ShortDescription]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var after = await ReadStampPairAsync(seed.DocumentId);
        after.Document.ContentVersion.Should().Be(seed.ContentVersion);
        after.Document.ContentLastModifiedAt.Should().Be(seed.ContentLastModifiedAt);
        after.Mirror.Should().Be(after.Document);
    }

    [Test]
    public async Task It_does_not_stamp_document_when_only_resource_key_id_changes()
    {
        // ResourceKeyId is immutable in production and deliberately excluded from the trigger's
        // no-op diff so a migration backfill UPDATE of the column cannot bump stamps. Prove the
        // exclusion by forcing an actual value change on that column alone.
        var seed = await SeedAsync();
        await _database.ExecuteNonQueryAsync(
            """
            IF NOT EXISTS (SELECT 1 FROM [dms].[ResourceKey] WHERE [ResourceKeyId] = 32000)
            INSERT INTO [dms].[ResourceKey] ([ResourceKeyId], [ProjectName], [ResourceName], [ResourceVersion])
            VALUES (32000, 'Bench', 'BenchDescriptor', '1.0.0');
            """
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
        after.Document.ContentVersion.Should().Be(seed.ContentVersion);
        after.Document.ContentLastModifiedAt.Should().Be(seed.ContentLastModifiedAt);
        after.Mirror.Should().Be(after.Document);
    }

    [Test]
    public async Task It_stamps_both_documents_on_multi_row_descriptor_update()
    {
        var seedA = await SeedAsync(codeValue: "Female");
        var seedB = await SeedAsync(codeValue: "Male");

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
        afterA.Mirror.Should().Be(afterA.Document);
        afterB.Mirror.Should().Be(afterB.Document);
        afterA.Document.ContentVersion.Should().BeGreaterThan(seedA.ContentVersion);
        afterB.Document.ContentVersion.Should().BeGreaterThan(seedB.ContentVersion);
        afterA
            .Document.ContentVersion.Should()
            .NotBe(
                afterB.Document.ContentVersion,
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
        after.Mirror.Should().Be(after.Document);
        after
            .Document.ContentVersion.Should()
            .BeGreaterThan(
                seed.ContentVersion,
                "byte-level CAST comparison must detect case-only + trailing-space-only change"
            );
    }
}
