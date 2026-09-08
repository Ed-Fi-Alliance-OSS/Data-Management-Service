// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Integration coverage for the NamespaceBased fail-closed 500 "no usable
/// root-table Namespace column" preflight terminal. The synthetic
/// <c>authorization-query</c> fixture's <c>AuthorizationRootChildResource</c>
/// has an empty <c>Namespace</c> securable element list, so every CRUD route
/// must short-circuit with the security-configuration message from
/// <c>NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn</c>
/// before any database read or write is issued. The shape mirrors the
/// relationship-strategy integration tests in this project so the harness
/// (write-session recorder, row-count assertions, fixture lifecycle) is reused.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[Category("RelationalNamespace")]
public class Given_A_Postgresql_Relational_Namespace_Authorization_With_A_Synthetic_Fixture
{
    private const long ClaimEducationOrganizationId =
        RelationshipAuthorizationCrudTestSupport.ClaimEducationOrganizationId;
    private const string ProjectEndpointName = RelationshipAuthorizationCrudTestSupport.ProjectEndpointName;
    private const string RootChildResourceName =
        RelationshipAuthorizationCrudTestSupport.RootAndChildEdOrgResourceName;
    private const string NoUsableRootColumnMarker =
        "no Namespace securable element resolves to a root table column";

    private static readonly IReadOnlyList<string> _namespaceBasedStrategy =
    [
        AuthorizationStrategyNameConstants.NamespaceBased,
    ];

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("d1d1d1d1-0000-0000-0000-000000000001")), 100, "North School"),
        new(
            new DocumentUuid(Guid.Parse("d1d1d1d1-0000-0000-0000-000000000002")),
            (int)ClaimEducationOrganizationId,
            "Claim School"
        ),
    ];

    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("d2d2d2d2-0000-0000-0000-000000000001")), 100, "P1"),
    ];

    private static readonly AuthorizationRootChildSeed _seededRootChild = new(
        new DocumentUuid(Guid.Parse("d3d3d3d3-0000-0000-0000-000000000001")),
        1,
        "preexisting-row",
        100,
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
    }

    [SetUp]
    public async Task SetUp()
    {
        await _context.Database.ResetAsync();
        await _context.SeedSchoolDescriptorDataAsync();

        foreach (var schoolSeed in _schoolSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateSchoolAsync(schoolSeed)
            );
        }

        foreach (var classPeriodSeed in _classPeriodSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateClassPeriodAsync(classPeriodSeed)
            );
        }

        RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
            await _context.CreateAuthorizationRootChildAsync(_seededRootChild)
        );

        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
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
    public async Task It_returns_500_security_configuration_for_query_when_no_usable_root_table_namespace_column()
    {
        var result = await _context.QueryAsync(
            ProjectEndpointName,
            RootChildResourceName,
            [ClaimEducationOrganizationId],
            _namespaceBasedStrategy
        );

        var failure = result.Should().BeOfType<QueryResult.QueryFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle().Which.Should().Contain(NoUsableRootColumnMarker);
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_500_security_configuration_for_get_by_id_when_no_usable_root_table_namespace_column()
    {
        var result = await _context.GetByIdAsync(
            ProjectEndpointName,
            RootChildResourceName,
            _seededRootChild.DocumentUuid,
            [ClaimEducationOrganizationId],
            _namespaceBasedStrategy
        );

        var failure = result.Should().BeOfType<GetResult.GetFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle().Which.Should().Contain(NoUsableRootColumnMarker);
        _context.AssertNoHydration();
    }

    [Test]
    public async Task It_returns_500_security_configuration_for_post_create_when_no_usable_root_table_namespace_column()
    {
        var newSeed = new AuthorizationRootChildSeed(
            new DocumentUuid(Guid.Parse("d4d4d4d4-0000-0000-0000-000000000001")),
            99,
            "post-attempt",
            100,
            []
        );

        var result = await _context.UpsertAuthorizationRootChildAsync(
            newSeed,
            [ClaimEducationOrganizationId],
            _namespaceBasedStrategy
        );

        var failure = result.Should().BeOfType<UpsertResult.UpsertFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle().Which.Should().Contain(NoUsableRootColumnMarker);
        (await _context.CountDocumentRowsAsync(newSeed.DocumentUuid)).Should().Be(0);
        (
            await _context.CountResourceRootRowsAsync(
                ProjectEndpointName,
                RootChildResourceName,
                newSeed.DocumentUuid
            )
        )
            .Should()
            .Be(0);
    }

    [Test]
    public async Task It_returns_500_security_configuration_for_put_when_no_usable_root_table_namespace_column()
    {
        var result = await _context.UpdateAuthorizationRootChildByIdAsync(
            _seededRootChild,
            _seededRootChild.DocumentUuid,
            [ClaimEducationOrganizationId],
            _namespaceBasedStrategy
        );

        var failure = result.Should().BeOfType<UpdateResult.UpdateFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle().Which.Should().Contain(NoUsableRootColumnMarker);
        (await _context.CountDocumentRowsAsync(_seededRootChild.DocumentUuid)).Should().Be(1);
    }

    [Test]
    public async Task It_returns_500_security_configuration_for_delete_when_no_usable_root_table_namespace_column()
    {
        var result = await _context.DeleteByIdAsync(
            ProjectEndpointName,
            RootChildResourceName,
            _seededRootChild.DocumentUuid,
            [ClaimEducationOrganizationId],
            _namespaceBasedStrategy
        );

        var failure = result.Should().BeOfType<DeleteResult.DeleteFailureSecurityConfiguration>().Subject;
        failure.Errors.Should().ContainSingle().Which.Should().Contain(NoUsableRootColumnMarker);
        (await _context.CountDocumentRowsAsync(_seededRootChild.DocumentUuid)).Should().Be(1);
    }
}
