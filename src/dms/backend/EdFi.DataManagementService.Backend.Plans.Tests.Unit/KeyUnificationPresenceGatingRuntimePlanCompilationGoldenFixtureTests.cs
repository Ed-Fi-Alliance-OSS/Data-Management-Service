// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_KeyUnificationPresenceGating_RuntimePlanCompilation_GoldenFixture
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/key-unification-presence-gating/fixture.manifest.json";
    private static readonly QualifiedResourceName _presenceGateResource = new("Ed-Fi", "PresenceGateExample");

    private string _diffOutput = null!;
    private string _manifest = null!;

    [SetUp]
    public void Setup()
    {
        var projectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            "EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj"
        );

        var expectedPath = Path.Combine(
            projectRoot,
            "Fixtures",
            "runtime-plan-compilation",
            "key-unification-presence-gating",
            "expected",
            "mappingset.manifest.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "runtime-plan-compilation",
            "key-unification-presence-gating",
            "actual",
            "mappingset.manifest.json"
        );

        _manifest = BuildManifest();

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, _manifest);

        if (GoldenFixtureTestHelpers.ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, _manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue($"mappingset manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        _diffOutput = GoldenFixtureTestHelpers.RunGitDiff(expectedPath, actualPath);
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
    public void It_should_emit_precomputed_bindings_for_canonical_and_synthetic_presence_columns()
    {
        foreach (var mappingSet in ParseMappingSetObjects(_manifest))
        {
            var writePlan = RequirePresenceGateWritePlan(mappingSet);
            var tablePlans = RequireArray(
                writePlan["table_plans_in_dependency_order"],
                "table_plans_in_dependency_order"
            );
            var rootTablePlan = RequireObject(tablePlans[0], "table_plans_in_dependency_order[0]");
            var columnBindings = RequireArray(
                rootTablePlan["column_bindings_in_order"],
                "column_bindings_in_order"
            );
            var keyUnificationPlans = RequireArray(
                rootTablePlan["key_unification_plans"],
                "key_unification_plans"
            );

            var keyUnificationPlan =
                keyUnificationPlans.Should().ContainSingle().Subject as JsonObject
                ?? throw new InvalidOperationException(
                    "Expected key_unification_plans entry to be an object."
                );
            var canonicalBindingIndex = RequireInt(keyUnificationPlan, "canonical_binding_index");

            ReadWriteSourceKind(columnBindings, canonicalBindingIndex).Should().Be("precomputed");

            var membersInOrder = RequireArray(keyUnificationPlan["members_in_order"], "members_in_order");
            membersInOrder.Should().HaveCountGreaterThan(1);

            foreach (var memberNode in membersInOrder)
            {
                var member = RequireObject(memberNode, "members_in_order entry");
                RequireBool(member, "presence_is_synthetic").Should().BeTrue();
                RequireString(member, "presence_column_name");

                var presenceBindingIndex = ReadOptionalInt(member, "presence_binding_index");
                presenceBindingIndex.Should().NotBeNull();
                ReadWriteSourceKind(columnBindings, presenceBindingIndex!.Value).Should().Be("precomputed");
            }
        }
    }

    [Test]
    public void It_should_emit_stable_sql_hashes_across_repeated_compilation_runs()
    {
        var firstHashes = ReadSqlHashesByMappingSetKey(_manifest);
        var secondHashes = ReadSqlHashesByMappingSetKey(BuildManifest());

        secondHashes.Keys.Should().BeEquivalentTo(firstHashes.Keys);

        foreach (var mappingSetKey in firstHashes.Keys)
        {
            secondHashes[mappingSetKey].Should().Equal(firstHashes[mappingSetKey]);
        }
    }

    private static string BuildManifest()
    {
        var compiler = new MappingSetCompiler();
        var mappingSets = new[]
        {
            compiler.Compile(ThinSliceFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql)),
            compiler.Compile(ThinSliceFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Mssql)),
        };

        return ThinSliceMappingSetManifestJsonEmitter.Emit(mappingSets);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSqlHashesByMappingSetKey(
        string manifest
    )
    {
        Dictionary<string, IReadOnlyList<string>> hashesByMappingSetKey = [];

        foreach (var mappingSet in ParseMappingSetObjects(manifest))
        {
            var mappingSetKey = ReadMappingSetKey(mappingSet);
            var writePlan = RequirePresenceGateWritePlan(mappingSet);
            var tablePlans = RequireArray(
                writePlan["table_plans_in_dependency_order"],
                "table_plans_in_dependency_order"
            );
            var tableHashes = tablePlans
                .Select(tablePlanNode =>
                {
                    var tablePlan = RequireObject(tablePlanNode, "table_plans_in_dependency_order entry");
                    var insertHash = RequireString(tablePlan, "insert_sql_sha256");
                    var updateHash = ReadOptionalString(tablePlan, "update_sql_sha256") ?? "<null>";
                    var deleteHash = ReadOptionalString(tablePlan, "delete_by_parent_sql_sha256") ?? "<null>";

                    return $"{insertHash}|{updateHash}|{deleteHash}";
                })
                .ToArray();

            hashesByMappingSetKey[mappingSetKey] = tableHashes;
        }

        return hashesByMappingSetKey;
    }

    private static string ReadMappingSetKey(JsonObject mappingSet)
    {
        var keyObject = RequireObject(mappingSet["mapping_set_key"], "mapping_set_key");
        var effectiveSchemaHash = RequireString(keyObject, "effective_schema_hash");
        var dialect = RequireString(keyObject, "dialect");
        var relationalMappingVersion = RequireString(keyObject, "relational_mapping_version");

        return $"{effectiveSchemaHash}|{dialect}|{relationalMappingVersion}";
    }

    private static JsonObject RequirePresenceGateWritePlan(JsonObject mappingSet)
    {
        var resources = RequireArray(mappingSet["resources"], "resources");

        var presenceGateResource = resources
            .Select(resourceNode => RequireObject(resourceNode, "resources entry"))
            .Single(resourceEntry =>
            {
                var resourceIdentity = RequireObject(resourceEntry["resource"], "resource");
                var projectName = RequireString(resourceIdentity, "project_name");
                var resourceName = RequireString(resourceIdentity, "resource_name");

                return projectName == _presenceGateResource.ProjectName
                    && resourceName == _presenceGateResource.ResourceName;
            });

        presenceGateResource["read_plan"].Should().BeNull();

        return RequireObject(presenceGateResource["write_plan"], "write_plan");
    }

    private static string ReadWriteSourceKind(JsonArray columnBindings, int bindingIndex)
    {
        if (bindingIndex < 0 || bindingIndex >= columnBindings.Count)
        {
            throw new InvalidOperationException(
                $"Binding index '{bindingIndex}' is outside column binding range."
            );
        }

        var binding = RequireObject(columnBindings[bindingIndex], "column_bindings_in_order entry");
        var source = RequireObject(binding["write_value_source"], "write_value_source");

        return RequireString(source, "kind");
    }

    private static IReadOnlyList<JsonObject> ParseMappingSetObjects(string manifest)
    {
        var rootNode = JsonNode.Parse(manifest);

        if (rootNode is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Manifest root must be a JSON object.");
        }

        var mappingSets = RequireArray(rootObject["mapping_sets"], "mapping_sets");

        return mappingSets.Select(mappingSet => RequireObject(mappingSet, "mapping_sets entry")).ToArray();
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
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_KeyUnificationPresenceGating_RuntimePlanCompilation_Negative(SqlDialect dialect)
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/key-unification-presence-gating/fixture.manifest.json";
    private static readonly QualifiedResourceName _presenceGateResource = new("Ed-Fi", "PresenceGateExample");

    [Test]
    public void It_should_fail_with_a_deterministic_diagnostic_when_synthetic_presence_constraints_are_missing()
    {
        var compiler = new MappingSetCompiler();
        var firstModelSet = BuildModelWithRemovedSyntheticPresenceNullOrTrueConstraints(
            reverseResourceSchemaOrder: false,
            reverseFixtureInputOrder: false
        );
        var secondModelSet = BuildModelWithRemovedSyntheticPresenceNullOrTrueConstraints(
            reverseResourceSchemaOrder: true,
            reverseFixtureInputOrder: true
        );

        var firstException = Assert.Throws<InvalidOperationException>(() => compiler.Compile(firstModelSet));
        var secondException = Assert.Throws<InvalidOperationException>(() =>
            compiler.Compile(secondModelSet)
        );

        firstException.Should().NotBeNull();
        secondException.Should().NotBeNull();
        secondException!.Message.Should().Be(firstException!.Message);
        secondException
            .Message.Should()
            .Contain(
                "Cannot compile key-unification plan for 'edfi.PresenceGateExample': synthetic presence column"
            )
            .And.Contain("must define a matching NullOrTrue constraint.");
    }

    private DerivedRelationalModelSet BuildModelWithRemovedSyntheticPresenceNullOrTrueConstraints(
        bool reverseResourceSchemaOrder,
        bool reverseFixtureInputOrder
    )
    {
        var modelSet = ThinSliceFixtureModelSetBuilder.Build(
            FixturePath,
            dialect,
            reverseResourceSchemaOrder,
            reverseFixtureInputOrder
        );
        var resources = modelSet.ConcreteResourcesInNameOrder.ToArray();
        var resourceIndex = Array.FindIndex(
            resources,
            resource => resource.ResourceKey.Resource == _presenceGateResource
        );

        resourceIndex.Should().BeGreaterOrEqualTo(0);

        var resourceModel = resources[resourceIndex].RelationalModel;
        var rootTable = resourceModel.Root;
        var syntheticPresenceColumns = ResolveSyntheticPresenceColumns(rootTable).ToHashSet();

        syntheticPresenceColumns.Should().NotBeEmpty();

        var reducedConstraints = rootTable
            .Constraints.Where(constraint =>
                constraint is not TableConstraint.NullOrTrue nullOrTrue
                || !syntheticPresenceColumns.Contains(nullOrTrue.Column)
            )
            .ToArray();
        var mutatedRootTable = rootTable with { Constraints = reducedConstraints };

        mutatedRootTable
            .Constraints.OfType<TableConstraint.NullOrTrue>()
            .Where(constraint => syntheticPresenceColumns.Contains(constraint.Column))
            .Should()
            .BeEmpty();

        var mutatedTables = resourceModel
            .TablesInDependencyOrder.Select(table =>
                table.Table.Equals(rootTable.Table) ? mutatedRootTable : table
            )
            .ToArray();
        var mutatedRelationalModel = resourceModel with
        {
            Root = mutatedRootTable,
            TablesInDependencyOrder = mutatedTables,
        };

        resources[resourceIndex] = resources[resourceIndex] with { RelationalModel = mutatedRelationalModel };

        return modelSet with
        {
            ConcreteResourcesInNameOrder = resources,
        };
    }

    private static IReadOnlyList<DbColumnName> ResolveSyntheticPresenceColumns(DbTableModel rootTable)
    {
        var keyUnificationClass = rootTable.KeyUnificationClasses.Should().ContainSingle().Subject;

        List<DbColumnName> syntheticPresenceColumns = [];

        foreach (var memberPathColumnName in keyUnificationClass.MemberPathColumns)
        {
            var memberPathColumn = rootTable.Columns.Single(column =>
                column.ColumnName.Equals(memberPathColumnName)
            );
            var aliasStorage = memberPathColumn
                .Storage.Should()
                .BeOfType<ColumnStorage.UnifiedAlias>()
                .Subject;

            aliasStorage.PresenceColumn.Should().NotBeNull();

            if (aliasStorage.PresenceColumn is not DbColumnName presenceColumn)
            {
                continue;
            }

            var presenceColumnModel = rootTable.Columns.Single(column =>
                column.ColumnName.Equals(presenceColumn)
            );

            if (presenceColumnModel.SourceJsonPath is null)
            {
                syntheticPresenceColumns.Add(presenceColumn);
            }
        }

        return syntheticPresenceColumns;
    }
}
