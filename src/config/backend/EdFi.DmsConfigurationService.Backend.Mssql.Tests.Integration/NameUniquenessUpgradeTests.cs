// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0034 to script 0035, which adds
/// the data store and API client name constraints. Each fixture seeds legacy rows directly with SQL,
/// so no repository rule can filter them, before running the upgrade.
/// </summary>
public abstract class NameUniquenessUpgradeFixture
{
    protected const string BlockedPrefix = "Name uniqueness upgrade blocked: ";
    protected const string Remediation =
        "Rename a duplicate with PUT /v3/dataStores/{id} or PUT /v3/apiClients/{id}, or remove it with DELETE, then retry the upgrade.";
    protected const string DataStoreConstraint = "UX_DataStore_TenantId_Name";
    protected const string ApiClientConstraint = "UX_ApiClient_ApplicationId_Name";

    private JobUpgradeTestDatabase _database = null!;

    protected sealed record DataStoreRow(int Id, long? TenantId, string Name);

    protected sealed record ApiClientRow(int Id, int ApplicationId, string Name);

    protected DatabaseDeployResult UpgradeResult { get; private set; } = null!;
    protected string[] JournalBeforeUpgrade { get; private set; } = [];
    protected string[] JournalAfterUpgrade { get; private set; } = [];
    protected DataStoreRow[] DataStoresBeforeUpgrade { get; private set; } = [];
    protected DataStoreRow[] DataStoresAfterUpgrade { get; private set; } = [];
    protected ApiClientRow[] ApiClientsBeforeUpgrade { get; private set; } = [];
    protected ApiClientRow[] ApiClientsAfterUpgrade { get; private set; } = [];
    protected string[] DataStoreObjectsAfterUpgrade { get; private set; } = [];
    protected string[] ApiClientObjectsAfterUpgrade { get; private set; } = [];

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new JobUpgradeTestDatabase();

        _database.DeployThrough(JobUpgradeTestDatabase.JobScript);
        JournalBeforeUpgrade = await _database.JournalAsync();

        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await SeedAsync(connection);
        DataStoresBeforeUpgrade = await DataStoresAsync(connection);
        ApiClientsBeforeUpgrade = await ApiClientsAsync(connection);

        UpgradeResult = _database.Deploy(JobUpgradeTestDatabase.NameUniquenessScript);

        JournalAfterUpgrade = await _database.JournalAsync();
        DataStoresAfterUpgrade = await DataStoresAsync(connection);
        ApiClientsAfterUpgrade = await ApiClientsAsync(connection);
        DataStoreObjectsAfterUpgrade = await _database.ConstraintAndIndexNamesAsync("DataStore");
        ApiClientObjectsAfterUpgrade = await _database.ConstraintAndIndexNamesAsync("ApiClient");

        await AfterUpgradeAsync(connection);
    }

    [OneTimeTearDown]
    public Task OneTimeTeardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    protected abstract Task SeedAsync(SqlConnection connection);

    protected virtual Task AfterUpgradeAsync(SqlConnection connection) => Task.CompletedTask;

    protected void Redeploy() => _database.DeployThrough(JobUpgradeTestDatabase.NameUniquenessScript);

    protected Task<string[]> JournalAsync() => _database.JournalAsync();

    protected string UpgradeFailureMessage()
    {
        DatabaseDeployResult.DatabaseDeployFailure failure = UpgradeResult
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;

        for (
            Exception? candidate = failure.Error;
            candidate is not null;
            candidate = candidate.InnerException
        )
        {
            if (candidate is SqlException sqlException)
            {
                return sqlException.Message;
            }
        }

        throw new AssertionException($"Expected a SqlException but found: {failure.Error}");
    }

    protected static async Task<long> InsertTenantAsync(SqlConnection connection) =>
        await connection.ExecuteScalarAsync<long>(
            "INSERT INTO dmscs.Tenant (Name) OUTPUT INSERTED.Id VALUES (@Name);",
            new { Name = $"Tenant {Guid.NewGuid():N}" }
        );

    protected static async Task<int> InsertDataStoreAsync(
        SqlConnection connection,
        long? tenantId,
        string name
    ) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStore (DataStoreType, Name, TenantId)
            OUTPUT INSERTED.Id
            VALUES ('Production', @Name, @TenantId);
            """,
            new { Name = name, TenantId = tenantId }
        );

    protected static async Task<int> InsertApplicationAsync(SqlConnection connection)
    {
        int vendorId = await connection.ExecuteScalarAsync<int>(
            "INSERT INTO dmscs.Vendor (Company) OUTPUT INSERTED.Id VALUES (@Company);",
            new { Company = $"Vendor {Guid.NewGuid():N}" }
        );

        return await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.Application (ApplicationName, VendorId, ClaimSetName)
            OUTPUT INSERTED.Id
            VALUES (@Name, @VendorId, 'Legacy Claim Set');
            """,
            new { Name = $"Application {Guid.NewGuid():N}", VendorId = vendorId }
        );
    }

    protected static async Task<int> InsertApiClientAsync(
        SqlConnection connection,
        int applicationId,
        string name
    )
    {
        Guid clientUuid = Guid.NewGuid();
        return await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.ApiClient (ApplicationId, Name, ClientId, ClientUuid)
            OUTPUT INSERTED.Id
            VALUES (@ApplicationId, @Name, @ClientId, @ClientUuid);
            """,
            new
            {
                ApplicationId = applicationId,
                Name = name,
                ClientId = clientUuid.ToString(),
                ClientUuid = clientUuid,
            }
        );
    }

    private const int UniqueConstraintViolation = 2627;

    /// <summary>
    /// SQL Server names the violated constraint only inside the message, so the name is recovered from
    /// it to keep the assertions identical to the PostgreSQL fixtures.
    /// </summary>
    private static string ViolatedConstraint(SqlException exception) =>
        Array.Find(
            [DataStoreConstraint, ApiClientConstraint],
            name => exception.Message.Contains($"'{name}'", StringComparison.Ordinal)
        ) ?? exception.Message;

    /// <summary>The violated constraint's name, or null when the insert succeeds.</summary>
    protected static async Task<string?> TryInsertDataStoreAsync(
        SqlConnection connection,
        long? tenantId,
        string name
    )
    {
        try
        {
            await InsertDataStoreAsync(connection, tenantId, name);
            return null;
        }
        catch (SqlException exception) when (exception.Number == UniqueConstraintViolation)
        {
            return ViolatedConstraint(exception);
        }
    }

    /// <summary>The violated constraint's name, or null when the insert succeeds.</summary>
    protected static async Task<string?> TryInsertApiClientAsync(
        SqlConnection connection,
        int applicationId,
        string name
    )
    {
        try
        {
            await InsertApiClientAsync(connection, applicationId, name);
            return null;
        }
        catch (SqlException exception) when (exception.Number == UniqueConstraintViolation)
        {
            return ViolatedConstraint(exception);
        }
    }

    private static async Task<DataStoreRow[]> DataStoresAsync(SqlConnection connection) =>
        (
            await connection.QueryAsync<DataStoreRow>(
                "SELECT Id, TenantId, Name FROM dmscs.DataStore ORDER BY Id;"
            )
        ).ToArray();

    private static async Task<ApiClientRow[]> ApiClientsAsync(SqlConnection connection) =>
        (
            await connection.QueryAsync<ApiClientRow>(
                "SELECT Id, ApplicationId, Name FROM dmscs.ApiClient ORDER BY Id;"
            )
        ).ToArray();
}

/// <summary>
/// The outcome every blocked upgrade shares: nothing is journaled, no constraint is added, and no row
/// is renamed or removed.
/// </summary>
public abstract class BlockedNameUniquenessUpgradeFixture : NameUniquenessUpgradeFixture
{
    [Test]
    public void It_does_not_journal_the_name_uniqueness_script() =>
        JournalAfterUpgrade.Should().Equal(JournalBeforeUpgrade);

    [Test]
    public void It_adds_neither_constraint()
    {
        DataStoreObjectsAfterUpgrade.Should().NotContain(DataStoreConstraint);
        ApiClientObjectsAfterUpgrade.Should().NotContain(ApiClientConstraint);
    }

    [Test]
    public void It_changes_no_data_store_row() =>
        DataStoresAfterUpgrade.Should().Equal(DataStoresBeforeUpgrade);

    [Test]
    public void It_changes_no_api_client_row() =>
        ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_names_that_repeat_only_across_tenants_and_applications : NameUniquenessUpgradeFixture
{
    private long _firstTenantId;
    private int _firstApplicationId;
    private int _otherApplicationId;
    private string[] _journalAfterRepeatDeploy = [];
    private string? _sameTenantDataStoreViolation;
    private string? _noTenantDataStoreViolation;
    private string? _sameApplicationApiClientViolation;
    private string? _otherApplicationApiClientViolation;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        _firstTenantId = await InsertTenantAsync(connection);
        long secondTenantId = await InsertTenantAsync(connection);

        await InsertDataStoreAsync(connection, _firstTenantId, "Shared Data Store");
        await InsertDataStoreAsync(connection, secondTenantId, "Shared Data Store");
        await InsertDataStoreAsync(connection, null, "Shared Data Store");
        await InsertDataStoreAsync(connection, _firstTenantId, "Other Data Store");

        _firstApplicationId = await InsertApplicationAsync(connection);
        int secondApplicationId = await InsertApplicationAsync(connection);
        _otherApplicationId = await InsertApplicationAsync(connection);

        await InsertApiClientAsync(connection, _firstApplicationId, "Shared Client");
        await InsertApiClientAsync(connection, secondApplicationId, "Shared Client");
    }

    protected override async Task AfterUpgradeAsync(SqlConnection connection)
    {
        Redeploy();
        _journalAfterRepeatDeploy = await JournalAsync();

        _sameTenantDataStoreViolation = await TryInsertDataStoreAsync(
            connection,
            _firstTenantId,
            "Shared Data Store"
        );
        _noTenantDataStoreViolation = await TryInsertDataStoreAsync(connection, null, "Shared Data Store");
        _sameApplicationApiClientViolation = await TryInsertApiClientAsync(
            connection,
            _firstApplicationId,
            "Shared Client"
        );
        _otherApplicationApiClientViolation = await TryInsertApiClientAsync(
            connection,
            _otherApplicationId,
            "Shared Client"
        );
    }

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_journals_exactly_the_name_uniqueness_script() =>
        JournalAfterUpgrade
            .Except(JournalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.NameUniquenessScriptName);

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(JournalAfterUpgrade);

    [Test]
    public void It_keeps_every_seeded_row()
    {
        DataStoresAfterUpgrade.Should().Equal(DataStoresBeforeUpgrade);
        ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);
    }

    [Test]
    public void It_rejects_a_repeat_data_store_name_within_a_tenant() =>
        _sameTenantDataStoreViolation.Should().Be(DataStoreConstraint);

    [Test]
    public void It_rejects_a_repeat_data_store_name_without_a_tenant() =>
        _noTenantDataStoreViolation.Should().Be(DataStoreConstraint);

    [Test]
    public void It_rejects_a_repeat_api_client_name_within_an_application() =>
        _sameApplicationApiClientViolation.Should().Be(ApiClientConstraint);

    [Test]
    public void It_accepts_an_existing_api_client_name_under_another_application() =>
        _otherApplicationApiClientViolation.Should().BeNull();
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_duplicate_data_store_names_within_a_tenant : BlockedNameUniquenessUpgradeFixture
{
    private int _firstDuplicateId;
    private int _secondDuplicateId;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        long tenantId = await InsertTenantAsync(connection);
        long otherTenantId = await InsertTenantAsync(connection);

        _firstDuplicateId = await InsertDataStoreAsync(connection, tenantId, "Duplicate Data Store");
        await InsertDataStoreAsync(connection, tenantId, "Unique Data Store");
        await InsertDataStoreAsync(connection, otherTenantId, "Duplicate Data Store");
        _secondDuplicateId = await InsertDataStoreAsync(connection, tenantId, "Duplicate Data Store");
    }

    [Test]
    public void It_blocks_the_upgrade_listing_only_the_same_tenant_duplicates() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 DataStore row(s) share a (TenantId, Name) with another row, ids: {_firstDuplicateId}, {_secondDuplicateId}. "
                    + Remediation
            );
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_duplicate_data_store_names_without_a_tenant : BlockedNameUniquenessUpgradeFixture
{
    private int _firstDuplicateId;
    private int _secondDuplicateId;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        long tenantId = await InsertTenantAsync(connection);

        _firstDuplicateId = await InsertDataStoreAsync(connection, null, "Duplicate Data Store");
        await InsertDataStoreAsync(connection, tenantId, "Duplicate Data Store");
        _secondDuplicateId = await InsertDataStoreAsync(connection, null, "Duplicate Data Store");
    }

    [Test]
    public void It_blocks_the_upgrade_listing_both_unassigned_duplicates() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 DataStore row(s) share a (TenantId, Name) with another row, ids: {_firstDuplicateId}, {_secondDuplicateId}. "
                    + Remediation
            );
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_duplicate_api_client_names_within_an_application : BlockedNameUniquenessUpgradeFixture
{
    private int _firstDuplicateId;
    private int _secondDuplicateId;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        int applicationId = await InsertApplicationAsync(connection);
        int otherApplicationId = await InsertApplicationAsync(connection);

        _firstDuplicateId = await InsertApiClientAsync(connection, applicationId, "Duplicate Client");
        await InsertApiClientAsync(connection, otherApplicationId, "Duplicate Client");
        _secondDuplicateId = await InsertApiClientAsync(connection, applicationId, "Duplicate Client");
    }

    [Test]
    public void It_blocks_the_upgrade_listing_only_the_same_application_duplicates() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 ApiClient row(s) share an (ApplicationId, Name) with another row, ids: {_firstDuplicateId}, {_secondDuplicateId}. "
                    + Remediation
            );
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_duplicate_names_in_both_tables : BlockedNameUniquenessUpgradeFixture
{
    private int[] _dataStoreIds = [];
    private int[] _apiClientIds = [];

    protected override async Task SeedAsync(SqlConnection connection)
    {
        long tenantId = await InsertTenantAsync(connection);
        int applicationId = await InsertApplicationAsync(connection);

        _dataStoreIds =
        [
            await InsertDataStoreAsync(connection, tenantId, "Duplicate Data Store"),
            await InsertDataStoreAsync(connection, tenantId, "Duplicate Data Store"),
        ];
        _apiClientIds =
        [
            await InsertApiClientAsync(connection, applicationId, "Duplicate Client"),
            await InsertApiClientAsync(connection, applicationId, "Duplicate Client"),
        ];
    }

    [Test]
    public void It_blocks_the_upgrade_with_one_message_listing_both_tables() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 DataStore row(s) share a (TenantId, Name) with another row, ids: {string.Join(", ", _dataStoreIds)}. "
                    + $"2 ApiClient row(s) share an (ApplicationId, Name) with another row, ids: {string.Join(", ", _apiClientIds)}. "
                    + Remediation
            );
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_more_duplicate_rows_than_the_message_lists : BlockedNameUniquenessUpgradeFixture
{
    private readonly List<int> _apiClientIds = [];

    protected override async Task SeedAsync(SqlConnection connection)
    {
        int applicationId = await InsertApplicationAsync(connection);

        for (int row = 0; row < 23; row++)
        {
            _apiClientIds.Add(await InsertApiClientAsync(connection, applicationId, "Duplicate Client"));
        }
    }

    [Test]
    public void It_lists_the_first_twenty_ids_and_counts_the_rest() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"23 ApiClient row(s) share an (ApplicationId, Name) with another row, ids: {string.Join(", ", _apiClientIds.Take(20))}, ... and 3 more. "
                    + Remediation
            );
}
