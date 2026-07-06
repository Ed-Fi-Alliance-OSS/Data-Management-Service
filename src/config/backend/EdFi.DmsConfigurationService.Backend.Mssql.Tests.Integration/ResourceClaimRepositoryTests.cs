// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

[TestFixture]
public class ResourceClaimRepositoryTests : DatabaseTestBase
{
    private static IResourceClaimRepository CreateRepository(TenantContext? tenantContext = null)
    {
        var tenantContextProvider = new TenantContextProvider();
        if (tenantContext is not null)
        {
            tenantContextProvider.Context = tenantContext;
        }

        var auditContext = new TestAuditContext();
        var hierarchyRepo = new ClaimsHierarchyRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ClaimsHierarchyRepository>.Instance,
            auditContext
        );
        var claimSetRepo = new ClaimSetRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ClaimSetRepository>.Instance,
            hierarchyRepo,
            new ClaimsHierarchyManager(),
            auditContext,
            tenantContextProvider
        );
        return new ResourceClaimRepository(
            MssqlTestConfiguration.DatabaseOptions,
            hierarchyRepo,
            claimSetRepo,
            NullLogger<ResourceClaimRepository>.Instance
        );
    }

    [TestFixture]
    public class Given_standard_hierarchy : ResourceClaimRepositoryTests
    {
        [SetUp]
        public async Task SetUp()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy();
        }

        [Test]
        public async Task It_returns_success_with_root_nodes()
        {
            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery());

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success.ResourceClaims.Should().NotBeEmpty();
        }

        [Test]
        public async Task It_returns_only_root_nodes()
        {
            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery());

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            // Root nodes have ParentId == 0
            success.ResourceClaims.Should().AllSatisfy(r => r.ParentId.Should().Be(0));
        }

        [Test]
        public async Task It_includes_extension_root_nodes()
        {
            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery());

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success.ResourceClaims.Should().Contain(r => r.Name == "homograph");
            success.ResourceClaims.Should().Contain(r => r.Name == "sample");
        }

        [Test]
        public async Task It_filters_by_name_case_insensitively()
        {
            var result = await CreateRepository()
                .GetResourceClaims(new ResourceClaimQuery { Name = "TYPES" });

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success
                .ResourceClaims.Should()
                .ContainSingle(r => r.Name.Equals("types", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public async Task It_filters_by_root_id()
        {
            var allResult = (ResourceClaimListResult.Success)
                await CreateRepository().GetResourceClaims(new ResourceClaimQuery());
            var firstId = allResult.ResourceClaims[0].Id;

            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery { Id = firstId });

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success.ResourceClaims.Should().ContainSingle(r => r.Id == firstId);
        }

        [Test]
        public async Task It_pages_with_offset_and_limit()
        {
            var result = await CreateRepository()
                .GetResourceClaims(new ResourceClaimQuery { Offset = 0, Limit = 1 });

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success.ResourceClaims.Should().HaveCount(1);
        }

        [Test]
        public async Task It_sorts_by_name_ascending_by_default()
        {
            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery());

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            var names = success.ResourceClaims.Select(r => r.Name).ToList();
            names.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        }

        [Test]
        public async Task It_sorts_by_id_descending()
        {
            var result = await CreateRepository()
                .GetResourceClaims(new ResourceClaimQuery { OrderBy = "id", Direction = "desc" });

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            var ids = success.ResourceClaims.Select(r => r.Id).ToList();
            ids.Should().BeInDescendingOrder();
        }

        [Test]
        public async Task It_returns_success_for_valid_id()
        {
            var allResult = (ResourceClaimListResult.Success)
                await CreateRepository().GetResourceClaims(new ResourceClaimQuery());
            var firstId = allResult.ResourceClaims[0].Id;

            var result = await CreateRepository().GetResourceClaim(firstId);

            result.Should().BeOfType<ResourceClaimGetResult.Success>();
        }

        [Test]
        public async Task It_returns_not_found_for_missing_id()
        {
            var result = await CreateRepository().GetResourceClaim(999999);

            result.Should().BeOfType<ResourceClaimGetResult.FailureNotFound>();
        }

        [Test]
        public async Task It_returns_actions_success()
        {
            var result = await CreateRepository().GetResourceClaimActions(new ResourceClaimActionQuery());

            result.Should().BeOfType<ResourceClaimActionListResult.Success>();
            var success = (ResourceClaimActionListResult.Success)result;
            success.ResourceClaimActions.Should().NotBeEmpty();
        }

        [Test]
        public async Task It_includes_action_names_in_result()
        {
            var result = await CreateRepository().GetResourceClaimActions(new ResourceClaimActionQuery());

            result.Should().BeOfType<ResourceClaimActionListResult.Success>();
            var success = (ResourceClaimActionListResult.Success)result;
            // The ClaimsHierarchyMetadata.json root claim has DefaultAuthorization with "Read" action
            success.ResourceClaimActions.Should().Contain(r => r.Actions.Any(a => a.Name == "Read"));
        }

        [Test]
        public async Task It_filters_actions_by_resource_name_case_insensitively()
        {
            var result = await CreateRepository()
                .GetResourceClaimActions(new ResourceClaimActionQuery { ResourceName = "TYPES" });

            result.Should().BeOfType<ResourceClaimActionListResult.Success>();
            var success = (ResourceClaimActionListResult.Success)result;
            success.ResourceClaimActions.Should().AllSatisfy(r => r.ResourceName.Should().Be("types"));
        }

        [Test]
        public async Task It_returns_auth_strategies_success()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.Success>();
            var success = (ResourceClaimActionAuthStrategyListResult.Success)result;
            success.ResourceClaimActionAuthStrategies.Should().NotBeEmpty();
        }

        [Test]
        public async Task It_includes_resolved_auth_strategy_ids()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.Success>();
            var success = (ResourceClaimActionAuthStrategyListResult.Success)result;
            // NoFurtherAuthorizationRequired has Id=1 in seeded data
            success
                .ResourceClaimActionAuthStrategies.Should()
                .Contain(r =>
                    r.AuthorizationStrategiesForActions.Any(a =>
                        a.AuthorizationStrategies.Any(s => s.AuthStrategyId == 1)
                    )
                );
        }

        [Test]
        public async Task It_returns_root_node_with_full_recursive_subtree()
        {
            // Root node Id=1 ('types') has child schoolYearType (Id=12) in ClaimsHierarchyMetadata.json
            var result = await CreateRepository().GetResourceClaim(1);

            result.Should().BeOfType<ResourceClaimGetResult.Success>();
            var success = (ResourceClaimGetResult.Success)result;
            success.ResourceClaim.Children.Should().NotBeEmpty();
            success.ResourceClaim.Children.Should().ContainSingle(c => c.Name == "schoolYearType");
        }

        [Test]
        public async Task It_returns_empty_list_when_filter_matches_nothing()
        {
            var result = await CreateRepository()
                .GetResourceClaims(new ResourceClaimQuery { Name = "nonexistentclaim" });

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success.ResourceClaims.Should().BeEmpty();
        }

        [Test]
        public async Task It_filter_by_id_applies_to_root_nodes_only()
        {
            // Id=12 is the child node 'schoolYearType'. Filtering GetResourceClaims by a child id
            // should return empty because the list filter applies to root nodes only.
            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery { Id = 12 });

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success.ResourceClaims.Should().BeEmpty();
        }

        [Test]
        public async Task It_filter_by_name_applies_to_root_nodes_only()
        {
            // schoolYearType is a child of types; filtering by child name should return empty
            var result = await CreateRepository()
                .GetResourceClaims(new ResourceClaimQuery { Name = "schoolYearType" });

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success.ResourceClaims.Should().BeEmpty();
        }

        [Test]
        public async Task It_filters_by_name_retains_full_recursive_subtree()
        {
            // types (Id=1) has child schoolYearType (Id=12) in ClaimsHierarchyMetadata.json
            var result = await CreateRepository()
                .GetResourceClaims(new ResourceClaimQuery { Name = "types" });

            result.Should().BeOfType<ResourceClaimListResult.Success>();
            var success = (ResourceClaimListResult.Success)result;
            success.ResourceClaims.Should().ContainSingle();
            var types = success.ResourceClaims[0];
            types.Children.Should().NotBeEmpty();
            types.Children.Should().ContainSingle(c => c.Name == "schoolYearType");
        }

        [Test]
        public async Task It_applies_both_id_and_name_filters_together()
        {
            var allResult = (ResourceClaimListResult.Success)
                await CreateRepository().GetResourceClaims(new ResourceClaimQuery());
            var first = allResult.ResourceClaims[0];

            // Matching id + matching name → returns the node (AND semantics)
            var match = (ResourceClaimListResult.Success)
                await CreateRepository()
                    .GetResourceClaims(new ResourceClaimQuery { Id = first.Id, Name = first.Name });
            match.ResourceClaims.Should().ContainSingle(r => r.Id == first.Id);

            // Matching id + non-matching name → returns empty (AND semantics)
            var noMatch = (ResourceClaimListResult.Success)
                await CreateRepository()
                    .GetResourceClaims(new ResourceClaimQuery { Id = first.Id, Name = "definitelynotaname" });
            noMatch.ResourceClaims.Should().BeEmpty();
        }

        [Test]
        public async Task It_sorts_actions_by_resource_claim_id_ascending_by_default()
        {
            var result = await CreateRepository().GetResourceClaimActions(new ResourceClaimActionQuery());

            result.Should().BeOfType<ResourceClaimActionListResult.Success>();
            var success = (ResourceClaimActionListResult.Success)result;
            var ids = success.ResourceClaimActions.Select(r => r.ResourceClaimId).ToList();
            ids.Should().BeInAscendingOrder();
        }

        [Test]
        public async Task It_sorts_auth_strategies_by_resource_claim_id_ascending_by_default()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.Success>();
            var success = (ResourceClaimActionAuthStrategyListResult.Success)result;
            var ids = success.ResourceClaimActionAuthStrategies.Select(r => r.ResourceClaimId).ToList();
            ids.Should().BeInAscendingOrder();
        }

        [Test]
        public async Task It_returns_empty_list_for_actions_when_filter_matches_nothing()
        {
            var result = await CreateRepository()
                .GetResourceClaimActions(
                    new ResourceClaimActionQuery { ResourceName = "nonexistentresource" }
                );

            result.Should().BeOfType<ResourceClaimActionListResult.Success>();
            var success = (ResourceClaimActionListResult.Success)result;
            success.ResourceClaimActions.Should().BeEmpty();
        }

        [Test]
        public async Task It_returns_empty_list_for_auth_strategies_when_filter_matches_nothing()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(
                    new ResourceClaimActionAuthStrategyQuery { ResourceName = "nonexistentresource" }
                );

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.Success>();
            var success = (ResourceClaimActionAuthStrategyListResult.Success)result;
            success.ResourceClaimActionAuthStrategies.Should().BeEmpty();
        }

        [Test]
        public async Task It_returns_child_node_with_correct_parent_fields()
        {
            // schoolYearType (Id=12) is a child of types (Id=1) in ClaimsHierarchyMetadata.json
            var result = await CreateRepository().GetResourceClaim(12);

            result.Should().BeOfType<ResourceClaimGetResult.Success>();
            var success = (ResourceClaimGetResult.Success)result;
            success.ResourceClaim.ParentId.Should().Be(1);
            success.ResourceClaim.ParentName.Should().Be("types");
        }
    }

    [TestFixture]
    public class Given_hierarchy_with_missing_resource_claim_metadata : ResourceClaimRepositoryTests
    {
        [SetUp]
        public async Task SetUp()
        {
            // Insert a custom hierarchy that has a URI not present in dmscs.ResourceClaim
            await using var conn = new SqlConnection(MssqlTestConfiguration.DatabaseConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM dmscs.ClaimsHierarchy");
            const string OrphanJson = """
                [
                  {
                    "name": "http://ed-fi.org/identity/claims/fake/doesNotExistInResourceClaimTable",
                    "defaultAuthorization": {
                      "actions": [
                        {
                          "name": "Read",
                          "authorizationStrategies": [
                            { "name": "NoFurtherAuthorizationRequired" }
                          ]
                        }
                      ]
                    },
                    "claimSets": [],
                    "claims": []
                  }
                ]
                """;
            await conn.ExecuteAsync(
                "INSERT INTO dmscs.ClaimsHierarchy(hierarchy) VALUES (@Hierarchy)",
                new { Hierarchy = OrphanJson }
            );
        }

        [Test]
        public async Task It_returns_projection_integrity_failure_for_list()
        {
            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery());

            result.Should().BeOfType<ResourceClaimListResult.FailureProjectionIntegrity>();
        }

        [Test]
        public async Task It_returns_projection_integrity_failure_for_actions()
        {
            var result = await CreateRepository().GetResourceClaimActions(new ResourceClaimActionQuery());

            result.Should().BeOfType<ResourceClaimActionListResult.FailureProjectionIntegrity>();
        }

        [Test]
        public async Task It_returns_projection_integrity_failure_for_auth_strategies()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity>();
        }

        [Test]
        public async Task It_returns_projection_integrity_failure_for_get_by_id()
        {
            var result = await CreateRepository().GetResourceClaim(1);

            result.Should().BeOfType<ResourceClaimGetResult.FailureProjectionIntegrity>();
        }
    }

    [TestFixture]
    public class Given_hierarchy_with_unknown_action_name : ResourceClaimRepositoryTests
    {
        [SetUp]
        public async Task SetUp()
        {
            // 'types' (Id=1) IS in dmscs.ResourceClaim but action "UnknownAction" is not in GetActions()
            await using var conn = new SqlConnection(MssqlTestConfiguration.DatabaseConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM dmscs.ClaimsHierarchy");
            const string BadActionJson = """
                [
                  {
                    "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
                    "defaultAuthorization": {
                      "actions": [
                        {
                          "name": "UnknownAction",
                          "authorizationStrategies": [
                            { "name": "NoFurtherAuthorizationRequired" }
                          ]
                        }
                      ]
                    },
                    "claimSets": [],
                    "claims": []
                  }
                ]
                """;
            await conn.ExecuteAsync(
                "INSERT INTO dmscs.ClaimsHierarchy(hierarchy) VALUES (@Hierarchy)",
                new { Hierarchy = BadActionJson }
            );
        }

        [Test]
        public async Task It_returns_projection_integrity_failure_for_actions()
        {
            var result = await CreateRepository().GetResourceClaimActions(new ResourceClaimActionQuery());

            result.Should().BeOfType<ResourceClaimActionListResult.FailureProjectionIntegrity>();
        }

        [Test]
        public async Task It_returns_projection_integrity_failure_for_auth_strategies()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity>();
        }
    }

    [TestFixture]
    public class Given_hierarchy_with_unknown_auth_strategy_name : ResourceClaimRepositoryTests
    {
        [SetUp]
        public async Task SetUp()
        {
            await using var conn = new SqlConnection(MssqlTestConfiguration.DatabaseConnectionString);
            await conn.OpenAsync();
            await conn.ExecuteAsync("DELETE FROM dmscs.ClaimsHierarchy");
            const string BadStrategyJson = """
                [
                  {
                    "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
                    "defaultAuthorization": {
                      "actions": [
                        {
                          "name": "Read",
                          "authorizationStrategies": [
                            { "name": "FakeStrategyThatDoesNotExist" }
                          ]
                        }
                      ]
                    },
                    "claimSets": [],
                    "claims": []
                  }
                ]
                """;
            await conn.ExecuteAsync(
                "INSERT INTO dmscs.ClaimsHierarchy(hierarchy) VALUES (@Hierarchy)",
                new { Hierarchy = BadStrategyJson }
            );
        }

        [Test]
        public async Task It_returns_projection_integrity_failure_for_auth_strategies()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity>();
        }
    }

    [TestFixture]
    public class Given_empty_hierarchy : ResourceClaimRepositoryTests
    {
        [SetUp]
        public async Task SetUp()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy(clearOnly: true);
        }

        [Test]
        public async Task It_returns_hierarchy_not_found_for_list()
        {
            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery());

            result.Should().BeOfType<ResourceClaimListResult.FailureHierarchyNotFound>();
        }

        [Test]
        public async Task It_returns_hierarchy_not_found_for_get_by_id()
        {
            var result = await CreateRepository().GetResourceClaim(1);

            result.Should().BeOfType<ResourceClaimGetResult.FailureHierarchyNotFound>();
        }

        [Test]
        public async Task It_returns_hierarchy_not_found_for_actions()
        {
            var result = await CreateRepository().GetResourceClaimActions(new ResourceClaimActionQuery());

            result.Should().BeOfType<ResourceClaimActionListResult.FailureHierarchyNotFound>();
        }

        [Test]
        public async Task It_returns_hierarchy_not_found_for_auth_strategies()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.FailureHierarchyNotFound>();
        }
    }

    [TestFixture]
    public class Given_multitenant_context : ResourceClaimRepositoryTests
    {
        [SetUp]
        public async Task SetUp()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy();
        }

        [Test]
        public async Task It_succeeds_regardless_of_tenant_id()
        {
            // ResourceClaim table has TenantId=NULL (global). The repository must NOT filter by TenantId
            // when loading ResourceClaim metadata. Multitenant context should not break projection.
            var multitenantContext = new TenantContext.Multitenant(999, "test-tenant");
            var result = await CreateRepository(multitenantContext)
                .GetResourceClaims(new ResourceClaimQuery());

            result.Should().BeOfType<ResourceClaimListResult.Success>();
        }

        [Test]
        public async Task It_fails_for_actions_when_tenant_scoped_auth_strategy_lookup_has_no_matching_rows()
        {
            // ResourceClaim metadata (TenantId IS NULL) remains visible, but authorization strategies
            // still follow the tenant-scoped IClaimSetRepository behavior. With no strategies for this
            // tenant, DefaultAuthorization references cannot resolve. Per story line 104, endpoints
            // must fail closed when any referenced auth strategy cannot be resolved, even if the
            // endpoint doesn't return auth strategies in its response.
            var multitenantContext = new TenantContext.Multitenant(999, "test-tenant");
            var result = await CreateRepository(multitenantContext)
                .GetResourceClaimActions(new ResourceClaimActionQuery());

            result.Should().BeOfType<ResourceClaimActionListResult.FailureProjectionIntegrity>();
        }

        [Test]
        public async Task It_fails_for_auth_strategies_when_tenant_scoped_lookup_has_no_matching_rows()
        {
            // ResourceClaim metadata (TenantId IS NULL) remains visible, but authorization strategies
            // still follow the tenant-scoped IClaimSetRepository behavior. With no strategies for this
            // tenant, DefaultAuthorization references cannot resolve and the projection must fail closed.
            var multitenantContext = new TenantContext.Multitenant(999, "test-tenant");
            var result = await CreateRepository(multitenantContext)
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.FailureProjectionIntegrity>();
        }
    }

    [TestFixture]
    public class Given_multiple_hierarchies : ResourceClaimRepositoryTests
    {
        [SetUp]
        public async Task SetUp()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy();

            // Insert a second hierarchy row to trigger FailureMultipleHierarchiesFound
            await using var connection = new SqlConnection(MssqlTestConfiguration.DatabaseConnectionString);
            await connection.ExecuteAsync(
                "INSERT INTO dmscs.ClaimsHierarchy (Hierarchy) VALUES (@hierarchy)",
                new { hierarchy = """[{"name":"http://duplicate.claim","claimSets":[]}]""" }
            );
        }

        [Test]
        public async Task It_returns_failure_unknown_for_resource_claims()
        {
            var result = await CreateRepository().GetResourceClaims(new ResourceClaimQuery());

            result.Should().BeOfType<ResourceClaimListResult.FailureUnknown>();
        }

        [Test]
        public async Task It_returns_failure_unknown_for_resource_claim_actions()
        {
            var result = await CreateRepository().GetResourceClaimActions(new ResourceClaimActionQuery());

            result.Should().BeOfType<ResourceClaimActionListResult.FailureUnknown>();
        }

        [Test]
        public async Task It_returns_failure_unknown_for_resource_claim_action_auth_strategies()
        {
            var result = await CreateRepository()
                .GetResourceClaimActionAuthStrategies(new ResourceClaimActionAuthStrategyQuery());

            result.Should().BeOfType<ResourceClaimActionAuthStrategyListResult.FailureUnknown>();
        }
    }
}
