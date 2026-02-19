// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for schemas with different property order.
/// </summary>
[TestFixture]
public class Given_Schemas_With_Different_Property_Order
{
    private RelationalResourceModel _modelA = default!;
    private RelationalResourceModel _modelB = default!;
    private IReadOnlyList<string> _snapshotA = default!;
    private IReadOnlyList<string> _snapshotB = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var descriptorPath = JsonPathExpressionCompiler.Compile("$.zetaDescriptor");
        var descriptorInfo = new DescriptorPathInfo(
            descriptorPath,
            new QualifiedResourceName("Ed-Fi", "ZetaDescriptor")
        );

        var schemaA = CreateSchema(descriptorFirst: true);
        var schemaB = CreateSchema(descriptorFirst: false);

        _modelA = CanonicalizeOrderingStepTestContext.BuildModel(
            schemaA,
            context =>
            {
                context.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [descriptorPath.Canonical] = descriptorInfo,
                };
            }
        );

        _modelB = CanonicalizeOrderingStepTestContext.BuildModel(
            schemaB,
            context =>
            {
                context.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [descriptorPath.Canonical] = descriptorInfo,
                };
            }
        );

        _snapshotA = CanonicalizeOrderingStepTestContext.CaptureSnapshot(_modelA);
        _snapshotB = CanonicalizeOrderingStepTestContext.CaptureSnapshot(_modelB);
    }

    /// <summary>
    /// It should produce identical ordered output.
    /// </summary>
    [Test]
    public void It_should_produce_identical_ordered_output()
    {
        _snapshotA.Should().Equal(_snapshotB);
    }

    /// <summary>
    /// It should place descriptor columns before scalars.
    /// </summary>
    [Test]
    public void It_should_place_descriptor_columns_before_scalars()
    {
        var rootColumns = _modelA
            .TablesInDependencyOrder.Single(table =>
                string.Equals(table.JsonScope.Canonical, "$", StringComparison.Ordinal)
            )
            .Columns.Select(column => column.ColumnName.Value);

        rootColumns.Should().Equal("DocumentId", "ZetaDescriptor_DescriptorId", "Alpha");
    }

    /// <summary>
    /// Create schema.
    /// </summary>
    private static JsonObject CreateSchema(bool descriptorFirst)
    {
        var descriptorSchema = new JsonObject { ["type"] = "string", ["maxLength"] = 306 };
        var scalarSchema = new JsonObject { ["type"] = "string", ["maxLength"] = 10 };
        var addressesSchema = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                },
            },
        };

        JsonObject properties = new();

        if (descriptorFirst)
        {
            properties["zetaDescriptor"] = descriptorSchema;
            properties["alpha"] = scalarSchema;
            properties["addresses"] = addressesSchema;
        }
        else
        {
            properties["addresses"] = addressesSchema;
            properties["alpha"] = scalarSchema;
            properties["zetaDescriptor"] = descriptorSchema;
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties };
    }
}

/// <summary>
/// Test fixture for descriptor path mappings with different order.
/// </summary>
[TestFixture]
public class Given_Descriptor_Path_Mappings_With_Different_Order
{
    private IReadOnlyList<string> _edgesFirst = default!;
    private IReadOnlyList<string> _edgesSecond = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = CreateDescriptorSchema();
        var gradeLevelPath = JsonPathExpressionCompiler.Compile("$.gradeLevelDescriptor");
        var schoolTypePath = JsonPathExpressionCompiler.Compile("$.schoolTypeDescriptor");

        var gradeLevelInfo = new DescriptorPathInfo(
            gradeLevelPath,
            new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
        );
        var schoolTypeInfo = new DescriptorPathInfo(
            schoolTypePath,
            new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
        );

        var modelA = CanonicalizeOrderingStepTestContext.BuildModel(
            schema,
            context =>
            {
                context.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [gradeLevelPath.Canonical] = gradeLevelInfo,
                    [schoolTypePath.Canonical] = schoolTypeInfo,
                };
            }
        );

        var modelB = CanonicalizeOrderingStepTestContext.BuildModel(
            schema,
            context =>
            {
                context.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [schoolTypePath.Canonical] = schoolTypeInfo,
                    [gradeLevelPath.Canonical] = gradeLevelInfo,
                };
            }
        );

        _edgesFirst = CanonicalizeOrderingStepTestContext.CaptureDescriptorEdges(modelA);
        _edgesSecond = CanonicalizeOrderingStepTestContext.CaptureDescriptorEdges(modelB);
    }

    /// <summary>
    /// It should produce identical descriptor edge ordering.
    /// </summary>
    [Test]
    public void It_should_produce_identical_descriptor_edge_ordering()
    {
        _edgesFirst.Should().Equal(_edgesSecond);
    }

    /// <summary>
    /// Create descriptor schema.
    /// </summary>
    private static JsonObject CreateDescriptorSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolTypeDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 306 },
                ["gradeLevelDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 306 },
            },
        };
    }
}

/// <summary>
/// Test fixture for mixed constraint types.
/// </summary>
[TestFixture]
public class Given_Mixed_Constraint_Types
{
    private IReadOnlyList<string> _orderedConstraints = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var jsonScope = JsonPathExpressionCompiler.Compile("$");
        var keyColumn = new DbKeyColumn(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart
        );

        var fkColumn = new DbColumnName("Student_DocumentId");
        var dependentColumns = new[]
        {
            new DbColumnName("Student_StudentUniqueId"),
            new DbColumnName("Student_SchoolId"),
        };

        var columns = new[]
        {
            new DbColumnModel(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                fkColumn,
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.studentReference"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "Student")
            ),
            new DbColumnModel(
                dependentColumns[0],
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, MaxLength: 32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.studentReference.studentUniqueId"),
                TargetResource: null
            ),
            new DbColumnModel(
                dependentColumns[1],
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.studentReference.schoolId"),
                TargetResource: null
            ),
            new DbColumnModel(
                new DbColumnName("BooleanFlag"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Boolean),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.booleanFlag"),
                TargetResource: null
            ),
        };

        var constraints = new TableConstraint[]
        {
            new TableConstraint.AllOrNoneNullability(
                "CK_School_Student_DocumentId_AllOrNone_B",
                fkColumn,
                dependentColumns
            ),
            new TableConstraint.NullOrTrue(
                "CK_School_BooleanFlag_NullOrTrue_B",
                new DbColumnName("BooleanFlag")
            ),
            new TableConstraint.ForeignKey(
                "FK_School_Student_B",
                new[] { fkColumn },
                new DbTableName(schema, "Student"),
                new[] { RelationalNameConventions.DocumentIdColumnName }
            ),
            new TableConstraint.Unique("UK_School_B", new[] { fkColumn }),
            new TableConstraint.AllOrNoneNullability(
                "CK_School_Student_DocumentId_AllOrNone_A",
                fkColumn,
                dependentColumns
            ),
            new TableConstraint.NullOrTrue(
                "CK_School_BooleanFlag_NullOrTrue_A",
                new DbColumnName("BooleanFlag")
            ),
            new TableConstraint.ForeignKey(
                "FK_School_Student_A",
                new[] { fkColumn },
                new DbTableName(schema, "Student"),
                new[] { RelationalNameConventions.DocumentIdColumnName }
            ),
            new TableConstraint.Unique(
                "UK_School_A",
                new[] { RelationalNameConventions.DocumentIdColumnName }
            ),
        };

        var table = new DbTableModel(
            tableName,
            jsonScope,
            new TableKey($"PK_{tableName.Name}", [keyColumn]),
            columns,
            constraints
        );

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "School"),
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            new[] { table },
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        var context = new RelationalModelBuilderContext { ResourceModel = resourceModel };

        var canonicalize = new CanonicalizeOrderingStep();
        canonicalize.Execute(context);

        _orderedConstraints = context.ResourceModel!.Root.Constraints.Select(GetConstraintName).ToArray();
    }

    /// <summary>
    /// It should order constraints by kind then name.
    /// </summary>
    [Test]
    public void It_should_order_constraints_by_kind_then_name()
    {
        _orderedConstraints
            .Should()
            .Equal(
                "UK_School_A",
                "UK_School_B",
                "FK_School_Student_A",
                "FK_School_Student_B",
                "CK_School_Student_DocumentId_AllOrNone_A",
                "CK_School_Student_DocumentId_AllOrNone_B",
                "CK_School_BooleanFlag_NullOrTrue_A",
                "CK_School_BooleanFlag_NullOrTrue_B"
            );
    }

    /// <summary>
    /// Get constraint name.
    /// </summary>
    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            TableConstraint.NullOrTrue nullOrTrue => nullOrTrue.Name,
            _ => string.Empty,
        };
    }
}

/// <summary>
/// Test fixture for key-unification alias dependency ordering.
/// </summary>
[TestFixture]
public class Given_Key_Unification_Alias_Dependencies
{
    private IReadOnlyDictionary<string, int> _columnIndexByName = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "Enrollment");
        var keyColumn = new DbKeyColumn(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart
        );

        var canonicalColumn = new DbColumnName("School_SchoolId_Unified");
        var referencePresenceColumn = new DbColumnName("School_DocumentId");
        var scalarPresenceColumn = new DbColumnName("LocalSchoolId_Present");
        var referenceAliasColumn = new DbColumnName("School_SchoolId");
        var scalarAliasColumn = new DbColumnName("LocalSchoolId");
        var descriptorColumn = new DbColumnName("GradeLevelDescriptor_DescriptorId");
        var scalarColumn = new DbColumnName("AcademicYear");

        var columns = new[]
        {
            new DbColumnModel(
                referenceAliasColumn,
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.schoolReference.schoolId"),
                TargetResource: null
            )
            {
                Storage = new ColumnStorage.UnifiedAlias(canonicalColumn, referencePresenceColumn),
            },
            new DbColumnModel(
                scalarAliasColumn,
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.localSchoolId"),
                TargetResource: null
            )
            {
                Storage = new ColumnStorage.UnifiedAlias(canonicalColumn, scalarPresenceColumn),
            },
            new DbColumnModel(
                canonicalColumn,
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                scalarPresenceColumn,
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Boolean),
                IsNullable: true,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                referencePresenceColumn,
                ColumnKind.DocumentFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.schoolReference"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "School")
            ),
            new DbColumnModel(
                descriptorColumn,
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.gradeLevelDescriptor"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
            ),
            new DbColumnModel(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                scalarColumn,
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: true,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.academicYear"),
                TargetResource: null
            ),
        };

        var table = new DbTableModel(
            tableName,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey("PK_Enrollment", [keyColumn]),
            columns,
            Array.Empty<TableConstraint>()
        );

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "Enrollment"),
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            new[] { table },
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        var context = new RelationalModelBuilderContext { ResourceModel = resourceModel };
        var canonicalize = new CanonicalizeOrderingStep();
        canonicalize.Execute(context);

        _columnIndexByName = context
            .ResourceModel!.Root.Columns.Select(
                (column, index) => new { ColumnName = column.ColumnName.Value, Index = index }
            )
            .ToDictionary(entry => entry.ColumnName, entry => entry.Index, StringComparer.Ordinal);
    }

    /// <summary>
    /// It should place canonical and presence columns before dependent unified aliases.
    /// </summary>
    [Test]
    public void It_should_place_canonical_and_presence_columns_before_dependent_aliases()
    {
        _columnIndexByName["School_SchoolId_Unified"]
            .Should()
            .BeLessThan(_columnIndexByName["School_SchoolId"]);
        _columnIndexByName["School_DocumentId"].Should().BeLessThan(_columnIndexByName["School_SchoolId"]);
        _columnIndexByName["School_SchoolId_Unified"]
            .Should()
            .BeLessThan(_columnIndexByName["LocalSchoolId"]);
        _columnIndexByName["LocalSchoolId_Present"].Should().BeLessThan(_columnIndexByName["LocalSchoolId"]);
    }

    /// <summary>
    /// It should keep existing grouping stable when dependencies do not require reordering.
    /// </summary>
    [Test]
    public void It_should_keep_existing_grouping_stable_when_dependencies_allow()
    {
        _columnIndexByName["DocumentId"].Should().Be(0);
        _columnIndexByName["GradeLevelDescriptor_DescriptorId"]
            .Should()
            .BeLessThan(_columnIndexByName["AcademicYear"]);
        _columnIndexByName["LocalSchoolId"].Should().BeLessThan(_columnIndexByName["School_DocumentId"]);
    }
}

/// <summary>
/// Test type canonicalize ordering step test context.
/// </summary>
internal static class CanonicalizeOrderingStepTestContext
{
    /// <summary>
    /// Build model.
    /// </summary>
    public static RelationalResourceModel BuildModel(
        JsonObject schema,
        Action<RelationalModelBuilderContext>? configure = null
    )
    {
        var context = new RelationalModelBuilderContext
        {
            ProjectName = "Ed-Fi",
            ProjectEndpointName = "ed-fi",
            ResourceName = "School",
            JsonSchemaForInsert = schema,
        };

        configure?.Invoke(context);

        var deriveTables = new DeriveTableScopesAndKeysStep();
        deriveTables.Execute(context);

        var deriveColumns = new DeriveColumnsAndBindDescriptorEdgesStep();
        deriveColumns.Execute(context);

        var canonicalize = new CanonicalizeOrderingStep();
        canonicalize.Execute(context);

        return context.ResourceModel
            ?? throw new InvalidOperationException(
                "Expected ResourceModel to be set after canonicalization."
            );
    }

    /// <summary>
    /// Capture snapshot.
    /// </summary>
    public static IReadOnlyList<string> CaptureSnapshot(RelationalResourceModel model)
    {
        List<string> snapshot = [];

        foreach (var table in model.TablesInDependencyOrder)
        {
            var columnNames = string.Join(",", table.Columns.Select(column => column.ColumnName.Value));
            var constraintNames = string.Join(",", table.Constraints.Select(GetConstraintName));

            snapshot.Add($"{table.JsonScope.Canonical}|{table.Table}|{columnNames}|{constraintNames}");
        }

        snapshot.Add($"DescriptorEdges:{string.Join(",", CaptureDescriptorEdges(model))}");

        return snapshot.ToArray();
    }

    /// <summary>
    /// Capture descriptor edges.
    /// </summary>
    public static IReadOnlyList<string> CaptureDescriptorEdges(RelationalResourceModel model)
    {
        return model
            .DescriptorEdgeSources.Select(edge =>
                $"{edge.Table}|{edge.FkColumn.Value}|{edge.DescriptorValuePath.Canonical}"
            )
            .ToArray();
    }

    /// <summary>
    /// Get constraint name.
    /// </summary>
    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            TableConstraint.NullOrTrue nullOrTrue => nullOrTrue.Name,
            _ => string.Empty,
        };
    }
}
