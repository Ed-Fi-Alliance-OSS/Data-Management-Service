// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Identification-code query fields compile to collection-table targets over the authoritative Data
/// Standard 5.2 and Sample model, without changing any resource's existing support.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_The_Authoritative_Ds52_And_Sample_Identification_Code_Query_Capability(SqlDialect dialect)
{
    private static readonly (string FixtureRelativePath, bool IsExtensionProject)[] _authoritativeInputs =
    [
        ("../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json", false),
        ("../Fixtures/authoritative/sample/inputs/sample-api-schema-authoritative.json", true),
    ];

    private readonly RelationalQueryCapabilityCompiler _compiler = new();
    private IReadOnlyList<ConcreteResourceModel> _resources = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _resources = RuntimePlanFixtureModelSetBuilder
            .Build(_authoritativeInputs, dialect)
            .ConcreteResourcesInNameOrder;
    }

    [Test]
    public void It_supports_each_qualifying_resource_with_a_collection_target_for_every_derived_field()
    {
        var qualifyingResources = _resources
            .Where(static resource => resource.IdentificationCodeQueryFieldMappingsByQueryField.Count > 0)
            .ToArray();

        qualifyingResources.Should().HaveCount(15);

        foreach (var resource in qualifyingResources)
        {
            var capability = _compiler.Compile(resource);
            var resourceName = resource.RelationalModel.Resource.ResourceName;

            capability.Support.Should().BeOfType<RelationalQuerySupport.Supported>(resourceName);

            foreach (var derivedField in resource.IdentificationCodeQueryFieldMappingsByQueryField.Values)
            {
                var supportedField = capability.SupportedFieldsByQueryField[derivedField.QueryFieldName];
                var target = supportedField
                    .Target.Should()
                    .BeOfType<RelationalQueryFieldTarget.CollectionColumn>(
                        $"{resourceName}.{derivedField.QueryFieldName}"
                    )
                    .Subject;

                supportedField.Path.Path.Canonical.Should().Be(derivedField.ItemPath.Canonical);
                supportedField.Path.Type.Should().Be(derivedField.Type);
                resource
                    .RelationalModel.TablesInDependencyOrder.Single(table =>
                        table.Table == target.CollectionTable
                    )
                    .JsonScope.Canonical.Should()
                    .Be(derivedField.CollectionPath.Canonical);
            }
        }
    }

    [Test]
    public void It_targets_the_school_identification_code_table_columns()
    {
        var school = Resource("School");
        var capability = _compiler.Compile(school);
        var descriptorEdge = school.RelationalModel.DescriptorEdgeSources.Single(edge =>
            edge.DescriptorValuePath.Canonical
            == "$.identificationCodes[*].educationOrganizationIdentificationSystemDescriptor"
        );
        var collectionTable = new DbTableName(new DbSchemaName("edfi"), "SchoolIdentificationCode");
        var rootLocatorColumn = new DbColumnName("School_DocumentId");

        capability
            .SupportedFieldsByQueryField["identificationCode"]
            .Target.Should()
            .Be(
                new RelationalQueryFieldTarget.CollectionColumn(
                    collectionTable,
                    rootLocatorColumn,
                    new DbColumnName("IdentificationCode"),
                    DescriptorResource: null
                )
            );
        capability
            .SupportedFieldsByQueryField["educationOrganizationIdentificationSystemDescriptor"]
            .Target.Should()
            .Be(
                new RelationalQueryFieldTarget.CollectionColumn(
                    collectionTable,
                    rootLocatorColumn,
                    descriptorEdge.FkColumn,
                    new QualifiedResourceName("Ed-Fi", "EducationOrganizationIdentificationSystemDescriptor")
                )
            );
    }

    [Test]
    public void It_targets_a_non_descriptor_qualifier_column()
    {
        _compiler
            .Compile(Resource("Staff"))
            .SupportedFieldsByQueryField["assigningOrganizationIdentificationCode"]
            .Target.Should()
            .Be(
                new RelationalQueryFieldTarget.CollectionColumn(
                    new DbTableName(new DbSchemaName("edfi"), "StaffIdentificationCode"),
                    new DbColumnName("Staff_DocumentId"),
                    new DbColumnName("AssigningOrganizationIdentificationCode"),
                    DescriptorResource: null
                )
            );
    }

    [Test]
    public void It_keeps_every_resource_support_state_and_upstream_field_compilation()
    {
        IdentificationCodeCapabilityAssertions.ShouldKeepSupportAndUpstreamFields(_compiler, _resources);
    }

    [Test]
    public void It_still_omits_a_resource_with_an_upstream_array_crossing_mapping()
    {
        var school = Resource("School");
        var capability = _compiler.Compile(
            school with
            {
                QueryFieldMappingsByQueryField = new Dictionary<string, RelationalQueryFieldMapping>(
                    school.QueryFieldMappingsByQueryField
                )
                {
                    ["gradeLevelDescriptor"] = new(
                        "gradeLevelDescriptor",
                        [
                            new RelationalQueryFieldPath(
                                Path("$.gradeLevels[*].gradeLevelDescriptor"),
                                "string"
                            ),
                        ]
                    ),
                },
            }
        );

        capability
            .Support.Should()
            .BeOfType<RelationalQuerySupport.Omitted>()
            .Which.Omission.Kind.Should()
            .Be(RelationalQueryCapabilityOmissionKind.UnsupportedQueryFields);
        capability
            .UnsupportedFieldsByQueryField.Values.Select(static field =>
                (field.QueryFieldName, field.FailureKind)
            )
            .Should()
            .Equal(("gradeLevelDescriptor", RelationalQueryFieldFailureKind.ArrayCrossing));
        capability
            .SupportedFieldsByQueryField["identificationCode"]
            .Target.Should()
            .BeOfType<RelationalQueryFieldTarget.CollectionColumn>();
    }

    [TestCase("$.missingCodes[*]", "$.missingCodes[*].identificationCode", "string")]
    [TestCase("$.identificationCodes[*]", "$.identificationCodes[*].missingProperty", "string")]
    [TestCase("$.identificationCodes[*]", "$.identificationCodes[*].identificationCode", "number")]
    [TestCase(
        "$.identificationCodes[*]",
        "$.identificationCodes[*].educationOrganizationIdentificationSystemDescriptor",
        "number"
    )]
    [TestCase("$.addresses[*]", "$.identificationCodes[*].identificationCode", "string")]
    public void It_omits_the_resource_when_a_derived_field_does_not_resolve(
        string collectionPath,
        string itemPath,
        string type
    )
    {
        var capability = _compiler.Compile(
            Resource("School") with
            {
                IdentificationCodeQueryFieldMappingsByQueryField = new Dictionary<
                    string,
                    RelationalIdentificationCodeQueryFieldMapping
                >
                {
                    ["identificationCode"] = new(
                        "identificationCode",
                        Path(collectionPath),
                        Path(itemPath),
                        type
                    ),
                },
            }
        );

        capability
            .Support.Should()
            .BeOfType<RelationalQuerySupport.Omitted>()
            .Which.Omission.Reason.Should()
            .Contain(
                "identificationCode: did not resolve to one stored column of one identification-code collection table"
            );
        capability
            .UnsupportedFieldsByQueryField["identificationCode"]
            .Should()
            .BeEquivalentTo(
                new UnsupportedRelationalQueryField(
                    "identificationCode",
                    [new RelationalQueryFieldPath(Path(itemPath), type)],
                    RelationalQueryFieldFailureKind.IdentificationCodeCollectionUnresolved
                )
            );
        capability.SupportedFieldsByQueryField.Should().NotContainKey("identificationCode");
    }

    [Test]
    public void It_rejects_a_derived_name_that_collides_with_an_upstream_query_field()
    {
        var school = Resource("School");
        var derivedField = school.IdentificationCodeQueryFieldMappingsByQueryField["identificationCode"];

        var compile = () =>
            _compiler.Compile(
                school with
                {
                    IdentificationCodeQueryFieldMappingsByQueryField = new Dictionary<
                        string,
                        RelationalIdentificationCodeQueryFieldMapping
                    >
                    {
                        ["SchoolId"] = derivedField with { QueryFieldName = "SchoolId" },
                    },
                }
            );

        compile.Should().Throw<InvalidOperationException>().WithMessage("*'SchoolId', 'schoolId'*");
    }

    private ConcreteResourceModel Resource(string resourceName) =>
        _resources.Single(resource =>
            resource.RelationalModel.Resource == new QualifiedResourceName("Ed-Fi", resourceName)
        );

    private static JsonPathExpression Path(string canonical) => JsonPathExpressionCompiler.Compile(canonical);
}

/// <summary>
/// Identification-code query fields compile to collection-table targets over the packaged Data
/// Standard 6.1 model, which moved most identification codes out of collections, without changing any
/// resource's existing support.
/// </summary>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_The_Packaged_Ds61_Identification_Code_Query_Capability(SqlDialect dialect)
{
    private readonly RelationalQueryCapabilityCompiler _compiler = new();
    private IReadOnlyList<ConcreteResourceModel> _resources = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        // Staged into the test output by StageDataStandard61ApiSchema in the project file.
        var apiSchemaPath = System.IO.Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "StagedApiSchema",
            "EdFi.DataStandard61.ApiSchema",
            "ApiSchema.json"
        );

        File.Exists(apiSchemaPath)
            .Should()
            .BeTrue($"the staged DS 6.1 ApiSchema is missing at {apiSchemaPath}");

        _resources = RuntimePlanFixtureModelSetBuilder
            .Build([(apiSchemaPath, false)], dialect)
            .ConcreteResourcesInNameOrder;
    }

    [Test]
    public void It_targets_each_derived_field_of_exactly_the_four_qualifying_resources()
    {
        _resources
            .SelectMany(resource =>
            {
                var capability = _compiler.Compile(resource);

                return resource.IdentificationCodeQueryFieldMappingsByQueryField.Values.Select(field =>
                {
                    var target = (RelationalQueryFieldTarget.CollectionColumn)
                        capability.SupportedFieldsByQueryField[field.QueryFieldName].Target;

                    resource
                        .RelationalModel.TablesInDependencyOrder.Single(table =>
                            table.Table == target.CollectionTable
                        )
                        .JsonScope.Canonical.Should()
                        .Be(field.CollectionPath.Canonical);

                    return $"{resource.RelationalModel.Resource.ResourceName}.{field.QueryFieldName} "
                        + (target.DescriptorResource?.ResourceName ?? "scalar");
                });
            })
            .Should()
            .BeEquivalentTo(
                "Assessment.assessmentIdentificationSystemDescriptor AssessmentIdentificationSystemDescriptor",
                "Assessment.assigningOrganizationIdentificationCode scalar",
                "Assessment.identificationCode scalar",
                "Course.assigningOrganizationIdentificationCode scalar",
                "Course.courseCatalogURL scalar",
                "Course.courseIdentificationSystemDescriptor CourseIdentificationSystemDescriptor",
                "Course.identificationCode scalar",
                "CourseTranscript.courseIdentificationSystemDescriptor CourseIdentificationSystemDescriptor",
                "CourseTranscript.identificationCode scalar",
                "LearningStandard.contentStandardName scalar",
                "LearningStandard.identificationCode scalar"
            );
    }

    [Test]
    public void It_keeps_every_resource_support_state_and_upstream_field_compilation()
    {
        IdentificationCodeCapabilityAssertions.ShouldKeepSupportAndUpstreamFields(_compiler, _resources);
    }
}

/// <summary>
/// Compiling a resource with its identification-code query fields must leave its support state, its
/// unsupported fields and its upstream supported fields exactly as compiling it without them.
/// </summary>
file static class IdentificationCodeCapabilityAssertions
{
    public static void ShouldKeepSupportAndUpstreamFields(
        RelationalQueryCapabilityCompiler compiler,
        IReadOnlyList<ConcreteResourceModel> resources
    )
    {
        resources.Should().NotBeEmpty();

        foreach (var resource in resources)
        {
            var withoutDerivedFields = compiler.Compile(
                resource with
                {
                    IdentificationCodeQueryFieldMappingsByQueryField =
                        new Dictionary<string, RelationalIdentificationCodeQueryFieldMapping>(),
                }
            );
            var withDerivedFields = compiler.Compile(resource);
            var resourceName =
                $"{resource.RelationalModel.Resource.ProjectName}.{resource.RelationalModel.Resource.ResourceName}";

            withDerivedFields.Support.Should().Be(withoutDerivedFields.Support, resourceName);
            withDerivedFields
                .UnsupportedFieldsByQueryField.Should()
                .BeEquivalentTo(withoutDerivedFields.UnsupportedFieldsByQueryField, resourceName);
            withDerivedFields
                .SupportedFieldsByQueryField.Where(field =>
                    !resource.IdentificationCodeQueryFieldMappingsByQueryField.ContainsKey(field.Key)
                )
                .Should()
                .BeEquivalentTo(withoutDerivedFields.SupportedFieldsByQueryField, resourceName);
        }
    }
}
