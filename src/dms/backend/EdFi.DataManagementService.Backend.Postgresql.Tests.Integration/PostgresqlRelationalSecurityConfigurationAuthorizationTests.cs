// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("SecurityConfiguration")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Backend_Security_Configuration_Authorization_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string ProjectEndpointName = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string ResourceName =
        RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName;
    private const string UnknownStrategyName = "SecurityConfigurationUnknownStrategy";
    private const string ResourceFullName = "Authz.AuthorizationRootChildResource";

    private static readonly IReadOnlyList<string> _unknownStrategy = [UnknownStrategyName];
    private static readonly QuerySchoolSeed _schoolSeed = new(
        new DocumentUuid(Guid.Parse("93939393-0000-0000-0000-000000000001")),
        (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId,
        "Unknown Strategy School"
    );
    private static readonly AuthorizationRootChildSeed _seededRootChild = new(
        new DocumentUuid(Guid.Parse("93939393-0000-0000-0000-000000000101")),
        101,
        "seeded-root-child",
        (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId,
        []
    );

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(
            RelationshipAuthorizationCrudTestSupport.FixtureRelativePath,
            strict: false,
            replaceReadTargetLookup: false
        );
        await _context.SeedSchoolDescriptorDataAsync();
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateSchoolAsync(_schoolSeed)
        );
        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(_seededRootChild)
        );
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, _schoolSeed.SchoolId);
        _context.ResetRecorder();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Test]
    public async Task It_returns_typed_security_configuration_for_query_unknown_strategy()
    {
        var result = await _context.QueryAsync(
            ProjectEndpointName,
            ResourceName,
            [ClaimEducationOrganizationId],
            _unknownStrategy,
            totalCount: false
        );

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        RelationshipAuthorizationBackendFailureAssertions.AssertUnknownStrategySecurityConfiguration(
            failure.Errors,
            failure.Diagnostics,
            UnknownStrategyName,
            ResourceFullName
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_typed_security_configuration_for_get_by_id_unknown_strategy()
    {
        var result = await _context.GetByIdAsync(
            ProjectEndpointName,
            ResourceName,
            _seededRootChild.DocumentUuid,
            [ClaimEducationOrganizationId],
            _unknownStrategy
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        RelationshipAuthorizationBackendFailureAssertions.AssertUnknownStrategySecurityConfiguration(
            failure.Errors,
            failure.Diagnostics,
            UnknownStrategyName,
            ResourceFullName
        );
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_typed_security_configuration_for_post_unknown_strategy_without_writing()
    {
        var newSeed = new AuthorizationRootChildSeed(
            new DocumentUuid(Guid.Parse("93939393-0000-0000-0000-000000000201")),
            201,
            "post-rejected-root-child",
            (int)RelationshipAuthorizationCrudTestSupport.AuthorizedSchoolId,
            []
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            newSeed,
            [ClaimEducationOrganizationId],
            _unknownStrategy
        );

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        RelationshipAuthorizationBackendFailureAssertions.AssertUnknownStrategySecurityConfiguration(
            failure.Errors,
            failure.Diagnostics,
            UnknownStrategyName,
            ResourceFullName
        );
        (await _context.CountDocumentRowsAsync(newSeed.DocumentUuid)).Should().Be(0);
        (await _context.CountResourceRootRowsAsync(ProjectEndpointName, ResourceName, newSeed.DocumentUuid))
            .Should()
            .Be(0);
    }
}
