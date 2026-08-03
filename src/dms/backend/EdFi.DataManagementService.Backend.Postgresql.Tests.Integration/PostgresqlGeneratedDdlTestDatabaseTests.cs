// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal sealed record PostgresqlGeneratedDdlBaselineCounts(
    long EffectiveSchemaCount,
    long ResourceKeyCount,
    long SchemaComponentCount
);

internal sealed record PostgresqlGeneratedDdlMutableCounts(long SchoolCount, long SchoolAddressCount);

internal sealed record PostgresqlGeneratedDdlDocumentState(
    long DocumentId,
    long ContentVersion,
    long IdentityVersion
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_PostgresqlGeneratedDdlTestDatabase
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlGeneratedDdlTestDatabase _database = null!;

    [SetUp]
    public async Task Setup()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_resets_mutable_generated_ddl_state_without_reprovisioning_the_database()
    {
        var baselineCounts = await ReadBaselineCountsAsync();

        var firstDocumentState = await InsertSchoolDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var firstCollectionItemId = await InsertSchoolAddressAsync(
            firstDocumentState.DocumentId,
            1,
            "Austin"
        );

        await _database.ResetAsync();

        var resetMutableCounts = await ReadMutableCountsAsync();
        var resetBaselineCounts = await ReadBaselineCountsAsync();
        var schoolAddressCollectionItemDefault = await _database.GetColumnDefaultAsync(
            "edfi",
            "SchoolAddress",
            "CollectionItemId"
        );

        var secondDocumentState = await InsertSchoolDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            100
        );
        var secondCollectionItemId = await InsertSchoolAddressAsync(
            secondDocumentState.DocumentId,
            1,
            "Austin"
        );

        resetMutableCounts.Should().Be(new PostgresqlGeneratedDdlMutableCounts(0, 0));
        resetBaselineCounts.Should().Be(baselineCounts);
        schoolAddressCollectionItemDefault.Should().Contain("CollectionItemIdSequence");
        (await _database.SequenceExistsAsync("dms", "CollectionItemIdSequence")).Should().BeTrue();

        secondDocumentState.Should().Be(firstDocumentState);
        secondCollectionItemId.Should().Be(firstCollectionItemId);
    }

    private async Task<PostgresqlGeneratedDdlBaselineCounts> ReadBaselineCountsAsync()
    {
        return new(
            await ReadTableCountAsync("dms.\"EffectiveSchema\""),
            await ReadTableCountAsync("dms.\"ResourceKey\""),
            await ReadTableCountAsync("dms.\"SchemaComponent\"")
        );
    }

    private async Task<PostgresqlGeneratedDdlMutableCounts> ReadMutableCountsAsync()
    {
        return new(
            await ReadTableCountAsync("edfi.\"School\""),
            await ReadTableCountAsync("edfi.\"SchoolAddress\"")
        );
    }

    private async Task<long> ReadTableCountAsync(string qualifiedTableName)
    {
        return await _database.ExecuteScalarAsync<long>($"""SELECT COUNT(*) FROM {qualifiedTableName};""");
    }

    private async Task<PostgresqlGeneratedDdlDocumentState> InsertSchoolDocumentAsync(
        Guid documentUuid,
        int schoolId
    )
    {
        // The School root row is the document: it originates its own DocumentId from
        // dms.DocumentIdSequence and its BEFORE INSERT stamp trigger writes ContentVersion /
        // IdentityVersion onto the same row, so one INSERT returns the whole document state.
        var documentRows = await _database.QueryRowsAsync(
            """
            INSERT INTO "edfi"."School" ("DocumentUuid", "SchoolId")
            VALUES (@documentUuid, @schoolId)
            RETURNING "DocumentId", "ContentVersion", "IdentityVersion";
            """,
            new NpgsqlParameter("documentUuid", documentUuid),
            new NpgsqlParameter("schoolId", schoolId)
        );
        var documentRow = documentRows.Should().ContainSingle().Which;

        return new PostgresqlGeneratedDdlDocumentState(
            Convert.ToInt64(documentRow["DocumentId"]),
            Convert.ToInt64(documentRow["ContentVersion"]),
            Convert.ToInt64(documentRow["IdentityVersion"])
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
}
