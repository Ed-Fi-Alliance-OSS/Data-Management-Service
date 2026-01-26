// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_An_Inlined_Object_Property
{
    private DbColumnModel _column = default!;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["a"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["b"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["c"] = new JsonObject { ["type"] = "string", ["maxLength"] = 10 },
                            },
                            ["required"] = new JsonArray("c"),
                        },
                    },
                    ["required"] = new JsonArray("b"),
                },
            },
        };

        var context = DeriveColumnsAndDescriptorEdgesStepTestContext.BuildContext(schema);

        var rootTable = context.ResourceModel!.Root;
        _column = rootTable.Columns.Single(column => column.ColumnName.Value == "ABC");
    }

    [Test]
    public void It_should_inline_object_properties_with_a_prefixed_name()
    {
        _column.ColumnName.Value.Should().Be("ABC");
        _column.Kind.Should().Be(ColumnKind.Scalar);
        _column.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 10));
        _column.IsNullable.Should().BeTrue();
        var sourcePath =
            _column.SourceJsonPath
            ?? throw new InvalidOperationException("Expected SourceJsonPath to be set.");
        sourcePath.Canonical.Should().Be("$.a.b.c");
    }
}

[TestFixture]
public class Given_A_Descriptor_Path
{
    private DbColumnModel _column = default!;
    private DescriptorEdgeSource _edge = default!;
    private TableConstraint.ForeignKey _foreignKey = default!;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolTypeDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 306 },
            },
            ["required"] = new JsonArray("schoolTypeDescriptor"),
        };

        var descriptorPath = JsonPathExpressionCompiler.Compile("$.schoolTypeDescriptor");
        var descriptorInfo = new DescriptorPathInfo(
            descriptorPath,
            new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
        );

        var context = DeriveColumnsAndDescriptorEdgesStepTestContext.BuildContext(
            schema,
            builderContext =>
            {
                builderContext.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [descriptorPath.Canonical] = descriptorInfo,
                };
                builderContext.IdentityJsonPaths = new[] { descriptorPath };
            }
        );

        var rootTable = context.ResourceModel!.Root;
        _column = rootTable.Columns.Single(column => column.Kind == ColumnKind.DescriptorFk);
        _edge = context.ResourceModel.DescriptorEdgeSources.Single();
        _foreignKey = rootTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(fk => fk.TargetTable.Name == "Descriptor");
    }

    [Test]
    public void It_should_create_descriptor_fk_columns()
    {
        _column.ColumnName.Value.Should().Be("SchoolTypeDescriptor_DescriptorId");
        _column.Kind.Should().Be(ColumnKind.DescriptorFk);
        _column.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
        _column.IsNullable.Should().BeFalse();
        var sourcePath =
            _column.SourceJsonPath
            ?? throw new InvalidOperationException("Expected SourceJsonPath to be set.");
        sourcePath.Canonical.Should().Be("$.schoolTypeDescriptor");
        _column.TargetResource.Should().Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
    }

    [Test]
    public void It_should_create_descriptor_foreign_keys()
    {
        _foreignKey.Columns.Should().Equal(new DbColumnName("SchoolTypeDescriptor_DescriptorId"));
        _foreignKey.TargetTable.Should().Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
        _foreignKey.TargetColumns.Should().Equal(RelationalNameConventions.DocumentIdColumnName);
        _foreignKey.OnDelete.Should().Be(ReferentialAction.NoAction);
        _foreignKey.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    [Test]
    public void It_should_record_descriptor_edges()
    {
        _edge.IsIdentityComponent.Should().BeTrue();
        _edge.DescriptorValuePath.Canonical.Should().Be("$.schoolTypeDescriptor");
        _edge.Table.Should().Be(new DbTableName(new DbSchemaName("edfi"), "School"));
        _edge.FkColumn.Should().Be(new DbColumnName("SchoolTypeDescriptor_DescriptorId"));
        _edge.DescriptorResource.Should().Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
    }
}

[TestFixture]
public class Given_A_Descriptor_Scalar_Array
{
    private DescriptorEdgeSource _edge = default!;
    private DbTableModel _table = default!;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["gradeLevelDescriptors"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
            },
        };

        var descriptorPath = JsonPathExpressionCompiler.Compile("$.gradeLevelDescriptors[*]");
        var descriptorInfo = new DescriptorPathInfo(
            descriptorPath,
            new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
        );

        var context = DeriveColumnsAndDescriptorEdgesStepTestContext.BuildContext(
            schema,
            builderContext =>
            {
                builderContext.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [descriptorPath.Canonical] = descriptorInfo,
                };
            }
        );

        _table = context.ResourceModel!.TablesInReadDependencyOrder.Single(table =>
            table.Table.Name == "SchoolGradeLevelDescriptor"
        );
        _edge = context.ResourceModel.DescriptorEdgeSources.Single();
    }

    [Test]
    public void It_should_create_a_descriptor_fk_column_in_the_child_table()
    {
        _table
            .Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("School_DocumentId", "Ordinal", "GradeLevelDescriptor_DescriptorId");

        var descriptorColumn = _table.Columns.Single(column => column.Kind == ColumnKind.DescriptorFk);

        descriptorColumn.ColumnName.Value.Should().Be("GradeLevelDescriptor_DescriptorId");
        descriptorColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
    }

    [Test]
    public void It_should_record_a_descriptor_edge_source_for_the_array_element_path()
    {
        var expectedTable = new DbTableName(new DbSchemaName("edfi"), "SchoolGradeLevelDescriptor");

        _edge.DescriptorValuePath.Canonical.Should().Be("$.gradeLevelDescriptors[*]");
        _edge.Table.Should().Be(expectedTable);
        _edge.FkColumn.Should().Be(new DbColumnName("GradeLevelDescriptor_DescriptorId"));
        _edge.DescriptorResource.Should().Be(new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor"));
    }
}

[TestFixture]
public class Given_A_Property_With_XNullable
{
    private DbColumnModel _column = default!;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["value"] = new JsonObject { ["type"] = "integer", ["x-nullable"] = true },
            },
            ["required"] = new JsonArray("value"),
        };

        var context = DeriveColumnsAndDescriptorEdgesStepTestContext.BuildContext(schema);

        _column = context.ResourceModel!.Root.Columns.Single(column => column.ColumnName.Value == "Value");
    }

    [Test]
    public void It_should_override_requiredness_for_nullability()
    {
        _column.IsNullable.Should().BeTrue();
    }
}

[TestFixture]
public class Given_A_String_Property_Without_MaxLength
{
    private DbColumnModel _column = default!;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("value"),
        };

        var context = DeriveColumnsAndDescriptorEdgesStepTestContext.BuildContext(schema);

        _column = context.ResourceModel!.Root.Columns.Single(column => column.ColumnName.Value == "Value");
    }

    [Test]
    public void It_should_allow_strings_without_max_length()
    {
        _column.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String));
        _column.IsNullable.Should().BeFalse();
    }
}

[TestFixture]
public class Given_A_Number_Property_With_Decimal_Validation
{
    private DbColumnModel _column = default!;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["amount"] = new JsonObject { ["type"] = "number" } },
            ["required"] = new JsonArray("amount"),
        };

        var decimalPath = JsonPathExpressionCompiler.Compile("$.amount");
        var decimalInfo = new DecimalPropertyValidationInfo(decimalPath, 9, 2);

        var context = DeriveColumnsAndDescriptorEdgesStepTestContext.BuildContext(
            schema,
            builderContext =>
            {
                builderContext.DecimalPropertyValidationInfosByPath = new Dictionary<
                    string,
                    DecimalPropertyValidationInfo
                >(StringComparer.Ordinal)
                {
                    [decimalPath.Canonical] = decimalInfo,
                };
            }
        );

        _column = context.ResourceModel!.Root.Columns.Single(column => column.ColumnName.Value == "Amount");
    }

    [Test]
    public void It_should_map_decimals_using_validation_info()
    {
        _column.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Decimal, Decimal: (9, 2)));
        _column.IsNullable.Should().BeFalse();
    }
}

[TestFixture]
public class Given_A_Number_Property_Without_Decimal_Validation
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["amount"] = new JsonObject { ["type"] = "number" } },
            ["required"] = new JsonArray("amount"),
        };

        try
        {
            _ = DeriveColumnsAndDescriptorEdgesStepTestContext.BuildContext(schema);
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    [Test]
    public void It_should_fail_with_a_schema_compilation_error()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception?.Message.Should().Contain("$.amount");
    }
}

[TestFixture]
public class Given_A_Number_Property_With_Incomplete_Decimal_Validation
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["amount"] = new JsonObject { ["type"] = "number" } },
            ["required"] = new JsonArray("amount"),
        };

        var decimalPath = JsonPathExpressionCompiler.Compile("$.amount");
        var decimalInfo = new DecimalPropertyValidationInfo(decimalPath, null, 2);

        try
        {
            _ = DeriveColumnsAndDescriptorEdgesStepTestContext.BuildContext(
                schema,
                builderContext =>
                {
                    builderContext.DecimalPropertyValidationInfosByPath = new Dictionary<
                        string,
                        DecimalPropertyValidationInfo
                    >(StringComparer.Ordinal)
                    {
                        [decimalPath.Canonical] = decimalInfo,
                    };
                }
            );
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    [Test]
    public void It_should_fail_with_a_schema_compilation_error()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception?.Message.Should().Contain("$.amount");
    }
}

internal static class DeriveColumnsAndDescriptorEdgesStepTestContext
{
    public static RelationalModelBuilderContext BuildContext(
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

        var deriveTablesStep = new DeriveTableScopesAndKeysStep();
        deriveTablesStep.Execute(context);

        var deriveColumnsStep = new DeriveColumnsAndDescriptorEdgesStep();
        deriveColumnsStep.Execute(context);

        return context;
    }
}
