// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Runs the education organization projection claim migration in databases with a
/// case-insensitive and a case-sensitive default collation, to show that its claim matching
/// follows CMS rather than SQL Server's collation and padding rules. CMS's own SQL Server
/// repositories cannot run in a case-sensitive database (they address columns such as Id in lower
/// case), so the pre-upgrade catalog is written directly instead of loaded by CMS.
/// </summary>
public abstract class Given_a_SqlServer_catalog_upgraded_with_the_projection_claim(string collation)
{
    protected const string ProjectionClaimName =
        "http://ed-fi.org/identity/claims/services/educationOrganizationProjection";
    protected const string CaseInsensitiveCollation = "SQL_Latin1_General_CP1_CI_AS";
    protected const string CaseSensitiveCollation = "Latin1_General_100_CS_AS";
    protected const string ProjectionClaimScriptName =
        "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0036_Add_EducationOrganizationProjection_Claim.sql";

    private const int PreviousScript = 35;
    private const int ProjectionClaimScript = 36;
    private const string ParentClaimName = "http://ed-fi.org/identity/claims/services/identity";

    protected sealed record ResourceClaimRow(int Id, string ResourceName, string ClaimName);

    protected sealed record HierarchyRow(
        string Hierarchy,
        DateTime LastModifiedDate,
        DateTime? LastModifiedAt,
        string? ModifiedBy
    );

    private JobUpgradeTestDatabase? _database;

    protected List<ResourceClaimRow> ResourceClaimsBefore { get; private set; } = [];
    protected List<ResourceClaimRow> ResourceClaimsAfter { get; private set; } = [];
    protected HierarchyRow HierarchyBefore { get; private set; } = null!;
    protected HierarchyRow HierarchyAfter { get; private set; } = null!;
    protected string[] JournalAfter { get; private set; } = [];
    protected DatabaseDeployResult DeployResult { get; private set; } = null!;

    /// <summary>The claim the operator's hierarchy already nests, or null when it has none.</summary>
    protected virtual string? HierarchyClaimName => null;

    /// <summary>The resource-claim metadata row the operator's catalog already has, or null.</summary>
    protected virtual string? ResourceClaimName => null;

    private JobUpgradeTestDatabase Database =>
        _database ?? throw new InvalidOperationException("The isolated database was not created.");

    [OneTimeSetUp]
    public async Task Setup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new JobUpgradeTestDatabase();

        await CreateDatabaseAsync();
        Database.DeployThrough(PreviousScript);
        await WritePreUpgradeCatalogAsync();

        ResourceClaimsBefore = await ResourceClaimsAsync();
        HierarchyBefore = await HierarchyAsync();

        DeployResult = DeployProjectionClaimScript();

        ResourceClaimsAfter = await ResourceClaimsAsync();
        HierarchyAfter = await HierarchyAsync();
        JournalAfter = await Database.JournalAsync();
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_database is not null)
        {
            await _database.DropAsync();
        }
    }

    [Test]
    public async Task It_runs_in_the_requested_collation()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        (
            await connection.ExecuteScalarAsync<string>(
                "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));"
            )
        )
            .Should()
            .Be(collation);
    }

    protected DatabaseDeployResult DeployProjectionClaimScript() =>
        new Deploy.DatabaseDeploy
        {
            ScriptFilter = scriptName =>
                JobUpgradeTestDatabase.ScriptNumber(scriptName) <= ProjectionClaimScript,
        }.DeployDatabase(Database.ConnectionString);

    protected async Task<List<ResourceClaimRow>> ResourceClaimsAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return (
            await connection.QueryAsync<ResourceClaimRow>(
                "SELECT Id, ResourceName, ClaimName FROM dmscs.ResourceClaim ORDER BY Id;"
            )
        ).ToList();
    }

    protected async Task<HierarchyRow> HierarchyAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.QuerySingleAsync<HierarchyRow>(
            "SELECT Hierarchy, LastModifiedDate, LastModifiedAt, ModifiedBy FROM dmscs.ClaimsHierarchy;"
        );
    }

    protected async Task RemoveProjectionClaimJournalEntryAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        (
            await connection.ExecuteAsync(
                "DELETE FROM dbo.dmscs_SchemaVersions WHERE ScriptName = @ScriptName;",
                new { ScriptName = ProjectionClaimScriptName }
            )
        )
            .Should()
            .Be(1);
    }

    protected static List<string?> ClaimNames(string hierarchyJson)
    {
        List<string?> names = [];
        Stack<JsonNode?> pending = new(JsonNode.Parse(hierarchyJson)!.AsArray());

        while (pending.TryPop(out JsonNode? claim))
        {
            names.Add(claim?["name"]?.GetValue<string>());

            if (claim?["claims"] is JsonArray children)
            {
                foreach (JsonNode? child in children)
                {
                    pending.Push(child);
                }
            }
        }

        return names;
    }

    // CMS resolves claim names exactly and then with OrdinalIgnoreCase, so either spelling is the claim.
    protected static bool IsProjectionClaimName(string? claimName) =>
        string.Equals(claimName, ProjectionClaimName, StringComparison.OrdinalIgnoreCase);

    private async Task CreateDatabaseAsync()
    {
        string databaseName = new SqlConnectionStringBuilder(Database.ConnectionString).InitialCatalog;
        SqlConnectionStringBuilder master = new(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };

        await using SqlConnection connection = new(master.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE DATABASE [{databaseName}] COLLATE {collation};");
    }

    private async Task WritePreUpgradeCatalogAsync()
    {
        JsonArray nestedClaims = [];
        if (HierarchyClaimName is not null)
        {
            nestedClaims.Add(
                new JsonObject
                {
                    ["name"] = HierarchyClaimName,
                    ["defaultAuthorization"] = JsonNode.Parse(
                        """
                        {
                          "actions": [
                            { "name": "Read", "authorizationStrategies": [{ "name": "RelationshipsWithEdOrgsOnly" }] }
                          ]
                        }
                        """
                    ),
                    ["claimSets"] = JsonNode.Parse(
                        """[{ "name": "Operator Reader", "actions": [{ "name": "Read" }] }]"""
                    ),
                    ["claims"] = new JsonArray(),
                }
            );
        }

        JsonArray hierarchy =
        [
            new JsonObject
            {
                ["name"] = ParentClaimName,
                ["claimSets"] = new JsonArray(),
                ["claims"] = nestedClaims,
            },
        ];

        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO dmscs.ClaimsHierarchy (Hierarchy, ModifiedBy) VALUES (@Hierarchy, N'operator');",
            new { Hierarchy = hierarchy.ToJsonString() }
        );

        if (ResourceClaimName is not null)
        {
            await connection.ExecuteAsync(
                "INSERT INTO dmscs.ResourceClaim (ResourceName, ClaimName) VALUES (@ResourceName, @ClaimName);",
                new { ResourceName = ResourceClaimName.Split('/')[^1].Trim(), ClaimName = ResourceClaimName }
            );
        }
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        SqlConnection connection = new(Database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

/// <summary>An upgrade the migration is expected to complete.</summary>
public abstract class Given_a_SqlServer_catalog_upgraded_successfully_with_the_projection_claim(
    string collation
) : Given_a_SqlServer_catalog_upgraded_with_the_projection_claim(collation)
{
    [Test]
    public void It_deploys_and_journals_the_script()
    {
        DeployResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();
        JournalAfter.Should().ContainSingle(script => script == ProjectionClaimScriptName);
    }

    [Test]
    public async Task It_changes_nothing_when_the_script_runs_again()
    {
        List<ResourceClaimRow> resourceClaimsBefore = await ResourceClaimsAsync();
        HierarchyRow hierarchyBefore = await HierarchyAsync();

        await RemoveProjectionClaimJournalEntryAsync();
        DeployProjectionClaimScript().Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

        (await ResourceClaimsAsync()).Should().Equal(resourceClaimsBefore);
        (await HierarchyAsync()).Should().Be(hierarchyBefore);
    }
}

[TestFixture(CaseInsensitiveCollation)]
[TestFixture(CaseSensitiveCollation)]
[Category("MssqlIntegration")]
public class Given_a_SqlServer_catalog_without_the_projection_claim(string collation)
    : Given_a_SqlServer_catalog_upgraded_successfully_with_the_projection_claim(collation)
{
    [Test]
    public void It_adds_one_resource_claim_row_with_the_canonical_name()
    {
        ResourceClaimsAfter
            .Except(ResourceClaimsBefore)
            .Should()
            .ContainSingle()
            .Which.ClaimName.Should()
            .Be(ProjectionClaimName);
    }

    [Test]
    public void It_appends_the_canonical_claim()
    {
        ClaimNames(HierarchyAfter.Hierarchy).Where(IsProjectionClaimName).Should().Equal(ProjectionClaimName);
        HierarchyAfter.ModifiedBy.Should().Be("0036_Add_EducationOrganizationProjection_Claim");
    }
}

/// <summary>An upgrade of a catalog that already provides a claim CMS treats as the projection claim.</summary>
public abstract class Given_a_SqlServer_catalog_that_already_has_the_projection_claim(string collation)
    : Given_a_SqlServer_catalog_upgraded_successfully_with_the_projection_claim(collation)
{
    protected abstract string ExistingClaimName { get; }

    protected override string? HierarchyClaimName => ExistingClaimName;

    protected override string? ResourceClaimName => ExistingClaimName;

    [Test]
    public void It_adds_no_resource_claim_row()
    {
        ResourceClaimsAfter.Should().Equal(ResourceClaimsBefore);
    }

    [Test]
    public void It_leaves_the_stored_hierarchy_and_its_audit_values_unchanged()
    {
        HierarchyAfter.Should().Be(HierarchyBefore);
        ClaimNames(HierarchyAfter.Hierarchy).Where(IsProjectionClaimName).Should().Equal(ExistingClaimName);
    }
}

[TestFixture(CaseInsensitiveCollation)]
[TestFixture(CaseSensitiveCollation)]
[Category("MssqlIntegration")]
public class Given_a_SqlServer_catalog_with_the_projection_claim_nested_under_another_claim(string collation)
    : Given_a_SqlServer_catalog_that_already_has_the_projection_claim(collation)
{
    protected override string ExistingClaimName => ProjectionClaimName;
}

[TestFixture(CaseInsensitiveCollation)]
[TestFixture(CaseSensitiveCollation)]
[Category("MssqlIntegration")]
public class Given_a_SqlServer_catalog_with_a_nested_case_variant_of_the_projection_claim(string collation)
    : Given_a_SqlServer_catalog_that_already_has_the_projection_claim(collation)
{
    protected override string ExistingClaimName =>
        "http://ed-fi.org/identity/claims/services/educationorganizationprojection";
}

/// <summary>
/// A hierarchy claim whose name differs from the projection claim only by a trailing space. SQL
/// Server's = ignores trailing spaces; CMS does not, so the projection claim is still missing.
/// </summary>
[TestFixture(CaseInsensitiveCollation)]
[TestFixture(CaseSensitiveCollation)]
[Category("MssqlIntegration")]
public class Given_a_SqlServer_catalog_with_a_trailing_space_variant_in_the_hierarchy(string collation)
    : Given_a_SqlServer_catalog_upgraded_successfully_with_the_projection_claim(collation)
{
    private const string TrailingSpaceName = ProjectionClaimName + " ";

    protected override string? HierarchyClaimName => TrailingSpaceName;

    [Test]
    public void It_does_not_treat_the_trailing_space_name_as_the_projection_claim()
    {
        IsProjectionClaimName(TrailingSpaceName).Should().BeFalse();
        ClaimNames(HierarchyBefore.Hierarchy).Should().Contain(TrailingSpaceName);
    }

    [Test]
    public void It_adds_one_resource_claim_row_with_the_canonical_name()
    {
        ResourceClaimsAfter
            .Except(ResourceClaimsBefore)
            .Should()
            .ContainSingle()
            .Which.ClaimName.Should()
            .Be(ProjectionClaimName);
    }

    [Test]
    public void It_appends_the_canonical_claim_and_keeps_the_trailing_space_claim()
    {
        List<string?> namesAfter = ClaimNames(HierarchyAfter.Hierarchy);

        namesAfter.Where(name => name == ProjectionClaimName).Should().ContainSingle();
        namesAfter.Where(name => name == TrailingSpaceName).Should().ContainSingle();
        HierarchyAfter.ModifiedBy.Should().Be("0036_Add_EducationOrganizationProjection_Claim");
    }
}

/// <summary>
/// A metadata row whose name differs from the projection claim only by a trailing space. CMS treats
/// it as a different claim, but UX_ResourceClaim_ClaimName treats it as the same name and would
/// reject the projection claim's row, so the migration must stop before changing anything.
/// </summary>
[TestFixture(CaseInsensitiveCollation)]
[TestFixture(CaseSensitiveCollation)]
[Category("MssqlIntegration")]
public class Given_a_SqlServer_catalog_with_a_conflicting_trailing_space_resource_claim(string collation)
    : Given_a_SqlServer_catalog_upgraded_with_the_projection_claim(collation)
{
    private const string TrailingSpaceName = ProjectionClaimName + " ";

    protected override string? HierarchyClaimName => TrailingSpaceName;

    protected override string? ResourceClaimName => TrailingSpaceName;

    [Test]
    public void It_fails_the_deployment_with_an_actionable_diagnostic()
    {
        int conflictingId = ResourceClaimsBefore.Single(row => row.ClaimName == TrailingSpaceName).Id;

        SqlException error = FindSqlException(
            DeployResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeployFailure>().Subject.Error
        );

        error.Number.Should().Be(50000);
        error
            .Message.Should()
            .StartWith("0036_Add_EducationOrganizationProjection_Claim:")
            .And.Contain($"dmscs.ResourceClaim row with Id {conflictingId} ")
            .And.Contain("UX_ResourceClaim_ClaimName")
            .And.Contain("No changes were made.")
            .And.Contain("Rename or remove that resource claim");
    }

    [Test]
    public void It_leaves_the_resource_claims_unchanged()
    {
        ResourceClaimsAfter.Should().Equal(ResourceClaimsBefore);
    }

    [Test]
    public void It_leaves_the_stored_hierarchy_and_its_audit_values_unchanged()
    {
        HierarchyAfter.Should().Be(HierarchyBefore);
    }

    [Test]
    public void It_does_not_journal_the_script()
    {
        JournalAfter.Should().NotContain(ProjectionClaimScriptName);
    }

    private static SqlException FindSqlException(Exception exception)
    {
        for (Exception? candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is SqlException sqlException)
            {
                return sqlException;
            }
        }

        throw new AssertionException($"Expected a SqlException but found: {exception}");
    }
}
