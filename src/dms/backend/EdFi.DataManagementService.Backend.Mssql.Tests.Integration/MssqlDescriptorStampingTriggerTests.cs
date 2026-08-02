// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Behavior of the no-tombstone variant of <c>TR_Descriptor_Stamp_Document</c> (the
/// <c>small/minimal</c> fixture carries no shared descriptor tracked-change table). The descriptor row
/// is the authoritative stamp store: the trigger writes <c>dms.Descriptor</c>'s own columns and never
/// touches <c>dms.Document</c>.
/// </summary>
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

    private async Task<(long DocumentId, StampValues Stamp)> SeedAsync(
        string shortDescription = "Female",
        string codeValue = "Female"
    )
    {
        var documentId = await InsertDocumentAsync();
        await InsertDescriptorAsync(documentId, shortDescription, codeValue);

        return (documentId, await ReadDescriptorContentStampAsync(documentId));
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
        var uriOrDiscriminator = $"uri://ed-fi.org/SexDescriptor#{codeValue}";
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Descriptor]
                ([DocumentId], [Namespace], [CodeValue], [ShortDescription], [Description],
                 [EffectiveBeginDate], [EffectiveEndDate], [Discriminator], [Uri])
            VALUES (@documentId, @namespace, @codeValue, @shortDescription, @description,
                    NULL, NULL, @discriminator, @uri);
            """,
            new SqlParameter("@documentId", documentId),
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
        return await ReadStampAsync(
            "[dms].[Document]",
            "ContentVersion",
            "ContentLastModifiedAt",
            documentId
        );
    }

    private async Task<StampValues> ReadDescriptorContentStampAsync(long documentId)
    {
        return await ReadStampAsync(
            "[dms].[Descriptor]",
            "ContentVersion",
            "ContentLastModifiedAt",
            documentId
        );
    }

    private async Task<StampValues> ReadDescriptorIdentityStampAsync(long documentId)
    {
        return await ReadStampAsync(
            "[dms].[Descriptor]",
            "IdentityVersion",
            "IdentityLastModifiedAt",
            documentId
        );
    }

    private async Task<StampValues> ReadStampAsync(
        string table,
        string versionColumn,
        string lastModifiedColumn,
        long documentId
    )
    {
        var rows = await _database.QueryRowsAsync(
            $"""
            SELECT [{versionColumn}] AS [Version], [{lastModifiedColumn}] AS [LastModifiedAt]
            FROM {table}
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
        var row = rows.Single();
        return new(Convert.ToInt64(row["Version"]), Convert.ToDateTime(row["LastModifiedAt"]));
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

    private sealed record StampValues(long Version, DateTime LastModifiedAt);

    [Test]
    public async Task It_stamps_both_version_pairs_on_descriptor_insert()
    {
        // A new descriptor row has no prior stamp to preserve, so the pure-insert workset takes the
        // identity stamp alongside the content stamp — two sequence values, both landing on the
        // descriptor row.
        var documentId = await InsertDocumentAsync();
        var beforeDocument = await ReadDocumentStampAsync(documentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        await InsertDescriptorAsync(documentId);

        var contentStamp = await ReadDescriptorContentStampAsync(documentId);
        var identityStamp = await ReadDescriptorIdentityStampAsync(documentId);
        var afterMaxChangeVersion = await ReadMaxChangeVersionAsync();

        contentStamp.Version.Should().BeGreaterThan(beforeMaxChangeVersion);
        identityStamp.Version.Should().BeGreaterThan(beforeMaxChangeVersion);
        contentStamp.Version.Should().NotBe(identityStamp.Version);
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(2L, "a descriptor insert allocates one content stamp and one identity stamp");
        (await ReadDocumentStampAsync(documentId))
            .Should()
            .Be(beforeDocument, "no trigger writes dms.Document any more");
    }

    [Test]
    public async Task It_does_not_stamp_on_descriptor_delete()
    {
        // The descriptor row is the stamp store and it is the row going away, so the no-tombstone
        // variant leaves pure deletes out of every stamp workset.
        var seed = await SeedAsync();
        var beforeDocument = await ReadDocumentStampAsync(seed.DocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[Descriptor]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        (await ReadMaxChangeVersionAsync()).Should().Be(beforeMaxChangeVersion);
        (await ReadDocumentStampAsync(seed.DocumentId)).Should().Be(beforeDocument);
    }

    [Test]
    public async Task It_stamps_the_descriptor_row_on_descriptor_value_change()
    {
        var seed = await SeedAsync();
        var beforeDocument = await ReadDocumentStampAsync(seed.DocumentId);
        var beforeIdentity = await ReadDescriptorIdentityStampAsync(seed.DocumentId);
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
        var after = await ReadDescriptorContentStampAsync(seed.DocumentId);
        after.Version.Should().BeGreaterThan(seed.Stamp.Version);
        after.LastModifiedAt.Should().BeAfter(seed.Stamp.LastModifiedAt);
        (afterMaxChangeVersion - beforeMaxChangeVersion)
            .Should()
            .Be(1L, "a single descriptor value change must allocate exactly one content stamp");
        (await ReadDescriptorIdentityStampAsync(seed.DocumentId))
            .Should()
            .Be(beforeIdentity, "a content change must not bump the identity stamp");
        (await ReadDocumentStampAsync(seed.DocumentId)).Should().Be(beforeDocument);
    }

    [Test]
    public async Task It_does_not_stamp_on_descriptor_no_op_update()
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

        (await ReadDescriptorContentStampAsync(seed.DocumentId)).Should().Be(seed.Stamp);
    }

    [Test]
    public async Task It_stamps_both_descriptor_rows_on_multi_row_descriptor_update()
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

        var afterA = await ReadDescriptorContentStampAsync(seedA.DocumentId);
        var afterB = await ReadDescriptorContentStampAsync(seedB.DocumentId);
        afterA.Version.Should().BeGreaterThan(seedA.Stamp.Version);
        afterB.Version.Should().BeGreaterThan(seedB.Stamp.Version);
        afterA
            .Version.Should()
            .NotBe(
                afterB.Version,
                "the affectedDocs workset must allocate a distinct NEXT VALUE per stamped descriptor row"
            );
    }

    [Test]
    public async Task It_stamps_the_descriptor_row_on_case_or_trailing_space_change()
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

        var after = await ReadDescriptorContentStampAsync(seed.DocumentId);
        after
            .Version.Should()
            .BeGreaterThan(
                seed.Stamp.Version,
                "byte-level CAST comparison must detect case-only + trailing-space-only change"
            );
    }
}
