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
public class Given_AuthoritativeDs52SampleExtension_RuntimePlanCompilation_GoldenFixture
{
    private const string Ds52FixturePath =
        "../Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json";
    private const string SampleExtensionFixturePath =
        "../Fixtures/authoritative/sample/inputs/sample-api-schema-authoritative.json";
    private const string FixtureRootFolderName = "authoritative";
    private const string FixtureFolderName = "sample";

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
    public void It_should_emit_extension_table_delete_or_merge_contracts_for_one_to_one_and_collection_scopes()
    {
        foreach (var mappingSet in ParseMappingSets(_manifest))
        {
            var extensionTablePlans = ReadResources(mappingSet)
                .Select(ReadOptionalWritePlan)
                .Where(writePlan => writePlan is not null)
                .SelectMany(writePlan => ReadTablePlans(writePlan!))
                .Where(IsSampleExtensionTable)
                .ToArray();

            extensionTablePlans.Should().NotBeEmpty();
            Array
                .Exists(
                    extensionTablePlans,
                    tablePlan => ReadOptionalString(tablePlan, "update_sql_sha256") is not null
                )
                .Should()
                .BeTrue();
            Array
                .Exists(
                    extensionTablePlans,
                    tablePlan => ReadOptionalString(tablePlan, "update_sql_sha256") is null
                )
                .Should()
                .BeTrue();

            var collectionScopedTablePlans = 0;
            var oneToOneScopedTablePlans = 0;

            foreach (var extensionTablePlan in extensionTablePlans)
            {
                var collectionMergePlan = ReadOptionalObject(
                    extensionTablePlan["collection_merge_plan"],
                    "collection_merge_plan"
                );

                if (collectionMergePlan is not null)
                {
                    collectionScopedTablePlans++;
                    ReadOptionalString(extensionTablePlan, "delete_by_parent_sql_sha256").Should().BeNull();
                    ReadRequiredString(collectionMergePlan, "update_by_stable_row_identity_sql_sha256");
                    ReadRequiredString(collectionMergePlan, "delete_by_stable_row_identity_sql_sha256");
                }
                else
                {
                    oneToOneScopedTablePlans++;
                    ReadRequiredString(extensionTablePlan, "delete_by_parent_sql_sha256");
                }
            }

            collectionScopedTablePlans.Should().BeGreaterThan(0);
            oneToOneScopedTablePlans.Should().BeGreaterThan(0);
        }
    }

    [Test]
    public void It_should_emit_deterministic_manifest_output_across_repeated_runs()
    {
        BuildManifest().Should().Be(_manifest);
    }

    [Test]
    public void It_should_emit_populated_projection_inventory_for_authoritative_and_extension_resources()
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
                .Be(125);

            readPlans.Count(readPlan => ReadDescriptorProjectionPlans(readPlan).Count > 0).Should().Be(117);

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
    public void It_should_emit_projection_metadata_for_sample_bus_route()
    {
        foreach (var mappingSet in ParseMappingSets(_manifest))
        {
            var busRouteResource = FindResource(mappingSet, "Sample", "BusRoute");
            var readPlan = ReadRequiredReadPlan(busRouteResource);

            var referencePlans = ReadReferenceIdentityProjectionPlans(readPlan);
            referencePlans
                .Select(ReadProjectionTableIdentity)
                .Should()
                .Equal("sample.BusRoute", "sample.BusRouteProgram");

            var busRouteBindings = ReadBindingsInOrder(referencePlans[0]);
            busRouteBindings
                .Select(binding => ReadRequiredString(binding, "reference_object_path"))
                .Should()
                .Equal("$.busReference", "$.staffEducationOrganizationAssignmentAssociationReference");
            ReadRequiredInt(busRouteBindings[0], "fk_column_ordinal").Should().Be(1);
            ReadRequiredInt(busRouteBindings[1], "fk_column_ordinal").Should().Be(3);

            ReadIdentityFieldOrdinalsInOrder(busRouteBindings[1])
                .Select(identityField => ReadRequiredString(identityField, "reference_json_path"))
                .Should()
                .Equal(
                    "$.staffEducationOrganizationAssignmentAssociationReference.beginDate",
                    "$.staffEducationOrganizationAssignmentAssociationReference.educationOrganizationId",
                    "$.staffEducationOrganizationAssignmentAssociationReference.staffClassificationDescriptor",
                    "$.staffEducationOrganizationAssignmentAssociationReference.staffUniqueId"
                );

            var descriptorPlans = ReadDescriptorProjectionPlans(readPlan);
            descriptorPlans.Should().HaveCount(1);

            var descriptorPlan = descriptorPlans[0];
            ReadRequiredString(descriptorPlan, "select_by_keyset_sql_sha256");

            var descriptorSources = ReadDescriptorSourcesInOrder(descriptorPlan);
            descriptorSources
                .Select(source => ReadRequiredString(source, "descriptor_value_path"))
                .Should()
                .Equal(
                    "$.disabilityDescriptor",
                    "$.staffEducationOrganizationAssignmentAssociationReference.staffClassificationDescriptor",
                    "$.programs[*].programReference.programTypeDescriptor",
                    "$.telephones[*].telephoneNumberTypeDescriptor"
                );
        }
    }

    private static string BuildManifest()
    {
        var authoritativeInputs = new (string FixtureRelativePath, bool IsExtensionProject)[]
        {
            (Ds52FixturePath, false),
            (SampleExtensionFixturePath, true),
        };
        var compiler = new MappingSetCompiler();
        var mappingSets = new[]
        {
            compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(authoritativeInputs, SqlDialect.Pgsql)),
            compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(authoritativeInputs, SqlDialect.Mssql)),
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

    private static bool IsSampleExtensionTable(JsonObject tablePlan)
    {
        var tableIdentity = ReadRequiredObject(tablePlan["table"], "table");
        var tableSchema = ReadRequiredString(tableIdentity, "schema");
        var tableName = ReadRequiredString(tableIdentity, "name");

        return tableSchema == "sample" && tableName.Contains("Extension", StringComparison.Ordinal);
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
