// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Runs the education organization projection claim migration in a database whose default
/// collation is case-sensitive, to show its claim matching does not depend on that collation.
/// CMS's own SQL Server repositories cannot run in such a database (they address columns such as
/// Id in lower case), so the pre-upgrade hierarchy is written directly instead of loaded by CMS.
/// </summary>
public abstract class Given_a_case_sensitive_catalog_upgraded_with_the_projection_claim
{
    protected const string ProjectionClaimName =
        "http://ed-fi.org/identity/claims/services/educationOrganizationProjection";

    private const string CaseSensitiveCollation = "Latin1_General_100_CS_AS";
    private const string ProjectionClaimScriptName =
        "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0035_Add_EducationOrganizationProjection_Claim.sql";
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

    /// <summary>The claim the operator's hierarchy already nests, or null when it has none.</summary>
    protected abstract string? ExistingClaimName { get; }

    private JobUpgradeTestDatabase Database =>
        _database ?? throw new InvalidOperationException("The isolated database was not created.");

    [OneTimeSetUp]
    public async Task Setup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new JobUpgradeTestDatabase();

        await CreateCaseSensitiveDatabaseAsync();
        Database.DeployThrough(34);
        await WritePreUpgradeHierarchyAsync();

        ResourceClaimsBefore = await ResourceClaimsAsync();
        HierarchyBefore = await HierarchyAsync();

        Database.DeployThrough(35);

        ResourceClaimsAfter = await ResourceClaimsAsync();
        HierarchyAfter = await HierarchyAsync();
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
    public async Task It_runs_in_a_case_sensitive_database()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        (
            await connection.ExecuteScalarAsync<string>(
                "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));"
            )
        )
            .Should()
            .Be(CaseSensitiveCollation);
    }

    [Test]
    public async Task It_changes_nothing_when_the_script_runs_again()
    {
        List<ResourceClaimRow> resourceClaimsBefore = await ResourceClaimsAsync();
        HierarchyRow hierarchyBefore = await HierarchyAsync();

        await using (SqlConnection connection = await OpenConnectionAsync())
        {
            (
                await connection.ExecuteAsync(
                    "DELETE FROM dbo.dmscs_SchemaVersions WHERE ScriptName = @ScriptName;",
                    new { ScriptName = ProjectionClaimScriptName }
                )
            )
                .Should()
                .Be(1);
        }

        Database.DeployThrough(35);

        (await ResourceClaimsAsync()).Should().Equal(resourceClaimsBefore);
        (await HierarchyAsync()).Should().Be(hierarchyBefore);
    }

    protected static IEnumerable<string?> ClaimNames(string hierarchyJson)
    {
        Stack<JsonNode?> pending = new(JsonNode.Parse(hierarchyJson)!.AsArray());

        while (pending.TryPop(out JsonNode? claim))
        {
            yield return claim?["name"]?.GetValue<string>();

            if (claim?["claims"] is JsonArray children)
            {
                foreach (JsonNode? child in children)
                {
                    pending.Push(child);
                }
            }
        }
    }

    protected static bool IsProjectionClaimName(string? claimName) =>
        string.Equals(claimName, ProjectionClaimName, StringComparison.OrdinalIgnoreCase);

    private async Task CreateCaseSensitiveDatabaseAsync()
    {
        string databaseName = new SqlConnectionStringBuilder(Database.ConnectionString).InitialCatalog;
        SqlConnectionStringBuilder master = new(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        };

        await using SqlConnection connection = new(master.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync($"CREATE DATABASE [{databaseName}] COLLATE {CaseSensitiveCollation};");
    }

    private async Task WritePreUpgradeHierarchyAsync()
    {
        JsonArray nestedClaims = [];
        if (ExistingClaimName is not null)
        {
            nestedClaims.Add(
                JsonNode.Parse(
                    $$"""
                    {
                      "name": "{{ExistingClaimName}}",
                      "defaultAuthorization": {
                        "actions": [
                          { "name": "Read", "authorizationStrategies": [{ "name": "RelationshipsWithEdOrgsOnly" }] }
                        ]
                      },
                      "claimSets": [{ "name": "Operator Reader", "actions": [{ "name": "Read" }] }],
                      "claims": []
                    }
                    """
                )
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

        if (ExistingClaimName is not null)
        {
            await connection.ExecuteAsync(
                "INSERT INTO dmscs.ResourceClaim (ResourceName, ClaimName) VALUES (@ResourceName, @ClaimName);",
                new { ResourceName = ExistingClaimName.Split('/')[^1], ClaimName = ExistingClaimName }
            );
        }
    }

    private async Task<List<ResourceClaimRow>> ResourceClaimsAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return (
            await connection.QueryAsync<ResourceClaimRow>(
                "SELECT Id, ResourceName, ClaimName FROM dmscs.ResourceClaim ORDER BY Id;"
            )
        ).ToList();
    }

    private async Task<HierarchyRow> HierarchyAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.QuerySingleAsync<HierarchyRow>(
            "SELECT Hierarchy, LastModifiedDate, LastModifiedAt, ModifiedBy FROM dmscs.ClaimsHierarchy;"
        );
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        SqlConnection connection = new(Database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_case_sensitive_catalog_without_the_projection_claim
    : Given_a_case_sensitive_catalog_upgraded_with_the_projection_claim
{
    protected override string? ExistingClaimName => null;

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
        HierarchyAfter.ModifiedBy.Should().Be("0035_Add_EducationOrganizationProjection_Claim");
    }
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_case_sensitive_catalog_with_a_nested_case_variant_of_the_projection_claim
    : Given_a_case_sensitive_catalog_upgraded_with_the_projection_claim
{
    private const string CaseVariantName =
        "http://ed-fi.org/identity/claims/services/educationorganizationprojection";

    protected override string? ExistingClaimName => CaseVariantName;

    [Test]
    public void It_adds_no_resource_claim_row()
    {
        ResourceClaimsAfter.Should().Equal(ResourceClaimsBefore);
    }

    [Test]
    public void It_leaves_the_stored_hierarchy_and_its_audit_values_unchanged()
    {
        HierarchyAfter.Should().Be(HierarchyBefore);
        ClaimNames(HierarchyAfter.Hierarchy).Where(IsProjectionClaimName).Should().Equal(CaseVariantName);
    }
}
