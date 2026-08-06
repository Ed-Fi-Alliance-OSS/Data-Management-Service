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

    /// <summary>
    /// A strategy (and therefore auth view) name carrying PostgreSQL's default <c>$$</c> dollar-quote
    /// delimiter. The validator embeds the view name in a <c>DO</c> block as a string literal, so a fixed
    /// <c>$$</c> delimiter would be closed by the name itself.
    /// </summary>
    private const string DollarQuotedCustomViewStrategyName = "SchoolWithDollar$$QuoteCustomViewProviderTest";

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

        // Only School 100 is authorized by the custom view. The claim's edorg relationship edges cover
        // both School 100 and 200, so the composition test below can prove the custom view is an AND
        // filter rather than an alternative (OR) path into the same schools.
        await _context.CreateSchoolCustomAuthViewAsync(CustomViewStrategyName, [100]);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 100);
        await _context.InsertAuthEdgeAsync(ClaimEducationOrganizationId, 200);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_context is not null)
        {
            await _context.DropCustomAuthViewAsync(CustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(DollarQuotedCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(InvalidCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(IncompatibleDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(EmptyIncompatibleDocumentIdCustomViewStrategyName);
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

    private static async Task<PostgresException> AssertCustomViewValidationFailure(Func<Task> action)
    {
        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        return assertion.Which.InnerException.Should().BeOfType<PostgresException>().Subject;
    }
}
