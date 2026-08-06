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
            await _context.DropCustomAuthViewAsync(InvalidCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(IncompatibleDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(EmptyCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(EmptyTextDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(NoRootMatchTextDocumentIdCustomViewStrategyName);
            await _context.DropCustomAuthViewAsync(MixedTextDocumentIdCustomViewStrategyName);
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

    private static async Task<SqlException> AssertCustomViewValidationFailure(Func<Task> action)
    {
        var assertion = await action.Should().ThrowAsync<CustomViewAuthorizationValidationException>();
        return assertion.Which.InnerException.Should().BeOfType<SqlException>().Subject;
    }
}
