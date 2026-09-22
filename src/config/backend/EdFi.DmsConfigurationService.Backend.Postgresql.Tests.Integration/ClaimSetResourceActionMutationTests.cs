// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Postgresql.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public abstract class ClaimSetMutationTestBase : DatabaseTest
{
    protected const string ClaimSetName = "DMS-853 Vendor";
    protected const string StudentClaimName = "http://ed-fi.org/identity/claims/ed-fi/student";
    protected const string SchoolClaimName = "http://ed-fi.org/identity/claims/ed-fi/school";
    protected const string NoFurtherAuthorizationRequired = "NoFurtherAuthorizationRequired";
    protected const string RelationshipsWithEdOrgsAndPeople = "RelationshipsWithEdOrgsAndPeople";

    protected IClaimSetRepository Repository { get; private set; } = null!;
    protected int StudentResourceClaimId { get; private set; }
    protected int SchoolResourceClaimId { get; private set; }

    [SetUp]
    public async Task Setup()
    {
        Repository = CreateRepository();
        await EnsureClaimsDataLoaded();

        StudentResourceClaimId = await GetResourceClaimId(StudentClaimName);
        SchoolResourceClaimId = await GetResourceClaimId(SchoolClaimName);
    }

    protected async Task<int> CreateVendorClaimSet()
    {
        var result = await Repository.InsertClaimSet(new ClaimSetInsertCommand { Name = ClaimSetName });
        return ((ClaimSetInsertResult.Success)result).Id;
    }

    protected async Task GrantRead(int claimSetId, int resourceClaimId)
    {
        var result = await Repository.GrantResourceClaimActions(
            new ResourceClaimActionMutationCommand(claimSetId, resourceClaimId, ["Read"])
        );

        result.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
    }

    protected async Task<ResourceClaim> ExportResourceClaim(int claimSetId, string claimName)
    {
        var result = await Repository.Export(claimSetId);
        var export = ((ClaimSetExportResult.Success)result).ClaimSetExportResponse;

        return export.ResourceClaims!.Single(resourceClaim => resourceClaim.ClaimName == claimName);
    }

    protected async Task<IReadOnlyList<string>> ExportEnabledActions(int claimSetId, string claimName)
    {
        ResourceClaim resourceClaim = await ExportResourceClaim(claimSetId, claimName);
        return resourceClaim.Actions!.Where(action => action.Enabled).Select(action => action.Name!).ToList();
    }

    private static IClaimSetRepository CreateRepository()
    {
        var tenantContextProvider = new TenantContextProvider();
        var claimsHierarchyRepository = new ClaimsHierarchyRepository(
            Configuration.DatabaseOptions,
            NullLogger<ClaimsHierarchyRepository>.Instance,
            new TestAuditContext()
        );

        return new ClaimSetRepository(
            Configuration.DatabaseOptions,
            NullLogger<ClaimSetRepository>.Instance,
            claimsHierarchyRepository,
            new EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.ClaimsHierarchyManager(),
            new TestAuditContext(),
            tenantContextProvider
        );
    }

    private async Task<int> GetResourceClaimId(string claimName)
    {
        return await Connection!.ExecuteScalarAsync<int>(
            "SELECT \"Id\" FROM \"dmscs\".\"ResourceClaim\" WHERE \"ClaimName\" = @ClaimName",
            new { ClaimName = claimName }
        );
    }

    private async Task EnsureClaimsDataLoaded()
    {
        var claimsHierarchyRepository = new ClaimsHierarchyRepository(
            Configuration.DatabaseOptions,
            NullLogger<ClaimsHierarchyRepository>.Instance,
            new TestAuditContext()
        );
        var claimsDataLoader = new EdFi.DmsConfigurationService.Backend.ClaimsDataLoader.ClaimsDataLoader(
            new ClaimsProvider(
                NullLogger<ClaimsProvider>.Instance,
                Options.Create(
                    new ClaimsOptions { ClaimsSource = ClaimsSource.Embedded, ClaimsDirectory = "" }
                ),
                new ClaimsValidator(NullLogger<ClaimsValidator>.Instance),
                new ClaimsFragmentComposer(NullLogger<ClaimsFragmentComposer>.Instance)
            ),
            Repository,
            claimsHierarchyRepository,
            new ClaimsTableValidator(
                Configuration.DatabaseOptions,
                NullLogger<ClaimsTableValidator>.Instance
            ),
            new ClaimsDocumentRepository(
                Configuration.DatabaseOptions,
                NullLogger<ClaimsDocumentRepository>.Instance
            ),
            NullLogger<EdFi.DmsConfigurationService.Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance,
            new ResourceClaimMetadataRepository(
                Configuration.DatabaseOptions,
                NullLogger<ResourceClaimMetadataRepository>.Instance
            )
        );

        ClaimsDataLoadResult result = await claimsDataLoader.LoadInitialClaimsAsync();
        result.Should().BeAssignableTo<ClaimsDataLoadResult.Success>();
    }
}

public class ClaimSetResourceActionMutationTests
{
    public class Given_replacing_actions_with_disabled_entries : ClaimSetMutationTestBase
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task It_rejects_invalid_disabled_actions_without_mutating_the_hierarchy(bool modify)
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);
            await GrantRead(claimSetId, SchoolResourceClaimId);
            const string hierarchySql = """
                SELECT "Hierarchy"::text FROM "dmscs"."ClaimsHierarchy"
                """;
            string before = await Connection!.QuerySingleAsync<string>(hierarchySql);
            var command = new ResourceClaimActionMutationCommand(claimSetId, StudentResourceClaimId, ["Create"])
            {
                SuppliedActionNames = ["Create", "NotAnAction"],
            };

            var result = modify
                ? await Repository.ModifyResourceClaimActions(command)
                : await Repository.GrantResourceClaimActions(command);

            (await Connection.QuerySingleAsync<string>(hierarchySql)).Should().Be(before);
            result.Should().Be(new ClaimSetResourceActionMutationResult.FailureInvalidAction("NotAnAction"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task It_persists_only_enabled_actions_when_disabled_names_are_valid(bool modify)
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);
            var command = new ResourceClaimActionMutationCommand(claimSetId, StudentResourceClaimId, ["create"])
            {
                SuppliedActionNames = ["create", "Read"],
            };

            var result = modify
                ? await Repository.ModifyResourceClaimActions(command)
                : await Repository.GrantResourceClaimActions(command);

            result.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            (await ExportEnabledActions(claimSetId, StudentClaimName)).Should().Equal("Create");
        }
    }

    public class Given_granting_resource_claim_actions : ClaimSetMutationTestBase
    {
        [Test]
        public async Task It_replaces_only_the_target_association_actions()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, SchoolResourceClaimId);

            var result = await Repository.GrantResourceClaimActions(
                new ResourceClaimActionMutationCommand(claimSetId, StudentResourceClaimId, ["Read", "Create"])
            );

            result.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            (await ExportEnabledActions(claimSetId, StudentClaimName))
                .Should()
                .BeEquivalentTo("Read", "Create");
            (await ExportEnabledActions(claimSetId, SchoolClaimName)).Should().Equal("Read");
        }
    }

    public class Given_modifying_resource_claim_actions : ClaimSetMutationTestBase
    {
        [Test]
        public async Task It_replaces_target_actions_and_preserves_an_unrelated_association()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);
            await GrantRead(claimSetId, SchoolResourceClaimId);

            var result = await Repository.ModifyResourceClaimActions(
                new ResourceClaimActionMutationCommand(claimSetId, StudentResourceClaimId, ["Create"])
            );

            result.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            (await ExportEnabledActions(claimSetId, StudentClaimName)).Should().Equal("Create");
            (await ExportEnabledActions(claimSetId, SchoolClaimName)).Should().Equal("Read");
        }

        [Test]
        public async Task It_requires_an_existing_target_association_without_mutating_other_associations()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, SchoolResourceClaimId);

            var result = await Repository.ModifyResourceClaimActions(
                new ResourceClaimActionMutationCommand(claimSetId, StudentResourceClaimId, ["Create"])
            );

            result.Should().Be(new ClaimSetResourceActionMutationResult.FailureInvalidAction("Create"));
            (await ExportEnabledActions(claimSetId, SchoolClaimName)).Should().Equal("Read");
            (await Repository.Export(claimSetId)).Should().BeOfType<ClaimSetExportResult.Success>();
            ((ClaimSetExportResult.Success)await Repository.Export(claimSetId))
                .ClaimSetExportResponse.ResourceClaims.Should()
                .NotContain(resourceClaim => resourceClaim.ClaimName == StudentClaimName);
        }
    }

    public class Given_revoking_resource_claim_actions : ClaimSetMutationTestBase
    {
        [Test]
        public async Task It_removes_only_the_target_association_and_nested_overrides()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);
            await GrantRead(claimSetId, SchoolResourceClaimId);
            var overrideResult = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    StudentResourceClaimId,
                    "Read",
                    [NoFurtherAuthorizationRequired],
                    []
                )
            );
            overrideResult.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .ContainSingle(overrideAction =>
                    overrideAction.ActionName == "Read"
                    && overrideAction.AuthorizationStrategies!.Single().AuthorizationStrategyName
                        == NoFurtherAuthorizationRequired
                );

            var result = await Repository.RevokeResourceClaimActions(claimSetId, StudentResourceClaimId);

            result.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            ((ClaimSetExportResult.Success)await Repository.Export(claimSetId))
                .ClaimSetExportResponse.ResourceClaims.Should()
                .NotContain(resourceClaim => resourceClaim.ClaimName == StudentClaimName);
            (await ExportEnabledActions(claimSetId, SchoolClaimName)).Should().Equal("Read");
        }
    }

    public class Given_overriding_authorization_strategies : ClaimSetMutationTestBase
    {
        [Test]
        public async Task It_accepts_names_only_and_persists_canonical_names()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);

            var result = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    StudentResourceClaimId,
                    "read",
                    [NoFurtherAuthorizationRequired.ToLowerInvariant()],
                    []
                )
            );

            result.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .ContainSingle(overrideAction =>
                    overrideAction.ActionName == "Read"
                    && overrideAction.AuthorizationStrategies!.Single().AuthorizationStrategyName
                        == NoFurtherAuthorizationRequired
                );
        }

        [Test]
        public async Task It_accepts_matching_ids_and_names()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);

            var result = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    StudentResourceClaimId,
                    "Read",
                    [NoFurtherAuthorizationRequired],
                    [1]
                )
            );

            result.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .ContainSingle();
        }

        [Test]
        public async Task It_rejects_disagreeing_ids_and_names_without_mutating_the_target_action()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);

            var result = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    StudentResourceClaimId,
                    "Read",
                    [NoFurtherAuthorizationRequired],
                    [2]
                )
            );

            result
                .Should()
                .BeOfType<ClaimSetResourceActionMutationResult.FailureAuthorizationStrategyMismatch>();
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .BeEmpty();
        }

        [Test]
        public async Task It_rejects_disabled_actions_without_mutating_the_target_action()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);

            var result = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    StudentResourceClaimId,
                    "Create",
                    [NoFurtherAuthorizationRequired],
                    []
                )
            );

            result.Should().BeOfType<ClaimSetResourceActionMutationResult.FailureTargetAssociationNotFound>();
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .BeEmpty();
        }

        [Test]
        public async Task It_rejects_invalid_actions_without_mutating_the_target_action()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);

            var result = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    StudentResourceClaimId,
                    "NotAnAction",
                    [NoFurtherAuthorizationRequired],
                    []
                )
            );

            result.Should().Be(new ClaimSetResourceActionMutationResult.FailureInvalidAction("NotAnAction"));
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .BeEmpty();
        }

        [Test]
        public async Task It_rejects_invalid_strategies_without_mutating_the_target_action()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);

            var result = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    StudentResourceClaimId,
                    "Read",
                    ["NotAStrategy"],
                    []
                )
            );

            result
                .Should()
                .Be(
                    new ClaimSetResourceActionMutationResult.FailureInvalidAuthorizationStrategy(
                        "NotAStrategy"
                    )
                );
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .BeEmpty();
        }
    }

    public class Given_resetting_authorization_strategies : ClaimSetMutationTestBase
    {
        [Test]
        public async Task It_preserves_unrelated_overrides()
        {
            int claimSetId = await CreateVendorClaimSet();
            await GrantRead(claimSetId, StudentResourceClaimId);
            await GrantRead(claimSetId, SchoolResourceClaimId);

            var studentOverrideResult = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    StudentResourceClaimId,
                    "Read",
                    [NoFurtherAuthorizationRequired],
                    []
                )
            );
            var schoolOverrideResult = await Repository.OverrideAuthorizationStrategy(
                new AuthorizationStrategyOverrideCommand(
                    claimSetId,
                    SchoolResourceClaimId,
                    "Read",
                    [RelationshipsWithEdOrgsAndPeople],
                    []
                )
            );
            studentOverrideResult.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            schoolOverrideResult.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .ContainSingle(overrideAction =>
                    overrideAction.ActionName == "Read"
                    && overrideAction.AuthorizationStrategies!.Single().AuthorizationStrategyName
                        == NoFurtherAuthorizationRequired
                );

            var firstResult = await Repository.ResetAuthorizationStrategies(
                claimSetId,
                StudentResourceClaimId
            );
            var secondResult = await Repository.ResetAuthorizationStrategies(
                claimSetId,
                StudentResourceClaimId
            );

            firstResult.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            secondResult.Should().BeOfType<ClaimSetResourceActionMutationResult.Success>();
            (await ExportResourceClaim(claimSetId, StudentClaimName))
                .AuthorizationStrategyOverrides.Should()
                .BeEmpty();
            (await ExportResourceClaim(claimSetId, SchoolClaimName))
                .AuthorizationStrategyOverrides.Should()
                .ContainSingle(overrideAction =>
                    overrideAction.AuthorizationStrategies!.Single().AuthorizationStrategyName
                    == RelationshipsWithEdOrgsAndPeople
                );
        }
    }

    public class Given_mutating_a_reserved_claim_set : ClaimSetMutationTestBase
    {
        [Test]
        public async Task It_returns_system_reserved_without_mutating_the_hierarchy()
        {
            int claimSetId = await Connection!.ExecuteScalarAsync<int>(
                """
                INSERT INTO "dmscs"."ClaimSet" ("ClaimSetName", "IsSystemReserved")
                VALUES ('DMS-853 Reserved', true)
                RETURNING "Id";
                """
            );

            var result = await Repository.GrantResourceClaimActions(
                new ResourceClaimActionMutationCommand(claimSetId, StudentResourceClaimId, ["Read"])
            );

            result.Should().BeOfType<ClaimSetResourceActionMutationResult.FailureSystemReserved>();
            ((ClaimSetExportResult.Success)await Repository.Export(claimSetId))
                .ClaimSetExportResponse.ResourceClaims.Should()
                .BeEmpty();
        }
    }
}
