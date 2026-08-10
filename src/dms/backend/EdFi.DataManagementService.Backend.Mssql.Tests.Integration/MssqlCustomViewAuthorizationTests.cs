// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_A_Mssql_Relational_Query_Authorization_With_A_Custom_View_Strategy
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/synthetic/authorization-query";
    private const long ClaimEducationOrganizationId = 900;
    private const string CustomViewStrategyName = "SchoolWithCustomViewProviderTest";
    private const string MissingCustomViewStrategyName = "SchoolWithMissingCustomViewProviderTest";
    private const string InvalidCustomViewStrategyName = "SchoolWithInvalidCustomViewProviderTest";
    private const string IncompatibleDocumentIdCustomViewStrategyName =
        "SchoolWithIncompatibleDocumentIdCustomViewProviderTest";
    private const string EmptyCustomViewStrategyName = "SchoolWithEmptyCustomViewProviderTest";
    private const string EmptyTextDocumentIdCustomViewStrategyName =
        "SchoolWithEmptyTextDocumentIdCustomViewProviderTest";
    private const string NoRootMatchTextDocumentIdCustomViewStrategyName =
        "SchoolWithNoRootMatchTextDocumentIdCustomViewProviderTest";
    private const string MixedTextDocumentIdCustomViewStrategyName =
        "SchoolWithMixedTextDocumentIdCustomViewProviderTest";
    private const string TableInsteadOfCustomViewStrategyName =
        "SchoolWithTableInsteadOfCustomViewProviderTest";
    private const string BroadIntersectionCustomViewStrategyName =
        "SchoolWithBroadIntersectionCustomViewProviderTest";
    private const string NarrowIntersectionCustomViewStrategyName =
        "SchoolWithNarrowIntersectionCustomViewProviderTest";
    private const string NamespaceIntersectionCustomViewStrategyName =
        "AuthorizationNamespaceResourceWithIntersectionCustomViewProviderTest";

    /// <summary>
    /// A configured strategy whose auth view exists in the database under
    /// <see cref="MixedCaseCustomViewObjectName"/> — the same identifier differing only by case.
    /// </summary>
    private const string MixedCaseCustomViewStrategyName = "SchoolWithMixedCaseCustomViewProviderTest";
    private const string MixedCaseCustomViewObjectName = "SCHOOLWITHMIXEDCASECUSTOMVIEWPROVIDERTEST";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("77777777-1000-0000-0000-000000000001")), 100, "Authorized School"),
        new(new DocumentUuid(Guid.Parse("77777777-1000-0000-0000-000000000002")), 200, "Filtered School"),
    ];
    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("88888888-1000-0000-0000-000000000001")), 100, "P1-Authorized"),
        new(new DocumentUuid(Guid.Parse("88888888-1000-0000-0000-000000000002")), 200, "P2-Filtered"),
    ];

    /// <summary>
    /// Rows for the custom-view-plus-NamespaceBased intersection: seed 1 is authorized by both strategies,
    /// seed 2 only by the namespace prefix, and seed 3 only by the custom view. Each strategy therefore
    /// authorizes a different superset of the single row both agree on.
    /// </summary>
    private static readonly AuthorizationNamespaceSeed[] _namespaceSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("99999999-1000-0000-0000-000000000001")),
            1,
            "namespace-and-custom-view",
            RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix + "alpha",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("99999999-1000-0000-0000-000000000002")),
            2,
            "namespace-only",
            RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix + "beta",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("99999999-1000-0000-0000-000000000003")),
            3,
            "custom-view-only",
            RelationshipAuthorizationCrudTestSupport.UnauthorizedNamespacePrefix + "gamma",
            100,
            []
        ),
    ];

    private MssqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _context = new MssqlRelationalQueryAuthorizationTestContext();
        await _context.InitializeAsync(FixtureRelativePath, strict: false);
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

        foreach (var namespaceSeed in _namespaceSeeds)
        {
            RelationalQueryAuthorizationAssertions.AssertInsertSuccess(
                await _context.CreateAuthorizationNamespaceAsync(namespaceSeed)
            );
        }

        await _context.CreateSchoolCustomAuthViewAsync(CustomViewStrategyName, [100]);
        await _context.CreateSchoolCustomAuthViewAsync(BroadIntersectionCustomViewStrategyName, [100, 200]);
        await _context.CreateSchoolCustomAuthViewAsync(NarrowIntersectionCustomViewStrategyName, [200]);
        await _context.CreateAuthorizationNamespaceCustomAuthViewAsync(
            NamespaceIntersectionCustomViewStrategyName,
            [1, 3]
        );
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DropCustomAuthViewAsync(CustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(InvalidCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(IncompatibleDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(EmptyCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(EmptyTextDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(NoRootMatchTextDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(MixedTextDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(BroadIntersectionCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(NarrowIntersectionCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(NamespaceIntersectionCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(MixedCaseCustomViewObjectName);
            await _context.DisposeAsync();
        }
    }

    [SetUp]
    public void SetUp()
    {
        _context.ResetRecorder();
    }

    [Test]
    public async Task It_filters_get_many_results_by_the_custom_view_for_the_basis_resource_itself()
    {
        var result = await _context.QueryAsync("ed-fi", "School", [], [CustomViewStrategyName]);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_schoolSeeds[0].DocumentUuid.Value.ToString());

        _context
            .AssertSingleQueryHydration()
            .Plan.PageDocumentIdSql.Should()
            .Contain($"[auth].[{CustomViewStrategyName}]");
    }

    [Test]
    public async Task It_filters_get_many_results_for_a_subject_resource_transitively_related_to_the_basis_resource()
    {
        var result = await _context.QueryAsync("ed-fi", "ClassPeriod", [], [CustomViewStrategyName]);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_classPeriodSeeds[0].DocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_composes_the_custom_view_as_an_and_filter_alongside_relationship_or_authorization()
    {
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [ClaimEducationOrganizationId],
            [CustomViewStrategyName, AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_schoolSeeds[0].DocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_intersects_two_custom_views_on_get_many()
    {
        // Two custom views compose as AND, not OR: the broad view authorizes schools 100 and 200 while the
        // narrow one authorizes only 200, so only the intersection survives. An OR composition would
        // return both schools — the broad view alone already covers the whole seeded set.
        var result = await _context.QueryAsync(
            "ed-fi",
            "School",
            [],
            [BroadIntersectionCustomViewStrategyName, NarrowIntersectionCustomViewStrategyName]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_schoolSeeds[1].DocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_intersects_a_custom_view_with_namespace_based_filtering()
    {
        // NamespaceBased authorizes the two rows carrying the configured prefix (seeds 1 and 2); the custom
        // view authorizes a different superset (seeds 1 and 3). Only seed 1 satisfies both, so an OR
        // composition would return all three seeded rows.
        var result = await _context.QueryAsync(
            RelationshipAuthorizationCrudTestSupport.ProjectEndpointName,
            RelationshipAuthorizationCrudTestSupport.NamespaceResourceName,
            [],
            [NamespaceIntersectionCustomViewStrategyName, AuthorizationStrategyNameConstants.NamespaceBased],
            namespacePrefixes: [RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix]
        );

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_namespaceSeeds[0].DocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_wraps_a_provider_error_when_the_configured_custom_view_does_not_exist_ahead_of_the_ownership_terminal()
    {
        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [MissingCustomViewStrategyName, AuthorizationStrategyNameConstants.OwnershipBased]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_wraps_a_provider_error_when_the_custom_view_omits_the_document_id_column()
    {
        await _context.CreateCustomAuthViewWithoutDocumentIdAsync(InvalidCustomViewStrategyName);

        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [
                    InvalidCustomViewStrategyName,
                    AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                ]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_wraps_a_provider_error_when_the_custom_view_document_id_is_not_type_compatible_before_namespace_no_prefixes()
    {
        await _context.CreateCustomAuthViewWithTextDocumentIdAsync(
            IncompatibleDocumentIdCustomViewStrategyName
        );

        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [
                    IncompatibleDocumentIdCustomViewStrategyName,
                    AuthorizationStrategyNameConstants.NamespaceBased,
                ]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_accepts_an_empty_custom_view_with_a_bigint_document_id()
    {
        await _context.CreateSchoolCustomAuthViewAsync(EmptyCustomViewStrategyName, []);

        var result = await _context.QueryAsync("ed-fi", "School", [], [EmptyCustomViewStrategyName]);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;
        success.EdfiDocs.Should().BeEmpty();
        success.TotalCount.Should().Be(0);
    }

    [Test]
    public async Task It_rejects_an_empty_custom_view_with_a_text_document_id()
    {
        await _context.CreateEmptyCustomAuthViewWithTextDocumentIdAsync(
            EmptyTextDocumentIdCustomViewStrategyName
        );

        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [EmptyTextDocumentIdCustomViewStrategyName, AuthorizationStrategyNameConstants.NamespaceBased]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_rejects_a_text_document_id_view_with_no_matching_root_row()
    {
        await _context.CreateCustomAuthViewWithTextDocumentIdAndNoRootMatchAsync(
            NoRootMatchTextDocumentIdCustomViewStrategyName
        );

        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [
                    NoRootMatchTextDocumentIdCustomViewStrategyName,
                    AuthorizationStrategyNameConstants.OwnershipBased,
                ]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_rejects_a_text_document_id_view_with_mixed_convertible_values()
    {
        await _context.CreateCustomAuthViewWithMixedTextDocumentIdsAsync(
            MixedTextDocumentIdCustomViewStrategyName
        );

        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [MixedTextDocumentIdCustomViewStrategyName, AuthorizationStrategyNameConstants.OwnershipBased]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_rejects_multiple_custom_views_when_the_second_view_is_invalid()
    {
        await _context.CreateEmptyCustomAuthViewWithTextDocumentIdAsync(
            EmptyTextDocumentIdCustomViewStrategyName
        );

        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [
                    CustomViewStrategyName,
                    EmptyTextDocumentIdCustomViewStrategyName,
                    AuthorizationStrategyNameConstants.OwnershipBased,
                ]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_rejects_a_custom_view_whose_object_name_differs_from_the_strategy_name_only_by_case()
    {
        // sys catalog name columns are sysname, carrying the database collation — case-insensitive here
        // (SQL_Latin1_General_CP1_CI_AS) as on a default install — and the bracketed bind probe resolves
        // identifiers case-insensitively too. Without a binary collation on the catalog comparison both
        // accept this view, and the request returns a filtered 200 against an object that is not the
        // configured auth.{StrategyName}. PostgreSQL already matches case-sensitively, so this keeps the
        // documented contract identical on both engines.
        await _context.CreateSchoolCustomAuthViewAsync(MixedCaseCustomViewObjectName, [100]);

        Func<Task> act = () => _context.QueryAsync("ed-fi", "School", [], [MixedCaseCustomViewStrategyName]);

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_throws_when_the_custom_authorization_object_is_a_table_with_a_bigint_document_id()
    {
        // Mirrors the PostgreSQL fixture: an empty table masquerading as an auth view satisfies the
        // DocumentId join, so without the object-kind guard the request would fail open with an empty
        // 200 instead of reporting the misconfiguration.
        await _context.Database.ExecuteNonQueryAsync(
            $"""
            CREATE TABLE [auth].[{TableInsteadOfCustomViewStrategyName}] (
                [DocumentId] bigint NOT NULL
            );
            """
        );

        try
        {
            Func<Task> act = () =>
                _context.QueryAsync("ed-fi", "School", [], [TableInsteadOfCustomViewStrategyName]);

            var exception = await AssertCustomViewValidationFailure(act);
            exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
        }
        finally
        {
            await _context.Database.ExecuteNonQueryAsync(
                $"""
                IF OBJECT_ID(N'[auth].[{TableInsteadOfCustomViewStrategyName}]', N'U') IS NOT NULL
                    DROP TABLE [auth].[{TableInsteadOfCustomViewStrategyName}];
                """
            );
        }
    }

    [Test]
    public async Task It_denies_delete_for_a_school_the_custom_view_excludes_and_leaves_the_row()
    {
        var result = await _context.DeleteByIdAsync(
            "ed-fi",
            "School",
            _schoolSeeds[1].DocumentUuid,
            [ClaimEducationOrganizationId],
            [CustomViewStrategyName]
        );

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureCustomViewNotAuthorized>()
            .Which.CustomViewFailure.StrategyName.Should()
            .Be(CustomViewStrategyName);
        // The check runs inside the locked-target boundary before the delete, so a denial must leave the row.
        (await _context.CountDocumentRowsAsync(_schoolSeeds[1].DocumentUuid))
            .Should()
            .Be(1);
    }

    [Test]
    public async Task It_denies_delete_with_if_match_for_a_school_the_custom_view_excludes()
    {
        // The If-Match delete authorizes through a different seam than the plain delete — the locked
        // precondition path — so the denial has to hold there too, and ahead of the precondition outcome.
        var result = await _context.DeleteByIdAsync(
            "ed-fi",
            "School",
            _schoolSeeds[1].DocumentUuid,
            [ClaimEducationOrganizationId],
            [CustomViewStrategyName],
            ifMatch: "*"
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureCustomViewNotAuthorized>();
        (await _context.CountDocumentRowsAsync(_schoolSeeds[1].DocumentUuid)).Should().Be(1);
    }

    [Test]
    public async Task It_wraps_a_provider_error_when_the_configured_custom_view_does_not_exist_on_delete()
    {
        // GET-many already proves the view contract at the provider level; this proves the single-record
        // DELETE path reaches it rather than reporting a generic delete failure.
        await AssertCustomViewValidationFailure(async () =>
            await _context.DeleteByIdAsync(
                "ed-fi",
                "School",
                _schoolSeeds[0].DocumentUuid,
                [ClaimEducationOrganizationId],
                [MissingCustomViewStrategyName]
            )
        );

        (await _context.CountDocumentRowsAsync(_schoolSeeds[0].DocumentUuid)).Should().Be(1);
    }

    [Test]
    public async Task It_reports_a_referencing_document_failure_after_the_custom_view_authorizes_the_delete()
    {
        // School 100 IS in the view, so authorization passes and the delete proceeds — and then fails because
        // a ClassPeriod references it. A denial would stop before the delete and prove nothing, so this is the
        // case that shows custom-view validation and error attribution do not swallow an ordinary failure.
        var result = await _context.DeleteByIdAsync(
            "ed-fi",
            "School",
            _schoolSeeds[0].DocumentUuid,
            [ClaimEducationOrganizationId],
            [CustomViewStrategyName]
        );

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureReference>()
            .Which.ReferencingDocumentResourceNames.Should()
            .NotBeEmpty();
        (await _context.CountDocumentRowsAsync(_schoolSeeds[0].DocumentUuid)).Should().Be(1);
    }

    private static async Task<SqlException> AssertCustomViewValidationFailure(Func<Task> action)
    {
        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        return assertion.Which.InnerException.Should().BeOfType<SqlException>().Subject;
    }
}
