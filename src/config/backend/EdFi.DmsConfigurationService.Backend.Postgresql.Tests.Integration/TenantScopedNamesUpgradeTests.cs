// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Exercises the tenant-scoped names upgrade against a journaled pre-upgrade database, which is the
/// state a real deployment upgrades from. Each test reverts the isolated database to that state,
/// seeds legacy rows directly with SQL, and runs the deploy again so only the upgrade script executes.
/// </summary>
public abstract class TenantScopedNamesUpgradeTests
{
    private const string JournalPattern = "%0032_Scope_Vendor_And_Profile_Names_By_Tenant%";

    private string _databaseName = string.Empty;

    protected string ConnectionString { get; private set; } = string.Empty;

    protected sealed record ProfileRow(
        int Id,
        string ProfileName,
        string Definition,
        string? CreatedBy,
        long? TenantId
    );

    protected sealed record LegacyProfileRow(
        int Id,
        string ProfileName,
        string Definition,
        string? CreatedBy
    );

    protected sealed record Assignment(int ApplicationId, int ProfileId);

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = $"dms1530_upgrade_{Guid.NewGuid():N}",
            Pooling = false,
        };
        _databaseName = builder.Database!;
        ConnectionString = builder.ConnectionString;

        DeploySuccessfully();
    }

    [OneTimeTearDown]
    public async Task OneTimeTeardown()
    {
        if (string.IsNullOrEmpty(_databaseName))
        {
            return;
        }

        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = "postgres",
            Pooling = false,
        };
        await using NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"""DROP DATABASE IF EXISTS "{_databaseName}" WITH (FORCE);""");
    }

    /// <summary>
    /// Clearing the data must come first: after a shared-profile test the copies share names with
    /// their originals, so restoring the global constraints before clearing would fail.
    /// </summary>
    protected async Task RevertToPreUpgradeStateAsync()
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();

        await connection.ExecuteAsync(
            """
            DELETE FROM "dmscs"."ApplicationProfile";
            DELETE FROM "dmscs"."ApiClient";
            DELETE FROM "dmscs"."Application";
            DELETE FROM "dmscs"."Profile";
            DELETE FROM "dmscs"."Vendor";
            DELETE FROM "dmscs"."Tenant";
            """
        );

        await connection.ExecuteAsync(
            """
            ALTER TABLE "dmscs"."Vendor" DROP CONSTRAINT IF EXISTS "UX_Vendor_TenantId_Company";
            ALTER TABLE "dmscs"."Profile" DROP CONSTRAINT IF EXISTS "UX_Profile_TenantId_ProfileName";
            ALTER TABLE "dmscs"."Profile" DROP CONSTRAINT IF EXISTS "FK_Profile_Tenant";
            DROP INDEX IF EXISTS "dmscs"."IX_Profile_TenantId";
            ALTER TABLE "dmscs"."Profile" DROP COLUMN IF EXISTS "TenantId";
            """
        );

        await connection.ExecuteAsync(
            """
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'UX_Vendor_Company' AND conrelid = '"dmscs"."Vendor"'::regclass
                ) THEN
                    ALTER TABLE "dmscs"."Vendor" ADD CONSTRAINT "UX_Vendor_Company" UNIQUE ("Company");
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname = 'UX_Profile_ProfileName' AND conrelid = '"dmscs"."Profile"'::regclass
                ) THEN
                    ALTER TABLE "dmscs"."Profile" ADD CONSTRAINT "UX_Profile_ProfileName" UNIQUE ("ProfileName");
                END IF;
            END$$;
            """
        );

        int removedJournalEntries = await DeleteJournalEntryAsync(connection);
        removedJournalEntries
            .Should()
            .BeLessThanOrEqualTo(1, "the pattern must identify only the tenant-scoped names upgrade script");
    }

    protected static async Task<int> DeleteJournalEntryAsync(NpgsqlConnection connection) =>
        await connection.ExecuteAsync(
            """DELETE FROM public."dmscs_SchemaVersions" WHERE scriptname LIKE @Pattern;""",
            new { Pattern = JournalPattern }
        );

    protected static async Task<int> CountJournalEntriesAsync(NpgsqlConnection connection) =>
        await connection.ExecuteScalarAsync<int>(
            """SELECT count(*) FROM public."dmscs_SchemaVersions" WHERE scriptname LIKE @Pattern;""",
            new { Pattern = JournalPattern }
        );

    protected async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    protected void DeploySuccessfully()
    {
        DatabaseDeployResult result = new Deploy.DatabaseDeploy().DeployDatabase(ConnectionString);

        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Assert.Fail($"Database deploy failed: {failure.Error}");
        }
    }

    protected static async Task<long> InsertTenantAsync(NpgsqlConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long>(
            """INSERT INTO "dmscs"."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = name }
        );

    protected static async Task<int> InsertVendorAsync(
        NpgsqlConnection connection,
        long? tenantId,
        string company
    ) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."Vendor" ("Company", "TenantId")
            VALUES (@Company, @TenantId)
            RETURNING "Id";
            """,
            new { Company = company, TenantId = tenantId }
        );

    protected static async Task<int> InsertApplicationAsync(
        NpgsqlConnection connection,
        int vendorId,
        string name
    ) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."Application" ("ApplicationName", "VendorId", "ClaimSetName")
            VALUES (@Name, @VendorId, 'Legacy Claim Set')
            RETURNING "Id";
            """,
            new { Name = name, VendorId = vendorId }
        );

    /// <summary>
    /// Inserts a profile the way a pre-upgrade database holds it, with no tenant column.
    /// </summary>
    protected static async Task<int> InsertLegacyProfileAsync(NpgsqlConnection connection, string name) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."Profile" ("ProfileName", "Definition", "CreatedBy")
            VALUES (@Name, @Definition, 'legacy-creator')
            RETURNING "Id";
            """,
            new { Name = name, Definition = $"<Profile name=\"{name}\"/>" }
        );

    protected static async Task AssignAsync(NpgsqlConnection connection, int applicationId, int profileId) =>
        await connection.ExecuteAsync(
            """
            INSERT INTO "dmscs"."ApplicationProfile" ("ApplicationId", "ProfileId")
            VALUES (@ApplicationId, @ProfileId);
            """,
            new { ApplicationId = applicationId, ProfileId = profileId }
        );

    protected static async Task<List<ProfileRow>> GetProfilesAsync(NpgsqlConnection connection) =>
        [
            .. await connection.QueryAsync<ProfileRow>(
                """
                SELECT "Id", "ProfileName", "Definition", "CreatedBy", "TenantId"
                FROM "dmscs"."Profile"
                ORDER BY "Id";
                """
            ),
        ];

    protected static async Task<List<LegacyProfileRow>> GetLegacyProfilesAsync(NpgsqlConnection connection) =>
        [
            .. await connection.QueryAsync<LegacyProfileRow>(
                """
                SELECT "Id", "ProfileName", "Definition", "CreatedBy"
                FROM "dmscs"."Profile"
                ORDER BY "Id";
                """
            ),
        ];

    protected static async Task<List<Assignment>> GetAssignmentsAsync(NpgsqlConnection connection) =>
        [
            .. await connection.QueryAsync<Assignment>(
                """
                SELECT "ApplicationId", "ProfileId"
                FROM "dmscs"."ApplicationProfile"
                ORDER BY "ApplicationId", "ProfileId";
                """
            ),
        ];
}

[TestFixture]
public class Given_a_multitenant_database_upgraded_to_tenant_scoped_names : TenantScopedNamesUpgradeTests
{
    private const string SingleUseName = "Single Use Profile";
    private const string SharedName = "Shared Profile";
    private const string MixedName = "Mixed Profile";
    private const string UnusedName = "Unused Profile";

    private long _tenant1;
    private long _tenant2;
    private long _tenant3;
    private int _unassignedApplication;
    private int _tenant1Application;
    private int _tenant2Application;
    private int _tenant3Application;
    private int _singleUseProfileId;
    private int _sharedProfileId;
    private int _mixedProfileId;
    private int _unusedProfileId;
    private List<ProfileRow> _profiles = [];
    private List<Assignment> _assignments = [];

    [SetUp]
    public async Task Setup()
    {
        await RevertToPreUpgradeStateAsync();

        await using (NpgsqlConnection connection = await OpenConnectionAsync())
        {
            _tenant1 = await InsertTenantAsync(connection, "Tenant 1");
            _tenant2 = await InsertTenantAsync(connection, "Tenant 2");
            _tenant3 = await InsertTenantAsync(connection, "Tenant 3");

            _unassignedApplication = await InsertApplicationAsync(
                connection,
                await InsertVendorAsync(connection, null, "Unassigned Vendor"),
                "Unassigned Application"
            );
            _tenant1Application = await InsertApplicationAsync(
                connection,
                await InsertVendorAsync(connection, _tenant1, "Tenant 1 Vendor"),
                "Tenant 1 Application"
            );
            _tenant2Application = await InsertApplicationAsync(
                connection,
                await InsertVendorAsync(connection, _tenant2, "Tenant 2 Vendor"),
                "Tenant 2 Application"
            );
            _tenant3Application = await InsertApplicationAsync(
                connection,
                await InsertVendorAsync(connection, _tenant3, "Tenant 3 Vendor"),
                "Tenant 3 Application"
            );

            _singleUseProfileId = await InsertLegacyProfileAsync(connection, SingleUseName);
            _sharedProfileId = await InsertLegacyProfileAsync(connection, SharedName);
            _mixedProfileId = await InsertLegacyProfileAsync(connection, MixedName);
            _unusedProfileId = await InsertLegacyProfileAsync(connection, UnusedName);

            await AssignAsync(connection, _tenant1Application, _singleUseProfileId);
            await AssignAsync(connection, _tenant1Application, _sharedProfileId);
            await AssignAsync(connection, _tenant2Application, _sharedProfileId);
            await AssignAsync(connection, _tenant3Application, _sharedProfileId);
            await AssignAsync(connection, _unassignedApplication, _mixedProfileId);
            await AssignAsync(connection, _tenant2Application, _mixedProfileId);
        }

        DeploySuccessfully();

        await using NpgsqlConnection upgraded = await OpenConnectionAsync();
        _profiles = await GetProfilesAsync(upgraded);
        _assignments = await GetAssignmentsAsync(upgraded);
    }

    private ProfileRow Profile(int id) => _profiles.Single(profile => profile.Id == id);

    private List<ProfileRow> CopiesOf(int originalId) =>
        [
            .. _profiles.Where(profile =>
                profile.ProfileName == Profile(originalId).ProfileName && profile.Id != originalId
            ),
        ];

    private int AssignedProfileId(int applicationId, string profileName) =>
        _assignments
            .Where(assignment => assignment.ApplicationId == applicationId)
            .Select(assignment => Profile(assignment.ProfileId))
            .Single(profile => profile.ProfileName == profileName)
            .Id;

    [Test]
    public void It_should_assign_a_profile_used_by_one_tenant_to_that_tenant_keeping_its_id()
    {
        Profile(_singleUseProfileId).TenantId.Should().Be(_tenant1);
        CopiesOf(_singleUseProfileId).Should().BeEmpty();
        AssignedProfileId(_tenant1Application, SingleUseName).Should().Be(_singleUseProfileId);
    }

    [Test]
    public void It_should_keep_a_shared_profile_under_the_lowest_tenant_that_uses_it()
    {
        Profile(_sharedProfileId).TenantId.Should().Be(_tenant1);
        AssignedProfileId(_tenant1Application, SharedName).Should().Be(_sharedProfileId);
    }

    [Test]
    public void It_should_copy_a_shared_profile_for_every_other_tenant_that_uses_it()
    {
        ProfileRow original = Profile(_sharedProfileId);
        List<ProfileRow> copies = CopiesOf(_sharedProfileId);

        copies.Select(copy => copy.TenantId).Should().BeEquivalentTo(new long?[] { _tenant2, _tenant3 });
        copies.Should().AllSatisfy(copy => copy.Definition.Should().Be(original.Definition));
        copies.Should().AllSatisfy(copy => copy.CreatedBy.Should().Be("legacy-creator"));

        AssignedProfileId(_tenant2Application, SharedName)
            .Should()
            .Be(copies.Single(copy => copy.TenantId == _tenant2).Id);
        AssignedProfileId(_tenant3Application, SharedName)
            .Should()
            .Be(copies.Single(copy => copy.TenantId == _tenant3).Id);
    }

    [Test]
    public void It_should_keep_a_mixed_profile_unassigned_and_copy_it_for_the_tenant()
    {
        Profile(_mixedProfileId).TenantId.Should().BeNull();
        AssignedProfileId(_unassignedApplication, MixedName).Should().Be(_mixedProfileId);

        ProfileRow copy = CopiesOf(_mixedProfileId).Should().ContainSingle().Subject;
        copy.TenantId.Should().Be(_tenant2);
        AssignedProfileId(_tenant2Application, MixedName).Should().Be(copy.Id);
    }

    [Test]
    public void It_should_leave_an_unused_profile_unassigned()
    {
        Profile(_unusedProfileId).TenantId.Should().BeNull();
        CopiesOf(_unusedProfileId).Should().BeEmpty();
    }

    [Test]
    public async Task It_should_journal_the_upgrade_exactly_once()
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        (await CountJournalEntriesAsync(connection)).Should().Be(1);
    }

    [Test]
    public async Task It_should_change_nothing_when_the_script_runs_again()
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        (await DeleteJournalEntryAsync(connection)).Should().Be(1);

        DeploySuccessfully();

        (await GetProfilesAsync(connection)).Should().Equal(_profiles);
        (await GetAssignmentsAsync(connection)).Should().Equal(_assignments);
    }

    [Test]
    public async Task It_should_scope_both_unique_constraints_by_tenant_with_nulls_not_distinct()
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();

        (await UniqueConstraintAsync(connection, "UX_Vendor_TenantId_Company"))
            .Should()
            .Be(("TenantId,Company", true));
        (await UniqueConstraintAsync(connection, "UX_Profile_TenantId_ProfileName"))
            .Should()
            .Be(("TenantId,ProfileName", true));

        int oldConstraints = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*) FROM pg_constraint
            WHERE conname IN ('UX_Vendor_Company', 'UX_Profile_ProfileName');
            """
        );
        oldConstraints.Should().Be(0);
    }

    [TestCase("Vendor", "UX_Vendor_TenantId_Company")]
    [TestCase("Profile", "UX_Profile_TenantId_ProfileName")]
    public async Task It_should_reject_a_duplicate_name_without_a_tenant(string table, string constraint)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        await InsertNamedRowAsync(connection, table, null, "X");

        (await TryInsertNamedRowAsync(connection, table, null, "X")).Should().Be(constraint);
    }

    [TestCase("Vendor", "UX_Vendor_TenantId_Company")]
    [TestCase("Profile", "UX_Profile_TenantId_ProfileName")]
    public async Task It_should_reject_a_duplicate_name_within_a_tenant(string table, string constraint)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        await InsertNamedRowAsync(connection, table, _tenant1, "X");

        (await TryInsertNamedRowAsync(connection, table, _tenant1, "X")).Should().Be(constraint);
    }

    [TestCase("Vendor")]
    [TestCase("Profile")]
    public async Task It_should_accept_the_same_name_in_two_tenants(string table)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        await InsertNamedRowAsync(connection, table, _tenant1, "X");

        (await TryInsertNamedRowAsync(connection, table, _tenant2, "X")).Should().BeEmpty();
    }

    private static async Task InsertNamedRowAsync(
        NpgsqlConnection connection,
        string table,
        long? tenantId,
        string name
    ) =>
        await connection.ExecuteAsync(
            table == "Vendor"
                ? """INSERT INTO "dmscs"."Vendor" ("Company", "TenantId") VALUES (@Name, @TenantId);"""
                : """
                INSERT INTO "dmscs"."Profile" ("ProfileName", "Definition", "TenantId")
                VALUES (@Name, '<Profile/>', @TenantId);
                """,
            new { Name = name, TenantId = tenantId }
        );

    /// <summary>
    /// Returns the name of the unique constraint that rejected the insert, or an empty string when
    /// the insert succeeded.
    /// </summary>
    private static async Task<string> TryInsertNamedRowAsync(
        NpgsqlConnection connection,
        string table,
        long? tenantId,
        string name
    )
    {
        try
        {
            await InsertNamedRowAsync(connection, table, tenantId, name);
            return string.Empty;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return exception.ConstraintName!;
        }
    }

    private static async Task<(string Columns, bool NullsNotDistinct)> UniqueConstraintAsync(
        NpgsqlConnection connection,
        string name
    ) =>
        await connection.QuerySingleAsync<(string, bool)>(
            """
            SELECT string_agg(attribute_info.attname, ',' ORDER BY key_columns.ordinality),
                   index_info.indnullsnotdistinct
            FROM pg_constraint constraint_info
            JOIN pg_index index_info ON index_info.indexrelid = constraint_info.conindid
            JOIN LATERAL unnest(constraint_info.conkey) WITH ORDINALITY AS key_columns(attnum, ordinality)
                ON true
            JOIN pg_attribute attribute_info
                ON attribute_info.attrelid = constraint_info.conrelid
               AND attribute_info.attnum = key_columns.attnum
            WHERE constraint_info.conname = @Name AND constraint_info.contype = 'u'
            GROUP BY index_info.indnullsnotdistinct;
            """,
            new { Name = name }
        );
}

[TestFixture]
public class Given_a_single_tenant_database_upgraded_to_tenant_scoped_names : TenantScopedNamesUpgradeTests
{
    private List<LegacyProfileRow> _profilesBefore = [];
    private List<Assignment> _assignmentsBefore = [];
    private List<ProfileRow> _profilesAfter = [];
    private List<Assignment> _assignmentsAfter = [];

    [SetUp]
    public async Task Setup()
    {
        await RevertToPreUpgradeStateAsync();

        await using (NpgsqlConnection connection = await OpenConnectionAsync())
        {
            int firstApplication = await InsertApplicationAsync(
                connection,
                await InsertVendorAsync(connection, null, "First Vendor"),
                "First Application"
            );
            int secondApplication = await InsertApplicationAsync(
                connection,
                await InsertVendorAsync(connection, null, "Second Vendor"),
                "Second Application"
            );

            int sharedProfile = await InsertLegacyProfileAsync(connection, "Shared Profile");
            int singleUseProfile = await InsertLegacyProfileAsync(connection, "Single Use Profile");
            await InsertLegacyProfileAsync(connection, "Unused Profile");

            await AssignAsync(connection, firstApplication, sharedProfile);
            await AssignAsync(connection, secondApplication, sharedProfile);
            await AssignAsync(connection, secondApplication, singleUseProfile);

            _profilesBefore = await GetLegacyProfilesAsync(connection);
            _assignmentsBefore = await GetAssignmentsAsync(connection);
        }

        DeploySuccessfully();

        await using NpgsqlConnection upgraded = await OpenConnectionAsync();
        _profilesAfter = await GetProfilesAsync(upgraded);
        _assignmentsAfter = await GetAssignmentsAsync(upgraded);
    }

    [Test]
    public void It_should_leave_every_profile_unassigned_and_unchanged()
    {
        _profilesAfter.Should().AllSatisfy(profile => profile.TenantId.Should().BeNull());
        _profilesAfter
            .Select(profile => new LegacyProfileRow(
                profile.Id,
                profile.ProfileName,
                profile.Definition,
                profile.CreatedBy
            ))
            .Should()
            .Equal(_profilesBefore);
    }

    [Test]
    public void It_should_leave_every_assignment_unchanged()
    {
        _assignmentsAfter.Should().Equal(_assignmentsBefore);
    }
}
