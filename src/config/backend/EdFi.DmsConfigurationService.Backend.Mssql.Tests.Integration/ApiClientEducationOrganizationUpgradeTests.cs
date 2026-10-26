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
/// Script 0038 (DMS-1639) on an isolated database: the upgrade from 0037 with its backfill, and the
/// constraints a fresh install enforces on ApiClientEducationOrganization. Fixtures seed and change
/// rows with SQL, because no repository writes the table yet. Every concrete fixture carries
/// <see cref="Category"/> and the MssqlIntegration category so a step-level filter selects exactly this group.
/// </summary>
public abstract class ApiClientEducationOrganizationFixture
{
    public const string Category = "ApiClientEducationOrganizationUpgrade";

    protected const int ForeignKeyViolation = 547;

    protected sealed record ClientRow(int ApiClientId, int ApplicationId, long EducationOrganizationId);

    protected sealed record ClientState(
        int Id,
        bool InheritsApplicationEducationOrganizations,
        DateTime? IdentityProviderSyncPendingSince
    );

    private JobUpgradeTestDatabase? _database;

    private protected JobUpgradeTestDatabase Database =>
        _database ?? throw new InvalidOperationException("The SQL Server test database is not configured.");

    [OneTimeTearDown]
    public Task OneTimeTeardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    protected void RequireSqlServer()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new JobUpgradeTestDatabase();
    }

    protected static async Task<int> InsertVendorAsync(SqlConnection connection) =>
        await connection.ExecuteScalarAsync<int>(
            """INSERT INTO dmscs.Vendor (Company) OUTPUT INSERTED.Id VALUES (@Company);""",
            new { Company = $"Vendor {Guid.NewGuid():N}" }
        );

    protected static async Task<int> InsertApplicationAsync(
        SqlConnection connection,
        int vendorId,
        params long[] educationOrganizationIds
    )
    {
        int applicationId = await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.Application (ApplicationName, VendorId, ClaimSetName)
            OUTPUT INSERTED.Id
            VALUES (@Name, @VendorId, 'Test Claim Set');
            """,
            new { Name = $"Application {Guid.NewGuid():N}", VendorId = vendorId }
        );

        foreach (long educationOrganizationId in educationOrganizationIds)
        {
            await InsertApplicationEducationOrganizationAsync(
                connection,
                applicationId,
                educationOrganizationId
            );
        }

        return applicationId;
    }

    protected static Task InsertApplicationEducationOrganizationAsync(
        SqlConnection connection,
        int applicationId,
        long educationOrganizationId
    ) =>
        connection.ExecuteAsync(
            """
            INSERT INTO dmscs.ApplicationEducationOrganization (ApplicationId, EducationOrganizationId)
            VALUES (@ApplicationId, @EducationOrganizationId);
            """,
            new { ApplicationId = applicationId, EducationOrganizationId = educationOrganizationId }
        );

    protected static async Task<int> InsertApiClientAsync(SqlConnection connection, int applicationId) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.ApiClient (ApplicationId, Name, ClientId, ClientUuid)
            OUTPUT INSERTED.Id
            VALUES (@ApplicationId, @Name, @ClientId, @ClientUuid);
            """,
            new
            {
                ApplicationId = applicationId,
                Name = $"Client {Guid.NewGuid():N}",
                ClientId = Guid.NewGuid().ToString("N"),
                ClientUuid = Guid.NewGuid(),
            }
        );

    protected static Task InsertClientRowAsync(
        SqlConnection connection,
        int apiClientId,
        int applicationId,
        long educationOrganizationId,
        SqlTransaction? transaction = null
    ) =>
        connection.ExecuteAsync(
            """
            INSERT INTO dmscs.ApiClientEducationOrganization (ApiClientId, ApplicationId, EducationOrganizationId)
            VALUES (@ApiClientId, @ApplicationId, @EducationOrganizationId);
            """,
            new
            {
                ApiClientId = apiClientId,
                ApplicationId = applicationId,
                EducationOrganizationId = educationOrganizationId,
            },
            transaction
        );

    protected static async Task<ClientRow[]> ClientRowsAsync(SqlConnection connection) =>
        (
            await connection.QueryAsync<ClientRow>(
                """
                SELECT ApiClientId, ApplicationId, EducationOrganizationId
                FROM dmscs.ApiClientEducationOrganization
                ORDER BY ApiClientId, EducationOrganizationId;
                """
            )
        ).ToArray();

    protected static async Task<ClientRow[]> ClientRowsAsync(SqlConnection connection, int apiClientId) =>
        (await ClientRowsAsync(connection)).Where(row => row.ApiClientId == apiClientId).ToArray();

    protected static async Task<ClientState[]> ClientStatesAsync(SqlConnection connection) =>
        (
            await connection.QueryAsync<ClientState>(
                """
                SELECT Id, InheritsApplicationEducationOrganizations, IdentityProviderSyncPendingSince
                FROM dmscs.ApiClient
                ORDER BY Id;
                """
            )
        ).ToArray();

    protected static async Task<bool> ApiClientExistsAsync(SqlConnection connection, int apiClientId) =>
        await connection.ExecuteScalarAsync<bool>(
            """SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dmscs.ApiClient WHERE Id = @Id) THEN 1 ELSE 0 END AS bit);""",
            new { Id = apiClientId }
        );

    /// <summary>Runs a statement expected to violate a constraint and returns the violation, if any.</summary>
    protected static async Task<SqlException?> ViolationAsync(Func<Task> statement)
    {
        try
        {
            await statement();
            return null;
        }
        catch (SqlException exception)
        {
            return exception;
        }
    }
}

[TestFixture]
[Category(ApiClientEducationOrganizationFixture.Category)]
[Category("MssqlIntegration")]
public class Given_a_database_deployed_through_0037_with_api_clients : ApiClientEducationOrganizationFixture
{
    private DatabaseDeployResult _upgradeResult = null!;
    private DatabaseDeployResult _repeatDeployResult = null!;
    private string[] _journalBeforeUpgrade = [];
    private string[] _journalAfterUpgrade = [];
    private string[] _journalAfterRepeatDeploy = [];
    private ClientRow[] _expectedRows = [];
    private ClientRow[] _rowsAfterUpgrade = [];
    private ClientRow[] _rowsAfterRepeatDeploy = [];
    private ClientState[] _statesAfterUpgrade = [];
    private int _clientOfEmptyApplication;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        RequireSqlServer();
        Database.DeployThrough(JobUpgradeTestDatabase.NamespacePrefixWideningScript);
        _journalBeforeUpgrade = await Database.JournalAsync();

        await using SqlConnection connection = new(Database.ConnectionString);
        await connection.OpenAsync();

        int vendor = await InsertVendorAsync(connection);
        int twoEdOrgApplication = await InsertApplicationAsync(connection, vendor, 255901, 255902);
        int oneEdOrgApplication = await InsertApplicationAsync(connection, vendor, 255950);
        int emptyApplication = await InsertApplicationAsync(connection, vendor);

        int otherVendor = await InsertVendorAsync(connection);
        int sharedEdOrgApplication = await InsertApplicationAsync(connection, otherVendor, 255901);

        int firstOfTwo = await InsertApiClientAsync(connection, twoEdOrgApplication);
        int secondOfTwo = await InsertApiClientAsync(connection, twoEdOrgApplication);
        int clientOfOne = await InsertApiClientAsync(connection, oneEdOrgApplication);
        _clientOfEmptyApplication = await InsertApiClientAsync(connection, emptyApplication);
        int clientOfShared = await InsertApiClientAsync(connection, sharedEdOrgApplication);

        _expectedRows =
        [
            new(firstOfTwo, twoEdOrgApplication, 255901),
            new(firstOfTwo, twoEdOrgApplication, 255902),
            new(secondOfTwo, twoEdOrgApplication, 255901),
            new(secondOfTwo, twoEdOrgApplication, 255902),
            new(clientOfOne, oneEdOrgApplication, 255950),
            new(clientOfShared, sharedEdOrgApplication, 255901),
        ];

        _upgradeResult = Database.Deploy(JobUpgradeTestDatabase.ApiClientEducationOrganizationScript);
        _journalAfterUpgrade = await Database.JournalAsync();
        _rowsAfterUpgrade = await ClientRowsAsync(connection);
        _statesAfterUpgrade = await ClientStatesAsync(connection);

        _repeatDeployResult = Database.Deploy(JobUpgradeTestDatabase.ApiClientEducationOrganizationScript);
        _journalAfterRepeatDeploy = await Database.JournalAsync();
        _rowsAfterRepeatDeploy = await ClientRowsAsync(connection);
    }

    [Test]
    public void It_upgrades_successfully() =>
        _upgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_journals_exactly_the_client_education_organization_script() =>
        _journalAfterUpgrade
            .Except(_journalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.ApiClientEducationOrganizationScriptName);

    [Test]
    public void It_makes_every_existing_client_inherit()
    {
        _statesAfterUpgrade.Should().HaveCount(5);
        _statesAfterUpgrade.Should().OnlyContain(state => state.InheritsApplicationEducationOrganizations);
    }

    [Test]
    public void It_leaves_every_client_without_a_pending_sync() =>
        _statesAfterUpgrade.Should().OnlyContain(state => !state.IdentityProviderSyncPendingSince.HasValue);

    [Test]
    public void It_copies_each_client_rows_from_its_Application() =>
        _rowsAfterUpgrade.Should().Equal(_expectedRows);

    [Test]
    public void It_gives_a_client_of_an_Application_without_education_organizations_no_rows() =>
        _rowsAfterUpgrade.Should().NotContain(row => row.ApiClientId == _clientOfEmptyApplication);

    [Test]
    public void It_succeeds_on_a_repeat_deploy() =>
        _repeatDeployResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(_journalAfterUpgrade);

    [Test]
    public void It_leaves_the_client_rows_unchanged_on_a_repeat_deploy() =>
        _rowsAfterRepeatDeploy.Should().Equal(_rowsAfterUpgrade);
}

[TestFixture]
[Category(ApiClientEducationOrganizationFixture.Category)]
[Category("MssqlIntegration")]
public class Given_a_fresh_database_with_client_education_organizations
    : ApiClientEducationOrganizationFixture
{
    private string[] _journal = [];
    private ClientState _defaultState = null!;
    private ClientRow[] _scopedRowsBefore = [];
    private ClientRow[] _scopedRowsAfterAddition = [];
    private ClientRow[] _scopedRowsAfterRemoval = [];
    private int _scopedApplication;
    private int _inheritingClient;
    private int _narrowedClient;

    private int _sourceApplication;
    private int _destinationApplication;
    private int _mover;
    private SqlException? _crossApplicationInsert;
    private SqlException? _educationOrganizationNotOnApplicationInsert;
    private SqlException? _moveKeepingRows;
    private SqlException? _deleteKeepingRows;
    private ClientRow[] _moverRowsAfterRejections = [];
    private ClientRow[] _moverRowsAfterMove = [];

    private SqlException? _applicationDelete;
    private int _clientOfDeletedApplication;
    private ClientRow[] _rowsOfDeletedApplicationClient = [];
    private bool _clientOfDeletedApplicationExists;

    private SqlException? _vendorDelete;
    private int _clientOfDeletedVendor;
    private ClientRow[] _rowsOfDeletedVendorClient = [];

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        RequireSqlServer();
        Database.DeployThrough(JobUpgradeTestDatabase.ApiClientEducationOrganizationScript);
        _journal = await Database.JournalAsync();

        await using SqlConnection connection = new(Database.ConnectionString);
        await connection.OpenAsync();

        int vendor = await InsertVendorAsync(connection);

        // An Application education organization removal cascades; an addition changes no client.
        _scopedApplication = await InsertApplicationAsync(connection, vendor, 1001, 1002);
        _inheritingClient = await InsertApiClientAsync(connection, _scopedApplication);
        _narrowedClient = await InsertApiClientAsync(connection, _scopedApplication);
        _defaultState = (await ClientStatesAsync(connection)).Single(state => state.Id == _inheritingClient);

        await connection.ExecuteAsync(
            """UPDATE dmscs.ApiClient SET InheritsApplicationEducationOrganizations = 0 WHERE Id = @Id;""",
            new { Id = _narrowedClient }
        );
        await InsertClientRowAsync(connection, _inheritingClient, _scopedApplication, 1001);
        await InsertClientRowAsync(connection, _inheritingClient, _scopedApplication, 1002);
        await InsertClientRowAsync(connection, _narrowedClient, _scopedApplication, 1001);
        _scopedRowsBefore = await ClientRowsAsync(connection);

        await InsertApplicationEducationOrganizationAsync(connection, _scopedApplication, 1003);
        _scopedRowsAfterAddition = await ClientRowsAsync(connection);

        await connection.ExecuteAsync(
            """
            DELETE FROM dmscs.ApplicationEducationOrganization
            WHERE ApplicationId = @ApplicationId AND EducationOrganizationId = 1001;
            """,
            new { ApplicationId = _scopedApplication }
        );
        _scopedRowsAfterRemoval = await ClientRowsAsync(connection);

        // A client's rows stay tied to its current Application.
        _sourceApplication = await InsertApplicationAsync(connection, vendor, 2001);
        _destinationApplication = await InsertApplicationAsync(connection, vendor, 2001, 2002);
        _mover = await InsertApiClientAsync(connection, _sourceApplication);
        await InsertClientRowAsync(connection, _mover, _sourceApplication, 2001);

        _crossApplicationInsert = await ViolationAsync(() =>
            InsertClientRowAsync(connection, _mover, _destinationApplication, 2002)
        );
        _educationOrganizationNotOnApplicationInsert = await ViolationAsync(() =>
            InsertClientRowAsync(connection, _mover, _sourceApplication, 2002)
        );
        _moveKeepingRows = await ViolationAsync(() =>
            connection.ExecuteAsync(
                """UPDATE dmscs.ApiClient SET ApplicationId = @ApplicationId WHERE Id = @Id;""",
                new { ApplicationId = _destinationApplication, Id = _mover }
            )
        );
        _deleteKeepingRows = await ViolationAsync(() =>
            connection.ExecuteAsync("""DELETE FROM dmscs.ApiClient WHERE Id = @Id;""", new { Id = _mover })
        );
        _moverRowsAfterRejections = await ClientRowsAsync(connection, _mover);

        await using (SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
                """DELETE FROM dmscs.ApiClientEducationOrganization WHERE ApiClientId = @Id;""",
                new { Id = _mover },
                transaction
            );
            await connection.ExecuteAsync(
                """UPDATE dmscs.ApiClient SET ApplicationId = @ApplicationId WHERE Id = @Id;""",
                new { ApplicationId = _destinationApplication, Id = _mover },
                transaction
            );
            await InsertClientRowAsync(connection, _mover, _destinationApplication, 2001, transaction);
            await transaction.CommitAsync();
        }
        _moverRowsAfterMove = await ClientRowsAsync(connection, _mover);

        // Deleting an Application, directly or through its Vendor, removes its clients' rows.
        int deletedApplication = await InsertApplicationAsync(connection, vendor, 3001);
        _clientOfDeletedApplication = await InsertApiClientAsync(connection, deletedApplication);
        await InsertClientRowAsync(connection, _clientOfDeletedApplication, deletedApplication, 3001);
        _applicationDelete = await ViolationAsync(() =>
            connection.ExecuteAsync(
                """DELETE FROM dmscs.Application WHERE Id = @Id;""",
                new { Id = deletedApplication }
            )
        );
        _rowsOfDeletedApplicationClient = await ClientRowsAsync(connection, _clientOfDeletedApplication);
        _clientOfDeletedApplicationExists = await ApiClientExistsAsync(
            connection,
            _clientOfDeletedApplication
        );

        int deletedVendor = await InsertVendorAsync(connection);
        int applicationOfDeletedVendor = await InsertApplicationAsync(connection, deletedVendor, 4001);
        _clientOfDeletedVendor = await InsertApiClientAsync(connection, applicationOfDeletedVendor);
        await InsertClientRowAsync(connection, _clientOfDeletedVendor, applicationOfDeletedVendor, 4001);
        _vendorDelete = await ViolationAsync(() =>
            connection.ExecuteAsync(
                """DELETE FROM dmscs.Vendor WHERE Id = @Id;""",
                new { Id = deletedVendor }
            )
        );
        _rowsOfDeletedVendorClient = await ClientRowsAsync(connection, _clientOfDeletedVendor);
    }

    [Test]
    public void It_journals_the_client_education_organization_script() =>
        _journal.Should().EndWith(JobUpgradeTestDatabase.ApiClientEducationOrganizationScriptName);

    [Test]
    public void It_makes_a_new_client_inherit_by_default() =>
        _defaultState.InheritsApplicationEducationOrganizations.Should().BeTrue();

    [Test]
    public void It_leaves_a_new_client_without_a_pending_sync() =>
        _defaultState.IdentityProviderSyncPendingSince.Should().BeNull();

    [Test]
    public void It_changes_no_client_rows_when_an_Application_gains_an_education_organization() =>
        _scopedRowsAfterAddition.Should().Equal(_scopedRowsBefore);

    [Test]
    public void It_removes_an_education_organization_removed_from_the_Application_from_every_client() =>
        _scopedRowsAfterRemoval.Should().Equal(new ClientRow(_inheritingClient, _scopedApplication, 1002));

    [Test]
    public void It_rejects_a_row_for_an_Application_other_than_the_client_current_one()
    {
        _crossApplicationInsert.Should().NotBeNull();
        _crossApplicationInsert!.Number.Should().Be(ForeignKeyViolation);
        _crossApplicationInsert.Message.Should().Contain("\"FK_ApiClientEducationOrganization_ApiClient\"");
    }

    [Test]
    public void It_rejects_a_row_for_an_education_organization_not_on_the_Application()
    {
        _educationOrganizationNotOnApplicationInsert.Should().NotBeNull();
        _educationOrganizationNotOnApplicationInsert!.Number.Should().Be(ForeignKeyViolation);
        _educationOrganizationNotOnApplicationInsert
            .Message.Should()
            .Contain("\"FK_ApiClientEducationOrganization_ApplicationEducationOrg\"");
    }

    [Test]
    public void It_rejects_moving_a_client_that_keeps_rows_for_its_old_Application()
    {
        _moveKeepingRows.Should().NotBeNull();
        _moveKeepingRows!.Number.Should().Be(ForeignKeyViolation);
        _moveKeepingRows.Message.Should().Contain("\"FK_ApiClientEducationOrganization_ApiClient\"");
    }

    [Test]
    public void It_rejects_deleting_a_client_that_keeps_rows()
    {
        _deleteKeepingRows.Should().NotBeNull();
        _deleteKeepingRows!.Number.Should().Be(ForeignKeyViolation);
        _deleteKeepingRows.Message.Should().Contain("\"FK_ApiClientEducationOrganization_ApiClient\"");
    }

    [Test]
    public void It_leaves_the_rows_of_a_client_unchanged_by_rejected_statements() =>
        _moverRowsAfterRejections.Should().Equal(new ClientRow(_mover, _sourceApplication, 2001));

    [Test]
    public void It_moves_a_client_whose_rows_are_deleted_first_and_reinserted_for_the_destination() =>
        _moverRowsAfterMove.Should().Equal(new ClientRow(_mover, _destinationApplication, 2001));

    [Test]
    public void It_deletes_an_Application_with_client_rows() => _applicationDelete.Should().BeNull();

    [Test]
    public void It_removes_the_client_rows_and_clients_of_a_deleted_Application()
    {
        _rowsOfDeletedApplicationClient.Should().BeEmpty();
        _clientOfDeletedApplicationExists.Should().BeFalse();
    }

    [Test]
    public void It_deletes_a_Vendor_whose_Application_has_client_rows() => _vendorDelete.Should().BeNull();

    [Test]
    public void It_removes_the_client_rows_of_a_deleted_Vendor() =>
        _rowsOfDeletedVendorClient.Should().BeEmpty();
}
