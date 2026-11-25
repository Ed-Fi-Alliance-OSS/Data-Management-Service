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

        result.IsSatisfied.Should().BeTrue(result.Inventory.Message);
        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Satisfied);
    }

    [TestCase("DELETE FROM dms.MssqlPhysicalSchema")]
    [TestCase("UPDATE dms.MssqlPhysicalSchema SET MssqlPhysicalSchemaVersion = 'V2'")]
    [TestCase("DROP TABLE dms.MssqlPhysicalSchema")]
    [TestCase("ALTER TABLE dms.MssqlPhysicalSchema NOCHECK CONSTRAINT CK_MssqlPhysicalSchema_Singleton")]
    [TestCase("ALTER TABLE dms.MssqlPhysicalSchema DROP CONSTRAINT PK_MssqlPhysicalSchema")]
    [TestCase(
        """
            ALTER TABLE dms.DocumentCache DROP CONSTRAINT CK_DocumentCache_IsJsonObject;
            ALTER TABLE dms.DocumentCache DROP COLUMN DocumentJson;
            ALTER TABLE dms.DocumentCache ADD DocumentJson nvarchar(max) NOT NULL;
            ALTER TABLE dms.DocumentCache ADD CONSTRAINT CK_DocumentCache_IsJsonObject CHECK (ISJSON(DocumentJson, OBJECT) = 1);
            """
    )]
    public async Task It_rejects_invalid_physical_schema_before_cache_admission(string mutation)
    {
        await using var database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(database, mutation);

        var result = await _validator.ValidateInventoryAsync(database.ConnectionString);

        result.IsSatisfied.Should().BeFalse();
        result.Inventory.Message.Should().Contain("reprovision");
    }

    [Test]
    public async Task It_requires_only_read_access_to_the_physical_marker_on_the_cache_connection()
    {
        await using var database = await CreateDatabaseAsync();
        var login = $"cache_reader_{Guid.NewGuid():N}";
        var password = $"Test_{Guid.NewGuid():N}!";
        var connectionString = new SqlConnectionStringBuilder(database.ConnectionString)
        {
            IntegratedSecurity = false,
            UserID = login,
            Password = password,
            Pooling = false,
        }.ConnectionString;
        await ExecuteNonQueryAsync(database, $"CREATE LOGIN [{login}] WITH PASSWORD = '{password}'");
        try
        {
            await ExecuteNonQueryAsync(
                database,
                $"""
                CREATE USER [{login}] FOR LOGIN [{login}];
                CREATE ROLE [cache_runtime];
                ALTER ROLE [cache_runtime] ADD MEMBER [{login}];
                GRANT SELECT, VIEW DEFINITION ON SCHEMA::dms TO [cache_runtime];
                GRANT INSERT, UPDATE, DELETE ON OBJECT::dms.DocumentCache TO [cache_runtime];
                GRANT INSERT, UPDATE, DELETE ON OBJECT::dms.DocumentProjectionWork TO [cache_runtime];
                """
            );
            var admitted = await _validator.ValidateInventoryAsync(connectionString);
            admitted.IsSatisfied.Should().BeTrue(admitted.Inventory.Message);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT HAS_PERMS_BY_NAME('dms.MssqlPhysicalSchema','OBJECT','INSERT')
                     + HAS_PERMS_BY_NAME('dms.MssqlPhysicalSchema','OBJECT','UPDATE')
                     + HAS_PERMS_BY_NAME('dms.MssqlPhysicalSchema','OBJECT','DELETE');
                """;
            (await command.ExecuteScalarAsync()).Should().Be(0);

            await ExecuteNonQueryAsync(
                database,
                $"DENY SELECT ON OBJECT::dms.MssqlPhysicalSchema TO [{login}]"
            );
            var denied = await _validator.ValidateInventoryAsync(connectionString);
            denied.IsSatisfied.Should().BeFalse();
            denied.Inventory.Message.Should().Contain("reprovision");
        }
        finally
        {
            await ExecuteNonQueryAsync(database, $"DROP LOGIN [{login}]");
        }
    }

    [TestCase("NOCHECK CONSTRAINT")]
    [TestCase(
        "NOCHECK CONSTRAINT [CK_DocumentCache_IsJsonObject]; ALTER TABLE dms.DocumentCache WITH NOCHECK CHECK CONSTRAINT"
    )]
    public async Task It_requires_an_enabled_trusted_json_object_check(string mutation)
    {
        await using var database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            $"ALTER TABLE dms.DocumentCache {mutation} [CK_DocumentCache_IsJsonObject]"
        );

        var result = await _validator.ValidateInventoryAsync(database.ConnectionString);

        result.IsSatisfied.Should().BeFalse();
        result.Inventory.Message.Should().Contain("CK_DocumentCache_IsJsonObject");
    }

    [Test]
    public async Task It_rejects_missing_resource_key_inventory()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            EXEC sp_rename N'dms.ResourceKey', N'ResourceKeyRenamed';
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Missing);
        result.Inventory.Message.Should().Contain("dms.ResourceKey");
        result.IsSatisfied.Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_missing_resource_key_project_resource_unique_artifact()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE [dms].[ResourceKey]
            DROP CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName];
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Missing);
        result.Inventory.Message.Should().Contain("UX_ResourceKey_ProjectName_ResourceName");
        result.IsSatisfied.Should().BeFalse();
    }

    [TestCaseSource(nameof(InvalidMssqlResourceKeyUniqueArtifactMutations))]
    public async Task It_rejects_invalid_resource_key_project_resource_unique_artifacts(string mutationSql)
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(database, mutationSql);

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
        result.Inventory.Message.Should().Contain("UX_ResourceKey_ProjectName_ResourceName");
        result.IsSatisfied.Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_a_document_table_without_resource_key_fencing()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE [dms].[Document] DROP CONSTRAINT [FK_Document_ResourceKey];
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Missing);
        result.Inventory.Message.Should().Contain("FK_Document_ResourceKey");
        result.IsSatisfied.Should().BeFalse();
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

    [Test]
    public async Task It_rejects_a_uuid_validation_trigger_with_expected_tokens_only_in_comments()
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
                -- INSERTED
                -- dms.Document
                -- DocumentId
                -- DocumentUuid
                -- <>
                -- THROW
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

    [Test]
    public async Task It_rejects_an_enqueue_trigger_with_expected_tokens_only_in_comments()
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
                    INNER LOOP JOIN dms.DocumentProjectionWork
                    OPTION (FORCE ORDER)
                    INSERT INTO dms.DocumentProjectionWork
                    WHERE NOT EXISTS
                    work WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
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
                    INNER LOOP JOIN dms.DocumentProjectionWork
                    OPTION (FORCE ORDER)
                    INSERT INTO dms.DocumentProjectionWork
                    WHERE NOT EXISTS
                    work WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
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

    [TestCaseSource(nameof(MssqlComputedAtDefaultMutations))]
    public async Task It_rejects_invalid_DocumentCache_ComputedAt_defaults(
        string mutationSql,
        DocumentCacheInventoryStatus expectedStatus
    )
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(database, mutationSql);

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(expectedStatus);
        result.IsSatisfied.Should().BeFalse();
    }

    [TestCaseSource(nameof(PermissiveMssqlCriticalCheckConstraintMutations))]
    public async Task It_rejects_permissive_same_named_critical_check_constraints(string mutationSql)
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(database, mutationSql);

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
        result.IsSatisfied.Should().BeFalse();
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

    [Test]
    public async Task It_rejects_a_work_paging_index_with_included_columns()
    {
        await using MssqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP INDEX [IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId]
            ON [dms].[DocumentProjectionWork];

            CREATE INDEX [IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId]
            ON [dms].[DocumentProjectionWork] ([FirstEnqueuedAt], [DocumentId])
            INCLUDE ([RequiredContentVersion]);
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

    private static IEnumerable<TestCaseData> MssqlComputedAtDefaultMutations()
    {
        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[DocumentCache]
            DROP CONSTRAINT [DF_DocumentCache_ComputedAt];
            """,
            DocumentCacheInventoryStatus.Missing
        ).SetName("It_rejects_sqlserver_documentcache_computedat_without_default_constraint");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[DocumentCache]
            DROP CONSTRAINT [DF_DocumentCache_ComputedAt];

            ALTER TABLE [dms].[DocumentCache]
            ADD CONSTRAINT [DF_DocumentCache_ComputedAt]
            DEFAULT (sysdatetime()) FOR [ComputedAt];
            """,
            DocumentCacheInventoryStatus.Invalid
        ).SetName("It_rejects_sqlserver_documentcache_computedat_with_wrong_default_expression");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[DocumentCache]
            DROP CONSTRAINT [DF_DocumentCache_ComputedAt];

            ALTER TABLE [dms].[DocumentCache]
            ADD CONSTRAINT [DF_DocumentCache_ComputedAt_Renamed]
            DEFAULT (sysutcdatetime()) FOR [ComputedAt];
            """,
            DocumentCacheInventoryStatus.Missing
        ).SetName("It_rejects_sqlserver_documentcache_computedat_with_renamed_default_constraint");
    }

    private static IEnumerable<TestCaseData> InvalidMssqlResourceKeyUniqueArtifactMutations()
    {
        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[ResourceKey]
            DROP CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName];

            CREATE INDEX [UX_ResourceKey_ProjectName_ResourceName]
            ON [dms].[ResourceKey] ([ProjectName], [ResourceName]);
            """
        ).SetName("It_rejects_sqlserver_resource_key_artifact_that_is_not_unique");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[ResourceKey]
            DROP CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName];

            CREATE UNIQUE INDEX [UX_ResourceKey_ProjectName_ResourceName]
            ON [dms].[ResourceKey] ([ResourceName], [ProjectName]);
            """
        ).SetName("It_rejects_sqlserver_resource_key_artifact_with_wrong_column_order");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[ResourceKey]
            DROP CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName];

            CREATE UNIQUE INDEX [UX_ResourceKey_ProjectName_ResourceName]
            ON [dms].[ResourceKey] ([ProjectName], [ResourceVersion]);
            """
        ).SetName("It_rejects_sqlserver_resource_key_artifact_with_wrong_columns");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[ResourceKey]
            DROP CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName];

            CREATE UNIQUE INDEX [UX_ResourceKey_ProjectName_ResourceName]
            ON [dms].[ResourceKey] ([ProjectName], [ResourceName])
            WHERE [ResourceKeyId] > 0;
            """
        ).SetName("It_rejects_sqlserver_resource_key_artifact_that_is_filtered");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[ResourceKey]
            DROP CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName];

            ALTER TABLE [dms].[ResourceKey]
            ADD [ResourceNameComputed] AS LOWER([ResourceName]);

            CREATE UNIQUE INDEX [UX_ResourceKey_ProjectName_ResourceName]
            ON [dms].[ResourceKey] ([ProjectName], [ResourceNameComputed]);
            """
        ).SetName("It_rejects_sqlserver_resource_key_artifact_with_computed_expression_keys");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[ResourceKey]
            DROP CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName];

            CREATE UNIQUE INDEX [UX_ResourceKey_ProjectName_ResourceName]
            ON [dms].[ResourceKey] ([ProjectName], [ResourceName])
            INCLUDE ([ResourceVersion]);
            """
        ).SetName("It_rejects_sqlserver_resource_key_artifact_with_included_columns");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[ResourceKey]
            DROP CONSTRAINT [UX_ResourceKey_ProjectName_ResourceName];

            CREATE UNIQUE INDEX [UX_ResourceKey_ProjectName_ResourceName]
            ON [dms].[ResourceKey] ([ProjectName], [ResourceName]);

            ALTER INDEX [UX_ResourceKey_ProjectName_ResourceName]
            ON [dms].[ResourceKey] DISABLE;
            """
        ).SetName("It_rejects_sqlserver_resource_key_artifact_that_is_disabled");
    }

    private static IEnumerable<TestCaseData> PermissiveMssqlCriticalCheckConstraintMutations()
    {
        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[DocumentCache]
            DROP CONSTRAINT [CK_DocumentCache_IsJsonObject];

            ALTER TABLE [dms].[DocumentCache]
            ADD CONSTRAINT [CK_DocumentCache_IsJsonObject]
            CHECK (ISJSON([DocumentJson]) = 1);
            """
        ).SetName("It_rejects_sqlserver_documentcache_json_object_check_that_allows_arrays");

        yield return new TestCaseData(
            """
            ALTER TABLE dms.DocumentCache DROP CONSTRAINT CK_DocumentCache_IsJsonObject;
            ALTER TABLE dms.DocumentCache ADD [Document Json] json NULL;
            ALTER TABLE dms.DocumentCache ADD CONSTRAINT CK_DocumentCache_IsJsonObject
                CHECK (ISJSON([Document Json], OBJECT) = 1);
            """
        ).SetName("It_rejects_the_sqlserver_json_object_check_on_a_lookalike_column");

        yield return new TestCaseData(
            """
            ALTER TABLE dms.DocumentCache DROP CONSTRAINT CK_DocumentCache_IsJsonObject;
            ALTER TABLE dms.DocumentCache ADD CONSTRAINT CK_DocumentCache_IsJsonObject
                CHECK (ISJSON(DocumentJson, OBJECT) = -1);
            """
        ).SetName("It_rejects_sqlserver_documentcache_json_check_with_wrong_comparison_value");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[DataStoreIdentity]
            DROP CONSTRAINT [CK_DataStoreIdentity_Singleton];

            ALTER TABLE [dms].[DataStoreIdentity]
            ADD CONSTRAINT [CK_DataStoreIdentity_Singleton]
            CHECK ([DataStoreIdentitySingletonId] IN (1, 2));
            """
        ).SetName("It_rejects_sqlserver_datastoreidentity_singleton_check_that_allows_id_2");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[DocumentCacheState]
            DROP CONSTRAINT [CK_DocumentCacheState_Singleton];

            ALTER TABLE [dms].[DocumentCacheState]
            ADD CONSTRAINT [CK_DocumentCacheState_Singleton]
            CHECK ([StateId] IN (1, 2));
            """
        ).SetName("It_rejects_sqlserver_documentcachestate_singleton_check_that_allows_id_2");

        yield return new TestCaseData(
            """
            ALTER TABLE [dms].[DocumentCacheState]
            DROP CONSTRAINT [CK_DocumentCacheState_Lifecycle];

            ALTER TABLE [dms].[DocumentCacheState]
            ADD CONSTRAINT [CK_DocumentCacheState_Lifecycle]
            CHECK (([ProjectionLifecycleState] = 'Disabled' AND DATALENGTH([ProjectionLifecycleState]) = 8)
                OR ([ProjectionLifecycleState] = 'Resetting' AND DATALENGTH([ProjectionLifecycleState]) = 9)
                OR ([ProjectionLifecycleState] = 'Rebuilding' AND DATALENGTH([ProjectionLifecycleState]) = 10)
                OR ([ProjectionLifecycleState] = 'Tracking' AND DATALENGTH([ProjectionLifecycleState]) = 8)
                OR ([ProjectionLifecycleState] = 'Paused' AND DATALENGTH([ProjectionLifecycleState]) = 6));
            """
        ).SetName("It_rejects_sqlserver_lifecycle_check_that_allows_extra_state");
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
