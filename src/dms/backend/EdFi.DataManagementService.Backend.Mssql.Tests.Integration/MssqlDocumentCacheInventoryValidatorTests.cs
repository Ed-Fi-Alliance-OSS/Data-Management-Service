// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("DocumentCacheInventory")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCacheInventory_Validator
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlDocumentCacheInventoryValidator _validator = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _validator = new MssqlDocumentCacheInventoryValidator(
            NullLogger<MssqlDocumentCacheInventoryValidator>.Instance
        );
    }

    [Test]
    public void It_reports_the_sqlserver_provider_token()
    {
        _validator.ProviderToken.Should().Be(RelationalProviderToken.SqlServer);
    }

    [Test]
    public async Task It_accepts_valid_generated_DocumentCache_inventory()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.IsSatisfied.Should().BeTrue();
        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Satisfied);
    }

    [Test]
    public async Task It_classifies_missing_required_objects_as_missing_inventory()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP TABLE [dms].[DocumentCache];
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Missing);
        result.IsSatisfied.Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_renamed_required_objects()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            EXEC sp_rename N'dms.DocumentCache', N'DocumentCacheRenamed';
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Missing);
    }

    [Test]
    public async Task It_classifies_disabled_enqueue_triggers_separately()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DISABLE TRIGGER [dms].[TR_Document_EnqueueProjectionWork] ON [dms].[Document];
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Disabled);
    }

    [Test]
    public async Task It_classifies_missing_enqueue_triggers_as_missing()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP TRIGGER [dms].[TR_Document_EnqueueProjectionWork];
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Missing);
    }

    [Test]
    public async Task It_rejects_a_uuid_validation_trigger_with_the_expected_name_but_wrong_body()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            CREATE OR ALTER TRIGGER [dms].[TR_DocumentCache_ValidateDocumentUuid]
            ON [dms].[DocumentCache]
            AFTER INSERT, UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                RETURN;
            END;
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [TestCaseSource(nameof(InvalidMssqlAfterInsertUpdateTriggerShapes))]
    public async Task It_rejects_same_named_uuid_validation_triggers_with_invalid_sqlserver_shape(
        string timingAndEvents
    )
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            $$"""
            CREATE OR ALTER TRIGGER [dms].[TR_DocumentCache_ValidateDocumentUuid]
            ON [dms].[DocumentCache]
            {{timingAndEvents}}
            AS
            BEGIN
                SET NOCOUNT ON;
                /*
                    INSERTED
                    dms.Document
                    DocumentId
                    DocumentUuid
                    <>
                    THROW
                */
                RETURN;
            END;
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_rejects_an_enqueue_trigger_with_the_expected_name_but_wrong_body()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            CREATE OR ALTER TRIGGER [dms].[TR_Document_EnqueueProjectionWork]
            ON [dms].[Document]
            AFTER INSERT, UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                RETURN;
            END;
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Invalid);
    }

    [TestCaseSource(nameof(InvalidMssqlAfterInsertUpdateTriggerShapes))]
    public async Task It_rejects_same_named_enqueue_triggers_with_invalid_sqlserver_shape(
        string timingAndEvents
    )
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            $$"""
            CREATE OR ALTER TRIGGER [dms].[TR_Document_EnqueueProjectionWork]
            ON [dms].[Document]
            {{timingAndEvents}}
            AS
            BEGIN
                SET NOCOUNT ON;
                /*
                    DocumentCacheState
                    ProjectionLifecycleState
                    StateId = 1
                    'Disabled'
                    'Resetting'
                    'Rebuilding'
                    'Tracking'
                    inserted
                    deleted
                    MAX
                    GROUP BY i.DocumentId
                    dms.DocumentProjectionWork
                    UPDATE work
                    SET work.RequiredContentVersion = req.RequiredContentVersion
                    work.RequiredContentVersion < req.RequiredContentVersion
                    INSERT INTO dms.DocumentProjectionWork
                    LEFT JOIN dms.DocumentProjectionWork
                */
                RETURN;
            END;
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Invalid);
    }

    [Test]
    public async Task It_classifies_missing_singleton_rows_as_missing_inventory()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DELETE FROM [dms].[DataStoreIdentity];
            DELETE FROM [dms].[DocumentCacheState];
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Missing);
    }

    [Test]
    public async Task It_rejects_a_wrong_lifecycle_constraint_with_the_expected_name()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE [dms].[DocumentCacheState]
            DROP CONSTRAINT [CK_DocumentCacheState_Lifecycle];

            ALTER TABLE [dms].[DocumentCacheState]
            ADD CONSTRAINT [CK_DocumentCacheState_Lifecycle]
            CHECK ([ProjectionLifecycleState] = 'Disabled');
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_rejects_a_wrong_work_paging_index_column_order()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP INDEX [IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId]
            ON [dms].[DocumentProjectionWork];

            CREATE INDEX [IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId]
            ON [dms].[DocumentProjectionWork] ([DocumentId], [FirstEnqueuedAt]);
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_rejects_a_filtered_work_paging_index_with_the_expected_name_and_column_order()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP INDEX [IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId]
            ON [dms].[DocumentProjectionWork];

            CREATE INDEX [IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId]
            ON [dms].[DocumentProjectionWork] ([FirstEnqueuedAt], [DocumentId])
            WHERE [DocumentId] > 0;
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
        result.IsSatisfied.Should().BeFalse();
    }

    [TestCase("[dms].[DocumentCache]", "FK_DocumentCache_Document")]
    [TestCase("[dms].[DocumentProjectionWork]", "FK_DocumentProjectionWork_Document")]
    public async Task It_rejects_disabled_required_DocumentCache_foreign_keys(
        string tableName,
        string foreignKeyName
    )
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            $$"""
            ALTER TABLE {{tableName}}
            NOCHECK CONSTRAINT [{{foreignKeyName}}];
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
        result.IsSatisfied.Should().BeFalse();
    }

    [TestCase("[dms].[DocumentCache]", "FK_DocumentCache_Document")]
    [TestCase("[dms].[DocumentProjectionWork]", "FK_DocumentProjectionWork_Document")]
    public async Task It_rejects_untrusted_required_DocumentCache_foreign_keys(
        string tableName,
        string foreignKeyName
    )
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            $$"""
            ALTER TABLE {{tableName}}
            NOCHECK CONSTRAINT [{{foreignKeyName}}];

            ALTER TABLE {{tableName}}
            WITH NOCHECK CHECK CONSTRAINT [{{foreignKeyName}}];
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
        result.IsSatisfied.Should().BeFalse();
    }

    [Test]
    public async Task It_keeps_inventory_failures_scoped_to_the_validated_target()
    {
        await using MssqlGeneratedDdlTestDatabase invalidDatabase = await CreateDatabaseAsync();
        await using MssqlGeneratedDdlTestDatabase validDatabase = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            invalidDatabase,
            """
            DROP TABLE [dms].[DocumentCache];
            """
        );

        DocumentCacheProviderInventoryValidationResult invalidResult =
            await _validator.ValidateInventoryAsync(invalidDatabase.ConnectionString);
        DocumentCacheProviderInventoryValidationResult validResult = await _validator.ValidateInventoryAsync(
            validDatabase.ConnectionString
        );

        invalidResult.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Missing);
        validResult.IsSatisfied.Should().BeTrue();
    }

    private static IEnumerable<TestCaseData> InvalidMssqlAfterInsertUpdateTriggerShapes()
    {
        yield return new TestCaseData("INSTEAD OF INSERT, UPDATE").SetName(
            "It_rejects_sqlserver_trigger_with_instead_of_timing"
        );
        yield return new TestCaseData("AFTER UPDATE").SetName(
            "It_rejects_sqlserver_trigger_missing_insert_event"
        );
        yield return new TestCaseData("AFTER INSERT").SetName(
            "It_rejects_sqlserver_trigger_missing_update_event"
        );
        yield return new TestCaseData("AFTER INSERT, UPDATE, DELETE").SetName(
            "It_rejects_sqlserver_trigger_with_delete_event"
        );
    }

    private Task<MssqlGeneratedDdlTestDatabase> CreateDatabaseAsync() =>
        MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

    private static async Task ExecuteNonQueryAsync(MssqlGeneratedDdlTestDatabase database, string sql)
    {
        await using SqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
