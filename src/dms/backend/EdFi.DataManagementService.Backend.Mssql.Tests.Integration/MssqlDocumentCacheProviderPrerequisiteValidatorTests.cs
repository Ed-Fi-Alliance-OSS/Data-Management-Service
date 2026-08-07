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
[Category("DocumentCachePrerequisite")]
[Category(MssqlCiShards.Shard4)]
public class Given_A_Mssql_DocumentCachePrerequisite_Validator
{
    private MssqlDocumentCacheProviderPrerequisiteValidator _validator = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _validator = new MssqlDocumentCacheProviderPrerequisiteValidator(
            NullLogger<MssqlDocumentCacheProviderPrerequisiteValidator>.Instance
        );
    }

    [Test]
    public void It_reports_the_sqlserver_provider_token()
    {
        _validator.ProviderToken.Should().Be(RelationalProviderToken.SqlServer);
    }

    [Test]
    public async Task It_reads_database_and_server_prerequisites()
    {
        await using MssqlGeneratedDdlTestDatabase database =
            await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await SetReadCommittedSnapshotAsync(database.DatabaseName, enabled: true);

        DocumentCacheProviderPrerequisiteValidationResult result =
            await _validator.ValidateInitializationAsync(database.ConnectionString, DisabledLifecycle());

        result
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Satisfied);
        result
            .SqlServerPrerequisites.NestedTriggers.Status.Should()
            .Be(await ExpectedNestedTriggersStatusAsync());

        if (
            result.SqlServerPrerequisites.NestedTriggers.Status
            == DocumentCacheProviderPrerequisiteStatus.Satisfied
        )
        {
            result.IsSatisfied.Should().BeTrue();
            result.FailureCategory.Should().BeNull();
        }
    }

    [Test]
    public async Task It_classifies_SqlServerDocumentCachePrerequisite_disabled_rcsi_with_disabled_lifecycle_as_provider_prerequisite_failed()
    {
        await using MssqlGeneratedDdlTestDatabase database =
            await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await SetReadCommittedSnapshotAsync(database.DatabaseName, enabled: false);

        DocumentCacheProviderPrerequisiteValidationResult result =
            await _validator.ValidateInitializationAsync(database.ConnectionString, DisabledLifecycle());

        result
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Disabled);
        result.IsSatisfied.Should().BeFalse();
        result.FailureCategory.Should().Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
        result.Message.Should().NotContain(database.DatabaseName);
        result.Message.Should().NotContain(database.ConnectionString);
        (await ReadCommittedSnapshotEnabledAsync(database.DatabaseName)).Should().BeFalse();
    }

    [Test]
    public async Task It_retries_SqlServerDocumentCachePrerequisite_initialization_after_disabled_lifecycle_correction()
    {
        await using MssqlGeneratedDdlTestDatabase database =
            await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await SetReadCommittedSnapshotAsync(database.DatabaseName, enabled: false);

        DocumentCacheProviderPrerequisiteValidationResult initialResult =
            await _validator.ValidateInitializationAsync(database.ConnectionString, DisabledLifecycle());

        await SetReadCommittedSnapshotAsync(database.DatabaseName, enabled: true);

        DocumentCacheProviderPrerequisiteValidationResult retryResult =
            await _validator.ValidateInitializationAsync(database.ConnectionString, DisabledLifecycle());

        initialResult
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Disabled);
        retryResult
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Satisfied);

        if (
            retryResult.SqlServerPrerequisites.NestedTriggers.Status
            == DocumentCacheProviderPrerequisiteStatus.Satisfied
        )
        {
            retryResult.IsSatisfied.Should().BeTrue();
            retryResult.FailureCategory.Should().BeNull();
        }
    }

    [Test]
    public async Task It_classifies_SqlServerDocumentCachePrerequisite_disabled_rcsi_with_tracking_lifecycle_as_unsupported_incident()
    {
        await using MssqlGeneratedDdlTestDatabase database =
            await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await SetReadCommittedSnapshotAsync(database.DatabaseName, enabled: false);

        DocumentCacheProviderPrerequisiteValidationResult result =
            await _validator.ValidateInitializationAsync(
                database.ConnectionString,
                new DocumentCacheLifecycleObservation(
                    DocumentCacheLifecycleState.Tracking,
                    CacheAheadRecoveryRequired: false
                )
            );

        result
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Disabled);
        result
            .FailureCategory.Should()
            .Be(DocumentCacheTargetDiagnosticCategory.UnsupportedPrerequisiteIncident);
    }

    [Test]
    public async Task It_rereads_SqlServerDocumentCachePrerequisite_for_activation_preflight()
    {
        await using MssqlGeneratedDdlTestDatabase database =
            await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await SetReadCommittedSnapshotAsync(database.DatabaseName, enabled: true);

        DocumentCacheProviderPrerequisiteValidationResult initialResult =
            await _validator.ValidateInitializationAsync(database.ConnectionString, DisabledLifecycle());

        initialResult
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Satisfied);

        await SetReadCommittedSnapshotAsync(database.DatabaseName, enabled: false);

        DocumentCacheProviderPrerequisiteValidationResult preflightResult =
            await _validator.ValidateActivationPreflightAsync(database.ConnectionString);

        preflightResult
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Disabled);
        preflightResult
            .FailureCategory.Should()
            .Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
    }

    [Test]
    public async Task It_classifies_unreadable_prerequisites_with_sanitized_messages()
    {
        string missingDatabaseConnectionString = MssqlTestDatabaseHelper.BuildConnectionString(
            MssqlTestDatabaseHelper.GenerateUniqueDatabaseName()
        );

        DocumentCacheProviderPrerequisiteValidationResult result =
            await _validator.ValidateInitializationAsync(
                missingDatabaseConnectionString,
                DisabledLifecycle()
            );

        result
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Unreadable);
        result
            .SqlServerPrerequisites.NestedTriggers.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Unreadable);
        result.FailureCategory.Should().Be(DocumentCacheTargetDiagnosticCategory.ProviderPrerequisiteFailed);
        result.Message.Should().NotContain(missingDatabaseConnectionString);
    }

    [Test]
    public async Task It_keeps_prerequisite_failures_scoped_to_the_validated_target()
    {
        await using MssqlGeneratedDdlTestDatabase invalidDatabase =
            await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await using MssqlGeneratedDdlTestDatabase validDatabase =
            await MssqlGeneratedDdlTestDatabase.CreateEmptyAsync();
        await SetReadCommittedSnapshotAsync(invalidDatabase.DatabaseName, enabled: false);
        await SetReadCommittedSnapshotAsync(validDatabase.DatabaseName, enabled: true);

        DocumentCacheProviderPrerequisiteValidationResult invalidResult =
            await _validator.ValidateInitializationAsync(
                invalidDatabase.ConnectionString,
                DisabledLifecycle()
            );
        DocumentCacheProviderPrerequisiteValidationResult validResult =
            await _validator.ValidateInitializationAsync(validDatabase.ConnectionString, DisabledLifecycle());

        invalidResult
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Disabled);
        validResult
            .SqlServerPrerequisites.ReadCommittedSnapshot.Status.Should()
            .Be(DocumentCacheProviderPrerequisiteStatus.Satisfied);
    }

    private static DocumentCacheLifecycleObservation DisabledLifecycle() =>
        new(DocumentCacheLifecycleState.Disabled, CacheAheadRecoveryRequired: false);

    private static async Task<DocumentCacheProviderPrerequisiteStatus> ExpectedNestedTriggersStatusAsync()
    {
        bool enabled = await ReadAdminBitAsync(
            """
            SELECT CONVERT(int, [value_in_use])
            FROM [sys].[configurations]
            WHERE [name] = N'nested triggers';
            """
        );

        return enabled
            ? DocumentCacheProviderPrerequisiteStatus.Satisfied
            : DocumentCacheProviderPrerequisiteStatus.Disabled;
    }

    private static async Task SetReadCommittedSnapshotAsync(string databaseName, bool enabled)
    {
        SqlConnection.ClearAllPools();

        string quotedDatabaseName = MssqlTestDatabaseHelper.QuoteIdentifier(databaseName);
        string enabledSql = enabled ? "ON" : "OFF";

        await MssqlTestDatabaseHelper.ExecuteAdminNonQueryAsync(
            $"""
            ALTER DATABASE {quotedDatabaseName}
            SET READ_COMMITTED_SNAPSHOT {enabledSql} WITH ROLLBACK IMMEDIATE;
            """
        );

        SqlConnection.ClearAllPools();
    }

    private static Task<bool> ReadCommittedSnapshotEnabledAsync(string databaseName) =>
        ReadAdminBitAsync(
            $"""
            SELECT CONVERT(int, [is_read_committed_snapshot_on])
            FROM [sys].[databases]
            WHERE [name] = N'{MssqlTestDatabaseHelper.EscapeSqlLiteral(databaseName)}';
            """
        );

    private static async Task<bool> ReadAdminBitAsync(string sql)
    {
        await using SqlConnection connection = new(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync();
        return value is not null && value != DBNull.Value && Convert.ToInt32(value) == 1;
    }
}
