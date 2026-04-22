// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_AuthoritativeDs52_RuntimePlanCompilation_GoldenFixture
{
    private const string FixturePath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";
    private const string FixtureRootFolderName = "authoritative";
    private const string FixtureFolderName = "ds-5.2";

    private string _diffOutput = null!;
    private string _manifest = null!;

    [SetUp]
    public void Setup()
    {
        var result = RuntimePlanCompilationGoldenFixtureTestHelper.BuildAndDiffManifest(
            FixtureRootFolderName,
            FixtureFolderName,
            BuildManifest
        );

        _manifest = result.Manifest;
        _diffOutput = result.DiffOutput;
    }

    [Test]
    public void It_should_match_the_expected_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }

    [Test]
    public void It_should_emit_collection_merge_metadata_for_collection_child_tables_and_no_non_collection_children_in_the_ds52_fixture()
    {
        var childTablePlans = ParseMappingSets(_manifest)
            .SelectMany(ReadResources)
            .Select(ReadOptionalWritePlan)
            .Where(writePlan => writePlan is not null)
            .SelectMany(writePlan => ReadTablePlans(writePlan!).Skip(1))
            .ToArray();

        childTablePlans.Should().NotBeEmpty();

        var collectionChildTablePlans = 0;
        var nonCollectionChildTablePlans = 0;

        foreach (var childTablePlan in childTablePlans)
        {
            var collectionMergePlan = ReadOptionalObject(
                childTablePlan["collection_merge_plan"],
                "collection_merge_plan"
            );

            if (collectionMergePlan is not null)
            {
                collectionChildTablePlans++;
                ReadOptionalString(childTablePlan, "delete_by_parent_sql_sha256").Should().BeNull();
                ReadRequiredString(collectionMergePlan, "update_by_stable_row_identity_sql_sha256");
                ReadRequiredString(collectionMergePlan, "delete_by_stable_row_identity_sql_sha256");
                ReadRequiredArray(
                    collectionMergePlan["compare_binding_indexes_in_order"],
                    "compare_binding_indexes_in_order"
                )
                    .Count.Should()
                    .BeGreaterThan(0);
            }
            else
            {
                nonCollectionChildTablePlans++;
                ReadRequiredString(childTablePlan, "delete_by_parent_sql_sha256");
            }
        }

        collectionChildTablePlans.Should().BeGreaterThan(0);
        nonCollectionChildTablePlans.Should().Be(0);
    }

    [Test]
    public void It_should_emit_null_write_plan_for_descriptor_resources()
    {
        foreach (var mappingSet in ParseMappingSets(_manifest))
        {
            var descriptorResources = ReadResources(mappingSet).Where(IsDescriptorResource).ToArray();

            descriptorResources.Should().NotBeEmpty();

            foreach (var descriptorResource in descriptorResources)
            {
                descriptorResource["write_plan"].Should().BeNull();
            }
        }
    }

    [Test]
    public void It_should_emit_populated_projection_inventory_for_authoritative_resources()
    {
        foreach (var mappingSet in ParseMappingSets(_manifest))
        {
            var readPlans = ReadResources(mappingSet)
                .Select(ReadOptionalReadPlan)
                .Where(readPlan => readPlan is not null)
                .Select(readPlan => readPlan!)
                .ToArray();

            readPlans
                .Count(readPlan => ReadReferenceIdentityProjectionPlans(readPlan).Count > 0)
                .Should()
                .Be(122);

            readPlans.Count(readPlan => ReadDescriptorProjectionPlans(readPlan).Count > 0).Should().Be(114);

            var descriptorPlans = readPlans.SelectMany(ReadDescriptorProjectionPlans).ToArray();
            descriptorPlans.Should().NotBeEmpty();

            foreach (var descriptorPlan in descriptorPlans)
            {
                ReadRequiredString(descriptorPlan, "select_by_keyset_sql_sha256");

                var resultShape = ReadRequiredObject(descriptorPlan["result_shape"], "result_shape");
                ReadRequiredInt(resultShape, "descriptor_id_ordinal").Should().Be(0);
                ReadRequiredInt(resultShape, "uri_ordinal").Should().Be(1);
            }
        }
    }

    [Test]
    public void It_should_emit_reference_identity_alias_query_capabilities_for_authoritative_resources()
    {
        AuthoritativeManifestQueryCapabilityAssertions.AssertRootColumnFields(
            _manifest,
            AuthoritativeManifestQueryCapabilityAssertions.RootColumnField(
                "Ed-Fi",
                "Section",
                "schoolId",
                "$.courseOfferingReference.schoolId",
                "CourseOffering_SchoolReferenceSchoolId"
            ),
            AuthoritativeManifestQueryCapabilityAssertions.RootColumnField(
                "Ed-Fi",
                "SurveySectionResponseEducationOrganizationTargetAssociation",
                "namespace",
                "$.surveySectionResponseReference.namespace",
                "SurveySectionResponse_SurveyResponseReferenceNamespace"
            ),
            AuthoritativeManifestQueryCapabilityAssertions.RootColumnField(
                "Ed-Fi",
                "SurveySectionResponseEducationOrganizationTargetAssociation",
                "surveyIdentifier",
                "$.surveySectionResponseReference.surveyIdentifier",
                "SurveySectionResponse_SurveyResponseReferenceSurveyIdentifier"
            )
        );
    }

    [Test]
    public void It_should_emit_projection_metadata_for_assessment()
    {
        foreach (var mappingSet in ParseMappingSets(_manifest))
        {
            var assessmentResource = FindResource(mappingSet, "Ed-Fi", "Assessment");
            var readPlan = ReadRequiredReadPlan(assessmentResource);

            var referencePlans = ReadReferenceIdentityProjectionPlans(readPlan);
            referencePlans
                .Select(ReadProjectionTableIdentity)
                .Should()
                .Equal("edfi.Assessment", "edfi.AssessmentProgram", "edfi.AssessmentSection");

            var assessmentBindings = ReadBindingsInOrder(referencePlans[0]);
            assessmentBindings
                .Select(binding => ReadRequiredString(binding, "reference_object_path"))
                .Should()
                .Equal(
                    "$.contentStandard.mandatingEducationOrganizationReference",
                    "$.educationOrganizationReference"
                );
            ReadRequiredInt(assessmentBindings[0], "fk_column_ordinal").Should().Be(3);
            ReadRequiredInt(assessmentBindings[1], "fk_column_ordinal").Should().Be(1);

            ReadIdentityFieldOrdinalsInOrder(assessmentBindings[0])
                .Select(identityField => ReadRequiredString(identityField, "reference_json_path"))
                .Should()
                .Equal("$.contentStandard.mandatingEducationOrganizationReference.educationOrganizationId");

            var descriptorPlans = ReadDescriptorProjectionPlans(readPlan);
            descriptorPlans.Should().HaveCount(1);

            var descriptorPlan = descriptorPlans[0];
            ReadRequiredString(descriptorPlan, "select_by_keyset_sql_sha256");

            var descriptorSources = ReadDescriptorSourcesInOrder(descriptorPlan);
            descriptorSources.Should().HaveCount(14);
            descriptorSources
                .Select(source => ReadRequiredString(source, "descriptor_value_path"))
                .Should()
                .ContainInOrder(
                    "$.assessmentCategoryDescriptor",
                    "$.contentStandard.publicationStatusDescriptor",
                    "$.programs[*].programReference.programTypeDescriptor",
                    "$.scores[*].resultDatatypeTypeDescriptor"
                );
        }
    }

    private static string BuildManifest()
    {
        var compiler = new MappingSetCompiler();
        var mappingSets = new[]
        {
            compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql)),
            compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Mssql)),
        };

        return MappingSetManifestJsonEmitter.Emit(mappingSets);
    }

    private static IReadOnlyList<JsonObject> ParseMappingSets(string manifest)
    {
        var rootNode = JsonNode.Parse(manifest);

        if (rootNode is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Manifest root must be a JSON object.");
        }

        var mappingSets = ReadRequiredArray(rootObject["mapping_sets"], "mapping_sets");

        return mappingSets
            .Select(mappingSetNode => ReadRequiredObject(mappingSetNode, "mapping_sets entry"))
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> ReadResources(JsonObject mappingSet)
    {
        var resources = ReadRequiredArray(mappingSet["resources"], "resources");

        return resources
            .Select(resourceNode => ReadRequiredObject(resourceNode, "resources entry"))
            .ToArray();
    }

    private static JsonObject? ReadOptionalWritePlan(JsonObject resourceEntry)
    {
        return resourceEntry["write_plan"] switch
        {
            JsonObject writePlan => writePlan,
            null => null,
            _ => throw new InvalidOperationException(
                "Manifest property 'write_plan' must be an object or null."
            ),
        };
    }

    private static JsonObject? ReadOptionalReadPlan(JsonObject resourceEntry)
    {
        return resourceEntry["read_plan"] switch
        {
            JsonObject readPlan => readPlan,
            null => null,
            _ => throw new InvalidOperationException(
                "Manifest property 'read_plan' must be an object or null."
            ),
        };
    }

    private static JsonObject ReadRequiredReadPlan(JsonObject resourceEntry)
    {
        return ReadOptionalReadPlan(resourceEntry)
            ?? throw new InvalidOperationException("Manifest property 'read_plan' is required.");
    }

    private static JsonObject FindResource(JsonObject mappingSet, string projectName, string resourceName)
    {
        return ReadResources(mappingSet)
            .Single(resource =>
            {
                var identity = ReadRequiredObject(resource["resource"], "resource");

                return ReadRequiredString(identity, "project_name") == projectName
                    && ReadRequiredString(identity, "resource_name") == resourceName;
            });
    }

    private static IReadOnlyList<JsonObject> ReadTablePlans(JsonObject writePlan)
    {
        var tablePlans = ReadRequiredArray(
            writePlan["table_plans_in_dependency_order"],
            "table_plans_in_dependency_order"
        );

        return tablePlans
            .Select(tablePlanNode =>
                ReadRequiredObject(tablePlanNode, "table_plans_in_dependency_order entry")
            )
            .ToArray();
    }

    private static bool IsDescriptorResource(JsonObject resourceEntry)
    {
        var resourceIdentity = ReadRequiredObject(resourceEntry["resource"], "resource");
        var resourceName = ReadRequiredString(resourceIdentity, "resource_name");

        return resourceName.EndsWith("Descriptor", StringComparison.Ordinal);
    }

    private static IReadOnlyList<JsonObject> ReadReferenceIdentityProjectionPlans(JsonObject readPlan)
    {
        var referencePlans = ReadRequiredArray(
            readPlan["reference_identity_projection_plans_in_dependency_order"],
            "reference_identity_projection_plans_in_dependency_order"
        );

        return referencePlans
            .Select(referencePlanNode =>
                ReadRequiredObject(
                    referencePlanNode,
                    "reference_identity_projection_plans_in_dependency_order entry"
                )
            )
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> ReadBindingsInOrder(JsonObject referencePlan)
    {
        var bindings = ReadRequiredArray(referencePlan["bindings_in_order"], "bindings_in_order");

        return bindings
            .Select(bindingNode => ReadRequiredObject(bindingNode, "bindings_in_order entry"))
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> ReadIdentityFieldOrdinalsInOrder(JsonObject binding)
    {
        var identityFields = ReadRequiredArray(
            binding["identity_field_ordinals_in_order"],
            "identity_field_ordinals_in_order"
        );

        return identityFields
            .Select(identityFieldNode =>
                ReadRequiredObject(identityFieldNode, "identity_field_ordinals_in_order entry")
            )
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> ReadDescriptorProjectionPlans(JsonObject readPlan)
    {
        var descriptorPlans = ReadRequiredArray(
            readPlan["descriptor_projection_plans_in_order"],
            "descriptor_projection_plans_in_order"
        );

        return descriptorPlans
            .Select(descriptorPlanNode =>
                ReadRequiredObject(descriptorPlanNode, "descriptor_projection_plans_in_order entry")
            )
            .ToArray();
    }

    private static IReadOnlyList<JsonObject> ReadDescriptorSourcesInOrder(JsonObject descriptorPlan)
    {
        var descriptorSources = ReadRequiredArray(descriptorPlan["sources_in_order"], "sources_in_order");

        return descriptorSources
            .Select(sourceNode => ReadRequiredObject(sourceNode, "sources_in_order entry"))
            .ToArray();
    }

    private static string ReadProjectionTableIdentity(JsonObject projectionPlan)
    {
        var table = ReadRequiredObject(projectionPlan["table"], "table");

        return $"{ReadRequiredString(table, "schema")}.{ReadRequiredString(table, "name")}";
    }

    private static JsonObject ReadRequiredObject(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be an object."
            ),
        };
    }

    private static JsonObject? ReadOptionalObject(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => null,
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be an object or null."
            ),
        };
    }

    private static JsonArray ReadRequiredArray(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonArray jsonArray => jsonArray,
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be a JSON array."
            ),
        };
    }

    private static string ReadRequiredString(JsonObject node, string propertyName)
    {
        var value = node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException($"Manifest property '{propertyName}' must be a string."),
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Manifest property '{propertyName}' must be non-empty.");
        }

        return value;
    }

    private static int ReadRequiredInt(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<int>(),
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be an integer."
            ),
        };
    }

    private static string? ReadOptionalString(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            null => null,
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be a string or null."
            ),
        };
    }
}
