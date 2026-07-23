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
/// Asserts the change-version page-selection SQL shape (DMS-1182) against real authoritative
/// mapping sets: regular resources filter the concrete root table's mirrored
/// <c>ContentVersion</c> with no <c>dms.Document</c> join, and descriptor resources filter
/// <c>dms.Document.ContentVersion</c> scoped by the project-qualified <c>ResourceKeyId</c>
/// predicate.
/// </summary>
[TestFixture]
public class Given_ChangeVersionFilters_Over_Authoritative_MappingSets
{
    private const string Ds52FixturePath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";
    private const string SampleExtensionFixturePath =
        "../Fixtures/authoritative/sample/inputs/sample-api-schema-authoritative.json";

    private static readonly ChangeVersionRange _changeVersionRange = new(100L, 200L);
    private static readonly PaginationParameters _paginationParameters = new(
        Limit: 25,
        Offset: 0,
        TotalCount: true,
        MaximumPageSize: 500
    );

    private MappingSet _ds52MappingSet = null!;
    private MappingSet _sampleExtensionMappingSet = null!;

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
