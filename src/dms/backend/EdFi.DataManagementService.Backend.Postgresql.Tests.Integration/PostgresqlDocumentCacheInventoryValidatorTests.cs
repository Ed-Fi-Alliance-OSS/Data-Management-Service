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
