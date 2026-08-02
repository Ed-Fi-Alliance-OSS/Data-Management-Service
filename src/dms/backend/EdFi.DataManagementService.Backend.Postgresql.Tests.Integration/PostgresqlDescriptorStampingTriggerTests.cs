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
/// Behavior of the no-tombstone variant of <c>TR_Descriptor_Stamp_Document</c> (the
/// <c>small/minimal</c> fixture carries no shared descriptor tracked-change table). The descriptor row
/// is the authoritative stamp store: the trigger writes <c>dms.Descriptor</c>'s own columns and never
/// touches <c>dms.Document</c>.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Provisioned_Postgresql_Database_With_Descriptor_Stamping_Trigger
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

    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM dms."Descriptor";
            DELETE FROM dms."Document";
            """
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

    private async Task<(long DocumentId, StampValues Stamp)> SeedAsync(string codeValue = "Female")
    {
        var documentId = await InsertDocumentAsync();
        await InsertDescriptorAsync(documentId, codeValue);

        return (documentId, await ReadDescriptorContentStampAsync(documentId));
    }

    private async Task<long> InsertDocumentAsync()
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            """SELECT MIN("ResourceKeyId") FROM dms."ResourceKey";"""
        );

        return await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
            VALUES (@uuid, @resourceKeyId)
            RETURNING "DocumentId";
            """,
            new NpgsqlParameter("uuid", Guid.NewGuid()),
            new NpgsqlParameter("resourceKeyId", resourceKeyId)
        );
    }

    private async Task InsertDescriptorAsync(long documentId, string codeValue = "Female")
    {
        var uriOrDiscriminator = $"uri://ed-fi.org/SexDescriptor#{codeValue}";
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO dms."Descriptor"
                ("DocumentId", "Namespace", "CodeValue", "ShortDescription", "Description",
                 "EffectiveBeginDate", "EffectiveEndDate", "Discriminator", "Uri")
            VALUES (@documentId, @namespace, @codeValue, @shortDescription, @description,
                    NULL, NULL, @discriminator, @uri);
            """,
            new NpgsqlParameter("documentId", documentId),
            new NpgsqlParameter("namespace", "uri://ed-fi.org/SexDescriptor"),
            new NpgsqlParameter("codeValue", codeValue),
            new NpgsqlParameter("shortDescription", codeValue),
            new NpgsqlParameter("description", codeValue),
            new NpgsqlParameter("discriminator", uriOrDiscriminator),
            new NpgsqlParameter("uri", uriOrDiscriminator)
        );
    }

    private async Task<StampValues> ReadDocumentStampAsync(long documentId)
    {
        return await ReadStampAsync(
            @"dms.""Document""",
            "ContentVersion",
            "ContentLastModifiedAt",
            documentId
        );
    }

    private async Task<StampValues> ReadDescriptorContentStampAsync(long documentId)
    {
        return await ReadStampAsync(
            @"dms.""Descriptor""",
            "ContentVersion",
            "ContentLastModifiedAt",
            documentId
        );
    }

    private async Task<StampValues> ReadDescriptorIdentityStampAsync(long documentId)
    {
        return await ReadStampAsync(
            @"dms.""Descriptor""",
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
            SELECT "{versionColumn}" AS "Version", "{lastModifiedColumn}" AS "LastModifiedAt"
            FROM {table}
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", documentId)
        );
        var row = rows.Single();
        return new(Convert.ToInt64(row["Version"]), Convert.ToDateTime(row["LastModifiedAt"]));
    }

    private async Task<long> ReadMaxChangeVersionAsync()
    {
        return await _database.ExecuteScalarAsync<long>("""SELECT "dms"."GetMaxChangeVersion"();""");
    }

    private async Task DelayForDistinctTimestampsAsync()
    {
        // Server-side delay so the post-write ContentLastModifiedAt is strictly greater
        // than the seed stamp, letting assertions use BeAfter instead of the weaker
        // BeOnOrAfter (which cannot catch a stamp that never moved).
        await _database.ExecuteNonQueryAsync("SELECT pg_sleep(0.02);");
    }

    private sealed record StampValues(long Version, DateTime LastModifiedAt);

    [Test]
    public async Task It_stamps_both_version_pairs_on_descriptor_insert()
    {
        // A new descriptor row has no prior stamp to preserve, so the INSERT arm takes the identity
        // stamp alongside the content stamp — two sequence values, both landing on the descriptor row.
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
        // variant's DELETE arm has nothing left to write.
        var seed = await SeedAsync();
        var beforeDocument = await ReadDocumentStampAsync(seed.DocumentId);
        var beforeMaxChangeVersion = await ReadMaxChangeVersionAsync();

        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM dms."Descriptor"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", seed.DocumentId)
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
            UPDATE dms."Descriptor"
            SET "ShortDescription" = 'Changed Short Description'
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", seed.DocumentId)
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
            UPDATE dms."Descriptor"
            SET "ShortDescription" = "ShortDescription"
            WHERE "DocumentId" = @documentId;
            """,
            new NpgsqlParameter("documentId", seed.DocumentId)
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
            UPDATE dms."Descriptor"
            SET "ShortDescription" = 'Changed Short Description'
            WHERE "DocumentId" IN (@documentIdA, @documentIdB);
            """,
            new NpgsqlParameter("documentIdA", seedA.DocumentId),
            new NpgsqlParameter("documentIdB", seedB.DocumentId)
        );

        var afterA = await ReadDescriptorContentStampAsync(seedA.DocumentId);
        var afterB = await ReadDescriptorContentStampAsync(seedB.DocumentId);
        afterA.Version.Should().BeGreaterThan(seedA.Stamp.Version);
        afterB.Version.Should().BeGreaterThan(seedB.Stamp.Version);
        afterA
            .Version.Should()
            .NotBe(
                afterB.Version,
                "each row must pull a distinct nextval — a per-statement cache would collide"
            );
    }
}
