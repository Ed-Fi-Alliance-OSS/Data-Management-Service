// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ThinSlice_RuntimePlanCompilation_Determinism
{
    private const string FixturePath = "Fixtures/runtime-plan-compilation/ApiSchema.json";
    private static readonly JsonSerializerOptions _compactJson = new() { WriteIndented = false };
    private string _manifestRunOne = null!;
    private string _manifestRunTwo = null!;
    private string _manifestWithReversedResourceOrder = null!;

    [SetUp]
    public void Setup()
    {
        _manifestRunOne = BuildManifest(reverseResourceSchemaOrder: false);
        _manifestRunTwo = BuildManifest(reverseResourceSchemaOrder: false);
        _manifestWithReversedResourceOrder = BuildManifest(reverseResourceSchemaOrder: true);
    }

    [Test]
    public void It_should_emit_byte_identical_manifest_across_repeated_compilations()
    {
        _manifestRunOne.Should().Be(_manifestRunTwo);
    }

    [Test]
    public void It_should_emit_identical_manifest_when_effective_schema_resource_input_order_is_reversed()
    {
        _manifestRunOne.Should().Be(_manifestWithReversedResourceOrder);
    }

    [Test]
    public void It_should_keep_sql_hashes_and_binding_metadata_stable_for_each_mapping_set_key()
    {
        var runOneFingerprints = ReadResourcePlanFingerprintsByMappingSetKey(_manifestRunOne);
        var runTwoFingerprints = ReadResourcePlanFingerprintsByMappingSetKey(_manifestRunTwo);

        runTwoFingerprints.Keys.Should().BeEquivalentTo(runOneFingerprints.Keys);

        foreach (var mappingSetKey in runOneFingerprints.Keys)
        {
            runTwoFingerprints.Should().ContainKey(mappingSetKey);
            runTwoFingerprints[mappingSetKey].Should().BeEquivalentTo(runOneFingerprints[mappingSetKey]);
        }
    }

    private static string BuildManifest(bool reverseResourceSchemaOrder)
    {
        var compiler = new MappingSetCompiler();
        var mappingSets = new[]
        {
            compiler.Compile(
                ThinSliceFixtureModelSetBuilder.Build(
                    FixturePath,
                    SqlDialect.Pgsql,
                    reverseResourceSchemaOrder
                )
            ),
            compiler.Compile(
                ThinSliceFixtureModelSetBuilder.Build(
                    FixturePath,
                    SqlDialect.Mssql,
                    reverseResourceSchemaOrder
                )
            ),
        };

        return ThinSliceMappingSetManifestJsonEmitter.Emit(mappingSets);
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, ResourcePlanFingerprint>
    > ReadResourcePlanFingerprintsByMappingSetKey(string manifest)
    {
        Dictionary<string, IReadOnlyDictionary<string, ResourcePlanFingerprint>> fingerprintsByMappingSet =
        [];

        foreach (var mappingSetObject in ParseMappingSetObjects(manifest))
        {
            var mappingSetKey = ReadMappingSetKey(mappingSetObject);
            var resources = mappingSetObject["resources"] as JsonArray;

            if (resources is null)
            {
                throw new InvalidOperationException("Manifest mapping set resources array is required.");
            }

            Dictionary<string, ResourcePlanFingerprint> resourceFingerprints = [];

            foreach (var resourceEntry in resources)
            {
                var resourceObject = resourceEntry as JsonObject;

                if (resourceObject is null)
                {
                    throw new InvalidOperationException("Manifest resources entries must be JSON objects.");
                }

                if (!resourceObject.ContainsKey("write_plan") || !resourceObject.ContainsKey("read_plan"))
                {
                    throw new InvalidOperationException(
                        "Manifest resource entries must contain write_plan and read_plan properties."
                    );
                }

                var resourceIdentity = ReadResourceIdentity(resourceObject);
                var writePlanNode = resourceObject["write_plan"];
                var readPlanNode = resourceObject["read_plan"];
                var writePlanIsNull = writePlanNode is null;
                var readPlanIsNull = readPlanNode is null;

                string? tablePlansInDependencyOrderJson = null;

                if (!writePlanIsNull)
                {
                    var writePlan = RequireObject(writePlanNode, "write_plan");
                    var tablePlans = RequireArray(
                        writePlan["table_plans_in_dependency_order"],
                        "table_plans_in_dependency_order"
                    );
                    ValidateWritePlanTableInventory(tablePlans);
                    tablePlansInDependencyOrderJson = tablePlans.ToJsonString(_compactJson);
                }

                string? selectByKeysetSqlSha256 = null;

                if (!readPlanIsNull)
                {
                    var readPlan = RequireObject(readPlanNode, "read_plan");
                    selectByKeysetSqlSha256 = RequireString(readPlan, "select_by_keyset_sql_sha256");
                }

                resourceFingerprints[resourceIdentity] = new ResourcePlanFingerprint(
                    WritePlanIsNull: writePlanIsNull,
                    ReadPlanIsNull: readPlanIsNull,
                    SelectByKeysetSqlSha256: selectByKeysetSqlSha256,
                    TablePlansInDependencyOrderJson: tablePlansInDependencyOrderJson
                );
            }

            fingerprintsByMappingSet[mappingSetKey] = resourceFingerprints;
        }

        return fingerprintsByMappingSet;
    }

    private static IReadOnlyList<JsonObject> ParseMappingSetObjects(string manifest)
    {
        var rootNode = JsonNode.Parse(manifest);

        if (rootNode is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Manifest root must be a JSON object.");
        }

        var mappingSets = rootObject["mapping_sets"] as JsonArray;

        if (mappingSets is null)
        {
            throw new InvalidOperationException("Manifest mapping_sets property is required.");
        }

        return mappingSets
            .Select(mappingSet =>
                mappingSet as JsonObject
                ?? throw new InvalidOperationException("Manifest mapping_sets entries must be JSON objects.")
            )
            .ToArray();
    }

    private static string ReadMappingSetKey(JsonObject mappingSetObject)
    {
        var keyObject = RequireObject(mappingSetObject["mapping_set_key"], "mapping_set_key");
        var effectiveSchemaHash = RequireString(keyObject, "effective_schema_hash");
        var dialect = RequireString(keyObject, "dialect");
        var relationalMappingVersion = RequireString(keyObject, "relational_mapping_version");

        return $"{effectiveSchemaHash}|{dialect}|{relationalMappingVersion}";
    }

    private static string ReadResourceIdentity(JsonObject resourceObject)
    {
        var resourceIdentity = RequireObject(resourceObject["resource"], "resource");
        var projectName = RequireString(resourceIdentity, "project_name");
        var resourceName = RequireString(resourceIdentity, "resource_name");

        return $"{projectName}.{resourceName}";
    }

    private static void ValidateWritePlanTableInventory(JsonArray tablePlans)
    {
        foreach (var tablePlanNode in tablePlans)
        {
            var tablePlan = RequireObject(tablePlanNode, "table_plans_in_dependency_order entry");
            _ = RequireObject(tablePlan["table"], "table");
            _ = RequireString(tablePlan, "insert_sql_sha256");
            _ = ReadOptionalString(tablePlan, "update_sql_sha256");
            _ = ReadOptionalString(tablePlan, "delete_by_parent_sql_sha256");

            var batching = RequireObject(tablePlan["bulk_insert_batching"], "bulk_insert_batching");
            _ = RequireInt(batching, "max_rows_per_batch");
            _ = RequireInt(batching, "parameters_per_row");
            _ = RequireInt(batching, "max_parameters_per_command");

            var columnBindings = RequireArray(
                tablePlan["column_bindings_in_order"],
                "column_bindings_in_order"
            );

            foreach (var columnBindingNode in columnBindings)
            {
                var columnBinding = RequireObject(columnBindingNode, "column_bindings_in_order entry");
                _ = RequireString(columnBinding, "column_name");
                _ = RequireString(columnBinding, "column_kind");
                _ = RequireString(columnBinding, "parameter_name");
                _ = RequireObject(columnBinding["write_value_source"], "write_value_source");
            }

            var keyUnificationPlans = RequireArray(
                tablePlan["key_unification_plans"],
                "key_unification_plans"
            );

            foreach (var keyUnificationPlanNode in keyUnificationPlans)
            {
                var keyUnificationPlan = RequireObject(keyUnificationPlanNode, "key_unification_plans entry");
                _ = RequireString(keyUnificationPlan, "canonical_column_name");
                _ = RequireInt(keyUnificationPlan, "canonical_binding_index");

                var membersInOrder = RequireArray(keyUnificationPlan["members_in_order"], "members_in_order");

                foreach (var memberNode in membersInOrder)
                {
                    var member = RequireObject(memberNode, "members_in_order entry");
                    _ = RequireString(member, "kind");
                    _ = RequireString(member, "member_path_column_name");
                    _ = RequireString(member, "relative_path");
                    _ = ReadOptionalString(member, "presence_column_name");
                    _ = ReadOptionalInt(member, "presence_binding_index");
                    _ = RequireBool(member, "presence_is_synthetic");
                }
            }
        }
    }

    private static JsonArray RequireArray(JsonNode? node, string propertyName)
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

    private static JsonObject RequireObject(JsonNode? node, string propertyName)
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

    private static string RequireString(JsonObject node, string propertyName)
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

    private static int RequireInt(JsonObject node, string propertyName)
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

    private static int? ReadOptionalInt(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<int>(),
            null => null,
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be an integer or null."
            ),
        };
    }

    private static bool RequireBool(JsonObject node, string propertyName)
    {
        return node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<bool>(),
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be a boolean."
            ),
        };
    }

    private sealed record ResourcePlanFingerprint(
        bool WritePlanIsNull,
        bool ReadPlanIsNull,
        string? SelectByKeysetSqlSha256,
        string? TablePlansInDependencyOrderJson
    );
}
