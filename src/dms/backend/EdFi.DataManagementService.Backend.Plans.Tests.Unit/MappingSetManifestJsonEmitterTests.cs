// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_MappingSetManifestJsonEmitter
{
    private const string FixturePath = "Fixtures/runtime-plan-compilation/ApiSchema.json";
    private const string CollectionsNestedExtensionFixturePath =
        "Fixtures/runtime-plan-compilation/collections-nested-extension/fixture.manifest.json";
    private string _manifest = null!;

    [SetUp]
    public void Setup()
    {
        var mappingSets = BuildPermutedMappingSets(FixturePath, reverseMappingSetOrder: true);
        _manifest = MappingSetManifestJsonEmitter.Emit(mappingSets);
    }

    [Test]
    public void It_should_emit_lf_only_line_endings_with_no_trailing_whitespace()
    {
        _manifest.Should().EndWith("\n");
        _manifest.Should().NotContain("\r");

        foreach (var line in _manifest.Split('\n'))
        {
            line.Should().NotMatchRegex("[ \\t]+$");
        }
    }

    [Test]
    public void It_should_emit_deterministically_when_mapping_set_and_dictionary_inputs_are_permuted()
    {
        var forwardInput = BuildPermutedMappingSets(FixturePath, reverseMappingSetOrder: false);
        var reverseInput = BuildPermutedMappingSets(FixturePath, reverseMappingSetOrder: true);

        var manifestFromForwardInput = MappingSetManifestJsonEmitter.Emit(forwardInput);
        var manifestFromReverseInput = MappingSetManifestJsonEmitter.Emit(reverseInput);

        manifestFromForwardInput.Should().Be(manifestFromReverseInput);

        ReadMappingSetDialects(manifestFromForwardInput).Should().Equal("mssql", "pgsql");
    }

    [Test]
    public void It_should_include_all_resources_in_deterministic_name_order()
    {
        var resourceNamesByDialect = ReadResourceNamesByDialect(_manifest);

        resourceNamesByDialect.Keys.Should().BeEquivalentTo("mssql", "pgsql");
        resourceNamesByDialect["mssql"]
            .Should()
            .Equal(
                "Ed-Fi.AcademicSubjectDescriptor",
                "Ed-Fi.Program",
                "Ed-Fi.School",
                "Ed-Fi.Student",
                "Ed-Fi.StudentAddressCollection"
            );
        resourceNamesByDialect["pgsql"]
            .Should()
            .Equal(
                "Ed-Fi.AcademicSubjectDescriptor",
                "Ed-Fi.Program",
                "Ed-Fi.School",
                "Ed-Fi.Student",
                "Ed-Fi.StudentAddressCollection"
            );
    }

    [Test]
    public void It_should_emit_write_and_read_plan_properties_as_explicit_object_or_null()
    {
        var planPresenceByDialect = ReadPlanPresenceByDialect(_manifest);

        planPresenceByDialect.Keys.Should().BeEquivalentTo("mssql", "pgsql");

        foreach (var dialect in planPresenceByDialect.Keys)
        {
            planPresenceByDialect[dialect]
                ["Ed-Fi.AcademicSubjectDescriptor"]
                .Should()
                .Be(new PlanPresence(WritePlanIsNull: true, ReadPlanIsNull: true));
            planPresenceByDialect[dialect]
                ["Ed-Fi.Program"]
                .Should()
                .Be(new PlanPresence(WritePlanIsNull: false, ReadPlanIsNull: false));
            planPresenceByDialect[dialect]
                ["Ed-Fi.School"]
                .Should()
                .Be(new PlanPresence(WritePlanIsNull: false, ReadPlanIsNull: false));
            planPresenceByDialect[dialect]
                ["Ed-Fi.Student"]
                .Should()
                .Be(new PlanPresence(WritePlanIsNull: false, ReadPlanIsNull: false));
            planPresenceByDialect[dialect]
                ["Ed-Fi.StudentAddressCollection"]
                .Should()
                .Be(new PlanPresence(WritePlanIsNull: false, ReadPlanIsNull: false));
        }
    }

    [Test]
    public void It_should_emit_single_table_read_plan_inventory_with_projection_placeholders()
    {
        var programResource = new QualifiedResourceName("Ed-Fi", "Program");
        var compiledMappingSets = BuildPermutedMappingSets(FixturePath, reverseMappingSetOrder: false);
        var summariesByDialect = ReadReadPlanSummariesByDialect(_manifest, "Ed-Fi", "Program");
        var expectedTableNamesByDialect = ReadCompiledReadTableNamesByDialect(
            compiledMappingSets,
            programResource
        );
        var expectedKeysetTableByDialect = ReadCompiledKeysetTableSummaryByDialect(
            compiledMappingSets,
            programResource
        );

        summariesByDialect.Keys.Should().BeEquivalentTo("mssql", "pgsql");

        foreach (var dialect in summariesByDialect.Keys)
        {
            var readPlanSummary = summariesByDialect[dialect];

            readPlanSummary.KeysetTable.Should().Be(expectedKeysetTableByDialect[dialect]);
            readPlanSummary
                .TablePlans.Select(summary => summary.TableName)
                .Should()
                .Equal(expectedTableNamesByDialect[dialect]);
            readPlanSummary.TablePlans.Should().ContainSingle();
            readPlanSummary
                .TablePlans.Select(summary => summary.SelectByKeysetSqlSha256)
                .Should()
                .OnlyContain(static value => !string.IsNullOrWhiteSpace(value));
            readPlanSummary
                .TablePlans.Select(summary => summary.SelectListColumnsInOrder.Count)
                .Should()
                .OnlyContain(static count => count > 0);
            readPlanSummary
                .TablePlans.Select(summary => summary.OrderByKeyColumnsInOrder.Count)
                .Should()
                .OnlyContain(static count => count > 0);
            readPlanSummary.ReferenceIdentityProjectionPlanCount.Should().Be(0);
            readPlanSummary.DescriptorProjectionPlanCount.Should().Be(0);
        }
    }

    [Test]
    public void It_should_emit_order_by_key_columns_in_compiled_sql_order_for_non_document_id_first_keys()
    {
        var mappingSets = BuildMappingSetsWithNonDocumentIdFirstStudentReadModel();
        var manifest = MappingSetManifestJsonEmitter.Emit(mappingSets);
        var orderByColumnsByDialect = ReadStudentOrderByColumnsByDialect(manifest);

        orderByColumnsByDialect.Keys.Should().BeEquivalentTo("mssql", "pgsql");
        orderByColumnsByDialect["mssql"].Should().Equal("SchoolYear", "DocumentId");
        orderByColumnsByDialect["pgsql"].Should().Equal("SchoolYear", "DocumentId");
    }

    [Test]
    public void It_should_emit_multi_table_read_plan_inventory_in_dependency_order()
    {
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var mappingSets = BuildPermutedMappingSets(
            CollectionsNestedExtensionFixturePath,
            reverseMappingSetOrder: false
        );
        var manifest = MappingSetManifestJsonEmitter.Emit(mappingSets);
        var summariesByDialect = ReadReadPlanSummariesByDialect(manifest, "Ed-Fi", "School");
        var expectedTableNamesByDialect = ReadCompiledReadTableNamesByDialect(mappingSets, schoolResource);
        var expectedKeysetTableByDialect = ReadCompiledKeysetTableSummaryByDialect(
            mappingSets,
            schoolResource
        );

        summariesByDialect.Keys.Should().BeEquivalentTo("mssql", "pgsql");

        foreach (var dialect in summariesByDialect.Keys)
        {
            var readPlanSummary = summariesByDialect[dialect];

            readPlanSummary.KeysetTable.Should().Be(expectedKeysetTableByDialect[dialect]);
            readPlanSummary
                .TablePlans.Select(summary => summary.TableName)
                .Should()
                .Equal(expectedTableNamesByDialect[dialect]);
            readPlanSummary.TablePlans.Should().HaveCountGreaterThan(1);
            readPlanSummary
                .TablePlans.Select(summary => summary.SelectByKeysetSqlSha256)
                .Should()
                .OnlyContain(static value => !string.IsNullOrWhiteSpace(value));
            readPlanSummary
                .TablePlans.Select(summary => summary.SelectListColumnsInOrder.Count)
                .Should()
                .OnlyContain(static count => count > 0);
            readPlanSummary
                .TablePlans.Select(summary => summary.OrderByKeyColumnsInOrder.Count)
                .Should()
                .OnlyContain(static count => count > 0);
            readPlanSummary.ReferenceIdentityProjectionPlanCount.Should().Be(0);
            readPlanSummary.DescriptorProjectionPlanCount.Should().Be(0);
        }
    }

    [Test]
    public void It_should_emit_write_plan_table_inventory_in_dependency_order_with_required_metadata()
    {
        var summariesByDialect = ReadWriteTablePlanSummariesByDialect(
            _manifest,
            "Ed-Fi",
            "StudentAddressCollection"
        );
        var expectedTableNamesByDialect = ReadCompiledTableNamesByDialect(
            BuildPermutedMappingSets(reverseMappingSetOrder: false),
            new QualifiedResourceName("Ed-Fi", "StudentAddressCollection")
        );

        summariesByDialect.Keys.Should().BeEquivalentTo("mssql", "pgsql");

        foreach (var dialect in summariesByDialect.Keys)
        {
            var tableSummaries = summariesByDialect[dialect];
            tableSummaries
                .Select(summary => summary.TableName)
                .Should()
                .Equal(expectedTableNamesByDialect[dialect]);

            tableSummaries
                .Select(summary => summary.InsertSqlSha256)
                .Should()
                .OnlyContain(static value => !string.IsNullOrWhiteSpace(value));
            tableSummaries
                .Select(summary => summary.ColumnBindingCount)
                .Should()
                .OnlyContain(static count => count > 0);
            tableSummaries
                .Select(summary => summary.BulkInsertBatching.MaxRowsPerBatch)
                .Should()
                .OnlyContain(static maxRows => maxRows > 0);
            tableSummaries
                .Select(summary => summary.BulkInsertBatching.ParametersPerRow)
                .Should()
                .OnlyContain(static width => width > 0);
            tableSummaries
                .Select(summary => summary.BulkInsertBatching.MaxParametersPerCommand)
                .Should()
                .OnlyContain(static maxParams => maxParams > 0);

            tableSummaries[0].DeleteByParentSqlSha256.Should().BeNull();

            if (tableSummaries.Count > 1)
            {
                tableSummaries
                    .Skip(1)
                    .Select(summary => summary.DeleteByParentSqlSha256)
                    .Should()
                    .OnlyContain(static hash => !string.IsNullOrWhiteSpace(hash));
            }
        }
    }

    [Test]
    public void It_should_preserve_key_unification_member_order_in_manifest()
    {
        var expectedMemberPathsByDialect = ReadCompiledKeyUnificationMemberPathsByDialect(
            BuildPermutedMappingSets(reverseMappingSetOrder: false),
            new QualifiedResourceName("Ed-Fi", "Program")
        );
        var actualMemberPathsByDialect = ReadManifestKeyUnificationMemberPathsByDialect(
            _manifest,
            "Ed-Fi",
            "Program"
        );

        actualMemberPathsByDialect.Keys.Should().BeEquivalentTo("mssql", "pgsql");

        foreach (var dialect in actualMemberPathsByDialect.Keys)
        {
            actualMemberPathsByDialect[dialect].Should().Equal(expectedMemberPathsByDialect[dialect]);
        }
    }

    private static IReadOnlyList<MappingSet> BuildPermutedMappingSets(bool reverseMappingSetOrder)
    {
        return BuildPermutedMappingSets(FixturePath, reverseMappingSetOrder);
    }

    private static IReadOnlyList<MappingSet> BuildPermutedMappingSets(
        string fixturePath,
        bool reverseMappingSetOrder
    )
    {
        var compiler = new MappingSetCompiler();
        var pgsql = compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(fixturePath, SqlDialect.Pgsql));
        var mssql = compiler.Compile(RuntimePlanFixtureModelSetBuilder.Build(fixturePath, SqlDialect.Mssql));

        var mappingSets = new[] { PermutePlanDictionaries(pgsql), PermutePlanDictionaries(mssql) };

        if (reverseMappingSetOrder)
        {
            Array.Reverse(mappingSets);
        }

        return mappingSets;
    }

    private static IReadOnlyList<MappingSet> BuildMappingSetsWithNonDocumentIdFirstStudentReadModel()
    {
        return BuildPermutedMappingSets(reverseMappingSetOrder: false)
            .Select(InjectNonDocumentIdFirstStudentReadPlan)
            .ToArray();
    }

    private static MappingSet InjectNonDocumentIdFirstStudentReadPlan(MappingSet mappingSet)
    {
        var studentResource = new QualifiedResourceName("Ed-Fi", "Student");
        var model = CreateRootOnlyModelWithNonDocumentIdFirstKeyOrder(studentResource);
        var readPlan = new RootOnlyReadPlanCompiler(mappingSet.Key.Dialect).Compile(model);
        var readPlansByResource = mappingSet.ReadPlansByResource.ToDictionary(
            entry => entry.Key,
            entry => entry.Value
        );

        readPlansByResource[studentResource] = readPlan;

        return mappingSet with
        {
            ReadPlansByResource = readPlansByResource,
        };
    }

    private static MappingSet PermutePlanDictionaries(MappingSet mappingSet)
    {
        var permutedWritePlans = mappingSet
            .WritePlansByResource.OrderByDescending(
                entry => ResourceSortKey(entry.Key),
                StringComparer.Ordinal
            )
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        var permutedReadPlans = mappingSet
            .ReadPlansByResource.OrderByDescending(
                entry => ResourceSortKey(entry.Key),
                StringComparer.Ordinal
            )
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        return mappingSet with
        {
            WritePlansByResource = permutedWritePlans,
            ReadPlansByResource = permutedReadPlans,
        };
    }

    private static string ResourceSortKey(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}.{resource.ResourceName}";
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadCompiledTableNamesByDialect(
        IReadOnlyList<MappingSet> mappingSets,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, IReadOnlyList<string>> tableNamesByDialect = [];

        foreach (var mappingSet in mappingSets)
        {
            var dialect = PlanManifestConventions.ToManifestDialect(mappingSet.Key.Dialect);

            if (!mappingSet.WritePlansByResource.TryGetValue(resource, out var writePlan))
            {
                throw new InvalidOperationException(
                    $"Compiled write plan for resource '{resource.ProjectName}.{resource.ResourceName}' is required."
                );
            }

            tableNamesByDialect[dialect] = writePlan
                .TablePlansInDependencyOrder.Select(tablePlan => tablePlan.TableModel.Table.Name)
                .ToArray();
        }

        return tableNamesByDialect;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadCompiledReadTableNamesByDialect(
        IReadOnlyList<MappingSet> mappingSets,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, IReadOnlyList<string>> tableNamesByDialect = [];

        foreach (var mappingSet in mappingSets)
        {
            var dialect = PlanManifestConventions.ToManifestDialect(mappingSet.Key.Dialect);

            if (!mappingSet.ReadPlansByResource.TryGetValue(resource, out var readPlan))
            {
                throw new InvalidOperationException(
                    $"Compiled read plan for resource '{resource.ProjectName}.{resource.ResourceName}' is required."
                );
            }

            tableNamesByDialect[dialect] = readPlan
                .TablePlansInDependencyOrder.Select(tablePlan => tablePlan.TableModel.Table.Name)
                .ToArray();
        }

        return tableNamesByDialect;
    }

    private static IReadOnlyDictionary<string, KeysetTableSummary> ReadCompiledKeysetTableSummaryByDialect(
        IReadOnlyList<MappingSet> mappingSets,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, KeysetTableSummary> keysetTableByDialect = [];

        foreach (var mappingSet in mappingSets)
        {
            var dialect = PlanManifestConventions.ToManifestDialect(mappingSet.Key.Dialect);

            if (!mappingSet.ReadPlansByResource.TryGetValue(resource, out var readPlan))
            {
                throw new InvalidOperationException(
                    $"Compiled read plan for resource '{resource.ProjectName}.{resource.ResourceName}' is required."
                );
            }

            keysetTableByDialect[dialect] = new KeysetTableSummary(
                TempTableName: readPlan.KeysetTable.Table.Name,
                DocumentIdColumnName: readPlan.KeysetTable.DocumentIdColumnName.Value
            );
        }

        return keysetTableByDialect;
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyList<string>
    > ReadCompiledKeyUnificationMemberPathsByDialect(
        IReadOnlyList<MappingSet> mappingSets,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, IReadOnlyList<string>> memberPathsByDialect = [];

        foreach (var mappingSet in mappingSets)
        {
            var dialect = PlanManifestConventions.ToManifestDialect(mappingSet.Key.Dialect);

            if (!mappingSet.WritePlansByResource.TryGetValue(resource, out var writePlan))
            {
                throw new InvalidOperationException(
                    $"Compiled write plan for resource '{resource.ProjectName}.{resource.ResourceName}' is required."
                );
            }

            memberPathsByDialect[dialect] = writePlan
                .TablePlansInDependencyOrder.SelectMany(tablePlan => tablePlan.KeyUnificationPlans)
                .SelectMany(keyUnificationPlan => keyUnificationPlan.MembersInOrder)
                .Select(member => member.MemberPathColumn.Value)
                .ToArray();
        }

        return memberPathsByDialect;
    }

    private static RelationalResourceModel CreateRootOnlyModelWithNonDocumentIdFirstKeyOrder(
        QualifiedResourceName resource
    )
    {
        var rootTable = new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), resource.ResourceName),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: $"PK_{resource.ResourceName}",
                Columns:
                [
                    new DbKeyColumn(new DbColumnName("SchoolYear"), ColumnKind.Scalar),
                    new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart),
                ]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("SchoolYear"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression(
                        "$.schoolYear",
                        [new JsonPathSegment.Property("schoolYear")]
                    ),
                    TargetResource: null
                ),
            ],
            Constraints: []
        );

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static IReadOnlyList<string> ReadMappingSetDialects(string manifest)
    {
        return ParseMappingSetObjects(manifest)
            .Select(mappingSetObject => mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>())
            .Select(value =>
                value is null
                    ? throw new InvalidOperationException("Manifest mapping set key dialect is required.")
                    : value
            )
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadResourceNamesByDialect(
        string manifest
    )
    {
        Dictionary<string, IReadOnlyList<string>> resourceNamesByDialect = [];

        foreach (var mappingSetObject in ParseMappingSetObjects(manifest))
        {
            var dialect = mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>();

            if (dialect is null)
            {
                throw new InvalidOperationException("Manifest mapping set key dialect is required.");
            }

            var resourcesNode = mappingSetObject["resources"];

            if (resourcesNode is not JsonArray resources)
            {
                throw new InvalidOperationException("Manifest mapping set resources array is required.");
            }

            resourceNamesByDialect[dialect] = resources
                .Select(ReadResourceName)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }

        return resourceNamesByDialect;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadStudentOrderByColumnsByDialect(
        string manifest
    )
    {
        Dictionary<string, IReadOnlyList<string>> orderByColumnsByDialect = [];

        foreach (var mappingSetObject in ParseMappingSetObjects(manifest))
        {
            var dialect = mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>();

            if (dialect is null)
            {
                throw new InvalidOperationException("Manifest mapping set key dialect is required.");
            }

            var studentResource = FindResource(mappingSetObject, "Ed-Fi", "Student");
            var readPlan = RequireObject(studentResource["read_plan"], "read_plan");
            var tablePlans = RequireArray(
                readPlan["table_plans_in_dependency_order"],
                "table_plans_in_dependency_order"
            );

            if (tablePlans.Count != 1)
            {
                throw new InvalidOperationException(
                    "Manifest Student read-plan must contain exactly one table plan for this test."
                );
            }

            var tablePlan = RequireObject(tablePlans[0], "table_plans_in_dependency_order[0]");
            var orderByColumns = RequireArray(
                tablePlan["order_by_key_columns_in_order"],
                "order_by_key_columns_in_order"
            );

            orderByColumnsByDialect[dialect] = orderByColumns
                .Select(column => column?.GetValue<string>())
                .Select(value =>
                    value is null
                        ? throw new InvalidOperationException(
                            "Manifest order-by key column name is required."
                        )
                        : value
                )
                .ToArray();
        }

        return orderByColumnsByDialect;
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, PlanPresence>
    > ReadPlanPresenceByDialect(string manifest)
    {
        Dictionary<string, IReadOnlyDictionary<string, PlanPresence>> planPresenceByDialect = [];

        foreach (var mappingSetObject in ParseMappingSetObjects(manifest))
        {
            var dialect = mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>();

            if (dialect is null)
            {
                throw new InvalidOperationException("Manifest mapping set key dialect is required.");
            }

            if (mappingSetObject["resources"] is not JsonArray resources)
            {
                throw new InvalidOperationException("Manifest mapping set resources array is required.");
            }

            Dictionary<string, PlanPresence> resourcePlanPresence = [];

            foreach (var resource in resources)
            {
                if (resource is not JsonObject resourceObject)
                {
                    throw new InvalidOperationException(
                        "Manifest mapping set resources entries must be JSON objects."
                    );
                }

                if (!resourceObject.ContainsKey("write_plan") || !resourceObject.ContainsKey("read_plan"))
                {
                    throw new InvalidOperationException(
                        "Manifest resource entries must contain write_plan and read_plan properties."
                    );
                }

                var resourceIdentity = ReadResourceName(resourceObject);
                resourcePlanPresence[resourceIdentity] = new PlanPresence(
                    WritePlanIsNull: resourceObject["write_plan"] is null,
                    ReadPlanIsNull: resourceObject["read_plan"] is null
                );
            }

            planPresenceByDialect[dialect] = resourcePlanPresence;
        }

        return planPresenceByDialect;
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyList<WriteTablePlanSummary>
    > ReadWriteTablePlanSummariesByDialect(string manifest, string projectName, string resourceName)
    {
        Dictionary<string, IReadOnlyList<WriteTablePlanSummary>> summariesByDialect = [];

        foreach (var mappingSetObject in ParseMappingSetObjects(manifest))
        {
            var dialect = mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>();

            if (dialect is null)
            {
                throw new InvalidOperationException("Manifest mapping set key dialect is required.");
            }

            var resource = FindResource(mappingSetObject, projectName, resourceName);
            var writePlan = RequireObject(resource["write_plan"], "write_plan");
            var tablePlans = RequireArray(
                writePlan["table_plans_in_dependency_order"],
                "table_plans_in_dependency_order"
            );

            summariesByDialect[dialect] = tablePlans
                .Select(tablePlanNode =>
                    RequireObject(tablePlanNode, "table_plans_in_dependency_order entry")
                )
                .Select(ReadWriteTablePlanSummary)
                .ToArray();
        }

        return summariesByDialect;
    }

    private static IReadOnlyDictionary<string, ReadPlanSummary> ReadReadPlanSummariesByDialect(
        string manifest,
        string projectName,
        string resourceName
    )
    {
        Dictionary<string, ReadPlanSummary> summariesByDialect = [];

        foreach (var mappingSetObject in ParseMappingSetObjects(manifest))
        {
            var dialect = mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>();

            if (dialect is null)
            {
                throw new InvalidOperationException("Manifest mapping set key dialect is required.");
            }

            var resource = FindResource(mappingSetObject, projectName, resourceName);
            var readPlan = RequireObject(resource["read_plan"], "read_plan");
            var keysetTable = ReadKeysetTableSummary(RequireObject(readPlan["keyset_table"], "keyset_table"));
            var tablePlans = RequireArray(
                readPlan["table_plans_in_dependency_order"],
                "table_plans_in_dependency_order"
            );
            var referenceIdentityProjectionPlans = RequireArray(
                readPlan["reference_identity_projection_plans_in_dependency_order"],
                "reference_identity_projection_plans_in_dependency_order"
            );
            var descriptorProjectionPlans = RequireArray(
                readPlan["descriptor_projection_plans_in_order"],
                "descriptor_projection_plans_in_order"
            );

            summariesByDialect[dialect] = new ReadPlanSummary(
                KeysetTable: keysetTable,
                TablePlans: tablePlans
                    .Select(tablePlanNode =>
                        RequireObject(tablePlanNode, "table_plans_in_dependency_order entry")
                    )
                    .Select(ReadReadTablePlanSummary)
                    .ToArray(),
                ReferenceIdentityProjectionPlanCount: referenceIdentityProjectionPlans.Count,
                DescriptorProjectionPlanCount: descriptorProjectionPlans.Count
            );
        }

        return summariesByDialect;
    }

    private static IReadOnlyDictionary<
        string,
        IReadOnlyList<string>
    > ReadManifestKeyUnificationMemberPathsByDialect(string manifest, string projectName, string resourceName)
    {
        Dictionary<string, IReadOnlyList<string>> memberPathsByDialect = [];

        foreach (var mappingSetObject in ParseMappingSetObjects(manifest))
        {
            var dialect = mappingSetObject["mapping_set_key"]?["dialect"]?.GetValue<string>();

            if (dialect is null)
            {
                throw new InvalidOperationException("Manifest mapping set key dialect is required.");
            }

            var resource = FindResource(mappingSetObject, projectName, resourceName);
            var writePlan = RequireObject(resource["write_plan"], "write_plan");
            var tablePlans = RequireArray(
                writePlan["table_plans_in_dependency_order"],
                "table_plans_in_dependency_order"
            );

            memberPathsByDialect[dialect] = tablePlans
                .SelectMany(tablePlanNode =>
                    RequireArray(
                        RequireObject(tablePlanNode, "table_plans_in_dependency_order entry")[
                            "key_unification_plans"
                        ],
                        "key_unification_plans"
                    )
                )
                .SelectMany(keyPlanNode =>
                    RequireArray(
                        RequireObject(keyPlanNode, "key_unification_plans entry")["members_in_order"],
                        "members_in_order"
                    )
                )
                .Select(memberNode =>
                    RequireString(
                        RequireObject(memberNode, "members_in_order entry"),
                        "member_path_column_name"
                    )
                )
                .ToArray();
        }

        return memberPathsByDialect;
    }

    private static WriteTablePlanSummary ReadWriteTablePlanSummary(JsonObject tablePlan)
    {
        var table = RequireObject(tablePlan["table"], "table");
        var batching = RequireObject(tablePlan["bulk_insert_batching"], "bulk_insert_batching");
        var columnBindings = RequireArray(tablePlan["column_bindings_in_order"], "column_bindings_in_order");
        var keyUnificationPlans = RequireArray(tablePlan["key_unification_plans"], "key_unification_plans");

        // Validate key-unification member metadata shape while building summary.
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

        return new WriteTablePlanSummary(
            Schema: RequireString(table, "schema"),
            TableName: RequireString(table, "name"),
            InsertSqlSha256: RequireString(tablePlan, "insert_sql_sha256"),
            UpdateSqlSha256: ReadOptionalString(tablePlan, "update_sql_sha256"),
            DeleteByParentSqlSha256: ReadOptionalString(tablePlan, "delete_by_parent_sql_sha256"),
            BulkInsertBatching: new BulkInsertBatchingSummary(
                MaxRowsPerBatch: RequireInt(batching, "max_rows_per_batch"),
                ParametersPerRow: RequireInt(batching, "parameters_per_row"),
                MaxParametersPerCommand: RequireInt(batching, "max_parameters_per_command")
            ),
            ColumnBindingCount: columnBindings.Count
        );
    }

    private static ReadTablePlanSummary ReadReadTablePlanSummary(JsonObject tablePlan)
    {
        var table = RequireObject(tablePlan["table"], "table");

        return new ReadTablePlanSummary(
            Schema: RequireString(table, "schema"),
            TableName: RequireString(table, "name"),
            SelectByKeysetSqlSha256: RequireString(tablePlan, "select_by_keyset_sql_sha256"),
            SelectListColumnsInOrder: ReadRequiredStringArray(
                tablePlan["select_list_columns_in_order"],
                "select_list_columns_in_order"
            ),
            OrderByKeyColumnsInOrder: ReadRequiredStringArray(
                tablePlan["order_by_key_columns_in_order"],
                "order_by_key_columns_in_order"
            )
        );
    }

    private static string ReadResourceName(JsonNode? resourceNode)
    {
        var projectName = resourceNode?["resource"]?["project_name"]?.GetValue<string>();
        var resourceName = resourceNode?["resource"]?["resource_name"]?.GetValue<string>();

        if (projectName is null || resourceName is null)
        {
            throw new InvalidOperationException("Manifest resource identity is required.");
        }

        return $"{projectName}.{resourceName}";
    }

    private static JsonObject FindResource(
        JsonObject mappingSetObject,
        string projectName,
        string resourceName
    )
    {
        if (mappingSetObject["resources"] is not JsonArray resources)
        {
            throw new InvalidOperationException("Manifest mapping set resources array is required.");
        }

        foreach (var resource in resources)
        {
            var candidateProjectName = resource?["resource"]?["project_name"]?.GetValue<string>();
            var candidateResourceName = resource?["resource"]?["resource_name"]?.GetValue<string>();

            if (candidateProjectName == projectName && candidateResourceName == resourceName)
            {
                return resource as JsonObject
                    ?? throw new InvalidOperationException(
                        "Manifest mapping set resources entries must be JSON objects."
                    );
            }
        }

        throw new InvalidOperationException($"Manifest resource '{projectName}.{resourceName}' is required.");
    }

    private static JsonArray RequireArray(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonArray array => array,
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
                $"Manifest property '{propertyName}' must be a JSON object."
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
        var value = node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<int>(),
            null => throw new InvalidOperationException($"Manifest property '{propertyName}' is required."),
            _ => throw new InvalidOperationException(
                $"Manifest property '{propertyName}' must be an integer."
            ),
        };

        return value;
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

    private static KeysetTableSummary ReadKeysetTableSummary(JsonObject keysetTable)
    {
        return new KeysetTableSummary(
            TempTableName: RequireString(keysetTable, "temp_table_name"),
            DocumentIdColumnName: RequireString(keysetTable, "document_id_column_name")
        );
    }

    private static IReadOnlyList<string> ReadRequiredStringArray(JsonNode? node, string propertyName)
    {
        return RequireArray(node, propertyName)
            .Select(arrayNode => arrayNode?.GetValue<string>())
            .Select(value =>
                value is null
                    ? throw new InvalidOperationException(
                        $"Manifest property '{propertyName}' must contain only string values."
                    )
                    : value
            )
            .ToArray();
    }

    private static JsonArray ParseMappingSetArray(string manifest)
    {
        var root = JsonNode.Parse(manifest);

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException("Manifest root must be a JSON object.");
        }

        var mappingSetsNode = rootObject["mapping_sets"];

        return mappingSetsNode switch
        {
            JsonArray mappingSets => mappingSets,
            null => throw new InvalidOperationException("Manifest mapping_sets property is required."),
            _ => throw new InvalidOperationException("Manifest mapping_sets property must be a JSON array."),
        };
    }

    private static IReadOnlyList<JsonObject> ParseMappingSetObjects(string manifest)
    {
        return ParseMappingSetArray(manifest)
            .Select(mappingSet =>
                mappingSet as JsonObject
                ?? throw new InvalidOperationException("Manifest mapping_sets entries must be JSON objects.")
            )
            .ToArray();
    }

    private sealed record PlanPresence(bool WritePlanIsNull, bool ReadPlanIsNull);

    private sealed record BulkInsertBatchingSummary(
        int MaxRowsPerBatch,
        int ParametersPerRow,
        int MaxParametersPerCommand
    );

    private sealed record WriteTablePlanSummary(
        string Schema,
        string TableName,
        string InsertSqlSha256,
        string? UpdateSqlSha256,
        string? DeleteByParentSqlSha256,
        BulkInsertBatchingSummary BulkInsertBatching,
        int ColumnBindingCount
    );

    private sealed record KeysetTableSummary(string TempTableName, string DocumentIdColumnName);

    private sealed record ReadPlanSummary(
        KeysetTableSummary KeysetTable,
        IReadOnlyList<ReadTablePlanSummary> TablePlans,
        int ReferenceIdentityProjectionPlanCount,
        int DescriptorProjectionPlanCount
    );

    private sealed record ReadTablePlanSummary(
        string Schema,
        string TableName,
        string SelectByKeysetSqlSha256,
        IReadOnlyList<string> SelectListColumnsInOrder,
        IReadOnlyList<string> OrderByKeyColumnsInOrder
    );
}
