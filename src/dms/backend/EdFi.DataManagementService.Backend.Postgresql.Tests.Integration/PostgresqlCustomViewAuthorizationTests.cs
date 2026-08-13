// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Provider-level integration coverage for custom-view (auth."{StrategyName}") authorization on GET-many,
/// executed against a real, provisioned PostgreSQL database. Complements the SQL-shape unit coverage in
/// PageDocumentIdSqlCompilerCustomViewTests by proving the emitted SQL actually executes correctly:
/// filtering the basis resource directly, filtering a subject resource transitively related to the basis
/// resource, composing as an AND filter alongside another authorization strategy, and failing loudly (not
/// silently) when the configured view is missing or malformed.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("Authorization")]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
public class Given_A_Postgresql_Relational_Query_Authorization_With_A_Custom_View_Strategy
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/synthetic/authorization-query";
    private const long ClaimEducationOrganizationId = 900;
    private const string CustomViewStrategyName = "SchoolWithCustomViewProviderTest";
    private const string MissingCustomViewStrategyName = "SchoolWithMissingCustomViewProviderTest";
    private const string InvalidCustomViewStrategyName = "SchoolWithInvalidCustomViewProviderTest";
    private const string IncompatibleDocumentIdCustomViewStrategyName =
        "SchoolWithIncompatibleDocumentIdCustomViewProviderTest";
    private const string EmptyIncompatibleDocumentIdCustomViewStrategyName =
        "SchoolWithEmptyIncompatibleDocumentIdCustomViewProviderTest";
    private const string TableInsteadOfCustomViewStrategyName =
        "SchoolWithTableInsteadOfCustomViewProviderTest";
    private const string MaterializedCustomViewStrategyName = "SchoolWithMaterializedCustomViewProviderTest";
    private const string BroadIntersectionCustomViewStrategyName =
        "SchoolWithBroadIntersectionCustomViewProviderTest";
    private const string NarrowIntersectionCustomViewStrategyName =
        "SchoolWithNarrowIntersectionCustomViewProviderTest";
    private const string NamespaceIntersectionCustomViewStrategyName =
        "AuthorizationNamespaceResourceWithIntersectionCustomViewProviderTest";

    /// <summary>
    /// A configured strategy whose auth view was created with unquoted DDL, so PostgreSQL folded the
    /// object name to lower case while the strategy name stays PascalCase.
    /// </summary>
    private const string FoldedCaseCustomViewStrategyName = "SchoolWithFoldedCaseCustomViewProviderTest";

    /// <summary>
    /// A strategy (and therefore auth view) name carrying PostgreSQL's default <c>$$</c> dollar-quote
    /// delimiter. The validator embeds the view name in a <c>DO</c> block as a string literal, so a fixed
    /// <c>$$</c> delimiter would be closed by the name itself.
    /// </summary>
    private const string DollarQuotedCustomViewStrategyName = "SchoolWithDollar$$QuoteCustomViewProviderTest";

    /// <summary>
    /// Descriptor DELETE authorizes inside the locked-target boundary rather than through an AUTH1
    /// statement, so it needs its own descriptor-basis views: one excluding the delete target and one
    /// including it.
    /// </summary>
    private const string DescriptorDeleteCustomViewStrategyName =
        "GradeLevelDescriptorWithLockedDeleteCustomViewProviderTest";
    private const string DescriptorDeleteAuthorizingCustomViewStrategyName =
        "GradeLevelDescriptorWithLockedDeleteAuthorizingCustomViewProviderTest";
    private static readonly DocumentUuid _gradeLevelDescriptorDocumentUuid = new(
        Guid.Parse("60666666-6666-6666-6666-666666666666")
    );
    private const string GradeLevelDescriptorCodeValue = "Tenth grade";
    private const string StaleETag = "\"stale-etag\"";

    private static readonly QuerySchoolSeed[] _schoolSeeds =
    [
        new(new DocumentUuid(Guid.Parse("77777777-0000-0000-0000-000000000001")), 100, "Authorized School"),
        new(new DocumentUuid(Guid.Parse("77777777-0000-0000-0000-000000000002")), 200, "Filtered School"),
    ];
    private static readonly ClassPeriodSeed[] _classPeriodSeeds =
    [
        new(new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000001")), 100, "P1-Authorized"),
        new(new DocumentUuid(Guid.Parse("88888888-0000-0000-0000-000000000002")), 200, "P2-Filtered"),
    ];

    /// <summary>
    /// Rows for the custom-view-plus-NamespaceBased intersection: seed 1 is authorized by both strategies,
    /// seed 2 only by the namespace prefix, and seed 3 only by the custom view. Each strategy therefore
    /// authorizes a different superset of the single row both agree on.
    /// </summary>
    private static readonly AuthorizationNamespaceSeed[] _namespaceSeeds =
    [
        new(
            new DocumentUuid(Guid.Parse("99999999-0000-0000-0000-000000000001")),
            1,
            "namespace-and-custom-view",
            RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix + "alpha",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("99999999-0000-0000-0000-000000000002")),
            2,
            "namespace-only",
            RelationshipAuthorizationCrudTestSupport.AuthorizedNamespacePrefix + "beta",
            100,
            []
        ),
        new(
            new DocumentUuid(Guid.Parse("99999999-0000-0000-0000-000000000003")),
            3,
            "custom-view-only",
            RelationshipAuthorizationCrudTestSupport.UnauthorizedNamespacePrefix + "gamma",
            100,
            []
        ),
    ];

    private PostgresqlRelationalQueryAuthorizationTestContext _context = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _context = new PostgresqlRelationalQueryAuthorizationTestContext();
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

        // Only School 100 is authorized by the custom view. The claim's edorg relationship edges cover
        // both School 100 and 200, so the composition test below can prove the custom view is an AND
        // filter rather than an alternative (OR) path into the same schools.
        await _context.CreateSchoolCustomAuthViewAsync(CustomViewStrategyName, [100]);
        await _context.CreateSchoolCustomAuthViewAsync(BroadIntersectionCustomViewStrategyName, [100, 200]);
        await _context.CreateSchoolCustomAuthViewAsync(NarrowIntersectionCustomViewStrategyName, [200]);
        await _context.CreateAuthorizationNamespaceCustomAuthViewAsync(
            NamespaceIntersectionCustomViewStrategyName,
            [1, 3]
        );
        // The excluding view authorizes a different descriptor's code value, so the GradeLevelDescriptor
        // delete target is genuinely absent from it rather than the view being empty.
        await _context.CreateDescriptorCustomAuthViewAsync(
            DescriptorDeleteCustomViewStrategyName,
            ["School"]
        );
        await _context.CreateDescriptorCustomAuthViewAsync(
            DescriptorDeleteAuthorizingCustomViewStrategyName,
            [GradeLevelDescriptorCodeValue]
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
            await _context.DropCustomAuthViewAsync(DescriptorDeleteCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(DescriptorDeleteAuthorizingCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(DollarQuotedCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(InvalidCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(IncompatibleDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(EmptyIncompatibleDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(BroadIntersectionCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(NarrowIntersectionCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(NamespaceIntersectionCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(
                PostgresqlRelationalQueryAuthorizationTestContext.FoldUnquotedIdentifier(
                    FoldedCaseCustomViewStrategyName
                )
            );
            await _context.DropCustomAuthMaterializedViewAsync(MaterializedCustomViewStrategyName);
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
            .Contain($"\"auth\".\"{CustomViewStrategyName}\"");
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

        // The claim's edorg relationship authorizes School 100 and 200, but the custom view authorizes
        // only School 100. Custom-view composes as an AND filter, not an OR alternative, so only the
        // intersection (School 100) survives.
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
    public async Task It_filters_get_many_results_when_the_custom_view_name_contains_a_dollar_quote_delimiter()
    {
        // Real-engine proof for the DO-block delimiter hardening: with a fixed $$ delimiter the view
        // name closes the block early and PostgreSQL raises a syntax error, which the validator would
        // surface as a custom-view validation failure instead of filtering the page.
        await _context.CreateSchoolCustomAuthViewAsync(DollarQuotedCustomViewStrategyName, [100]);

        var result = await _context.QueryAsync("ed-fi", "School", [], [DollarQuotedCustomViewStrategyName]);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_schoolSeeds[0].DocumentUuid.Value.ToString());
    }

    [Test]
    public async Task It_filters_get_many_results_by_a_materialized_custom_view()
    {
        // A materialized view is a valid custom authorization object, but its columns are not exposed
        // through information_schema — so a DocumentId type guard reading that view rejects a conforming
        // bigint column and fails the request. The pg_catalog guard must accept it and still filter.
        await _context.CreateSchoolCustomAuthMaterializedViewAsync(MaterializedCustomViewStrategyName, [100]);

        var result = await _context.QueryAsync("ed-fi", "School", [], [MaterializedCustomViewStrategyName]);

        var success = result.Should().BeOfType<QueryResult.QuerySuccess>().Subject;

        success.TotalCount.Should().Be(1);
        success
            .EdfiDocs.Select(static document => document!["id"]!.GetValue<string>())
            .Should()
            .Equal(_schoolSeeds[0].DocumentUuid.Value.ToString());
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
        exception.SqlState.Should().Be(PostgresErrorCodes.UndefinedTable);
        exception.Message.Should().Contain(MissingCustomViewStrategyName);
    }

    [Test]
    public async Task It_rejects_a_custom_view_created_with_an_unquoted_name()
    {
        // Unquoted DDL folds to lower case, so the view lands as auth.schoolwithfoldedcase... while the
        // configured strategy stays PascalCase. DMS quotes the identifier and compares the catalog name
        // verbatim, so the mismatch must surface as a validation failure rather than silently authorizing
        // against the folded object. This is the DDL mistake auth.md's quoting note warns about, and it
        // pairs with the SQL Server binary-collation guard so the contract is case-sensitive on both.
        await _context.CreateSchoolCustomAuthViewWithUnquotedNameAsync(
            FoldedCaseCustomViewStrategyName,
            [100]
        );

        Func<Task> act = () => _context.QueryAsync("ed-fi", "School", [], [FoldedCaseCustomViewStrategyName]);

        var exception = await AssertCustomViewValidationFailure(act);
        exception.SqlState.Should().Be(PostgresErrorCodes.UndefinedTable);
        exception.Message.Should().Contain(FoldedCaseCustomViewStrategyName);
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
        exception.SqlState.Should().Be(PostgresErrorCodes.UndefinedColumn);
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
        exception.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_throws_when_an_empty_custom_view_document_id_is_not_type_compatible()
    {
        // PostgreSQL has a valid bigint = integer operator, so an integer-typed DocumentId does not
        // raise the operator-does-not-exist error that a text column would, and an empty view produces
        // no rows for the join. Without a catalog type check the request would fail open and return an
        // empty 200 result rather than surfacing the misconfiguration (as MSSQL does).
        await _context.CreateEmptyCustomAuthViewWithIntegerDocumentIdAsync(
            EmptyIncompatibleDocumentIdCustomViewStrategyName
        );

        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [
                    EmptyIncompatibleDocumentIdCustomViewStrategyName,
                    AuthorizationStrategyNameConstants.NamespaceBased,
                ]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
    }

    [Test]
    public async Task It_throws_when_the_custom_authorization_object_is_a_table_with_a_bigint_document_id()
    {
        await _context.Database.ExecuteNonQueryAsync(
            $"""
            CREATE TABLE "auth"."{TableInsteadOfCustomViewStrategyName}" (
                "DocumentId" bigint NOT NULL
            );
            """
        );

        try
        {
            Func<Task> act = () =>
                _context.QueryAsync("ed-fi", "School", [], [TableInsteadOfCustomViewStrategyName]);

            var exception = await AssertCustomViewValidationFailure(act);
            exception.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
            exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
        }
        finally
        {
            await _context.Database.ExecuteNonQueryAsync(
                $"""DROP TABLE IF EXISTS "auth"."{TableInsteadOfCustomViewStrategyName}";"""
            );
        }
    }

    [Test]
    public async Task It_rejects_multiple_custom_views_when_the_second_view_is_invalid()
    {
        // Mirrors the MSSQL fixture: a valid first custom view must not mask an invalid second one. Both
        // are configured ahead of the OwnershipBased terminal, so both are validated and the second one's
        // non-conforming DocumentId still surfaces.
        await _context.CreateEmptyCustomAuthViewWithIntegerDocumentIdAsync(
            EmptyIncompatibleDocumentIdCustomViewStrategyName
        );

        Func<Task> act = () =>
            _context.QueryAsync(
                "ed-fi",
                "School",
                [],
                [
                    CustomViewStrategyName,
                    EmptyIncompatibleDocumentIdCustomViewStrategyName,
                    AuthorizationStrategyNameConstants.OwnershipBased,
                ]
            );

        var exception = await AssertCustomViewValidationFailure(act);
        exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
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
    public async Task It_throws_on_delete_when_the_custom_authorization_object_is_a_table_with_a_bigint_document_id()
    {
        // DELETE reaches the view through the co-batched stored run rather than a standalone query, and a table
        // whose DocumentId is type-compatible answers that run's membership SQL without raising anything. Without
        // validating the object the row would be deleted, or the caller denied, against something auth.md does
        // not accept.
        await _context.Database.ExecuteNonQueryAsync(
            $"""
            CREATE TABLE "auth"."{TableInsteadOfCustomViewStrategyName}" (
                "DocumentId" bigint NOT NULL
            );
            """
        );

        try
        {
            var exception = await AssertCustomViewValidationFailure(async () =>
                await _context.DeleteByIdAsync(
                    "ed-fi",
                    "School",
                    _schoolSeeds[0].DocumentUuid,
                    [ClaimEducationOrganizationId],
                    [TableInsteadOfCustomViewStrategyName]
                )
            );

            exception.Message.Should().Contain("Invalid custom authorization view DocumentId contract.");
            (await _context.CountDocumentRowsAsync(_schoolSeeds[0].DocumentUuid)).Should().Be(1);
        }
        finally
        {
            await _context.Database.ExecuteNonQueryAsync(
                $"""DROP TABLE IF EXISTS "auth"."{TableInsteadOfCustomViewStrategyName}";"""
            );
        }
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

    [Test]
    public async Task It_denies_a_descriptor_delete_when_the_custom_view_excludes_the_target()
    {
        var result = await _context.DeleteByIdAsync(
            "ed-fi",
            "GradeLevelDescriptor",
            _gradeLevelDescriptorDocumentUuid,
            [ClaimEducationOrganizationId],
            [DescriptorDeleteCustomViewStrategyName]
        );

        result
            .Should()
            .BeOfType<DeleteResult.DeleteFailureCustomViewNotAuthorized>()
            .Which.CustomViewFailure.StrategyName.Should()
            .Be(DescriptorDeleteCustomViewStrategyName);
        (await _context.CountDocumentRowsAsync(_gradeLevelDescriptorDocumentUuid)).Should().Be(1);
    }

    [Test]
    public async Task It_denies_a_descriptor_delete_with_a_stale_if_match_when_the_custom_view_excludes_the_target()
    {
        // A stale If-Match would also fail the delete, so reporting the custom-view denial proves the check
        // runs inside the locked-target boundary ahead of the precondition outcome rather than after it.
        var result = await _context.DeleteByIdAsync(
            "ed-fi",
            "GradeLevelDescriptor",
            _gradeLevelDescriptorDocumentUuid,
            [ClaimEducationOrganizationId],
            [DescriptorDeleteCustomViewStrategyName],
            ifMatch: StaleETag
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureCustomViewNotAuthorized>();
        (await _context.CountDocumentRowsAsync(_gradeLevelDescriptorDocumentUuid)).Should().Be(1);
    }

    [Test]
    public async Task It_reports_the_stale_if_match_when_the_custom_view_authorizes_the_descriptor_delete()
    {
        // Same request as above against a view that includes the target: the precondition failure surfaces,
        // which is what makes the denial above attributable to the view rather than to descriptor deletes
        // being refused outright. Deleting nothing also keeps the seeded descriptor available to other tests.
        var result = await _context.DeleteByIdAsync(
            "ed-fi",
            "GradeLevelDescriptor",
            _gradeLevelDescriptorDocumentUuid,
            [ClaimEducationOrganizationId],
            [DescriptorDeleteAuthorizingCustomViewStrategyName],
            ifMatch: StaleETag
        );

        result.Should().BeOfType<DeleteResult.DeleteFailureETagMisMatch>();
        (await _context.CountDocumentRowsAsync(_gradeLevelDescriptorDocumentUuid)).Should().Be(1);
    }

    private static async Task<PostgresException> AssertCustomViewValidationFailure(Func<Task> action)
    {
        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        return assertion.Which.InnerException.Should().BeOfType<PostgresException>().Subject;
    }
}
