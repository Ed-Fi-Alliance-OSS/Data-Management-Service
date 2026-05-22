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
public class Given_A_Provisioned_Mssql_Database_With_Descriptor_Stamping_Trigger
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

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
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(fixture.GeneratedDdl);
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
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    private async Task<(long DocumentId, long ContentVersion, DateTime ContentLastModifiedAt)> SeedAsync(
        string shortDescription = "Female"
    )
    {
        var resourceKeyId = await _database.ExecuteScalarAsync<short>(
            "SELECT MIN(ResourceKeyId) FROM [dms].[ResourceKey];"
        );

        var documentId = await _database.ExecuteScalarAsync<long>(
            """
            INSERT INTO [dms].[Document] (DocumentUuid, ResourceKeyId)
            VALUES (@uuid, @resourceKeyId);
            SELECT SCOPE_IDENTITY();
            """,
            new SqlParameter("@uuid", Guid.NewGuid()),
            new SqlParameter("@resourceKeyId", resourceKeyId)
        );

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
            new SqlParameter("@codeValue", "Female"),
            new SqlParameter("@shortDescription", shortDescription),
            new SqlParameter("@description", "Female"),
            new SqlParameter("@discriminator", "uri://ed-fi.org/SexDescriptor#Female"),
            new SqlParameter("@uri", "uri://ed-fi.org/SexDescriptor#Female")
        );

        var (cv, ts) = await ReadStampsAsync(documentId);
        return (documentId, cv, ts);
    }

    private async Task<(long ContentVersion, DateTime ContentLastModifiedAt)> ReadStampsAsync(long documentId)
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
        return (Convert.ToInt64(row["ContentVersion"]), Convert.ToDateTime(row["ContentLastModifiedAt"]));
    }

    private async Task<int> CountJournalRowsAsync(long documentId)
    {
        var rows = await _database.QueryRowsAsync(
            """
            SELECT COUNT(*) AS [Count]
            FROM [dms].[DocumentChangeEvent]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", documentId)
        );
        return Convert.ToInt32(rows.Single()["Count"]);
    }

    [Test]
    public async Task It_stamps_document_on_descriptor_value_change()
    {
        var seed = await SeedAsync();

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ShortDescription] = 'Changed Short Description'
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var after = await ReadStampsAsync(seed.DocumentId);
        after.ContentVersion.Should().BeGreaterThan(seed.ContentVersion);
        after.ContentLastModifiedAt.Should().BeOnOrAfter(seed.ContentLastModifiedAt);
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

        var after = await ReadStampsAsync(seed.DocumentId);
        after.ContentVersion.Should().Be(seed.ContentVersion);
        after.ContentLastModifiedAt.Should().Be(seed.ContentLastModifiedAt);
    }

    [Test]
    public async Task It_emits_journal_row_on_descriptor_value_change()
    {
        var seed = await SeedAsync();
        var journalsBefore = await CountJournalRowsAsync(seed.DocumentId);

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [Description] = 'Changed Description'
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var journalsAfter = await CountJournalRowsAsync(seed.DocumentId);
        (journalsAfter - journalsBefore).Should().Be(1);
    }

    [Test]
    public async Task It_does_not_emit_journal_row_on_descriptor_no_op_update()
    {
        var seed = await SeedAsync();
        var journalsBefore = await CountJournalRowsAsync(seed.DocumentId);

        await _database.ExecuteNonQueryAsync(
            """
            UPDATE [dms].[Descriptor]
            SET [ShortDescription] = [ShortDescription]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var journalsAfter = await CountJournalRowsAsync(seed.DocumentId);
        journalsAfter.Should().Be(journalsBefore);
    }

    [Test]
    public async Task It_does_not_double_stamp_on_descriptor_row_insert()
    {
        // SeedAsync inserts a Document (which produces one journal row via column defaults)
        // and then a Descriptor row. The descriptor stamping trigger is AFTER UPDATE only,
        // so the Descriptor INSERT must not produce an additional stamp / journal row.
        var seed = await SeedAsync();

        var journalRows = await CountJournalRowsAsync(seed.DocumentId);
        journalRows
            .Should()
            .Be(
                1,
                "Document INSERT defaults produce exactly one stamp; descriptor INSERT must not double-stamp"
            );
    }

    [Test]
    public async Task It_does_not_stamp_or_journal_on_descriptor_row_delete()
    {
        var seed = await SeedAsync();
        var journalsBefore = await CountJournalRowsAsync(seed.DocumentId);

        await _database.ExecuteNonQueryAsync(
            """
            DELETE FROM [dms].[Descriptor]
            WHERE DocumentId = @documentId;
            """,
            new SqlParameter("@documentId", seed.DocumentId)
        );

        var after = await ReadStampsAsync(seed.DocumentId);
        after.ContentVersion.Should().Be(seed.ContentVersion);
        after.ContentLastModifiedAt.Should().Be(seed.ContentLastModifiedAt);

        var journalsAfter = await CountJournalRowsAsync(seed.DocumentId);
        journalsAfter.Should().Be(journalsBefore);
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

        var after = await ReadStampsAsync(seed.DocumentId);
        after
            .ContentVersion.Should()
            .BeGreaterThan(
                seed.ContentVersion,
                "byte-level CAST comparison must detect case-only + trailing-space-only change"
            );
    }
}
