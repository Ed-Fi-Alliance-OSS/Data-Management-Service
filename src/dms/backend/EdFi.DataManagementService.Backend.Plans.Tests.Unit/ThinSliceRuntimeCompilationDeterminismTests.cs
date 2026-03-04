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

                var resourceIdentity = ReadResourceIdentity(resourceObject);
                var writePlan = RequireObject(resourceObject["write_plan"], "write_plan");
                var readPlan = RequireObject(resourceObject["read_plan"], "read_plan");
                var columnBindingsInOrder = writePlan["column_bindings_in_order"] as JsonArray;

                if (columnBindingsInOrder is null)
                {
                    throw new InvalidOperationException(
                        "Manifest write plan column_bindings_in_order is required."
                    );
                }

                resourceFingerprints[resourceIdentity] = new ResourcePlanFingerprint(
                    InsertSqlSha256: RequireString(writePlan, "insert_sql_sha256"),
                    UpdateSqlSha256: ReadOptionalString(writePlan, "update_sql_sha256"),
                    SelectByKeysetSqlSha256: RequireString(readPlan, "select_by_keyset_sql_sha256"),
                    ColumnBindingsInOrderJson: columnBindingsInOrder.ToJsonString(_compactJson)
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

    private sealed record ResourcePlanFingerprint(
        string InsertSqlSha256,
        string? UpdateSqlSha256,
        string SelectByKeysetSqlSha256,
        string ColumnBindingsInOrderJson
    );
}
