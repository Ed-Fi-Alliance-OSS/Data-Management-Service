// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal sealed record MssqlGeneratedDdlBaselineCounts(
    long EffectiveSchemaCount,
    long ResourceKeyCount,
    long SchemaComponentCount
);

internal sealed record MssqlGeneratedDdlMutableCounts(
    long DocumentCount,
    long ContactCount,
    long ContactExtensionCount,
    long ContactExtensionAuthorCount,
    long AuthEducationOrganizationCount
);

internal sealed record MssqlGeneratedDdlDocumentState(
    long DocumentId,
    long ContentVersion,
    long IdentityVersion
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
public class Given_MssqlGeneratedDdlTestDatabase
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/sample";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;

    [SetUp]
    public async Task Setup()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(
            FixtureRelativePath,
            strict: true
        );
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
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

        var firstDocumentState = await InsertContactDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "contact-1"
        );
        var firstCollectionItemId = await InsertContactExtensionAuthorAsync(
            firstDocumentState.DocumentId,
            1,
            "A"
        );
        await InsertAuthEducationOrganizationLinkAsync(100, 200);

        await _database.ResetAsync();

        var resetMutableCounts = await ReadMutableCountsAsync();
        var resetBaselineCounts = await ReadBaselineCountsAsync();
        var authorCollectionItemDefault = await _database.GetColumnDefaultAsync(
            "sample",
            "ContactExtensionAuthor",
            "CollectionItemId"
        );

        var secondDocumentState = await InsertContactDocumentAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "contact-1"
        );
        var secondCollectionItemId = await InsertContactExtensionAuthorAsync(
            secondDocumentState.DocumentId,
            1,
            "A"
        );

        resetMutableCounts.Should().Be(new MssqlGeneratedDdlMutableCounts(0, 0, 0, 0, 0));
        resetBaselineCounts.Should().Be(baselineCounts);
        authorCollectionItemDefault.Should().Contain("CollectionItemIdSequence");
        authorCollectionItemDefault.Should().Contain("NEXT VALUE FOR");
        (await _database.SequenceExistsAsync("dms", "ChangeVersionSequence")).Should().BeTrue();
        (await _database.SequenceExistsAsync("dms", "CollectionItemIdSequence")).Should().BeTrue();

        secondDocumentState.Should().Be(firstDocumentState);
        secondCollectionItemId.Should().Be(firstCollectionItemId);
    }

    private async Task<MssqlGeneratedDdlBaselineCounts> ReadBaselineCountsAsync()
    {
        return new(
            await ReadTableCountAsync("[dms].[EffectiveSchema]"),
            await ReadTableCountAsync("[dms].[ResourceKey]"),
            await ReadTableCountAsync("[dms].[SchemaComponent]")
        );
    }

    private async Task<MssqlGeneratedDdlMutableCounts> ReadMutableCountsAsync()
    {
        return new(
            await ReadTableCountAsync("[dms].[Document]"),
            await ReadTableCountAsync("[edfi].[Contact]"),
            await ReadTableCountAsync("[sample].[ContactExtension]"),
            await ReadTableCountAsync("[sample].[ContactExtensionAuthor]"),
            await ReadTableCountAsync("[auth].[EducationOrganizationIdToEducationOrganizationId]")
        );
    }

    private async Task<long> ReadTableCountAsync(string qualifiedTableName)
    {
        return await _database.ExecuteScalarAsync<long>($"""SELECT COUNT(*) FROM {qualifiedTableName};""");
    }

    private async Task<MssqlGeneratedDdlDocumentState> InsertContactDocumentAsync(
        Guid documentUuid,
        string contactUniqueId
    )
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            """
            SELECT [ResourceKeyId]
            FROM [dms].[ResourceKey]
            WHERE [ProjectName] = @projectName
              AND [ResourceName] = @resourceName;
            """,
            new SqlParameter("@projectName", "Ed-Fi"),
            new SqlParameter("@resourceName", "Contact")
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            VALUES (@documentUuid, @resourceKeyId);
            """,
            new SqlParameter("@documentUuid", documentUuid),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

        var documentRows = await _database.QueryRowsAsync(
            """
            SELECT [DocumentId], [ContentVersion], [IdentityVersion]
            FROM [dms].[Document]
            WHERE [DocumentUuid] = @documentUuid;
            """,
            new SqlParameter("@documentUuid", documentUuid)
        );
        var documentRow = documentRows.Should().ContainSingle().Which;
        var documentState = new MssqlGeneratedDdlDocumentState(
            Convert.ToInt64(documentRow["DocumentId"]),
            Convert.ToInt64(documentRow["ContentVersion"]),
            Convert.ToInt64(documentRow["IdentityVersion"])
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [edfi].[Contact] ([DocumentId], [ContactUniqueId], [FirstName], [LastSurname])
            VALUES (@documentId, @contactUniqueId, @firstName, @lastSurname);
            """,
            new SqlParameter("@documentId", documentState.DocumentId),
            new SqlParameter("@contactUniqueId", contactUniqueId),
            new SqlParameter("@firstName", "Test"),
            new SqlParameter("@lastSurname", "Contact")
        );

        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[ContactExtension] ([DocumentId])
            VALUES (@documentId);
            """,
            new SqlParameter("@documentId", documentState.DocumentId)
        );

        return documentState;
    }

    private async Task<long> InsertContactExtensionAuthorAsync(
        long contactDocumentId,
        int ordinal,
        string author
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [sample].[ContactExtensionAuthor] ([Contact_DocumentId], [Ordinal], [Author])
            VALUES (@contactDocumentId, @ordinal, @author);
            """,
            new SqlParameter("@contactDocumentId", contactDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@author", author)
        );

        return await _database.ExecuteScalarAsync<long>(
            """
            SELECT [CollectionItemId]
            FROM [sample].[ContactExtensionAuthor]
            WHERE [Contact_DocumentId] = @contactDocumentId
              AND [Ordinal] = @ordinal
              AND [Author] = @author;
            """,
            new SqlParameter("@contactDocumentId", contactDocumentId),
            new SqlParameter("@ordinal", ordinal),
            new SqlParameter("@author", author)
        );
    }

    private async Task InsertAuthEducationOrganizationLinkAsync(
        long sourceEducationOrganizationId,
        long targetEducationOrganizationId
    )
    {
        await _database.ExecuteNonQueryAsync(
            """
            INSERT INTO [auth].[EducationOrganizationIdToEducationOrganizationId]
                ([SourceEducationOrganizationId], [TargetEducationOrganizationId])
            VALUES (@sourceEducationOrganizationId, @targetEducationOrganizationId);
            """,
            new SqlParameter("@sourceEducationOrganizationId", sourceEducationOrganizationId),
            new SqlParameter("@targetEducationOrganizationId", targetEducationOrganizationId)
        );
    }
}
