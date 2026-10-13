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
/// Upgrades an isolated database deployed and journaled through script 0035 to script 0036, which adds
/// the ApiClient ClientId and ClientUuid constraints. Each fixture seeds legacy rows directly with SQL,
/// so no repository rule can filter them, before running the upgrade. Every concrete fixture carries
/// <see cref="Category"/> and the MssqlIntegration category so a step-level filter selects exactly
/// this group.
/// </summary>
public abstract class ApiClientIdentifierUniquenessUpgradeFixture
{
    public const string Category = "ApiClientIdentifierUniquenessUpgrade";

    protected const string BlockedPrefix = "API client identifier uniqueness upgrade blocked: ";
    protected const string Remediation =
        "Resolve the duplicates with the operator procedure in docs/OPERATIONS.md (\"Configuration Service database upgrades\"), then retry the upgrade. Do not use DELETE /v3/apiClients/{id} for these rows.";
    protected const string ClientIdConstraint = "UX_ApiClient_ClientId";
    protected const string ClientUuidConstraint = "UX_ApiClient_ClientUuid";

    /// <summary>SQL Server error for an index or statistics name that already exists on the table.</summary>
    protected const int DuplicateIndexName = 1913;

    private const int UniqueConstraintViolation = 2627;

    private JobUpgradeTestDatabase _database = null!;

    protected sealed record ApiClientRow(
        int Id,
        int ApplicationId,
        string ClientId,
        Guid ClientUuid,
        string Name
    );

    protected DatabaseDeployResult UpgradeResult { get; private set; } = null!;
    protected string[] JournalBeforeUpgrade { get; private set; } = [];
    protected string[] JournalAfterUpgrade { get; private set; } = [];
    protected ApiClientRow[] ApiClientsBeforeUpgrade { get; private set; } = [];
    protected ApiClientRow[] ApiClientsAfterUpgrade { get; private set; } = [];
    protected string[] ApiClientConstraintsAfterUpgrade { get; private set; } = [];

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new JobUpgradeTestDatabase();

        _database.DeployThrough(JobUpgradeTestDatabase.NameUniquenessScript);
        JournalBeforeUpgrade = await _database.JournalAsync();

        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await SeedAsync(connection);
        ApiClientsBeforeUpgrade = await ApiClientsAsync(connection);

        UpgradeResult = _database.Deploy(JobUpgradeTestDatabase.IdentifierUniquenessScript);

        JournalAfterUpgrade = await _database.JournalAsync();
        ApiClientsAfterUpgrade = await ApiClientsAsync(connection);
        ApiClientConstraintsAfterUpgrade = await ConstraintNamesAsync(connection);

        await AfterUpgradeAsync(connection);
    }

    [OneTimeTearDown]
    public Task OneTimeTeardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    protected abstract Task SeedAsync(SqlConnection connection);

    protected virtual Task AfterUpgradeAsync(SqlConnection connection) => Task.CompletedTask;

    protected DatabaseDeployResult Redeploy() =>
        _database.Deploy(JobUpgradeTestDatabase.IdentifierUniquenessScript);

    protected Task<string[]> JournalAsync() => _database.JournalAsync();

    protected static SqlException UpgradeFailure(DatabaseDeployResult result)
    {
        DatabaseDeployResult.DatabaseDeployFailure failure = result
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
                return sqlException;
            }
        }

        throw new AssertionException($"Expected a SqlException but found: {failure.Error}");
    }

    protected string UpgradeFailureMessage() => UpgradeFailure(UpgradeResult).Message;

    protected static async Task<long> InsertTenantAsync(SqlConnection connection) =>
        await connection.ExecuteScalarAsync<long>(
            "INSERT INTO dmscs.Tenant (Name) OUTPUT INSERTED.Id VALUES (@Name);",
            new { Name = $"Tenant {Guid.NewGuid():N}" }
        );

    /// <summary>An application under a new vendor of the given tenant, or of no tenant.</summary>
    protected static async Task<int> InsertApplicationAsync(SqlConnection connection, long? tenantId)
    {
        int vendorId = await connection.ExecuteScalarAsync<int>(
            "INSERT INTO dmscs.Vendor (Company, TenantId) OUTPUT INSERTED.Id VALUES (@Company, @TenantId);",
            new { Company = $"Vendor {Guid.NewGuid():N}", TenantId = tenantId }
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

    protected static async Task<int> InsertDataStoreAsync(SqlConnection connection) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStore (DataStoreType, Name)
            OUTPUT INSERTED.Id
            VALUES ('Production', @Name);
            """,
            new { Name = $"Data Store {Guid.NewGuid():N}" }
        );

    protected static async Task<int> InsertApiClientAsync(
        SqlConnection connection,
        int applicationId,
        string clientId,
        Guid clientUuid
    ) =>
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
                ClientId = clientId,
                ClientUuid = clientUuid,
            }
        );

    protected static Task<int> InsertApiClientAsync(SqlConnection connection, int applicationId) =>
        InsertApiClientAsync(connection, applicationId, Guid.NewGuid().ToString(), Guid.NewGuid());

    /// <summary>
    /// SQL Server names the violated constraint only inside the message, so the name is recovered from
    /// it to keep the assertions identical to the PostgreSQL fixtures.
    /// </summary>
    private static string ViolatedConstraint(SqlException exception) =>
        Array.Find(
            [ClientIdConstraint, ClientUuidConstraint],
            name => exception.Message.Contains($"'{name}'", StringComparison.Ordinal)
        ) ?? exception.Message;

    /// <summary>The violated constraint's name, or null when the insert succeeds.</summary>
    protected static async Task<string?> TryInsertApiClientAsync(
        SqlConnection connection,
        int applicationId,
        string clientId,
        Guid clientUuid
    )
    {
        try
        {
            await InsertApiClientAsync(connection, applicationId, clientId, clientUuid);
            return null;
        }
        catch (SqlException exception) when (exception.Number == UniqueConstraintViolation)
        {
            return ViolatedConstraint(exception);
        }
    }

    /// <summary>
    /// The names of the key, foreign key and check constraints on ApiClient, excluding indexes that back
    /// no constraint.
    /// </summary>
    protected static async Task<string[]> ConstraintNamesAsync(SqlConnection connection) =>
        (
            await connection.QueryAsync<string>(
                """
                SELECT name FROM sys.objects
                WHERE parent_object_id = OBJECT_ID('dmscs.ApiClient') AND type IN ('PK', 'UQ', 'F', 'C')
                ORDER BY name;
                """
            )
        ).ToArray();

    /// <summary>The collation the ClientId column compares under.</summary>
    protected static async Task<string> ClientIdCollationAsync(SqlConnection connection) =>
        await connection.ExecuteScalarAsync<string>(
            """
            SELECT collation_name FROM sys.columns
            WHERE object_id = OBJECT_ID('dmscs.ApiClient') AND name = 'ClientId';
            """
        ) ?? string.Empty;

    private static async Task<ApiClientRow[]> ApiClientsAsync(SqlConnection connection) =>
        (
            await connection.QueryAsync<ApiClientRow>(
                "SELECT Id, ApplicationId, ClientId, ClientUuid, Name FROM dmscs.ApiClient ORDER BY Id;"
            )
        ).ToArray();

    protected static Task<ApiClientRow[]> ApiClientsNowAsync(SqlConnection connection) =>
        ApiClientsAsync(connection);
}

/// <summary>
/// The outcome every blocked upgrade shares: nothing is journaled, neither constraint is added, and no
/// row is changed or removed.
/// </summary>
public abstract class BlockedApiClientIdentifierUniquenessUpgradeFixture
    : ApiClientIdentifierUniquenessUpgradeFixture
{
    [Test]
    public void It_does_not_journal_the_identifier_uniqueness_script() =>
        JournalAfterUpgrade.Should().Equal(JournalBeforeUpgrade);

    [Test]
    public void It_adds_neither_constraint() =>
        ApiClientConstraintsAfterUpgrade.Should().NotContain([ClientIdConstraint, ClientUuidConstraint]);

    [Test]
    public void It_changes_no_api_client_row() =>
        ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_api_client_identifiers_that_are_unique_across_applications_and_tenants
    : ApiClientIdentifierUniquenessUpgradeFixture
{
    private readonly string _firstClientId = Guid.NewGuid().ToString();
    private readonly Guid _firstClientUuid = Guid.NewGuid();
    private int _otherTenantApplicationId;
    private int _noTenantApplicationId;
    private string[] _journalAfterRepeatDeploy = [];
    private string? _repeatClientIdViolation;
    private string? _repeatClientUuidViolation;
    private string? _repeatBothViolation;
    private string? _freshIdentifiersViolation;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        long firstTenantId = await InsertTenantAsync(connection);
        long otherTenantId = await InsertTenantAsync(connection);

        int firstApplicationId = await InsertApplicationAsync(connection, firstTenantId);
        _otherTenantApplicationId = await InsertApplicationAsync(connection, otherTenantId);
        _noTenantApplicationId = await InsertApplicationAsync(connection, null);

        await InsertApiClientAsync(connection, firstApplicationId, _firstClientId, _firstClientUuid);
        await InsertApiClientAsync(connection, firstApplicationId);
        await InsertApiClientAsync(connection, _otherTenantApplicationId);
        await InsertApiClientAsync(connection, _noTenantApplicationId);
    }

    protected override async Task AfterUpgradeAsync(SqlConnection connection)
    {
        Redeploy().Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();
        _journalAfterRepeatDeploy = await JournalAsync();

        _repeatClientIdViolation = await TryInsertApiClientAsync(
            connection,
            _otherTenantApplicationId,
            _firstClientId,
            Guid.NewGuid()
        );
        _repeatClientUuidViolation = await TryInsertApiClientAsync(
            connection,
            _noTenantApplicationId,
            Guid.NewGuid().ToString(),
            _firstClientUuid
        );
        _repeatBothViolation = await TryInsertApiClientAsync(
            connection,
            _otherTenantApplicationId,
            _firstClientId,
            _firstClientUuid
        );
        _freshIdentifiersViolation = await TryInsertApiClientAsync(
            connection,
            _otherTenantApplicationId,
            Guid.NewGuid().ToString(),
            Guid.NewGuid()
        );
    }

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_journals_exactly_the_identifier_uniqueness_script() =>
        JournalAfterUpgrade
            .Except(JournalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.IdentifierUniquenessScriptName);

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(JournalAfterUpgrade);

    [Test]
    public void It_keeps_every_seeded_row() => ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);

    [Test]
    public void It_adds_both_constraints() =>
        ApiClientConstraintsAfterUpgrade.Should().Contain([ClientIdConstraint, ClientUuidConstraint]);

    [Test]
    public void It_rejects_a_repeat_client_id_under_another_application_and_tenant() =>
        _repeatClientIdViolation.Should().Be(ClientIdConstraint);

    [Test]
    public void It_rejects_a_repeat_client_uuid_under_another_application() =>
        _repeatClientUuidViolation.Should().Be(ClientUuidConstraint);

    [Test]
    public void It_rejects_a_row_that_repeats_both_identifiers_on_one_of_the_constraints() =>
        _repeatBothViolation.Should().BeOneOf(ClientIdConstraint, ClientUuidConstraint);

    [Test]
    public void It_accepts_fresh_identifiers() => _freshIdentifiersViolation.Should().BeNull();
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_duplicate_client_ids_across_applications_and_tenants
    : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private int _firstDuplicateId;
    private int _secondDuplicateId;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        string sharedClientId = Guid.NewGuid().ToString();
        int firstTenantApplicationId = await InsertApplicationAsync(
            connection,
            await InsertTenantAsync(connection)
        );
        int otherTenantApplicationId = await InsertApplicationAsync(
            connection,
            await InsertTenantAsync(connection)
        );
        int noTenantApplicationId = await InsertApplicationAsync(connection, null);

        _firstDuplicateId = await InsertApiClientAsync(
            connection,
            firstTenantApplicationId,
            sharedClientId,
            Guid.NewGuid()
        );
        await InsertApiClientAsync(connection, noTenantApplicationId);
        _secondDuplicateId = await InsertApiClientAsync(
            connection,
            otherTenantApplicationId,
            sharedClientId,
            Guid.NewGuid()
        );
    }

    [Test]
    public void It_blocks_the_upgrade_listing_the_rows_that_share_a_client_id() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 ApiClient row(s) share a ClientId with another row, ids: {_firstDuplicateId}, {_secondDuplicateId}. "
                    + Remediation
            );
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_duplicate_client_uuids_across_applications
    : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private int _firstDuplicateId;
    private int _secondDuplicateId;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        Guid sharedClientUuid = Guid.NewGuid();
        int firstApplicationId = await InsertApplicationAsync(connection, null);
        int otherApplicationId = await InsertApplicationAsync(connection, null);

        _firstDuplicateId = await InsertApiClientAsync(
            connection,
            firstApplicationId,
            Guid.NewGuid().ToString(),
            sharedClientUuid
        );
        await InsertApiClientAsync(connection, firstApplicationId);
        _secondDuplicateId = await InsertApiClientAsync(
            connection,
            otherApplicationId,
            Guid.NewGuid().ToString(),
            sharedClientUuid
        );
    }

    [Test]
    public void It_blocks_the_upgrade_listing_the_rows_that_share_a_client_uuid() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 ApiClient row(s) share a ClientUuid with another row, ids: {_firstDuplicateId}, {_secondDuplicateId}. "
                    + Remediation
            );
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_duplicates_in_both_identifier_columns : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private int[] _clientIdDuplicateIds = [];
    private int[] _clientUuidDuplicateIds = [];

    protected override async Task SeedAsync(SqlConnection connection)
    {
        int applicationId = await InsertApplicationAsync(connection, null);
        string sharedClientId = Guid.NewGuid().ToString();
        Guid sharedClientUuid = Guid.NewGuid();

        _clientIdDuplicateIds =
        [
            await InsertApiClientAsync(connection, applicationId, sharedClientId, Guid.NewGuid()),
            await InsertApiClientAsync(connection, applicationId, sharedClientId, Guid.NewGuid()),
        ];
        _clientUuidDuplicateIds =
        [
            await InsertApiClientAsync(
                connection,
                applicationId,
                Guid.NewGuid().ToString(),
                sharedClientUuid
            ),
            await InsertApiClientAsync(
                connection,
                applicationId,
                Guid.NewGuid().ToString(),
                sharedClientUuid
            ),
        ];
    }

    [Test]
    public void It_blocks_the_upgrade_with_one_message_listing_both_columns() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 ApiClient row(s) share a ClientId with another row, ids: {string.Join(", ", _clientIdDuplicateIds)}. "
                    + $"2 ApiClient row(s) share a ClientUuid with another row, ids: {string.Join(", ", _clientUuidDuplicateIds)}. "
                    + Remediation
            );
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_more_duplicate_identifier_rows_than_the_message_lists
    : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private readonly List<int> _duplicateIds = [];

    protected override async Task SeedAsync(SqlConnection connection)
    {
        int applicationId = await InsertApplicationAsync(connection, null);
        string sharedClientId = Guid.NewGuid().ToString();

        for (int row = 0; row < 23; row++)
        {
            _duplicateIds.Add(
                await InsertApiClientAsync(connection, applicationId, sharedClientId, Guid.NewGuid())
            );
        }
    }

    [Test]
    public void It_lists_the_first_twenty_ids_and_counts_the_rest() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"23 ApiClient row(s) share a ClientId with another row, ids: {string.Join(", ", _duplicateIds.Take(20))}, ... and 3 more. "
                    + Remediation
            );
}

/// <summary>
/// SQL Server compares the key under the ClientId column's collation. Whether two keys that differ only
/// in case are the same row to the preflight, and therefore to the constraint, follows that collation,
/// which the fixture reads from the catalog rather than assumes: a case-insensitive collation blocks the
/// upgrade, a case-sensitive one accepts both rows.
/// </summary>
[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_client_ids_that_differ_only_in_case : ApiClientIdentifierUniquenessUpgradeFixture
{
    private const string LowerCaseKey = "dms1529-case-variant-key";
    private const string UpperCaseKey = "DMS1529-CASE-VARIANT-KEY";
    private const string MixedCaseKey = "Dms1529-Case-Variant-Key";

    private string _collation = string.Empty;
    private bool _caseVariantsCompareEqual;
    private int _applicationId;
    private int _lowerCaseId;
    private int _upperCaseId;
    private string? _newCaseVariantViolation;
    private string? _exactRepeatViolation;

    private bool CollationIgnoresCase => _collation.Contains("_CI_", StringComparison.Ordinal);

    protected override async Task SeedAsync(SqlConnection connection)
    {
        _collation = await ClientIdCollationAsync(connection);
        _caseVariantsCompareEqual =
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT CASE WHEN CAST(@Lower AS NVARCHAR(36)) COLLATE DATABASE_DEFAULT = CAST(@Upper AS NVARCHAR(36)) COLLATE DATABASE_DEFAULT THEN 1 ELSE 0 END;
                """,
                new { Lower = LowerCaseKey, Upper = UpperCaseKey }
            ) == 1;

        _applicationId = await InsertApplicationAsync(connection, null);
        _lowerCaseId = await InsertApiClientAsync(connection, _applicationId, LowerCaseKey, Guid.NewGuid());
        _upperCaseId = await InsertApiClientAsync(connection, _applicationId, UpperCaseKey, Guid.NewGuid());
    }

    protected override async Task AfterUpgradeAsync(SqlConnection connection)
    {
        if (CollationIgnoresCase)
        {
            return;
        }

        _newCaseVariantViolation = await TryInsertApiClientAsync(
            connection,
            _applicationId,
            MixedCaseKey,
            Guid.NewGuid()
        );
        _exactRepeatViolation = await TryInsertApiClientAsync(
            connection,
            _applicationId,
            LowerCaseKey,
            Guid.NewGuid()
        );
    }

    [Test]
    public void It_compares_case_the_way_the_column_collation_does() =>
        _caseVariantsCompareEqual
            .Should()
            .Be(CollationIgnoresCase, $"the ClientId column collates as {_collation}");

    [Test]
    public void It_blocks_the_upgrade_exactly_when_the_collation_ignores_case()
    {
        if (CollationIgnoresCase)
        {
            UpgradeFailureMessage()
                .Should()
                .Be(
                    BlockedPrefix
                        + $"2 ApiClient row(s) share a ClientId with another row, ids: {_lowerCaseId}, {_upperCaseId}. "
                        + Remediation
                );
            JournalAfterUpgrade.Should().Equal(JournalBeforeUpgrade);
            ApiClientConstraintsAfterUpgrade.Should().NotContain([ClientIdConstraint, ClientUuidConstraint]);
        }
        else
        {
            UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();
            ApiClientConstraintsAfterUpgrade.Should().Contain([ClientIdConstraint, ClientUuidConstraint]);
            _newCaseVariantViolation.Should().BeNull();
            _exactRepeatViolation.Should().Be(ClientIdConstraint);
        }
    }

    [Test]
    public void It_changes_no_api_client_row() =>
        ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);
}

/// <summary>
/// SQL Server pads the shorter operand for =, so a key and the same key with a trailing space are the
/// same row to the preflight and to the constraint under every collation.
/// </summary>
[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_client_ids_that_differ_only_in_a_trailing_space
    : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private const string TrimmedKey = "dms1529-trailing-space";
    private const string OneTrailingSpaceKey = "dms1529-trailing-space ";

    private bool _trailingSpaceVariantsCompareEqual;
    private int _trimmedId;
    private int _paddedId;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        _trailingSpaceVariantsCompareEqual =
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT CASE WHEN CAST(@Trimmed AS NVARCHAR(36)) = CAST(@Padded AS NVARCHAR(36)) THEN 1 ELSE 0 END;
                """,
                new { Trimmed = TrimmedKey, Padded = OneTrailingSpaceKey }
            ) == 1;

        int applicationId = await InsertApplicationAsync(connection, null);
        _trimmedId = await InsertApiClientAsync(connection, applicationId, TrimmedKey, Guid.NewGuid());
        _paddedId = await InsertApiClientAsync(
            connection,
            applicationId,
            OneTrailingSpaceKey,
            Guid.NewGuid()
        );
    }

    [Test]
    public void It_compares_a_trailing_space_as_insignificant() =>
        _trailingSpaceVariantsCompareEqual.Should().BeTrue();

    [Test]
    public void It_blocks_the_upgrade_listing_both_variants() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 ApiClient row(s) share a ClientId with another row, ids: {_trimmedId}, {_paddedId}. "
                    + Remediation
            );
}

/// <summary>
/// A database on which an operator already added the key constraint by hand, or on which an earlier
/// run was interrupted after it: the script adds only what is missing and leaves the existing
/// constraint object untouched.
/// </summary>
[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_a_pre_existing_client_id_constraint : ApiClientIdentifierUniquenessUpgradeFixture
{
    private int _constraintObjectIdBeforeUpgrade;
    private int _constraintObjectIdAfterUpgrade;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        int applicationId = await InsertApplicationAsync(connection, null);
        await InsertApiClientAsync(connection, applicationId);
        await InsertApiClientAsync(connection, applicationId);

        await connection.ExecuteAsync(
            "ALTER TABLE dmscs.ApiClient ADD CONSTRAINT UX_ApiClient_ClientId UNIQUE (ClientId);"
        );
        _constraintObjectIdBeforeUpgrade = await ConstraintObjectIdAsync(connection);
    }

    protected override async Task AfterUpgradeAsync(SqlConnection connection) =>
        _constraintObjectIdAfterUpgrade = await ConstraintObjectIdAsync(connection);

    private static Task<int> ConstraintObjectIdAsync(SqlConnection connection) =>
        connection.ExecuteScalarAsync<int>(
            """
            SELECT object_id FROM sys.key_constraints
            WHERE name = 'UX_ApiClient_ClientId' AND parent_object_id = OBJECT_ID('dmscs.ApiClient');
            """
        );

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_journals_the_identifier_uniqueness_script() =>
        JournalAfterUpgrade
            .Except(JournalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.IdentifierUniquenessScriptName);

    [Test]
    public void It_adds_the_missing_client_uuid_constraint() =>
        ApiClientConstraintsAfterUpgrade.Should().Contain([ClientIdConstraint, ClientUuidConstraint]);

    [Test]
    public void It_leaves_the_existing_client_id_constraint_object_untouched() =>
        _constraintObjectIdAfterUpgrade.Should().Be(_constraintObjectIdBeforeUpgrade);

    [Test]
    public void It_keeps_every_seeded_row() => ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);
}

/// <summary>
/// A plain index that already carries the second constraint's name makes the second ALTER fail with
/// error 1913 after the first ALTER has succeeded. Because both run in one transaction that the CATCH
/// block rolls back, the first is undone as well; after the stray index is removed, a retry converges.
/// </summary>
[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_a_second_constraint_addition_that_fails : ApiClientIdentifierUniquenessUpgradeFixture
{
    private DatabaseDeployResult _retryResult = null!;
    private string[] _journalAfterRetry = [];
    private string[] _constraintsAfterRetry = [];
    private ApiClientRow[] _apiClientsAfterRetry = [];

    protected override async Task SeedAsync(SqlConnection connection)
    {
        int applicationId = await InsertApplicationAsync(connection, null);
        await InsertApiClientAsync(connection, applicationId);
        await InsertApiClientAsync(connection, applicationId);

        await connection.ExecuteAsync("CREATE INDEX UX_ApiClient_ClientUuid ON dmscs.ApiClient (Name);");
    }

    protected override async Task AfterUpgradeAsync(SqlConnection connection)
    {
        await connection.ExecuteAsync("DROP INDEX UX_ApiClient_ClientUuid ON dmscs.ApiClient;");

        _retryResult = Redeploy();
        _journalAfterRetry = await JournalAsync();
        _constraintsAfterRetry = await ConstraintNamesAsync(connection);
        _apiClientsAfterRetry = await ApiClientsNowAsync(connection);
    }

    [Test]
    public void It_fails_on_the_second_constraint() =>
        UpgradeFailure(UpgradeResult).Number.Should().Be(DuplicateIndexName);

    [Test]
    public void It_rolls_back_the_first_constraint() =>
        ApiClientConstraintsAfterUpgrade.Should().NotContain([ClientIdConstraint, ClientUuidConstraint]);

    [Test]
    public void It_does_not_journal_the_failed_script() =>
        JournalAfterUpgrade.Should().Equal(JournalBeforeUpgrade);

    [Test]
    public void It_changes_no_api_client_row() =>
        ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);

    [Test]
    public void It_upgrades_on_a_retry_after_the_stray_index_is_removed() =>
        _retryResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_journals_the_script_on_the_retry() =>
        _journalAfterRetry
            .Except(JournalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.IdentifierUniquenessScriptName);

    [Test]
    public void It_adds_both_constraints_on_the_retry() =>
        _constraintsAfterRetry.Should().Contain([ClientIdConstraint, ClientUuidConstraint]);

    [Test]
    public void It_keeps_every_seeded_row_through_the_retry() =>
        _apiClientsAfterRetry.Should().Equal(ApiClientsBeforeUpgrade);
}

/// <summary>
/// The documented remediation: the operator removes the reviewed stale row and its dependent rows
/// directly in the database, inside one transaction, and retries. The surviving row and its own
/// assignments are untouched.
/// </summary>
[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_a_blocked_upgrade_that_is_remediated : ApiClientIdentifierUniquenessUpgradeFixture
{
    private int _survivorId;
    private int _staleId;
    private int _survivorDataStoreId;
    private DatabaseDeployResult _retryResult = null!;
    private string[] _journalAfterRetry = [];
    private string[] _constraintsAfterRetry = [];
    private ApiClientRow[] _apiClientsAfterRetry = [];
    private int[] _survivorDataStoreIdsAfterRetry = [];
    private int _duplicateRowsAfterRemediation;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        string sharedClientId = Guid.NewGuid().ToString();
        int applicationId = await InsertApplicationAsync(connection, null);
        _survivorDataStoreId = await InsertDataStoreAsync(connection);
        int staleDataStoreId = await InsertDataStoreAsync(connection);

        _survivorId = await InsertApiClientAsync(connection, applicationId, sharedClientId, Guid.NewGuid());
        _staleId = await InsertApiClientAsync(connection, applicationId, sharedClientId, Guid.NewGuid());

        await connection.ExecuteAsync(
            """
            INSERT INTO dmscs.ApiClientDataStore (ApiClientId, DataStoreId)
            VALUES (@SurvivorId, @SurvivorDataStoreId), (@StaleId, @StaleDataStoreId);
            """,
            new
            {
                SurvivorId = _survivorId,
                SurvivorDataStoreId = _survivorDataStoreId,
                StaleId = _staleId,
                StaleDataStoreId = staleDataStoreId,
            }
        );

        short ownershipTokenId = await connection.ExecuteScalarAsync<short>(
            "INSERT INTO dmscs.OwnershipToken (Description) OUTPUT INSERTED.Id VALUES ('Stale owner');"
        );
        await connection.ExecuteAsync(
            """
            INSERT INTO dmscs.ApiClientOwnershipToken (ApiClientId, OwnershipTokenId)
            VALUES (@StaleId, @OwnershipTokenId);
            """,
            new { StaleId = _staleId, OwnershipTokenId = ownershipTokenId }
        );
    }

    protected override async Task AfterUpgradeAsync(SqlConnection connection)
    {
        await using (SqlTransaction remediation = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM dmscs.ApiClientOwnershipToken WHERE ApiClientId = @StaleId;
                DELETE FROM dmscs.ApiClientDataStore WHERE ApiClientId = @StaleId;
                DELETE FROM dmscs.ApiClient WHERE Id = @StaleId;
                """,
                new { StaleId = _staleId },
                remediation
            );
            await remediation.CommitAsync();
        }

        _duplicateRowsAfterRemediation = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM dmscs.ApiClient candidate
            WHERE EXISTS (
                SELECT 1 FROM dmscs.ApiClient other
                WHERE other.Id <> candidate.Id
                  AND (other.ClientId = candidate.ClientId OR other.ClientUuid = candidate.ClientUuid)
            );
            """
        );

        _retryResult = Redeploy();
        _journalAfterRetry = await JournalAsync();
        _constraintsAfterRetry = await ConstraintNamesAsync(connection);
        _apiClientsAfterRetry = await ApiClientsNowAsync(connection);
        _survivorDataStoreIdsAfterRetry = (
            await connection.QueryAsync<int>(
                "SELECT DataStoreId FROM dmscs.ApiClientDataStore WHERE ApiClientId = @SurvivorId;",
                new { SurvivorId = _survivorId }
            )
        ).ToArray();
    }

    [Test]
    public void It_blocks_the_upgrade_listing_both_rows() =>
        UpgradeFailureMessage()
            .Should()
            .Be(
                BlockedPrefix
                    + $"2 ApiClient row(s) share a ClientId with another row, ids: {_survivorId}, {_staleId}. "
                    + Remediation
            );

    [Test]
    public void It_leaves_no_duplicate_after_the_reviewed_row_is_removed() =>
        _duplicateRowsAfterRemediation.Should().Be(0);

    [Test]
    public void It_upgrades_on_the_retry() =>
        _retryResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_journals_the_script_on_the_retry() =>
        _journalAfterRetry
            .Except(JournalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.IdentifierUniquenessScriptName);

    [Test]
    public void It_adds_both_constraints_on_the_retry() =>
        _constraintsAfterRetry.Should().Contain([ClientIdConstraint, ClientUuidConstraint]);

    [Test]
    public void It_keeps_the_surviving_row_and_removes_only_the_stale_one() =>
        _apiClientsAfterRetry
            .Should()
            .Equal(ApiClientsBeforeUpgrade.Where(apiClient => apiClient.Id != _staleId));

    [Test]
    public void It_keeps_the_surviving_rows_data_store_assignment() =>
        _survivorDataStoreIdsAfterRetry.Should().Equal(_survivorDataStoreId);
}
