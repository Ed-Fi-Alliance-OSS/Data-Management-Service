// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Asserts the change-version page-selection SQL shape against real authoritative mapping sets:
/// regular resources filter the concrete root table's mirrored <c>ContentVersion</c> with no
/// <c>dms.Document</c> join, and descriptor resources filter the <c>dms.Descriptor</c> root's
/// mirrored <c>ContentVersion</c> scoped by the project-qualified <c>ResourceKeyId</c> predicate.
/// </summary>
[TestFixture]
public class Given_ChangeVersionFilters_Over_Authoritative_MappingSets
{
    private const string Ds52FixturePath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";
    private const string SampleExtensionFixturePath =
        "../Fixtures/authoritative/sample/inputs/sample-api-schema-authoritative.json";

    private static readonly ChangeVersionRange _changeVersionRange = new(100L, 200L);
    private static readonly CollectionPaging _paginationParameters = new CollectionPaging.Traditional(
        new PaginationParameters(Limit: 25, Offset: 0, TotalCount: true, MaximumPageSize: 500)
    );

    private MappingSet _ds52MappingSet = null!;
    private MappingSet _sampleExtensionMappingSet = null!;
    private MappingSet _ds52MssqlMappingSet = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        var compiler = new MappingSetCompiler();
        _ds52MappingSet = compiler.Compile(
            RuntimePlanFixtureModelSetBuilder.Build(Ds52FixturePath, SqlDialect.Pgsql)
        );
        _sampleExtensionMappingSet = compiler.Compile(
            RuntimePlanFixtureModelSetBuilder.Build(
                [(Ds52FixturePath, false), (SampleExtensionFixturePath, true)],
                SqlDialect.Pgsql
            )
        );
        _ds52MssqlMappingSet = compiler.Compile(
            RuntimePlanFixtureModelSetBuilder.Build(Ds52FixturePath, SqlDialect.Mssql)
        );
    }

    [Test]
    [TestCase("Student", "\"edfi\".\"Student\" r")]
    [TestCase("School", "\"edfi\".\"School\" r")]
    public void It_filters_the_concrete_root_mirrored_content_version_for_core_resources(
        string resourceName,
        string expectedRootFromFragment
    )
    {
        AssertRegularResourceChangeVersionSqlShape(
            _ds52MappingSet,
            new QualifiedResourceName("Ed-Fi", resourceName),
            expectedRootFromFragment
        );
    }

    [Test]
    public void It_filters_the_concrete_root_mirrored_content_version_for_extension_project_resources()
    {
        AssertRegularResourceChangeVersionSqlShape(
            _sampleExtensionMappingSet,
            new QualifiedResourceName("Sample", "Bus"),
            "\"sample\".\"Bus\" r"
        );
    }

    [Test]
    public void It_filters_the_descriptor_mirrored_content_version_with_the_resource_key_predicate_for_descriptor_resources()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        _ds52MappingSet.TryGetDescriptorResourceModel(descriptorResource, out _).Should().BeTrue();

        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);
        var keyset = planner.Plan(
            _ds52MappingSet,
            descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paginationParameters,
            changeVersionRange: _changeVersionRange
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("FROM \"dms\".\"Descriptor\" r");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ResourceKeyId\" = @resourceKeyId");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" >= @minChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain("r.\"ResourceKeyId\" = @resourceKeyId");
        keyset.Plan.TotalCountSql.Should().Contain("r.\"ContentVersion\" >= @minChangeVersion");
        keyset.Plan.TotalCountSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        keyset
            .ParameterValues["resourceKeyId"]
            .Should()
            .Be(_ds52MappingSet.ResourceKeyIdByResource[descriptorResource]);
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
    }

    [Test]
    public void It_orders_descriptor_page_selection_by_content_version_for_bounded_windows()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            _ds52MappingSet,
            descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paginationParameters,
            changeVersionRange: _changeVersionRange,
            orderingMode: ChangeQueryPageOrderingPolicy.Default.ResolveForLiveQuery(_changeVersionRange)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("ORDER BY r.\"ContentVersion\" ASC");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ResourceKeyId\" = @resourceKeyId");
    }

    [Test]
    public void It_orders_descriptor_page_selection_by_document_id_for_min_only_windows()
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var minOnlyRange = new ChangeVersionRange(100L, null);
        var planner = new DescriptorQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            _ds52MappingSet,
            descriptorResource,
            new DescriptorQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paginationParameters,
            changeVersionRange: minOnlyRange,
            orderingMode: ChangeQueryPageOrderingPolicy.Default.ResolveForLiveQuery(minOnlyRange)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("ORDER BY r.\"DocumentId\" ASC");
    }

    [Test]
    [TestCase(100L, 200L, "ORDER BY r.\"ContentVersion\" ASC")]
    [TestCase(null, 200L, "ORDER BY r.\"ContentVersion\" ASC")]
    [TestCase(100L, null, "ORDER BY r.\"DocumentId\" ASC")]
    [TestCase(null, null, "ORDER BY r.\"DocumentId\" ASC")]
    public void It_selects_page_ordering_from_the_change_version_filter_shape(
        long? minChangeVersion,
        long? maxChangeVersion,
        string expectedOrderByFragment
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var readPlan = _ds52MappingSet.GetReadPlanOrThrow(resource);
        var changeVersionRange = new ChangeVersionRange(minChangeVersion, maxChangeVersion);
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paginationParameters,
            changeVersionRange: changeVersionRange,
            orderingMode: ChangeQueryPageOrderingPolicy.Default.ResolveForLiveQuery(changeVersionRange)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedOrderByFragment);
    }

    [Test]
    public void It_retains_document_id_ordering_for_bounded_windows_when_the_kill_switch_is_enabled()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var readPlan = _ds52MappingSet.GetReadPlanOrThrow(resource);
        var legacyPolicy = new ChangeQueryPageOrderingPolicy(useLegacyDocumentIdOrdering: true);
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paginationParameters,
            changeVersionRange: _changeVersionRange,
            orderingMode: legacyPolicy.ResolveForLiveQuery(_changeVersionRange)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain("ORDER BY r.\"DocumentId\" ASC");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("ORDER BY r.\"ContentVersion\"");
    }

    [Test]
    public void It_defaults_to_document_id_ordering_when_no_ordering_mode_is_passed()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var readPlan = _ds52MappingSet.GetReadPlanOrThrow(resource);
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var planned = planner.TryPlan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paginationParameters,
            out var plannedQuery,
            out _,
            changeVersionRange: _changeVersionRange
        );

        planned.Should().BeTrue();
        plannedQuery!.Plan.PageDocumentIdSql.Should().Contain("ORDER BY r.\"DocumentId\" ASC");
    }

    [Test]
    [TestCase(100L, 200L, "ORDER BY r.[ContentVersion] ASC", TestName = "Mssql_ordering_min_and_max")]
    [TestCase(null, 200L, "ORDER BY r.[ContentVersion] ASC", TestName = "Mssql_ordering_max_only")]
    [TestCase(100L, null, "ORDER BY r.[DocumentId] ASC", TestName = "Mssql_ordering_min_only")]
    public void It_selects_mssql_page_ordering_from_the_change_version_filter_shape(
        long? minChangeVersion,
        long? maxChangeVersion,
        string expectedOrderByFragment
    )
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Student");
        var readPlan = _ds52MssqlMappingSet.GetReadPlanOrThrow(resource);
        var changeVersionRange = new ChangeVersionRange(minChangeVersion, maxChangeVersion);
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Mssql);

        var keyset = planner.Plan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paginationParameters,
            changeVersionRange: changeVersionRange,
            orderingMode: ChangeQueryPageOrderingPolicy.Default.ResolveForLiveQuery(changeVersionRange)
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain(expectedOrderByFragment);

        // Full generated SQL captured for the manual SQL Server plan-validation gate (Task 8).
        TestContext.Out.WriteLine(keyset.Plan.PageDocumentIdSql);
    }

    private static void AssertRegularResourceChangeVersionSqlShape(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        string expectedRootFromFragment
    )
    {
        var readPlan = mappingSet.GetReadPlanOrThrow(resource);
        var planner = new RelationalQueryPageKeysetPlanner(SqlDialect.Pgsql);

        var keyset = planner.Plan(
            readPlan.Model.Root,
            new RelationalQueryPreprocessingResult(new RelationalQueryPreprocessingOutcome.Continue(), []),
            _paginationParameters,
            changeVersionRange: _changeVersionRange
        );

        keyset.Plan.PageDocumentIdSql.Should().Contain($"FROM {expectedRootFromFragment}");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" >= @minChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        keyset.Plan.PageDocumentIdSql.Should().NotContain("\"dms\".\"Document\"");
        keyset.Plan.TotalCountSql.Should().NotBeNull();
        keyset.Plan.TotalCountSql.Should().Contain($"FROM {expectedRootFromFragment}");
        keyset.Plan.TotalCountSql.Should().Contain("r.\"ContentVersion\" >= @minChangeVersion");
        keyset.Plan.TotalCountSql.Should().Contain("r.\"ContentVersion\" <= @maxChangeVersion");
        keyset.Plan.TotalCountSql.Should().NotContain("\"dms\".\"Document\"");
        keyset.ParameterValues["minChangeVersion"].Should().Be(100L);
        keyset.ParameterValues["maxChangeVersion"].Should().Be(200L);
    }
}
