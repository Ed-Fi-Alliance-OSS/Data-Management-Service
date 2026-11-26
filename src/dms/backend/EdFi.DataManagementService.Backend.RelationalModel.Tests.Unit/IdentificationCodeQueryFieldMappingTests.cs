// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// The derived relational model carries each resource's identification-code query fields apart from
/// its <c>queryFieldMapping</c> entries.
/// </summary>
[TestFixture]
public class Given_The_Authoritative_Ds52_And_Sample_Model_Identification_Code_Query_Fields
{
    private JsonObject _coreResourceSchemas = null!;
    private IReadOnlyList<ConcreteResourceModel> _resources = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        var authoritativeFixtureRoot = BackendFixturePaths.GetAuthoritativeFixtureRoot(
            TestContext.CurrentContext.TestDirectory
        );
        var coreProjectSchema = LoadProjectSchema(
            Path.Combine(authoritativeFixtureRoot, "ds-5.2", "inputs", "ds-5.2-api-schema-authoritative.json")
        );
        var extensionProjectSchema = LoadProjectSchema(
            Path.Combine(authoritativeFixtureRoot, "sample", "inputs", "sample-api-schema-authoritative.json")
        );
        _coreResourceSchemas = RelationalModelSetSchemaHelpers.RequireObject(
            coreProjectSchema["resourceSchemas"],
            "resourceSchemas"
        );

        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
                coreProjectSchema,
                isExtensionProject: false
            ),
            EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
                extensionProjectSchema,
                isExtensionProject: true
            ),
        ]);

        _resources = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault())
            .Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules())
            .ConcreteResourcesInNameOrder;
    }

    [Test]
    public void It_populates_exactly_the_fifteen_qualifying_resources()
    {
        _resources
            .Where(static resource => resource.IdentificationCodeQueryFieldMappingsByQueryField.Count > 0)
            .Select(static resource => resource.RelationalModel.Resource)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    "Assessment",
                    "CommunityOrganization",
                    "CommunityProvider",
                    "Course",
                    "CourseTranscript",
                    "EducationOrganizationNetwork",
                    "EducationServiceCenter",
                    "LearningStandard",
                    "LocalEducationAgency",
                    "OrganizationDepartment",
                    "PostSecondaryInstitution",
                    "School",
                    "Staff",
                    "StateEducationAgency",
                    "StudentEducationOrganizationAssociation",
                }.Select(static resourceName => new QualifiedResourceName("Ed-Fi", resourceName))
            );
    }

    [Test]
    public void It_normalizes_each_field_to_canonical_paths_and_its_query_type()
    {
        Resource("School")
            .IdentificationCodeQueryFieldMappingsByQueryField.Values.Select(static field =>
                $"{field.QueryFieldName} {field.CollectionPath.Canonical} {field.ItemPath.Canonical} {field.Type}"
            )
            .Should()
            .BeEquivalentTo(
                "educationOrganizationIdentificationSystemDescriptor $.identificationCodes[*] "
                    + "$.identificationCodes[*].educationOrganizationIdentificationSystemDescriptor string",
                "identificationCode $.identificationCodes[*] $.identificationCodes[*].identificationCode string"
            );
    }

    [Test]
    public void It_leaves_the_course_transcript_root_query_field_names_out()
    {
        Resource("CourseTranscript")
            .IdentificationCodeQueryFieldMappingsByQueryField.Keys.Should()
            .BeEquivalentTo("courseIdentificationSystemDescriptor", "identificationCode");
    }

    [Test]
    public void It_scopes_each_collection_path_to_exactly_one_collection_table_of_the_resource()
    {
        foreach (var resource in _resources)
        {
            foreach (var field in resource.IdentificationCodeQueryFieldMappingsByQueryField.Values)
            {
                resource
                    .RelationalModel.TablesInDependencyOrder.Where(table =>
                        table.JsonScope.Canonical == field.CollectionPath.Canonical
                    )
                    .Select(static table => table.IdentityMetadata.TableKind)
                    .Should()
                    .Equal(
                        [DbTableKind.Collection],
                        $"{resource.RelationalModel.Resource.ResourceName}.{field.QueryFieldName} must target one collection table"
                    );
            }
        }
    }

    [Test]
    public void It_keeps_the_query_field_mappings_to_the_schema_query_field_mapping_entries()
    {
        foreach (
            var resource in _resources.Where(static resource =>
                resource.RelationalModel.Resource.ProjectName == "Ed-Fi"
            )
        )
        {
            var schemaQueryFieldNames = _coreResourceSchemas
                .Select(static entry => entry.Value!.AsObject())
                .Single(resourceSchema =>
                    resourceSchema["resourceName"]!.GetValue<string>()
                    == resource.RelationalModel.Resource.ResourceName
                )["queryFieldMapping"]
                ?.AsObject()
                .Select(static queryField => queryField.Key)
                .ToArray();

            resource
                .QueryFieldMappingsByQueryField.Keys.Should()
                .BeEquivalentTo(
                    schemaQueryFieldNames ?? [],
                    $"{resource.RelationalModel.Resource.ResourceName} must carry only its queryFieldMapping entries"
                );
            resource
                .QueryFieldMappingsByQueryField.Values.Should()
                .NotContain(
                    queryField => queryField.Paths.Any(static path => path.Path.Canonical.Contains("[*]")),
                    "derived identification-code fields must not be merged into queryFieldMapping"
                );
        }
    }

    private ConcreteResourceModel Resource(string resourceName) =>
        _resources.Single(resource =>
            resource.RelationalModel.Resource == new QualifiedResourceName("Ed-Fi", resourceName)
        );

    private static JsonObject LoadProjectSchema(string path) =>
        RelationalModelSetSchemaHelpers.RequireObject(
            JsonNode.Parse(File.ReadAllText(path))?["projectSchema"],
            "projectSchema"
        );
}
