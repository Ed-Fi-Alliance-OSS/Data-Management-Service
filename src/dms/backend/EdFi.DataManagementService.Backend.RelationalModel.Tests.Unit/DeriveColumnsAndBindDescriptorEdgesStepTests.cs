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
/// Test fixture for an inlined object property.
/// </summary>
[TestFixture]
public class Given_An_Inlined_Object_Property
{
    private DbColumnModel _column = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(schema);

        var rootTable = context.ResourceModel!.Root;
        _column = rootTable.Columns.Single(column => column.ColumnName.Value == "ABC");
    }

    /// <summary>
    /// It should inline object properties with a prefixed name.
    /// </summary>
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

/// <summary>
/// Test fixture for colliding column names.
/// </summary>
[TestFixture]
public class Given_Colliding_Column_Names
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["a-b"] = new JsonObject { ["type"] = "string", ["maxLength"] = 10 },
                ["a_b"] = new JsonObject { ["type"] = "string", ["maxLength"] = 10 },
            },
        };

        try
        {
            _ = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(schema);
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should include collision details in the error message.
    /// </summary>
    [Test]
    public void It_should_include_collision_details_in_the_error_message()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception?.Message.Should().Contain("School");
        _exception?.Message.Should().Contain("AB");
        _exception?.Message.Should().Contain("$.a-b");
        _exception?.Message.Should().Contain("$.a_b");
        _exception?.Message.Should().Contain("relational.nameOverrides");
    }
}

/// <summary>
/// Test fixture for a descriptor path.
/// </summary>
[TestFixture]
public class Given_A_Descriptor_Path
{
    private DbColumnModel _column = default!;
    private DescriptorEdgeSource _edge = default!;
    private TableConstraint.ForeignKey _foreignKey = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
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

    /// <summary>
    /// It should create descriptor fk columns.
    /// </summary>
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

    /// <summary>
    /// It should create descriptor foreign keys.
    /// </summary>
    [Test]
    public void It_should_create_descriptor_foreign_keys()
    {
        _foreignKey.Columns.Should().Equal(new DbColumnName("SchoolTypeDescriptor_DescriptorId"));
        _foreignKey.TargetTable.Should().Be(new DbTableName(new DbSchemaName("dms"), "Descriptor"));
        _foreignKey.TargetColumns.Should().Equal(RelationalNameConventions.DocumentIdColumnName);
        _foreignKey.OnDelete.Should().Be(ReferentialAction.NoAction);
        _foreignKey.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    /// <summary>
    /// It should record descriptor edges.
    /// </summary>
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

/// <summary>
/// Test fixture for a descriptor scalar array.
/// </summary>
[TestFixture]
public class Given_A_Descriptor_Scalar_Array
{
    private DescriptorEdgeSource _edge = default!;
    private DbTableModel _table = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
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

        _table = context.ResourceModel!.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "SchoolGradeLevelDescriptor"
        );
        _edge = context.ResourceModel.DescriptorEdgeSources.Single();
    }

    /// <summary>
    /// It should create a descriptor fk column in the child table.
    /// </summary>
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

    /// <summary>
    /// It should record a descriptor edge source for the array element path.
    /// </summary>
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

/// <summary>
/// Test fixture for descriptor uri strings without max length.
/// </summary>
[TestFixture]
public class Given_Descriptor_Uri_Strings_Without_MaxLength
{
    private DbTableModel _rootTable = default!;
    private DbTableModel _descriptorTable = default!;
    private DbColumnModel _referenceDescriptorColumn = default!;
    private DbColumnModel _arrayDescriptorColumn = default!;
    private JsonPathExpression _referenceDescriptorPath = default!;
    private JsonPathExpression _arrayDescriptorPath = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["educationOrganizationReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["schoolTypeDescriptor"] = new JsonObject { ["type"] = "string" },
                        ["educationOrganizationId"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["maxLength"] = 10,
                        },
                    },
                    ["required"] = new JsonArray("schoolTypeDescriptor", "educationOrganizationId"),
                },
                ["gradeLevelDescriptors"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
            },
        };

        _referenceDescriptorPath = JsonPathExpressionCompiler.Compile(
            "$.educationOrganizationReference.schoolTypeDescriptor"
        );
        _arrayDescriptorPath = JsonPathExpressionCompiler.Compile("$.gradeLevelDescriptors[*]");

        var referenceDescriptorInfo = new DescriptorPathInfo(
            _referenceDescriptorPath,
            new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
        );
        var arrayDescriptorInfo = new DescriptorPathInfo(
            _arrayDescriptorPath,
            new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor")
        );

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
            schema,
            builderContext =>
            {
                builderContext.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [_referenceDescriptorPath.Canonical] = referenceDescriptorInfo,
                    [_arrayDescriptorPath.Canonical] = arrayDescriptorInfo,
                };
            }
        );

        _rootTable = context.ResourceModel!.Root;
        _descriptorTable = context.ResourceModel.TablesInDependencyOrder.Single(table =>
            table.Table.Name == "SchoolGradeLevelDescriptor"
        );

        _referenceDescriptorColumn = _rootTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == _referenceDescriptorPath.Canonical
        );
        _arrayDescriptorColumn = _descriptorTable.Columns.Single(column =>
            column.SourceJsonPath?.Canonical == _arrayDescriptorPath.Canonical
        );
    }

    /// <summary>
    /// It should create descriptor fk columns instead of string scalars.
    /// </summary>
    [Test]
    public void It_should_create_descriptor_fk_columns_instead_of_string_scalars()
    {
        _referenceDescriptorColumn.Kind.Should().Be(ColumnKind.DescriptorFk);
        _referenceDescriptorColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));

        _arrayDescriptorColumn.Kind.Should().Be(ColumnKind.DescriptorFk);
        _arrayDescriptorColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));

        var hasReferenceScalar = _rootTable.Columns.Any(column =>
            column.Kind == ColumnKind.Scalar
            && column.SourceJsonPath is JsonPathExpression sourcePath
            && sourcePath.Canonical == _referenceDescriptorPath.Canonical
        );
        hasReferenceScalar.Should().BeFalse();

        var hasArrayScalar = _descriptorTable.Columns.Any(column =>
            column.Kind == ColumnKind.Scalar
            && column.SourceJsonPath is JsonPathExpression sourcePath
            && sourcePath.Canonical == _arrayDescriptorPath.Canonical
        );
        hasArrayScalar.Should().BeFalse();
    }
}

/// <summary>
/// Test fixture for a reference identity field.
/// </summary>
[TestFixture]
public class Given_A_Reference_Identity_Field
{
    private DbTableModel _rootTable = default!;
    private IReadOnlyList<DescriptorEdgeSource> _descriptorEdges = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["programReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["programName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 60 },
                        ["programTypeDescriptor"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["maxLength"] = 306,
                        },
                        ["link"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["href"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                            },
                        },
                    },
                    ["required"] = new JsonArray("programName", "programTypeDescriptor"),
                },
                ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = 60 },
            },
            ["required"] = new JsonArray("programReference", "name"),
        };

        var referenceObjectPath = JsonPathExpressionCompiler.Compile("$.programReference");
        var programNamePath = JsonPathExpressionCompiler.Compile("$.programReference.programName");
        var programTypeDescriptorPath = JsonPathExpressionCompiler.Compile(
            "$.programReference.programTypeDescriptor"
        );

        var descriptorInfo = new DescriptorPathInfo(
            programTypeDescriptorPath,
            new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
        );

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
            schema,
            builderContext =>
            {
                builderContext.DescriptorPathsByJsonPath = new Dictionary<string, DescriptorPathInfo>(
                    StringComparer.Ordinal
                )
                {
                    [programTypeDescriptorPath.Canonical] = descriptorInfo,
                };
                builderContext.DocumentReferenceMappings =
                [
                    new DocumentReferenceMapping(
                        "programReference",
                        new QualifiedResourceName("Ed-Fi", "Program"),
                        true,
                        false,
                        referenceObjectPath,
                        new[]
                        {
                            new ReferenceJsonPathBinding(
                                JsonPathExpressionCompiler.Compile("$.programName"),
                                programNamePath
                            ),
                            new ReferenceJsonPathBinding(
                                JsonPathExpressionCompiler.Compile("$.programTypeDescriptor"),
                                programTypeDescriptorPath
                            ),
                        }
                    ),
                ];
            }
        );

        _rootTable = context.ResourceModel!.Root;
        _descriptorEdges = context.ResourceModel.DescriptorEdgeSources;
    }

    /// <summary>
    /// It should suppress reference identity columns.
    /// </summary>
    [Test]
    public void It_should_suppress_reference_identity_columns()
    {
        var columnNames = _rootTable.Columns.Select(column => column.ColumnName.Value).ToArray();

        columnNames.Should().NotContain("ProgramReferenceProgramName");
        columnNames.Should().NotContain("ProgramReferenceProgramTypeDescriptor_DescriptorId");
        columnNames.Should().NotContain("ProgramReferenceLinkHref");
    }

    /// <summary>
    /// It should not emit descriptor edges for reference identity fields.
    /// </summary>
    [Test]
    public void It_should_not_emit_descriptor_edges_for_reference_identity_fields()
    {
        _descriptorEdges.Should().BeEmpty();
    }
}

/// <summary>
/// Test fixture for a property with x nullable.
/// </summary>
[TestFixture]
public class Given_A_Property_With_XNullable
{
    private DbColumnModel _column = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(schema);

        _column = context.ResourceModel!.Root.Columns.Single(column => column.ColumnName.Value == "Value");
    }

    /// <summary>
    /// It should override requiredness for nullability.
    /// </summary>
    [Test]
    public void It_should_override_requiredness_for_nullability()
    {
        _column.IsNullable.Should().BeTrue();
    }
}

/// <summary>
/// Test fixture for a nullable identity property.
/// </summary>
[TestFixture]
public class Given_A_Nullable_Identity_Property
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = 10,
                    ["x-nullable"] = true,
                },
            },
            ["required"] = new JsonArray("schoolId"),
        };

        var identityPath = JsonPathExpressionCompiler.Compile("$.schoolId");

        try
        {
            _ = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
                schema,
                builderContext =>
                {
                    builderContext.IdentityJsonPaths = new[] { identityPath };
                }
            );
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast for nullable identity.
    /// </summary>
    [Test]
    public void It_should_fail_fast_for_nullable_identity()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identity path");
        _exception.Message.Should().Contain("$.schoolId");
        _exception.Message.Should().Contain("nullable");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

/// <summary>
/// Test fixture for a string property without max length.
/// </summary>
[TestFixture]
public class Given_A_String_Property_Without_MaxLength
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("value"),
        };

        try
        {
            _ = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(schema);
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should throw when max length is missing.
    /// </summary>
    [Test]
    public void It_should_throw_when_max_length_is_missing()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception?.Message.Should().Contain("$.value");
        _exception?.Message.Should().Contain("Set maxLength");
    }
}

/// <summary>
/// Test fixture for string properties with format and no max length.
/// </summary>
[TestFixture]
public class Given_String_Properties_With_Format_And_No_MaxLength
{
    private DbColumnModel _dateColumn = default!;
    private DbColumnModel _dateTimeColumn = default!;
    private DbColumnModel _timeColumn = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["birthDate"] = new JsonObject { ["type"] = "string", ["format"] = "date" },
                ["lastModifiedDateTime"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
                ["startTime"] = new JsonObject { ["type"] = "string", ["format"] = "time" },
            },
            ["required"] = new JsonArray("birthDate", "lastModifiedDateTime", "startTime"),
        };

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(schema);

        var rootTable = context.ResourceModel!.Root;
        _dateColumn = rootTable.Columns.Single(column => column.ColumnName.Value == "BirthDate");
        _dateTimeColumn = rootTable.Columns.Single(column =>
            column.ColumnName.Value == "LastModifiedDateTime"
        );
        _timeColumn = rootTable.Columns.Single(column => column.ColumnName.Value == "StartTime");
    }

    /// <summary>
    /// It should map formatted strings to temporal types.
    /// </summary>
    [Test]
    public void It_should_map_formatted_strings_to_temporal_types()
    {
        _dateColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Date));
        _dateTimeColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.DateTime));
        _timeColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Time));
    }
}

/// <summary>
/// Test fixture for a duration string without max length.
/// </summary>
[TestFixture]
public class Given_A_Duration_String_Without_MaxLength
{
    private DbColumnModel _column = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["duration"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("duration"),
        };

        var durationPath = JsonPathExpressionCompiler.Compile("$.duration");

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
            schema,
            builderContext =>
            {
                builderContext.StringMaxLengthOmissionPaths = new HashSet<string>(StringComparer.Ordinal)
                {
                    durationPath.Canonical,
                };
            }
        );

        _column = context.ResourceModel!.Root.Columns.Single(column => column.ColumnName.Value == "Duration");
    }

    /// <summary>
    /// It should allow missing max length for duration strings.
    /// </summary>
    [Test]
    public void It_should_allow_missing_max_length_for_duration_strings()
    {
        _column.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String));
        _column.IsNullable.Should().BeFalse();
    }
}

/// <summary>
/// Test fixture for an enumeration string without max length.
/// </summary>
[TestFixture]
public class Given_An_Enumeration_String_Without_MaxLength
{
    private DbColumnModel _column = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["deliveryMethodType"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("deliveryMethodType"),
        };

        var enumerationPath = JsonPathExpressionCompiler.Compile("$.deliveryMethodType");

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
            schema,
            builderContext =>
            {
                builderContext.StringMaxLengthOmissionPaths = new HashSet<string>(StringComparer.Ordinal)
                {
                    enumerationPath.Canonical,
                };
            }
        );

        _column = context.ResourceModel!.Root.Columns.Single(column =>
            column.ColumnName.Value == "DeliveryMethodType"
        );
    }

    /// <summary>
    /// It should allow missing max length for enumeration strings.
    /// </summary>
    [Test]
    public void It_should_allow_missing_max_length_for_enumeration_strings()
    {
        _column.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String));
        _column.IsNullable.Should().BeFalse();
    }
}

/// <summary>
/// Test fixture for a number property with decimal validation.
/// </summary>
[TestFixture]
public class Given_A_Number_Property_With_Decimal_Validation
{
    private DbColumnModel _column = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
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

    /// <summary>
    /// It should map decimals using validation info.
    /// </summary>
    [Test]
    public void It_should_map_decimals_using_validation_info()
    {
        _column.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Decimal, Decimal: (9, 2)));
        _column.IsNullable.Should().BeFalse();
    }
}

/// <summary>
/// Test fixture for a number property without decimal validation.
/// </summary>
[TestFixture]
public class Given_A_Number_Property_Without_Decimal_Validation
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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
            _ = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(schema);
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail with a schema compilation error.
    /// </summary>
    [Test]
    public void It_should_fail_with_a_schema_compilation_error()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception?.Message.Should().Contain("$.amount");
    }
}

/// <summary>
/// Test fixture for a number property with incomplete decimal validation.
/// </summary>
[TestFixture]
public class Given_A_Number_Property_With_Incomplete_Decimal_Validation
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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
            _ = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(
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

    /// <summary>
    /// It should fail with a schema compilation error.
    /// </summary>
    [Test]
    public void It_should_fail_with_a_schema_compilation_error()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception?.Message.Should().Contain("$.amount");
    }
}

/// <summary>
/// Test fixture for a json schema with additional properties schema.
/// </summary>
[TestFixture]
public class Given_A_JsonSchema_With_AdditionalProperties_Schema
{
    private DbTableModel _rootTable = default!;
    private string[] _tableNames = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        // Verify that additionalProperties (dynamic/unknown fields) do not create tables or columns.
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
                ["periods"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["code"] = new JsonObject { ["type"] = "string", ["maxLength"] = 5 },
                        },
                    },
                },
            },
            ["additionalProperties"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["extra"] = new JsonObject { ["type"] = "string", ["maxLength"] = 10 },
                    ["extras"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["value"] = new JsonObject { ["type"] = "string", ["maxLength"] = 5 },
                            },
                        },
                    },
                },
            },
        };

        var context = DeriveColumnsAndBindDescriptorEdgesStepTestContext.BuildContext(schema);

        _rootTable = context.ResourceModel!.Root;
        _tableNames = context
            .ResourceModel.TablesInDependencyOrder.Select(table => table.Table.Name)
            .ToArray();
    }

    /// <summary>
    /// It should ignore additional properties when deriving columns.
    /// </summary>
    [Test]
    public void It_should_ignore_additional_properties_when_deriving_columns()
    {
        _rootTable.Columns.Select(column => column.ColumnName.Value).Should().Equal("DocumentId", "Name");
    }

    /// <summary>
    /// It should ignore additional properties when deriving tables.
    /// </summary>
    [Test]
    public void It_should_ignore_additional_properties_when_deriving_tables()
    {
        _tableNames.Should().Equal("School", "SchoolPeriod");
    }
}

/// <summary>
/// Test type derive columns and bind descriptor edges step test context.
/// </summary>
internal static class DeriveColumnsAndBindDescriptorEdgesStepTestContext
{
    /// <summary>
    /// Builds a minimal context and runs only the base traversal derivation steps (tables then columns).
    /// </summary>
    /// <remarks>
    /// These unit tests intentionally bypass the full pipeline so each step can be tested in isolation. The
    /// derivation steps still validate unsupported schema keywords on the schema nodes they visit.
    /// </remarks>
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

        var deriveColumnsStep = new DeriveColumnsAndBindDescriptorEdgesStep();
        deriveColumnsStep.Execute(context);

        return context;
    }
}
