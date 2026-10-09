// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Postgresql.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

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

    private const int PreviousScript = 35;
    protected const int ProjectionClaimScript = 36;
    protected const string ProjectionClaimScriptName =
        "EdFi.DmsConfigurationService.Backend.Postgresql.Deploy.Scripts.0036_Add_EducationOrganizationProjection_Claim.sql";

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

    private protected JobUpgradeTestDatabase Database { get; private set; } = null!;

    [OneTimeSetUp]
    public void CreateDatabase()
    {
        Database = new JobUpgradeTestDatabase();
    }

    [OneTimeTearDown]
    public async Task DropDatabase()
    {
        await Database.DropAsync();
    }

    protected void DeployThroughPreviousScript() => Database.DeployThrough(PreviousScript);

    protected IOptions<DatabaseOptions> DatabaseOptions =>
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = Database.ConnectionString,
                EncryptionKey = Configuration.DatabaseOptions.Value.EncryptionKey,
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

        await using NpgsqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            """UPDATE "dmscs"."ClaimsHierarchy" SET "Hierarchy" = @Hierarchy::jsonb;""",
            new { Hierarchy = roots.ToJsonString() }
        );
        await connection.ExecuteAsync(
            """DELETE FROM "dmscs"."ResourceClaim" WHERE "ClaimName" = @ClaimName;""",
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

        await using NpgsqlConnection connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(
            """UPDATE "dmscs"."ClaimsHierarchy" SET "Hierarchy" = @Hierarchy::jsonb;""",
            new { Hierarchy = roots.ToJsonString() }
        );
        await connection.ExecuteAsync(
            """INSERT INTO "dmscs"."ResourceClaim" ("ResourceName", "ClaimName") VALUES (@ResourceName, @ClaimName);""",
            new { ResourceName = resourceName, ClaimName = ClaimNameOf(claim) }
        );
    }

    /// <summary>
    /// Runs the migration a second time by removing its journal entry, which is the only way DbUp
    /// re-executes a script that already ran.
    /// </summary>
    protected async Task ReplayProjectionClaimScriptAsync()
    {
        await using (NpgsqlConnection connection = await OpenConnectionAsync())
        {
            int removed = await connection.ExecuteAsync(
                """DELETE FROM public."dmscs_SchemaVersions" WHERE scriptname = @ScriptName;""",
                new { ScriptName = ProjectionClaimScriptName }
            );
            removed.Should().Be(1);
        }

        Database.DeployThrough(ProjectionClaimScript);
    }

    protected async Task<List<ResourceClaimRow>> ResourceClaimsAsync()
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        return (
            await connection.QueryAsync<ResourceClaimRow>(
                """SELECT "Id", "ResourceName", "ClaimName" FROM "dmscs"."ResourceClaim" ORDER BY "Id";"""
            )
        ).ToList();
    }

    protected async Task<HierarchyRow> HierarchyAsync()
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        return await connection.QuerySingleAsync<HierarchyRow>(
            """
            SELECT "Hierarchy"::text AS "Hierarchy", "LastModifiedDate", "LastModifiedAt", "ModifiedBy"
            FROM "dmscs"."ClaimsHierarchy";
            """
        );
    }

    protected async Task<int> HierarchyRowCountAsync()
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            """SELECT COUNT(*) FROM "dmscs"."ClaimsHierarchy";"""
        );
    }

    protected async Task<List<int>> ClaimSetIdsAsync()
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync();
        return (
            await connection.QueryAsync<int>("""SELECT "Id" FROM "dmscs"."ClaimSet" ORDER BY "Id";""")
        ).ToList();
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

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        NpgsqlConnection connection = new(Database.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

[TestFixture]
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
public class Given_a_catalog_with_the_projection_claim_nested_under_another_claim
    : Given_a_catalog_that_already_has_the_projection_claim
{
    protected override string ExistingClaimName => ProjectionClaimName;
}

[TestFixture]
public class Given_a_catalog_with_a_nested_case_variant_of_the_projection_claim
    : Given_a_catalog_that_already_has_the_projection_claim
{
    protected override string ExistingClaimName =>
        "http://ed-fi.org/identity/claims/services/educationorganizationprojection";
}
