// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for identifier shortening collision in derived model set.
/// </summary>
[TestFixture]
public class Given_Identifier_Shortening_Collision_In_Derived_Model_Set
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new IdentifierShorteningCollisionPass(effectiveSchemaSet),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new CollisionDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with identifier shortening collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_identifier_shortening_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().StartWith("Identifier shortening collisions detected: ");
        _exception!
            .Message.Should()
            .Contain("table name collision AfterDialectShortening(Pgsql:8-bytes) in schema 'edfi'");
        _exception!.Message.Should().Contain("LongTableNameAlpha -> CollisionName");
        _exception!.Message.Should().Contain("LongTableNameBeta -> CollisionName");
    }

    /// <summary>
    /// Test type identifier shortening collision pass.
    /// </summary>
    private sealed class IdentifierShorteningCollisionPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _resourceOne;
        private readonly ResourceKeyEntry _resourceTwo;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public IdentifierShorteningCollisionPass(EffectiveSchemaSet effectiveSchemaSet)
        {
            _resourceOne = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "School"
            );
            _resourceTwo = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "SchoolTypeDescriptor"
            );
        }

        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.ConcreteResourcesInNameOrder.Add(
                new ConcreteResourceModel(
                    _resourceOne,
                    ResourceStorageKind.RelationalTables,
                    CreateModel(_resourceOne.Resource, "LongTableNameAlpha")
                )
            );
            context.ConcreteResourcesInNameOrder.Add(
                new ConcreteResourceModel(
                    _resourceTwo,
                    ResourceStorageKind.RelationalTables,
                    CreateModel(_resourceTwo.Resource, "LongTableNameBeta")
                )
            );
        }

        /// <summary>
        /// Create model.
        /// </summary>
        private static RelationalResourceModel CreateModel(QualifiedResourceName resource, string tableName)
        {
            var schema = new DbSchemaName("edfi");
            var keyColumn = new DbKeyColumn(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart
            );
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
            };
            var table = new DbTableModel(
                new DbTableName(schema, tableName),
                JsonPathExpressionCompiler.Compile("$"),
                new TableKey($"PK_{tableName}", [keyColumn]),
                columns,
                Array.Empty<TableConstraint>()
            );

            return new RelationalResourceModel(
                resource,
                schema,
                ResourceStorageKind.RelationalTables,
                table,
                new[] { table },
                Array.Empty<DocumentReferenceBinding>(),
                Array.Empty<DescriptorEdgeSource>()
            );
        }
    }

    /// <summary>
    /// Test type collision dialect rules.
    /// </summary>
    private sealed class CollisionDialectRules : ISqlDialectRules
    {
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        /// <summary>
        /// Gets dialect.
        /// </summary>
        public SqlDialect Dialect => SqlDialect.Pgsql;

        /// <summary>
        /// Gets max identifier length.
        /// </summary>
        public int MaxIdentifierLength => 8;

        /// <summary>
        /// Gets scalar type defaults.
        /// </summary>
        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        /// <summary>
        /// Shorten identifier.
        /// </summary>
        public string ShortenIdentifier(string identifier)
        {
            return identifier.Contains("LongTableName", StringComparison.Ordinal)
                ? "CollisionName"
                : identifier;
        }
    }
}

/// <summary>
/// Test fixture for primary-key constraint identifier shortening collisions in derived model set.
/// </summary>
[TestFixture]
public class Given_Primary_Key_Identifier_Shortening_Collision_In_Derived_Model_Set
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new PrimaryKeyIdentifierShorteningCollisionPass(effectiveSchemaSet),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PrimaryKeyCollisionDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with primary-key constraint shortening collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_primary_key_constraint_shortening_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("constraint name collision");
        _exception!.Message.Should().Contain("PK_LongSchoolAlpha");
        _exception!.Message.Should().Contain("PK_LongSchoolBeta");
        _exception!.Message.Should().Contain("PK_Collision");
    }

    private sealed class PrimaryKeyIdentifierShorteningCollisionPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _resourceOne;
        private readonly ResourceKeyEntry _resourceTwo;

        public PrimaryKeyIdentifierShorteningCollisionPass(EffectiveSchemaSet effectiveSchemaSet)
        {
            _resourceOne = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "School"
            );
            _resourceTwo = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "SchoolTypeDescriptor"
            );
        }

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.ConcreteResourcesInNameOrder.Add(
                new ConcreteResourceModel(
                    _resourceOne,
                    ResourceStorageKind.RelationalTables,
                    CreateModel(_resourceOne.Resource, "LongSchoolAlpha")
                )
            );
            context.ConcreteResourcesInNameOrder.Add(
                new ConcreteResourceModel(
                    _resourceTwo,
                    ResourceStorageKind.RelationalTables,
                    CreateModel(_resourceTwo.Resource, "LongSchoolBeta")
                )
            );
        }

        private static RelationalResourceModel CreateModel(QualifiedResourceName resource, string tableName)
        {
            var schema = new DbSchemaName("edfi");
            var keyColumn = new DbKeyColumn(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart
            );
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
            };
            var table = new DbTableModel(
                new DbTableName(schema, tableName),
                JsonPathExpressionCompiler.Compile("$"),
                new TableKey($"PK_{tableName}", [keyColumn]),
                columns,
                Array.Empty<TableConstraint>()
            );

            return new RelationalResourceModel(
                resource,
                schema,
                ResourceStorageKind.RelationalTables,
                table,
                new[] { table },
                Array.Empty<DocumentReferenceBinding>(),
                Array.Empty<DescriptorEdgeSource>()
            );
        }
    }

    private sealed class PrimaryKeyCollisionDialectRules : ISqlDialectRules
    {
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public int MaxIdentifierLength => 63;

        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        public string ShortenIdentifier(string identifier)
        {
            return identifier.StartsWith("PK_LongSchool", StringComparison.Ordinal)
                ? "PK_Collision"
                : identifier;
        }
    }
}

/// <summary>
/// Test fixture for abstract union-arm source-table collisions after shortening.
/// </summary>
[TestFixture]
public class Given_Abstract_Union_Arm_Source_Table_Shortening_Collision
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new UnionArmSourceTableCollisionPass(effectiveSchemaSet),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );

        var dialectRules = new UnionArmCollisionDialectRules(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LongUnionArmTableAlpha"] = "UnionArmTableCollision",
                ["LongUnionArmTableBeta"] = "UnionArmTableCollision",
            }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with an abstract union-arm source-table collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_abstract_union_arm_source_table_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("table name collision");
        _exception!.Message.Should().Contain("LongUnionArmTableAlpha");
        _exception!.Message.Should().Contain("LongUnionArmTableBeta");
        _exception!.Message.Should().Contain("UnionArmTableCollision");
    }

    private sealed class UnionArmSourceTableCollisionPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _abstractResource;
        private readonly ResourceKeyEntry _memberAlpha;
        private readonly ResourceKeyEntry _memberBeta;

        public UnionArmSourceTableCollisionPass(EffectiveSchemaSet effectiveSchemaSet)
        {
            ArgumentNullException.ThrowIfNull(effectiveSchemaSet);

            _abstractResource = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "SchoolTypeDescriptor"
            );
            _memberAlpha = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "School"
            );
            _memberBeta = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Sample",
                "Section"
            );
        }

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var schema = new DbSchemaName("edfi");

            context.AbstractUnionViewsInNameOrder.Add(
                new AbstractUnionViewInfo(
                    _abstractResource,
                    new DbTableName(schema, "SchoolTypeDescriptor_View"),
                    new[]
                    {
                        new AbstractUnionViewOutputColumn(
                            new DbColumnName("Discriminator"),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 256),
                            SourceJsonPath: null,
                            TargetResource: null
                        ),
                    },
                    new[]
                    {
                        new AbstractUnionViewArm(
                            _memberAlpha,
                            new DbTableName(schema, "LongUnionArmTableAlpha"),
                            new AbstractUnionViewProjectionExpression[]
                            {
                                new AbstractUnionViewProjectionExpression.StringLiteral("Alpha"),
                            }
                        ),
                        new AbstractUnionViewArm(
                            _memberBeta,
                            new DbTableName(schema, "LongUnionArmTableBeta"),
                            new AbstractUnionViewProjectionExpression[]
                            {
                                new AbstractUnionViewProjectionExpression.StringLiteral("Beta"),
                            }
                        ),
                    }
                )
            );
        }
    }

    private sealed class UnionArmCollisionDialectRules : ISqlDialectRules
    {
        private readonly IReadOnlyDictionary<string, string> _mapping;
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        public UnionArmCollisionDialectRules(IReadOnlyDictionary<string, string> mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public int MaxIdentifierLength => 63;

        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        public string ShortenIdentifier(string identifier)
        {
            return _mapping.TryGetValue(identifier, out var updated) ? updated : identifier;
        }
    }
}

/// <summary>
/// Test fixture for abstract union-arm source-column collisions after shortening.
/// </summary>
[TestFixture]
public class Given_Abstract_Union_Arm_Source_Column_Shortening_Collision
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new UnionArmSourceColumnCollisionPass(effectiveSchemaSet),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );

        var dialectRules = new UnionArmColumnCollisionDialectRules(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LongUnionArmSourceColumnAlpha"] = "UnionArmSourceColumnCollision",
                ["LongUnionArmSourceColumnBeta"] = "UnionArmSourceColumnCollision",
            }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with an abstract union-arm source-column collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_abstract_union_arm_source_column_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("column name collision");
        _exception!.Message.Should().Contain("LongUnionArmSourceColumnAlpha");
        _exception!.Message.Should().Contain("LongUnionArmSourceColumnBeta");
        _exception!.Message.Should().Contain("UnionArmSourceColumnCollision");
    }

    private sealed class UnionArmSourceColumnCollisionPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _abstractResource;
        private readonly ResourceKeyEntry _memberAlpha;
        private readonly ResourceKeyEntry _memberBeta;

        public UnionArmSourceColumnCollisionPass(EffectiveSchemaSet effectiveSchemaSet)
        {
            ArgumentNullException.ThrowIfNull(effectiveSchemaSet);

            _abstractResource = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "SchoolTypeDescriptor"
            );
            _memberAlpha = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "School"
            );
            _memberBeta = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Sample",
                "Section"
            );
        }

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var schema = new DbSchemaName("edfi");
            var sourceTable = new DbTableName(schema, "UnionArmSourceTable");

            context.AbstractUnionViewsInNameOrder.Add(
                new AbstractUnionViewInfo(
                    _abstractResource,
                    new DbTableName(schema, "SchoolTypeDescriptor_View"),
                    new[]
                    {
                        new AbstractUnionViewOutputColumn(
                            new DbColumnName("DocumentId"),
                            new RelationalScalarType(ScalarKind.Int64),
                            SourceJsonPath: null,
                            TargetResource: null
                        ),
                    },
                    new[]
                    {
                        new AbstractUnionViewArm(
                            _memberAlpha,
                            sourceTable,
                            new AbstractUnionViewProjectionExpression[]
                            {
                                new AbstractUnionViewProjectionExpression.SourceColumn(
                                    new DbColumnName("LongUnionArmSourceColumnAlpha")
                                ),
                            }
                        ),
                        new AbstractUnionViewArm(
                            _memberBeta,
                            sourceTable,
                            new AbstractUnionViewProjectionExpression[]
                            {
                                new AbstractUnionViewProjectionExpression.SourceColumn(
                                    new DbColumnName("LongUnionArmSourceColumnBeta")
                                ),
                            }
                        ),
                    }
                )
            );
        }
    }

    private sealed class UnionArmColumnCollisionDialectRules : ISqlDialectRules
    {
        private readonly IReadOnlyDictionary<string, string> _mapping;
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        public UnionArmColumnCollisionDialectRules(IReadOnlyDictionary<string, string> mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public int MaxIdentifierLength => 63;

        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        public string ShortenIdentifier(string identifier)
        {
            return _mapping.TryGetValue(identifier, out var updated) ? updated : identifier;
        }
    }
}
