// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("DocumentCacheInventory")]
public class Given_A_Postgresql_DocumentCacheInventory_Validator
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/small/minimal";

    private PostgresqlGeneratedDdlFixture _fixture = null!;
    private PostgresqlDocumentCacheInventoryValidator _validator = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _fixture = PostgresqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _validator = new PostgresqlDocumentCacheInventoryValidator(
            NullLogger<PostgresqlDocumentCacheInventoryValidator>.Instance
        );
    }

    [Test]
    public void It_reports_the_postgresql_provider_token()
    {
        _validator.ProviderToken.Should().Be(RelationalProviderToken.Postgresql);
    }

    [Test]
    public async Task It_accepts_valid_generated_DocumentCache_inventory()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();

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
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP TABLE "dms"."DocumentCache" CASCADE;
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
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE "dms"."DocumentCache" RENAME TO "DocumentCacheRenamed";
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
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE "dms"."Document" DISABLE TRIGGER "TR_Document_EnqueueProjectionInsert";
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Disabled);
    }

    [Test]
    public async Task It_accepts_postgresql_documentcache_triggers_enabled_always()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE "dms"."DocumentCache" ENABLE ALWAYS TRIGGER "TR_DocumentCache_ValidateDocumentUuid";
            ALTER TABLE "dms"."Document" ENABLE ALWAYS TRIGGER "TR_Document_EnqueueProjectionInsert";
            ALTER TABLE "dms"."Document" ENABLE ALWAYS TRIGGER "TR_Document_EnqueueProjectionUpdate";
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.IsSatisfied.Should().BeTrue();
    }

    [Test]
    public async Task It_rejects_a_uuid_validation_trigger_enabled_replica()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE "dms"."DocumentCache" ENABLE REPLICA TRIGGER "TR_DocumentCache_ValidateDocumentUuid";
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_classifies_replica_enqueue_triggers_as_disabled()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE "dms"."Document" ENABLE REPLICA TRIGGER "TR_Document_EnqueueProjectionUpdate";
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Disabled);
    }

    [Test]
    public async Task It_classifies_missing_enqueue_functions_and_triggers_as_missing()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP TRIGGER "TR_Document_EnqueueProjectionInsert" ON "dms"."Document";
            DROP FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"();
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Missing);
    }

    [Test]
    public async Task It_rejects_a_uuid_validation_trigger_bound_to_a_wrong_schema_function()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            CREATE SCHEMA "dms_alt";

            CREATE OR REPLACE FUNCTION "dms_alt"."TF_DocumentCache_ValidateDocumentUuid"()
            RETURNS TRIGGER
            LANGUAGE plpgsql
            AS $func$
            BEGIN
                RETURN NEW;
            END;
            $func$;

            DROP TRIGGER "TR_DocumentCache_ValidateDocumentUuid" ON "dms"."DocumentCache";

            CREATE TRIGGER "TR_DocumentCache_ValidateDocumentUuid"
            BEFORE INSERT OR UPDATE ON "dms"."DocumentCache"
            FOR EACH ROW
            EXECUTE FUNCTION "dms_alt"."TF_DocumentCache_ValidateDocumentUuid"();
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_rejects_a_uuid_validation_function_with_the_expected_name_but_wrong_body()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            CREATE OR REPLACE FUNCTION "dms"."TF_DocumentCache_ValidateDocumentUuid"()
            RETURNS TRIGGER
            LANGUAGE plpgsql
            AS $func$
            BEGIN
                RETURN NEW;
            END;
            $func$;
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_rejects_an_enqueue_trigger_bound_to_a_wrong_schema_function()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            CREATE SCHEMA "dms_alt";

            CREATE OR REPLACE FUNCTION "dms_alt"."TF_Document_EnqueueProjectionInsert"()
            RETURNS TRIGGER
            LANGUAGE plpgsql
            AS $func$
            BEGIN
                RETURN NULL;
            END;
            $func$;

            DROP TRIGGER "TR_Document_EnqueueProjectionInsert" ON "dms"."Document";

            CREATE TRIGGER "TR_Document_EnqueueProjectionInsert"
            AFTER INSERT ON "dms"."Document"
            REFERENCING NEW TABLE AS new_rows
            FOR EACH STATEMENT
            EXECUTE FUNCTION "dms_alt"."TF_Document_EnqueueProjectionInsert"();
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Invalid);
    }

    [TestCaseSource(nameof(InvalidPostgresqlEnqueueTriggerShapes))]
    public async Task It_rejects_same_named_enqueue_triggers_with_invalid_postgresql_shape(string mutationSql)
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(database, mutationSql);

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Satisfied);
        result.EnqueueTrigger.Status.Should().Be(DocumentCacheEnqueueTriggerStatus.Invalid);
    }

    [Test]
    public async Task It_rejects_an_enqueue_function_with_the_expected_name_but_wrong_body()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            CREATE OR REPLACE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"()
            RETURNS TRIGGER
            LANGUAGE plpgsql
            SECURITY DEFINER
            AS $func$
            BEGIN
                RETURN NULL;
            END;
            $func$;
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
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DELETE FROM "dms"."DataStoreIdentity";
            DELETE FROM "dms"."DocumentCacheState";
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
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            ALTER TABLE "dms"."DocumentCacheState"
            DROP CONSTRAINT "CK_DocumentCacheState_Lifecycle";

            ALTER TABLE "dms"."DocumentCacheState"
            ADD CONSTRAINT "CK_DocumentCacheState_Lifecycle"
            CHECK ("ProjectionLifecycleState" IN ('Disabled'));
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [TestCaseSource(nameof(PermissivePostgresqlCriticalCheckConstraintMutations))]
    public async Task It_rejects_permissive_same_named_critical_check_constraints(string mutationSql)
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
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
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP INDEX "dms"."IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId";

            CREATE INDEX "IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId"
            ON "dms"."DocumentProjectionWork" ("DocumentId", "FirstEnqueuedAt");
            """
        );

        DocumentCacheProviderInventoryValidationResult result = await _validator.ValidateInventoryAsync(
            database.ConnectionString
        );

        result.Inventory.Status.Should().Be(DocumentCacheInventoryStatus.Invalid);
    }

    [Test]
    public async Task It_rejects_a_partial_work_paging_index_with_the_expected_name_and_column_order()
    {
        await using PostgresqlGeneratedDdlTestDatabase database = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            database,
            """
            DROP INDEX "dms"."IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId";

            CREATE INDEX "IX_DocumentProjectionWork_FirstEnqueuedAt_DocumentId"
            ON "dms"."DocumentProjectionWork" ("FirstEnqueuedAt", "DocumentId")
            WHERE "DocumentId" > 0;
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
        await using PostgresqlGeneratedDdlTestDatabase invalidDatabase = await CreateDatabaseAsync();
        await using PostgresqlGeneratedDdlTestDatabase validDatabase = await CreateDatabaseAsync();
        await ExecuteNonQueryAsync(
            invalidDatabase,
            """
            DROP TABLE "dms"."DocumentCache" CASCADE;
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

    private static IEnumerable<TestCaseData> InvalidPostgresqlEnqueueTriggerShapes()
    {
        yield return new TestCaseData(
            """
            DROP TRIGGER "TR_Document_EnqueueProjectionInsert" ON "dms"."Document";

            CREATE TRIGGER "TR_Document_EnqueueProjectionInsert"
            AFTER UPDATE ON "dms"."Document"
            REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
            FOR EACH STATEMENT
            EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"();
            """
        ).SetName("It_rejects_insert_enqueue_trigger_with_wrong_event");

        yield return new TestCaseData(
            """
            DROP TRIGGER "TR_Document_EnqueueProjectionInsert" ON "dms"."Document";

            CREATE TRIGGER "TR_Document_EnqueueProjectionInsert"
            BEFORE INSERT ON "dms"."Document"
            FOR EACH STATEMENT
            EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"();
            """
        ).SetName("It_rejects_insert_enqueue_trigger_with_wrong_timing");

        yield return new TestCaseData(
            """
            DROP TRIGGER "TR_Document_EnqueueProjectionInsert" ON "dms"."Document";

            CREATE TRIGGER "TR_Document_EnqueueProjectionInsert"
            AFTER INSERT ON "dms"."Document"
            FOR EACH ROW
            EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"();
            """
        ).SetName("It_rejects_insert_enqueue_trigger_with_row_level_shape");

        yield return new TestCaseData(
            """
            DROP TRIGGER "TR_Document_EnqueueProjectionInsert" ON "dms"."Document";

            CREATE TRIGGER "TR_Document_EnqueueProjectionInsert"
            AFTER INSERT ON "dms"."Document"
            FOR EACH STATEMENT
            EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"();
            """
        ).SetName("It_rejects_insert_enqueue_trigger_without_transition_table");

        yield return new TestCaseData(
            """
            DROP TRIGGER "TR_Document_EnqueueProjectionInsert" ON "dms"."Document";

            CREATE TRIGGER "TR_Document_EnqueueProjectionInsert"
            AFTER INSERT ON "dms"."Document"
            REFERENCING NEW TABLE AS inserted_rows
            FOR EACH STATEMENT
            EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"();
            """
        ).SetName("It_rejects_insert_enqueue_trigger_with_wrong_transition_alias");

        yield return new TestCaseData(
            """
            DROP TRIGGER "TR_Document_EnqueueProjectionUpdate" ON "dms"."Document";

            CREATE TRIGGER "TR_Document_EnqueueProjectionUpdate"
            AFTER UPDATE ON "dms"."Document"
            REFERENCING NEW TABLE AS new_rows
            FOR EACH STATEMENT
            EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"();
            """
        ).SetName("It_rejects_update_enqueue_trigger_without_old_transition_table");

        yield return new TestCaseData(
            """
            DROP TRIGGER "TR_Document_EnqueueProjectionUpdate" ON "dms"."Document";

            CREATE TRIGGER "TR_Document_EnqueueProjectionUpdate"
            AFTER UPDATE ON "dms"."Document"
            REFERENCING OLD TABLE AS previous_rows NEW TABLE AS changed_rows
            FOR EACH STATEMENT
            EXECUTE FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"();
            """
        ).SetName("It_rejects_update_enqueue_trigger_with_wrong_transition_aliases");
    }

    private static IEnumerable<TestCaseData> PermissivePostgresqlCriticalCheckConstraintMutations()
    {
        yield return new TestCaseData(
            """
            ALTER TABLE "dms"."DocumentCache"
            DROP CONSTRAINT "CK_DocumentCache_JsonObject";

            ALTER TABLE "dms"."DocumentCache"
            ADD CONSTRAINT "CK_DocumentCache_JsonObject"
            CHECK (jsonb_typeof("DocumentJson") IN ('object', 'array'));
            """
        ).SetName("It_rejects_postgresql_documentcache_json_object_check_that_allows_arrays");

        yield return new TestCaseData(
            """
            ALTER TABLE "dms"."DataStoreIdentity"
            DROP CONSTRAINT "CK_DataStoreIdentity_Singleton";

            ALTER TABLE "dms"."DataStoreIdentity"
            ADD CONSTRAINT "CK_DataStoreIdentity_Singleton"
            CHECK ("DataStoreIdentitySingletonId" IN (1, 2));
            """
        ).SetName("It_rejects_postgresql_datastoreidentity_singleton_check_that_allows_id_2");

        yield return new TestCaseData(
            """
            ALTER TABLE "dms"."DocumentCacheState"
            DROP CONSTRAINT "CK_DocumentCacheState_Singleton";

            ALTER TABLE "dms"."DocumentCacheState"
            ADD CONSTRAINT "CK_DocumentCacheState_Singleton"
            CHECK ("StateId" IN (1, 2));
            """
        ).SetName("It_rejects_postgresql_documentcachestate_singleton_check_that_allows_id_2");

        yield return new TestCaseData(
            """
            ALTER TABLE "dms"."DocumentCacheState"
            DROP CONSTRAINT "CK_DocumentCacheState_Lifecycle";

            ALTER TABLE "dms"."DocumentCacheState"
            ADD CONSTRAINT "CK_DocumentCacheState_Lifecycle"
            CHECK ("ProjectionLifecycleState" IN ('Disabled', 'Resetting', 'Rebuilding', 'Tracking', 'Paused'));
            """
        ).SetName("It_rejects_postgresql_lifecycle_check_that_allows_extra_state");
    }

    private Task<PostgresqlGeneratedDdlTestDatabase> CreateDatabaseAsync() =>
        PostgresqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);

    private static async Task ExecuteNonQueryAsync(PostgresqlGeneratedDdlTestDatabase database, string sql)
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
