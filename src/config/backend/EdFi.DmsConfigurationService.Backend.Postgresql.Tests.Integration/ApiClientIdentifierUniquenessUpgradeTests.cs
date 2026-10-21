// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0035 to script 0036, which adds
/// the ApiClient ClientId and ClientUuid constraints. Each fixture seeds legacy rows directly with SQL,
/// so no repository rule can filter them, before running the upgrade. Every concrete fixture carries
/// <see cref="Category"/> so a step-level filter selects exactly this group.
/// </summary>
public abstract class ApiClientIdentifierUniquenessUpgradeFixture
{
    public const string Category = "ApiClientIdentifierUniquenessUpgrade";

    protected const string BlockedPrefix = "API client identifier uniqueness upgrade blocked: ";
    protected const string Remediation =
        "Resolve the duplicates with the operator procedure in docs/OPERATIONS.md (\"Configuration Service database upgrades\"), then retry the upgrade. Do not use DELETE /v3/apiClients/{id} for these rows.";
    protected const string ClientIdConstraint = "UX_ApiClient_ClientId";
    protected const string ClientUuidConstraint = "UX_ApiClient_ClientUuid";

    private readonly JobUpgradeTestDatabase _database = new();

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
        _database.DeployThrough(JobUpgradeTestDatabase.NameUniquenessScript);
        JournalBeforeUpgrade = await _database.JournalAsync();

        await using NpgsqlConnection connection = new(_database.ConnectionString);
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
    public Task OneTimeTeardown() => _database.DropAsync();

    protected abstract Task SeedAsync(NpgsqlConnection connection);

    protected virtual Task AfterUpgradeAsync(NpgsqlConnection connection) => Task.CompletedTask;

    protected DatabaseDeployResult Redeploy() =>
        _database.Deploy(JobUpgradeTestDatabase.IdentifierUniquenessScript);

    protected Task<string[]> JournalAsync() => _database.JournalAsync();

    protected static PostgresException UpgradeFailure(DatabaseDeployResult result)
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
            if (candidate is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        throw new AssertionException($"Expected a PostgresException but found: {failure.Error}");
    }

    protected string UpgradeFailureMessage() => UpgradeFailure(UpgradeResult).MessageText;

    protected static async Task<long> InsertTenantAsync(NpgsqlConnection connection) =>
        await connection.ExecuteScalarAsync<long>(
            """INSERT INTO "dmscs"."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = $"Tenant {Guid.NewGuid():N}" }
        );

    /// <summary>An application under a new vendor of the given tenant, or of no tenant.</summary>
    protected static async Task<int> InsertApplicationAsync(NpgsqlConnection connection, long? tenantId)
    {
        int vendorId = await connection.ExecuteScalarAsync<int>(
            """INSERT INTO "dmscs"."Vendor" ("Company", "TenantId") VALUES (@Company, @TenantId) RETURNING "Id";""",
            new { Company = $"Vendor {Guid.NewGuid():N}", TenantId = tenantId }
        );

        return await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."Application" ("ApplicationName", "VendorId", "ClaimSetName")
            VALUES (@Name, @VendorId, 'Legacy Claim Set')
            RETURNING "Id";
            """,
            new { Name = $"Application {Guid.NewGuid():N}", VendorId = vendorId }
        );
    }

    protected static async Task<int> InsertDataStoreAsync(NpgsqlConnection connection) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."DataStore" ("DataStoreType", "Name")
            VALUES ('Production', @Name)
            RETURNING "Id";
            """,
            new { Name = $"Data Store {Guid.NewGuid():N}" }
        );

    protected static async Task<int> InsertApiClientAsync(
        NpgsqlConnection connection,
        int applicationId,
        string clientId,
        Guid clientUuid
    ) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."ApiClient" ("ApplicationId", "Name", "ClientId", "ClientUuid")
            VALUES (@ApplicationId, @Name, @ClientId, @ClientUuid)
            RETURNING "Id";
            """,
            new
            {
                ApplicationId = applicationId,
                Name = $"Client {Guid.NewGuid():N}",
                ClientId = clientId,
                ClientUuid = clientUuid,
            }
        );

    protected static Task<int> InsertApiClientAsync(NpgsqlConnection connection, int applicationId) =>
        InsertApiClientAsync(connection, applicationId, Guid.NewGuid().ToString(), Guid.NewGuid());

    /// <summary>The violated constraint's name, or null when the insert succeeds.</summary>
    protected static async Task<string?> TryInsertApiClientAsync(
        NpgsqlConnection connection,
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
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return exception.ConstraintName;
        }
    }

    /// <summary>The names of the constraints on ApiClient, excluding indexes that back no constraint.</summary>
    protected static async Task<string[]> ConstraintNamesAsync(NpgsqlConnection connection) =>
        (
            await connection.QueryAsync<string>(
                """
                SELECT conname FROM pg_constraint
                WHERE conrelid = '"dmscs"."ApiClient"'::regclass
                ORDER BY conname;
                """
            )
        ).ToArray();

    private static async Task<ApiClientRow[]> ApiClientsAsync(NpgsqlConnection connection) =>
        (
            await connection.QueryAsync<ApiClientRow>(
                """
                SELECT "Id", "ApplicationId", "ClientId", "ClientUuid", "Name"
                FROM "dmscs"."ApiClient"
                ORDER BY "Id";
                """
            )
        ).ToArray();
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

    protected override async Task SeedAsync(NpgsqlConnection connection)
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

    protected override async Task AfterUpgradeAsync(NpgsqlConnection connection)
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
public class Given_duplicate_client_ids_across_applications_and_tenants
    : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private int _firstDuplicateId;
    private int _secondDuplicateId;

    protected override async Task SeedAsync(NpgsqlConnection connection)
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
public class Given_duplicate_client_uuids_across_applications
    : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private int _firstDuplicateId;
    private int _secondDuplicateId;

    protected override async Task SeedAsync(NpgsqlConnection connection)
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
public class Given_duplicates_in_both_identifier_columns : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private int[] _clientIdDuplicateIds = [];
    private int[] _clientUuidDuplicateIds = [];

    protected override async Task SeedAsync(NpgsqlConnection connection)
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
public class Given_more_duplicate_identifier_rows_than_the_message_lists
    : BlockedApiClientIdentifierUniquenessUpgradeFixture
{
    private readonly List<int> _duplicateIds = [];

    protected override async Task SeedAsync(NpgsqlConnection connection)
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
/// PostgreSQL compares the key under the column's deterministic collation, so keys that differ only
/// in case or in a trailing space are distinct both to the preflight and to the constraint. The
/// prerequisites are asserted, not assumed, so a database with a nondeterministic collation fails
/// here instead of silently changing what the fixture proves.
/// </summary>
[TestFixture]
[Category(Category)]
public class Given_client_ids_that_differ_only_in_case_or_a_trailing_space
    : ApiClientIdentifierUniquenessUpgradeFixture
{
    private const string LowerCaseKey = "dms1529-case-variant-key";
    private const string UpperCaseKey = "DMS1529-CASE-VARIANT-KEY";
    private const string MixedCaseKey = "Dms1529-Case-Variant-Key";
    private const string TrimmedKey = "dms1529-trailing-space";
    private const string OneTrailingSpaceKey = "dms1529-trailing-space ";
    private const string TwoTrailingSpacesKey = "dms1529-trailing-space  ";

    private bool _caseVariantsCompareEqual;
    private bool _trailingSpaceVariantsCompareEqual;
    private int _applicationId;
    private string? _newCaseVariantViolation;
    private string? _newTrailingSpaceVariantViolation;
    private string? _exactRepeatViolation;

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        _caseVariantsCompareEqual = await connection.ExecuteScalarAsync<bool>(
            """SELECT @Lower::varchar(36) = @Upper::varchar(36);""",
            new { Lower = LowerCaseKey, Upper = UpperCaseKey }
        );
        _trailingSpaceVariantsCompareEqual = await connection.ExecuteScalarAsync<bool>(
            """SELECT @Trimmed::varchar(36) = @Padded::varchar(36);""",
            new { Trimmed = TrimmedKey, Padded = OneTrailingSpaceKey }
        );

        _applicationId = await InsertApplicationAsync(connection, null);
        await InsertApiClientAsync(connection, _applicationId, LowerCaseKey, Guid.NewGuid());
        await InsertApiClientAsync(connection, _applicationId, UpperCaseKey, Guid.NewGuid());
        await InsertApiClientAsync(connection, _applicationId, TrimmedKey, Guid.NewGuid());
        await InsertApiClientAsync(connection, _applicationId, OneTrailingSpaceKey, Guid.NewGuid());
    }

    protected override async Task AfterUpgradeAsync(NpgsqlConnection connection)
    {
        _newCaseVariantViolation = await TryInsertApiClientAsync(
            connection,
            _applicationId,
            MixedCaseKey,
            Guid.NewGuid()
        );
        _newTrailingSpaceVariantViolation = await TryInsertApiClientAsync(
            connection,
            _applicationId,
            TwoTrailingSpacesKey,
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
    public void It_runs_against_a_deterministic_collation()
    {
        _caseVariantsCompareEqual.Should().BeFalse("the column collation must distinguish case");
        _trailingSpaceVariantsCompareEqual
            .Should()
            .BeFalse("the column collation must distinguish a trailing space");
    }

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_keeps_both_variant_pairs() =>
        ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);

    [Test]
    public void It_accepts_a_new_case_variant_after_the_upgrade() =>
        _newCaseVariantViolation.Should().BeNull();

    [Test]
    public void It_accepts_a_new_trailing_space_variant_after_the_upgrade() =>
        _newTrailingSpaceVariantViolation.Should().BeNull();

    [Test]
    public void It_rejects_an_exact_repeat_after_the_upgrade() =>
        _exactRepeatViolation.Should().Be(ClientIdConstraint);
}

/// <summary>
/// A database on which an operator already added the key constraint by hand, or on which an earlier
/// run was interrupted after it: the script adds only what is missing and leaves the existing
/// constraint object untouched.
/// </summary>
[TestFixture]
[Category(Category)]
public class Given_a_pre_existing_client_id_constraint : ApiClientIdentifierUniquenessUpgradeFixture
{
    private uint _constraintOidBeforeUpgrade;
    private uint _constraintOidAfterUpgrade;

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        int applicationId = await InsertApplicationAsync(connection, null);
        await InsertApiClientAsync(connection, applicationId);
        await InsertApiClientAsync(connection, applicationId);

        await connection.ExecuteAsync(
            """ALTER TABLE "dmscs"."ApiClient" ADD CONSTRAINT "UX_ApiClient_ClientId" UNIQUE ("ClientId");"""
        );
        _constraintOidBeforeUpgrade = await ConstraintOidAsync(connection);
    }

    protected override async Task AfterUpgradeAsync(NpgsqlConnection connection) =>
        _constraintOidAfterUpgrade = await ConstraintOidAsync(connection);

    private static Task<uint> ConstraintOidAsync(NpgsqlConnection connection) =>
        connection.ExecuteScalarAsync<uint>(
            """
            SELECT oid FROM pg_constraint
            WHERE conname = 'UX_ApiClient_ClientId' AND conrelid = '"dmscs"."ApiClient"'::regclass;
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
        _constraintOidAfterUpgrade.Should().Be(_constraintOidBeforeUpgrade);

    [Test]
    public void It_keeps_every_seeded_row() => ApiClientsAfterUpgrade.Should().Equal(ApiClientsBeforeUpgrade);
}

/// <summary>
/// A plain index that already carries the second constraint's name makes the second ALTER fail with
/// "relation already exists" after the first ALTER has succeeded. Because both run in one DO block,
/// the first is rolled back as well; after the stray index is removed, a retry converges.
/// </summary>
[TestFixture]
[Category(Category)]
public class Given_a_second_constraint_addition_that_fails : ApiClientIdentifierUniquenessUpgradeFixture
{
    private DatabaseDeployResult _retryResult = null!;
    private string[] _journalAfterRetry = [];
    private string[] _constraintsAfterRetry = [];
    private ApiClientRow[] _apiClientsAfterRetry = [];

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        int applicationId = await InsertApplicationAsync(connection, null);
        await InsertApiClientAsync(connection, applicationId);
        await InsertApiClientAsync(connection, applicationId);

        await connection.ExecuteAsync(
            """CREATE INDEX "UX_ApiClient_ClientUuid" ON "dmscs"."ApiClient" ("Name");"""
        );
    }

    protected override async Task AfterUpgradeAsync(NpgsqlConnection connection)
    {
        await connection.ExecuteAsync("""DROP INDEX "dmscs"."UX_ApiClient_ClientUuid";""");

        _retryResult = Redeploy();
        _journalAfterRetry = await JournalAsync();
        _constraintsAfterRetry = await ConstraintNamesAsync(connection);
        _apiClientsAfterRetry = (
            await connection.QueryAsync<ApiClientRow>(
                """
                SELECT "Id", "ApplicationId", "ClientId", "ClientUuid", "Name"
                FROM "dmscs"."ApiClient"
                ORDER BY "Id";
                """
            )
        ).ToArray();
    }

    [Test]
    public void It_fails_on_the_second_constraint() =>
        UpgradeFailure(UpgradeResult).SqlState.Should().Be(PostgresErrorCodes.DuplicateTable);

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

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        string sharedClientId = Guid.NewGuid().ToString();
        int applicationId = await InsertApplicationAsync(connection, null);
        _survivorDataStoreId = await InsertDataStoreAsync(connection);
        int staleDataStoreId = await InsertDataStoreAsync(connection);

        _survivorId = await InsertApiClientAsync(connection, applicationId, sharedClientId, Guid.NewGuid());
        _staleId = await InsertApiClientAsync(connection, applicationId, sharedClientId, Guid.NewGuid());

        await connection.ExecuteAsync(
            """
            INSERT INTO "dmscs"."ApiClientDataStore" ("ApiClientId", "DataStoreId")
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
            """INSERT INTO "dmscs"."OwnershipToken" ("Description") VALUES ('Stale owner') RETURNING "Id";"""
        );
        await connection.ExecuteAsync(
            """
            INSERT INTO "dmscs"."ApiClientOwnershipToken" ("ApiClientId", "OwnershipTokenId")
            VALUES (@StaleId, @OwnershipTokenId);
            """,
            new { StaleId = _staleId, OwnershipTokenId = ownershipTokenId }
        );
    }

    protected override async Task AfterUpgradeAsync(NpgsqlConnection connection)
    {
        await using (NpgsqlTransaction remediation = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync(
                """
                DELETE FROM "dmscs"."ApiClientOwnershipToken" WHERE "ApiClientId" = @StaleId;
                DELETE FROM "dmscs"."ApiClientDataStore" WHERE "ApiClientId" = @StaleId;
                DELETE FROM "dmscs"."ApiClient" WHERE "Id" = @StaleId;
                """,
                new { StaleId = _staleId },
                remediation
            );
            await remediation.CommitAsync();
        }

        _duplicateRowsAfterRemediation = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*) FROM "dmscs"."ApiClient" candidate
            WHERE EXISTS (
                SELECT 1 FROM "dmscs"."ApiClient" other
                WHERE other."Id" <> candidate."Id"
                  AND (other."ClientId" = candidate."ClientId" OR other."ClientUuid" = candidate."ClientUuid")
            );
            """
        );

        _retryResult = Redeploy();
        _journalAfterRetry = await JournalAsync();
        _constraintsAfterRetry = await ConstraintNamesAsync(connection);
        _apiClientsAfterRetry = (
            await connection.QueryAsync<ApiClientRow>(
                """
                SELECT "Id", "ApplicationId", "ClientId", "ClientUuid", "Name"
                FROM "dmscs"."ApiClient"
                ORDER BY "Id";
                """
            )
        ).ToArray();
        _survivorDataStoreIdsAfterRetry = (
            await connection.QueryAsync<int>(
                """SELECT "DataStoreId" FROM "dmscs"."ApiClientDataStore" WHERE "ApiClientId" = @SurvivorId;""",
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
