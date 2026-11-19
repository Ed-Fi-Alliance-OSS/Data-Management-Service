// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Mssql.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Exercises the education organization projection claim migration against isolated databases.
/// A catalog provisioned by an earlier release is reproduced by loading the embedded claims into
/// a database journaled through the previous script and then removing the projection claim, which
/// is exactly what that release's claims loading left behind.
/// </summary>
public abstract class EducationOrganizationProjectionClaimTests
{
    protected const string ProjectionClaimName =
        "http://ed-fi.org/identity/claims/services/educationOrganizationProjection";
    protected const string StudentClaimName = "http://ed-fi.org/identity/claims/ed-fi/student";

    private const int PreviousScript = JobUpgradeTestDatabase.ApiClientEducationOrganizationScript;
    protected const int ProjectionClaimScript = 39;
    protected const string ProjectionClaimScriptName =
        "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0039_Add_EducationOrganizationProjection_Claim.sql";

    // The name databases deployed before the migration was renumbered journaled it under.
    protected const string FormerProjectionClaimScriptName =
        "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0036_Add_EducationOrganizationProjection_Claim.sql";

    // The claim the migration appends, in the form the hierarchy repository stores.
    protected static JsonNode ExpectedProjectionClaim =>
        JsonNode.Parse(
            """
            {
              "name": "http://ed-fi.org/identity/claims/services/educationOrganizationProjection",
              "defaultAuthorization": {
                "actions": [
                  { "name": "Read", "authorizationStrategies": [{ "name": "NoFurtherAuthorizationRequired" }] }
                ]
              },
              "claimSets": [],
              "claims": []
            }
            """
        )!;

    protected sealed record ResourceClaimRow(int Id, string ResourceName, string ClaimName);

    protected sealed record HierarchyRow(
        string Hierarchy,
        DateTime LastModifiedDate,
        DateTime? LastModifiedAt,
        string? ModifiedBy
    );

    private JobUpgradeTestDatabase? _database;

    private protected JobUpgradeTestDatabase Database =>
        _database ?? throw new InvalidOperationException("The isolated database was not created.");

    [OneTimeSetUp]
    public void CreateDatabase()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new JobUpgradeTestDatabase();
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        if (_database is not null)
        {
            await _database.DropAsync();
        }
    }

    protected void DeployThroughPreviousScript() => Database.DeployThrough(PreviousScript);

    protected IOptions<DatabaseOptions> DatabaseOptions =>
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = Database.ConnectionString,
                EncryptionKey = MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey,
            }
        );

    protected IClaimSetRepository CreateClaimSetRepository() =>
        new ClaimSetRepository(
            DatabaseOptions,
            NullLogger<ClaimSetRepository>.Instance,
            CreateClaimsHierarchyRepository(),
            new ClaimsHierarchyManager(),
            new TestAuditContext(),
            new TenantContextProvider()
        );

    protected async Task LoadEmbeddedClaimsAsync()
    {
        var claimsDataLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
            new ClaimsProvider(
                NullLogger<ClaimsProvider>.Instance,
                Options.Create(
                    new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded, ClaimsDirectory = "" }
                ),
                new ClaimsValidator(NullLogger<ClaimsValidator>.Instance),
                new ClaimsFragmentComposer(NullLogger<ClaimsFragmentComposer>.Instance)
            ),
            CreateClaimSetRepository(),
            CreateClaimsHierarchyRepository(),
            new ClaimsTableValidator(DatabaseOptions, NullLogger<ClaimsTableValidator>.Instance),
            new ClaimsDocumentRepository(DatabaseOptions, NullLogger<ClaimsDocumentRepository>.Instance),
            NullLogger<Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance,
            new ResourceClaimMetadataRepository(
                DatabaseOptions,
                NullLogger<ResourceClaimMetadataRepository>.Instance
            )
        );

        ClaimsDataLoadResult result = await claimsDataLoader.LoadInitialClaimsAsync();
        result.Should().BeOfType<ClaimsDataLoadResult.Success>();
    }

    /// <summary>
    /// Returns the catalog to the state an earlier release left: its stored hierarchy and its
    /// resource-claim metadata lack the projection claim.
    /// </summary>
    protected async Task RemoveProjectionClaimAsync()
    {
        HierarchyRow hierarchy = await HierarchyAsync();
        JsonArray roots = Roots(hierarchy.Hierarchy);
        JsonNode projectionClaim = roots.Single(root => ClaimNameOf(root) == ProjectionClaimName)!;
        roots.Remove(projectionClaim);

        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dmscs.ClaimsHierarchy SET Hierarchy = @Hierarchy;",
            new { Hierarchy = roots.ToJsonString() }
        );
        await connection.ExecuteAsync(
            "DELETE FROM dmscs.ResourceClaim WHERE ClaimName = @ClaimName;",
            new { ClaimName = ProjectionClaimName }
        );
    }

    /// <summary>
    /// Places a claim under another claim of the stored hierarchy and records its resource-claim
    /// metadata, as an operator's own claims document would.
    /// </summary>
    protected async Task NestClaimAsync(string parentClaimName, JsonNode claim, string resourceName)
    {
        JsonArray roots = Roots((await HierarchyAsync()).Hierarchy);
        JsonNode parent = AllClaims(roots).Single(node => ClaimNameOf(node) == parentClaimName);
        if (parent["claims"] is not JsonArray children)
        {
            children = new JsonArray();
            parent["claims"] = children;
        }
        children.Add(claim);

        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "UPDATE dmscs.ClaimsHierarchy SET Hierarchy = @Hierarchy;",
            new { Hierarchy = roots.ToJsonString() }
        );
        await connection.ExecuteAsync(
            "INSERT INTO dmscs.ResourceClaim (ResourceName, ClaimName) VALUES (@ResourceName, @ClaimName);",
            new { ResourceName = resourceName, ClaimName = ClaimNameOf(claim) }
        );
    }

    /// <summary>
    /// Records resource-claim metadata without placing the claim in the stored hierarchy.
    /// </summary>
    protected async Task InsertResourceClaimAsync(string resourceName, string claimName)
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO dmscs.ResourceClaim (ResourceName, ClaimName) VALUES (@ResourceName, @ClaimName);",
            new { ResourceName = resourceName, ClaimName = claimName }
        );
    }

    /// <summary>
    /// Runs the migration a second time by removing its journal entry, which is the only way DbUp
    /// re-executes a script that already ran.
    /// </summary>
    protected async Task ReplayProjectionClaimScriptAsync()
    {
        await using (SqlConnection connection = await OpenConnectionAsync())
        {
            int removed = await connection.ExecuteAsync(
                "DELETE FROM dbo.dmscs_SchemaVersions WHERE ScriptName = @ScriptName;",
                new { ScriptName = ProjectionClaimScriptName }
            );
            removed.Should().Be(1);
        }

        Database.DeployThrough(ProjectionClaimScript);
    }

    /// <summary>
    /// Runs the migration as a database deployed before its renumbering did: its own text, with the
    /// former name in the values it records, executed and journaled under the former name.
    /// </summary>
    protected async Task RunClaimScriptUnderItsFormerNameAsync()
    {
        string script;
        await using (
            Stream stream =
                typeof(ClaimSetRepository).Assembly.GetManifestResourceStream(ProjectionClaimScriptName)
                ?? throw new InvalidOperationException(
                    $"Embedded script {ProjectionClaimScriptName} not found."
                )
        )
        using (StreamReader reader = new(stream))
        {
            script = await reader.ReadToEndAsync();
        }

        string formerScript = script.Replace(
            "0039_Add_EducationOrganizationProjection_Claim",
            "0036_Add_EducationOrganizationProjection_Claim",
            StringComparison.Ordinal
        );
        formerScript.Should().NotBe(script, "the migration records its own name");

        await using SqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(formerScript);
        await connection.ExecuteAsync(
            "INSERT INTO dbo.dmscs_SchemaVersions (ScriptName, Applied) VALUES (@ScriptName, GETDATE());",
            new { ScriptName = FormerProjectionClaimScriptName }
        );
    }

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

    protected async Task<int> HierarchyRowCountAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dmscs.ClaimsHierarchy;");
    }

    protected async Task<List<int>> ClaimSetIdsAsync()
    {
        await using SqlConnection connection = await OpenConnectionAsync();
        return (await connection.QueryAsync<int>("SELECT Id FROM dmscs.ClaimSet ORDER BY Id;")).ToList();
    }

    protected async Task<int> ResourceClaimIdAsync(string claimName) =>
        (await ResourceClaimsAsync()).Single(row => row.ClaimName == claimName).Id;

    protected async Task<int> InsertClaimSetAsync(string claimSetName)
    {
        ClaimSetInsertResult result = await CreateClaimSetRepository()
            .InsertClaimSet(new ClaimSetInsertCommand { Name = claimSetName });
        return result.Should().BeOfType<ClaimSetInsertResult.Success>().Subject.Id;
    }

    protected async Task<ClaimSetResourceActionMutationResult> GrantReadAsync(
        int claimSetId,
        string claimName
    ) =>
        await CreateClaimSetRepository()
            .GrantResourceClaimActions(
                new ResourceClaimActionMutationCommand(
                    claimSetId,
                    await ResourceClaimIdAsync(claimName),
                    ["Read"]
                )
            );

    protected async Task<IReadOnlyList<ResourceClaim>> ExportedResourceClaimsAsync(int claimSetId)
    {
        ClaimSetExportResult result = await CreateClaimSetRepository().Export(claimSetId);
        return result
                .Should()
                .BeOfType<ClaimSetExportResult.Success>()
                .Subject.ClaimSetExportResponse.ResourceClaims
            ?? [];
    }

    protected async Task AssertReadCanBeGrantedAndRevokedAsync(string claimSetName)
    {
        int claimSetId = await InsertClaimSetAsync(claimSetName);

        (await GrantReadAsync(claimSetId, ProjectionClaimName))
            .Should()
            .BeOfType<ClaimSetResourceActionMutationResult.Success>();
        (await ExportedResourceClaimsAsync(claimSetId))
            .Should()
            .ContainSingle(resourceClaim => resourceClaim.ClaimName == ProjectionClaimName)
            .Which.Actions!.Where(action => action.Enabled)
            .Select(action => action.Name)
            .Should()
            .Equal("Read");

        (
            await CreateClaimSetRepository()
                .RevokeResourceClaimActions(claimSetId, await ResourceClaimIdAsync(ProjectionClaimName))
        )
            .Should()
            .BeOfType<ClaimSetResourceActionMutationResult.Success>();
        (await ExportedResourceClaimsAsync(claimSetId))
            .Should()
            .NotContain(resourceClaim => resourceClaim.ClaimName == ProjectionClaimName);
    }

    protected async Task AssertReplayChangesNothingAsync()
    {
        List<ResourceClaimRow> resourceClaimsBefore = await ResourceClaimsAsync();
        HierarchyRow hierarchyBefore = await HierarchyAsync();

        await ReplayProjectionClaimScriptAsync();

        (await ResourceClaimsAsync()).Should().Equal(resourceClaimsBefore);
        (await HierarchyAsync()).Should().Be(hierarchyBefore);
    }

    protected static JsonArray Roots(string hierarchyJson) => JsonNode.Parse(hierarchyJson)!.AsArray();

    protected static string? ClaimNameOf(JsonNode? claim) => claim?["name"]?.GetValue<string>();

    // CMS resolves claim names exactly and then with OrdinalIgnoreCase, so either spelling is the claim.
    protected static bool IsProjectionClaimName(string? claimName) =>
        string.Equals(claimName, ProjectionClaimName, StringComparison.OrdinalIgnoreCase);

    protected static IEnumerable<JsonNode> AllClaims(JsonArray claims)
    {
        foreach (JsonNode? claim in claims)
        {
            if (claim is null)
            {
                continue;
            }

            yield return claim;

            if (claim["claims"] is JsonArray children)
            {
                foreach (JsonNode child in AllClaims(children))
                {
                    yield return child;
                }
            }
        }
    }

    private ClaimsHierarchyRepository CreateClaimsHierarchyRepository() =>
        new(DatabaseOptions, NullLogger<ClaimsHierarchyRepository>.Instance, new TestAuditContext());

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        SqlConnection connection = new(Database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_catalog_provisioned_before_the_projection_claim
    : EducationOrganizationProjectionClaimTests
{
    private const string VendorClaimSetName = "DMS-1440 Upgrade Vendor";

    private List<ResourceClaimRow> _resourceClaimsBefore = [];
    private List<ResourceClaimRow> _resourceClaimsAfter = [];
    private HierarchyRow _hierarchyBefore = null!;
    private HierarchyRow _hierarchyAfter = null!;
    private List<int> _claimSetsBefore = [];
    private List<int> _claimSetsAfter = [];
    private int _vendorClaimSetId;

    [OneTimeSetUp]
    public async Task Setup()
    {
        DeployThroughPreviousScript();
        await LoadEmbeddedClaimsAsync();
        await RemoveProjectionClaimAsync();

        _vendorClaimSetId = await InsertClaimSetAsync(VendorClaimSetName);
        (await GrantReadAsync(_vendorClaimSetId, StudentClaimName))
            .Should()
            .BeOfType<ClaimSetResourceActionMutationResult.Success>();

        _resourceClaimsBefore = await ResourceClaimsAsync();
        _hierarchyBefore = await HierarchyAsync();
        _claimSetsBefore = await ClaimSetIdsAsync();

        Database.DeployThrough(ProjectionClaimScript);

        _resourceClaimsAfter = await ResourceClaimsAsync();
        _hierarchyAfter = await HierarchyAsync();
        _claimSetsAfter = await ClaimSetIdsAsync();
    }

    [Test]
    public void It_starts_from_a_hierarchy_without_the_projection_claim()
    {
        AllClaims(Roots(_hierarchyBefore.Hierarchy))
            .Should()
            .NotContain(claim => ClaimNameOf(claim) == ProjectionClaimName);
        _resourceClaimsBefore.Should().NotContain(row => row.ClaimName == ProjectionClaimName);
    }

    [Test]
    public void It_adds_one_resource_claim_row_for_the_projection_claim()
    {
        _resourceClaimsAfter
            .Except(_resourceClaimsBefore)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<ResourceClaimRow>(row =>
                row.ResourceName == "educationOrganizationProjection" && row.ClaimName == ProjectionClaimName
            );
    }

    [Test]
    public void It_leaves_every_existing_resource_claim_row_unchanged()
    {
        _resourceClaimsAfter
            .Where(row => row.ClaimName != ProjectionClaimName)
            .Should()
            .Equal(_resourceClaimsBefore);
    }

    [Test]
    public void It_appends_the_projection_claim_without_grants()
    {
        JsonArray rootsAfter = Roots(_hierarchyAfter.Hierarchy);

        rootsAfter.Count.Should().Be(Roots(_hierarchyBefore.Hierarchy).Count + 1);
        JsonNode
            .DeepEquals(rootsAfter[^1], ExpectedProjectionClaim)
            .Should()
            .BeTrue(rootsAfter[^1]!.ToJsonString());
    }

    [Test]
    public void It_leaves_every_existing_claim_and_grant_unchanged()
    {
        JsonArray rootsBefore = Roots(_hierarchyBefore.Hierarchy);
        JsonArray rootsAfter = Roots(_hierarchyAfter.Hierarchy);

        for (int index = 0; index < rootsBefore.Count; index++)
        {
            JsonNode
                .DeepEquals(rootsAfter[index], rootsBefore[index])
                .Should()
                .BeTrue(ClaimNameOf(rootsBefore[index]));
        }
    }

    [Test]
    public async Task It_keeps_the_grants_made_before_the_upgrade()
    {
        (await ExportedResourceClaimsAsync(_vendorClaimSetId))
            .Should()
            .ContainSingle(resourceClaim => resourceClaim.ClaimName == StudentClaimName)
            .Which.Actions!.Where(action => action.Enabled)
            .Select(action => action.Name)
            .Should()
            .Equal("Read");
    }

    [Test]
    public void It_leaves_the_claim_sets_unchanged()
    {
        _claimSetsAfter.Should().Equal(_claimSetsBefore);
    }

    [Test]
    public void It_advances_the_hierarchy_concurrency_token()
    {
        _hierarchyAfter.LastModifiedDate.Should().BeAfter(_hierarchyBefore.LastModifiedDate);
    }

    [Test]
    public async Task It_journals_the_script_once()
    {
        (await Database.JournalAsync()).Should().ContainSingle(script => script == ProjectionClaimScriptName);
    }

    [Test]
    public async Task It_allows_Read_on_the_projection_claim_to_be_granted_and_revoked()
    {
        await AssertReadCanBeGrantedAndRevokedAsync("DMS-1440 Upgraded Projection Reader");
    }

    [Test]
    public async Task It_changes_nothing_when_the_script_runs_again()
    {
        await AssertReplayChangesNothingAsync();
    }
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_fresh_catalog_with_the_projection_claim : EducationOrganizationProjectionClaimTests
{
    private int _hierarchyRowsBeforeClaimsLoad;
    private List<ResourceClaimRow> _resourceClaims = [];
    private HierarchyRow _hierarchy = null!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Database.DeployThrough(ProjectionClaimScript);
        _hierarchyRowsBeforeClaimsLoad = await HierarchyRowCountAsync();

        await LoadEmbeddedClaimsAsync();

        _resourceClaims = await ResourceClaimsAsync();
        _hierarchy = await HierarchyAsync();
    }

    [Test]
    public void It_leaves_a_catalog_without_loaded_claims_for_the_claims_loader()
    {
        _hierarchyRowsBeforeClaimsLoad.Should().Be(0);
    }

    [Test]
    public void It_has_one_resource_claim_row_for_the_projection_claim()
    {
        _resourceClaims
            .Should()
            .ContainSingle(row => row.ClaimName == ProjectionClaimName)
            .Which.ResourceName.Should()
            .Be("educationOrganizationProjection");
    }

    [Test]
    public void It_stores_the_projection_claim_once_in_the_shape_the_migration_appends()
    {
        AllClaims(Roots(_hierarchy.Hierarchy))
            .Should()
            .ContainSingle(claim => ClaimNameOf(claim) == ProjectionClaimName)
            .Which.Should()
            .Match<JsonNode>(claim => JsonNode.DeepEquals(claim, ExpectedProjectionClaim));
    }

    [Test]
    public async Task It_allows_Read_on_the_projection_claim_to_be_granted_and_revoked()
    {
        await AssertReadCanBeGrantedAndRevokedAsync("DMS-1440 Fresh Projection Reader");
    }

    [Test]
    public async Task It_changes_nothing_when_the_script_runs_again()
    {
        await AssertReplayChangesNothingAsync();
    }
}

/// <summary>
/// Upgrades a catalog whose claims document already provides the projection claim nested under
/// another claim, with its own authorization and a grant, as an operator's customized claims would.
/// </summary>
public abstract class Given_a_catalog_that_already_has_the_projection_claim
    : EducationOrganizationProjectionClaimTests
{
    private const string ParentClaimName = "http://ed-fi.org/identity/claims/services/identity";
    private const string CustomClaimSetName = "DMS-1440 Existing Claim Reader";

    private List<ResourceClaimRow> _resourceClaimsBefore = [];
    private List<ResourceClaimRow> _resourceClaimsAfter = [];
    private HierarchyRow _hierarchyBefore = null!;
    private HierarchyRow _hierarchyAfter = null!;
    private int _customClaimSetId;

    protected abstract string ExistingClaimName { get; }

    [OneTimeSetUp]
    public async Task Setup()
    {
        DeployThroughPreviousScript();
        await LoadEmbeddedClaimsAsync();
        await RemoveProjectionClaimAsync();

        _customClaimSetId = await InsertClaimSetAsync(CustomClaimSetName);
        await NestClaimAsync(ParentClaimName, ExistingClaim(), ExistingClaimName.Split('/')[^1]);

        _resourceClaimsBefore = await ResourceClaimsAsync();
        _hierarchyBefore = await HierarchyAsync();

        Database.DeployThrough(ProjectionClaimScript);

        _resourceClaimsAfter = await ResourceClaimsAsync();
        _hierarchyAfter = await HierarchyAsync();
    }

    [Test]
    public void It_starts_with_the_existing_claim_nested_below_the_root()
    {
        JsonArray roots = Roots(_hierarchyBefore.Hierarchy);

        roots.Should().NotContain(root => IsProjectionClaimName(ClaimNameOf(root)));
        AllClaims(roots).Select(ClaimNameOf).Where(IsProjectionClaimName).Should().Equal(ExistingClaimName);
    }

    [Test]
    public void It_adds_no_resource_claim_row()
    {
        _resourceClaimsAfter.Should().Equal(_resourceClaimsBefore);
    }

    [Test]
    public void It_leaves_the_stored_hierarchy_and_its_audit_values_unchanged()
    {
        _hierarchyAfter.Should().Be(_hierarchyBefore);
    }

    [Test]
    public void It_keeps_the_existing_claim_as_the_only_projection_claim()
    {
        AllClaims(Roots(_hierarchyAfter.Hierarchy))
            .Select(ClaimNameOf)
            .Where(IsProjectionClaimName)
            .Should()
            .Equal(ExistingClaimName);
    }

    [Test]
    public async Task It_keeps_the_grant_on_the_existing_claim()
    {
        (await ExportedResourceClaimsAsync(_customClaimSetId))
            .Should()
            .ContainSingle(resourceClaim => resourceClaim.ClaimName == ExistingClaimName)
            .Which.Actions!.Where(action => action.Enabled)
            .Select(action => action.Name)
            .Should()
            .Equal("Read");
    }

    [Test]
    public async Task It_changes_nothing_when_the_script_runs_again()
    {
        await AssertReplayChangesNothingAsync();
    }

    private JsonNode ExistingClaim() =>
        JsonNode.Parse(
            $$"""
            {
              "name": "{{ExistingClaimName}}",
              "defaultAuthorization": {
                "actions": [
                  { "name": "Read", "authorizationStrategies": [{ "name": "RelationshipsWithEdOrgsOnly" }] }
                ]
              },
              "claimSets": [{ "name": "{{CustomClaimSetName}}", "actions": [{ "name": "Read" }] }],
              "claims": []
            }
            """
        )!;
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_catalog_with_the_projection_claim_nested_under_another_claim
    : Given_a_catalog_that_already_has_the_projection_claim
{
    protected override string ExistingClaimName => ProjectionClaimName;
}

[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_catalog_with_a_nested_case_variant_of_the_projection_claim
    : Given_a_catalog_that_already_has_the_projection_claim
{
    protected override string ExistingClaimName =>
        "http://ed-fi.org/identity/claims/services/educationorganizationprojection";
}

/// <summary>
/// Upgrades a catalog whose resource-claim metadata already has a case variant of the projection
/// claim while its stored hierarchy has no such claim. The migration keeps the metadata row and
/// appends a hierarchy claim with exactly that spelling, so grant changes keep resolving claims.
/// </summary>
[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_catalog_with_a_case_variant_metadata_row_and_no_projection_hierarchy_claim
    : EducationOrganizationProjectionClaimTests
{
    private const string RetainedClaimName =
        "http://ed-fi.org/identity/claims/services/EducationOrganizationProjection";
    private const string RetainedResourceName = "EducationOrganizationProjection";
    private const string VendorClaimSetName = "DMS-1440 Retained Spelling Vendor";

    private List<ResourceClaimRow> _resourceClaimsBefore = [];
    private List<ResourceClaimRow> _resourceClaimsAfter = [];
    private HierarchyRow _hierarchyBefore = null!;
    private HierarchyRow _hierarchyAfter = null!;
    private int _vendorClaimSetId;

    [OneTimeSetUp]
    public async Task Setup()
    {
        DeployThroughPreviousScript();
        await LoadEmbeddedClaimsAsync();
        await RemoveProjectionClaimAsync();
        await InsertResourceClaimAsync(RetainedResourceName, RetainedClaimName);

        _vendorClaimSetId = await InsertClaimSetAsync(VendorClaimSetName);
        (await GrantReadAsync(_vendorClaimSetId, StudentClaimName))
            .Should()
            .BeOfType<ClaimSetResourceActionMutationResult.Success>();

        _resourceClaimsBefore = await ResourceClaimsAsync();
        _hierarchyBefore = await HierarchyAsync();

        Database.DeployThrough(ProjectionClaimScript);

        _resourceClaimsAfter = await ResourceClaimsAsync();
        _hierarchyAfter = await HierarchyAsync();
    }

    [Test]
    public void It_starts_with_the_metadata_row_and_no_projection_hierarchy_claim()
    {
        _resourceClaimsBefore
            .Where(row => IsProjectionClaimName(row.ClaimName))
            .Select(row => row.ClaimName)
            .Should()
            .Equal(RetainedClaimName);
        AllClaims(Roots(_hierarchyBefore.Hierarchy))
            .Should()
            .NotContain(claim => IsProjectionClaimName(ClaimNameOf(claim)));
    }

    [Test]
    public void It_keeps_the_metadata_row_identity_and_spelling()
    {
        _resourceClaimsAfter.Should().Equal(_resourceClaimsBefore);
    }

    [Test]
    public void It_appends_a_claim_with_the_retained_spelling_and_no_grants()
    {
        JsonArray rootsAfter = Roots(_hierarchyAfter.Hierarchy);
        JsonNode expected = ExpectedProjectionClaim;
        expected["name"] = RetainedClaimName;

        rootsAfter.Count.Should().Be(Roots(_hierarchyBefore.Hierarchy).Count + 1);
        JsonNode.DeepEquals(rootsAfter[^1], expected).Should().BeTrue(rootsAfter[^1]!.ToJsonString());
        AllClaims(rootsAfter)
            .Select(ClaimNameOf)
            .Where(IsProjectionClaimName)
            .Should()
            .Equal(RetainedClaimName);
    }

    [Test]
    public void It_leaves_every_existing_claim_and_grant_unchanged()
    {
        JsonArray rootsBefore = Roots(_hierarchyBefore.Hierarchy);
        JsonArray rootsAfter = Roots(_hierarchyAfter.Hierarchy);

        for (int index = 0; index < rootsBefore.Count; index++)
        {
            JsonNode
                .DeepEquals(rootsAfter[index], rootsBefore[index])
                .Should()
                .BeTrue(ClaimNameOf(rootsBefore[index]));
        }
    }

    [Test]
    public void It_advances_the_hierarchy_concurrency_token()
    {
        _hierarchyAfter.LastModifiedDate.Should().BeAfter(_hierarchyBefore.LastModifiedDate);
    }

    [Test]
    public async Task It_allows_an_unrelated_grant_to_change_and_keeps_the_existing_one()
    {
        int claimSetId = await InsertClaimSetAsync("DMS-1440 Retained Spelling Student Reader");

        (await GrantReadAsync(claimSetId, StudentClaimName))
            .Should()
            .BeOfType<ClaimSetResourceActionMutationResult.Success>();
        (await ExportedResourceClaimsAsync(claimSetId))
            .Should()
            .ContainSingle(resourceClaim => resourceClaim.ClaimName == StudentClaimName);
        (await ExportedResourceClaimsAsync(_vendorClaimSetId))
            .Should()
            .ContainSingle(resourceClaim => resourceClaim.ClaimName == StudentClaimName)
            .Which.Actions!.Where(action => action.Enabled)
            .Select(action => action.Name)
            .Should()
            .Equal("Read");
    }

    [Test]
    public async Task It_changes_nothing_when_the_script_runs_again()
    {
        await AssertReplayChangesNothingAsync();
    }
}

/// <summary>
/// Upgrades a catalog whose journal records the migration under its former number, 0036, which the
/// migration had before the ApiClient identifier uniqueness migration took that number. The
/// ApiClient migrations 0036 to 0038 then run, and the renumbered migration finds the claim it added
/// earlier and changes nothing.
/// </summary>
[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_catalog_that_ran_the_migration_under_its_former_number
    : EducationOrganizationProjectionClaimTests
{
    private const string GrantingClaimSetName = "Former Number Projection Reader";

    private string[] _journalBefore = [];
    private string[] _journalAfter = [];
    private List<ResourceClaimRow> _resourceClaimsBefore = [];
    private List<ResourceClaimRow> _resourceClaimsAfter = [];
    private HierarchyRow _hierarchyBefore = null!;
    private HierarchyRow _hierarchyAfter = null!;
    private int _grantingClaimSetId;

    [OneTimeSetUp]
    public async Task Setup()
    {
        Database.DeployThrough(JobUpgradeTestDatabase.NameUniquenessScript);
        await LoadEmbeddedClaimsAsync();
        await RemoveProjectionClaimAsync();
        await RunClaimScriptUnderItsFormerNameAsync();

        _grantingClaimSetId = await InsertClaimSetAsync(GrantingClaimSetName);
        (await GrantReadAsync(_grantingClaimSetId, ProjectionClaimName))
            .Should()
            .BeOfType<ClaimSetResourceActionMutationResult.Success>();
        (await GrantReadAsync(_grantingClaimSetId, StudentClaimName))
            .Should()
            .BeOfType<ClaimSetResourceActionMutationResult.Success>();

        _journalBefore = await Database.JournalAsync();
        _resourceClaimsBefore = await ResourceClaimsAsync();
        _hierarchyBefore = await HierarchyAsync();

        Database.DeployThrough(ProjectionClaimScript);

        _journalAfter = await Database.JournalAsync();
        _resourceClaimsAfter = await ResourceClaimsAsync();
        _hierarchyAfter = await HierarchyAsync();
    }

    [Test]
    public void It_starts_from_the_claim_the_former_migration_added()
    {
        _journalBefore.Should().EndWith(FormerProjectionClaimScriptName);
        _journalBefore
            .Should()
            .NotContain([JobUpgradeTestDatabase.IdentifierUniquenessScriptName, ProjectionClaimScriptName]);
        _resourceClaimsBefore.Where(row => IsProjectionClaimName(row.ClaimName)).Should().ContainSingle();
        AllClaims(Roots(_hierarchyBefore.Hierarchy))
            .Select(ClaimNameOf)
            .Where(IsProjectionClaimName)
            .Should()
            .Equal(ProjectionClaimName);
    }

    [Test]
    public void It_journals_the_ApiClient_migrations_and_the_renumbered_migration_after_the_former_one()
    {
        string[] expected =
        [
            .. _journalBefore,
            JobUpgradeTestDatabase.IdentifierUniquenessScriptName,
            JobUpgradeTestDatabase.NamespacePrefixWideningScriptName,
            JobUpgradeTestDatabase.ApiClientEducationOrganizationScriptName,
            // The DataStore name-key migration shares number 0039 and sorts first.
            "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0039_Add_DataStore_NameComparisonKey.sql",
            ProjectionClaimScriptName,
        ];

        _journalAfter.Should().Equal(expected);
    }

    [Test]
    public void It_adds_no_resource_claim_row()
    {
        _resourceClaimsAfter.Should().Equal(_resourceClaimsBefore);
    }

    [Test]
    public void It_keeps_the_claim_once_in_the_hierarchy()
    {
        AllClaims(Roots(_hierarchyAfter.Hierarchy))
            .Select(ClaimNameOf)
            .Where(IsProjectionClaimName)
            .Should()
            .Equal(ProjectionClaimName);
    }

    [Test]
    public void It_leaves_the_stored_hierarchy_and_its_audit_values_unchanged()
    {
        _hierarchyAfter.Should().Be(_hierarchyBefore);
    }

    [Test]
    public async Task It_keeps_the_existing_grants()
    {
        (await ExportedResourceClaimsAsync(_grantingClaimSetId))
            .Where(resourceClaim => resourceClaim.ClaimName is ProjectionClaimName or StudentClaimName)
            .Select(resourceClaim =>
                (
                    resourceClaim.ClaimName,
                    string.Join(
                        ",",
                        resourceClaim.Actions!.Where(action => action.Enabled).Select(action => action.Name)
                    )
                )
            )
            .Should()
            .BeEquivalentTo([(ProjectionClaimName, "Read"), (StudentClaimName, "Read")]);
    }

    [Test]
    public async Task It_applies_the_ApiClient_migrations()
    {
        (await Database.ConstraintAndIndexNamesAsync("ApiClient"))
            .Should()
            .Contain(["UX_ApiClient_ClientId", "UX_ApiClient_ClientUuid"]);
        (await Database.TableExistsAsync("ApiClientEducationOrganization")).Should().BeTrue();
    }
}
