// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
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

internal sealed record MssqlGeneratedDdlResetIntegrityState(
    long DisabledForeignKeyCount,
    long UntrustedForeignKeyCount,
    long TriggerCount,
    long DisabledTriggerCount
);

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard2)]
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
        var resetIntegrityState = await ReadResetIntegrityStateAsync();
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
        resetIntegrityState.DisabledForeignKeyCount.Should().Be(0);
        resetIntegrityState.UntrustedForeignKeyCount.Should().Be(0);
        resetIntegrityState.TriggerCount.Should().BeGreaterThan(0);
        resetIntegrityState.DisabledTriggerCount.Should().Be(0);
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

    private async Task<MssqlGeneratedDdlResetIntegrityState> ReadResetIntegrityStateAsync()
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT
                COALESCE(SUM(CASE WHEN foreign_keys.[is_disabled] = 1 THEN 1 ELSE 0 END), 0) AS [DisabledForeignKeyCount],
                COALESCE(SUM(CASE WHEN foreign_keys.[is_not_trusted] = 1 THEN 1 ELSE 0 END), 0) AS [UntrustedForeignKeyCount],
                (
                    SELECT COUNT(*)
                    FROM sys.triggers triggers
                    INNER JOIN sys.tables tables
                        ON tables.[object_id] = triggers.[parent_id]
                    INNER JOIN sys.schemas schemas
                        ON schemas.[schema_id] = tables.[schema_id]
                    WHERE triggers.[parent_class_desc] = N'OBJECT_OR_COLUMN'
                      AND tables.[is_ms_shipped] = 0
                      AND schemas.[name] NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
                      AND NOT (
                          schemas.[name] = N'dms'
                          AND tables.[name] IN (N'EffectiveSchema', N'ResourceKey', N'SchemaComponent')
                      )
                ) AS [TriggerCount],
                (
                    SELECT COUNT(*)
                    FROM sys.triggers triggers
                    INNER JOIN sys.tables tables
                        ON tables.[object_id] = triggers.[parent_id]
                    INNER JOIN sys.schemas schemas
                        ON schemas.[schema_id] = tables.[schema_id]
                    WHERE triggers.[parent_class_desc] = N'OBJECT_OR_COLUMN'
                      AND triggers.[is_disabled] = 1
                      AND tables.[is_ms_shipped] = 0
                      AND schemas.[name] NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
                      AND NOT (
                          schemas.[name] = N'dms'
                          AND tables.[name] IN (N'EffectiveSchema', N'ResourceKey', N'SchemaComponent')
                      )
                ) AS [DisabledTriggerCount]
            FROM sys.foreign_keys foreign_keys
            INNER JOIN sys.tables tables
                ON tables.[object_id] = foreign_keys.[parent_object_id]
            INNER JOIN sys.schemas schemas
                ON schemas.[schema_id] = tables.[schema_id]
            WHERE tables.[is_ms_shipped] = 0
              AND schemas.[name] NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys')
              AND NOT (
                  schemas.[name] = N'dms'
                  AND tables.[name] IN (N'EffectiveSchema', N'ResourceKey', N'SchemaComponent')
              );
            """
        );
        var row = rows.Should().ContainSingle().Which;

        return new(
            Convert.ToInt64(row["DisabledForeignKeyCount"]),
            Convert.ToInt64(row["UntrustedForeignKeyCount"]),
            Convert.ToInt64(row["TriggerCount"]),
            Convert.ToInt64(row["DisabledTriggerCount"])
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
