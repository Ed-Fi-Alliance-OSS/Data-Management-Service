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

    /// <summary>
    /// Fixture pass that seeds two resources with primary key constraint names that shorten to the same value.
    /// </summary>
    private sealed class PrimaryKeyIdentifierShorteningCollisionPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _resourceOne;
        private readonly ResourceKeyEntry _resourceTwo;

        /// <summary>
        /// Initializes a new instance, resolving the resource keys required for seeding fixture models.
        /// </summary>
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

        /// <summary>
        /// Adds two concrete resource models whose primary key names are designed to collide after shortening.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
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

        /// <summary>
        /// Builds a minimal relational resource model with a table/key name designed for shortening collision.
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
    /// Dialect rules that force specific primary key names to shorten to a shared collision value.
    /// </summary>
    private sealed class PrimaryKeyCollisionDialectRules : ISqlDialectRules
    {
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        /// <summary>
        /// Gets the dialect for this fixture rules implementation.
        /// </summary>
        public SqlDialect Dialect => SqlDialect.Pgsql;

        /// <summary>
        /// Gets the maximum identifier length for this fixture rules implementation.
        /// </summary>
        public int MaxIdentifierLength => 63;

        /// <summary>
        /// Gets the scalar type defaults reused by this fixture rules implementation.
        /// </summary>
        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        /// <summary>
        /// Shortens primary key identifiers to a fixed collision string to trigger a collision error.
        /// </summary>
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

    /// <summary>
    /// Fixture pass that seeds an abstract union view with arms whose source tables collide after shortening.
    /// </summary>
    private sealed class UnionArmSourceTableCollisionPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _abstractResource;
        private readonly ResourceKeyEntry _memberAlpha;
        private readonly ResourceKeyEntry _memberBeta;

        /// <summary>
        /// Initializes a new instance, resolving resource keys for abstract and member resources.
        /// </summary>
        public UnionArmSourceTableCollisionPass(EffectiveSchemaSet effectiveSchemaSet)
        {
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

        /// <summary>
        /// Adds an abstract union view whose arm source tables are designed to collide after shortening.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
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

    /// <summary>
    /// Dialect rules that map specific union arm source table identifiers to a shared collision value.
    /// </summary>
    private sealed class UnionArmCollisionDialectRules : ISqlDialectRules
    {
        private readonly IReadOnlyDictionary<string, string> _mapping;
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        /// <summary>
        /// Initializes a new instance with a deterministic identifier mapping.
        /// </summary>
        public UnionArmCollisionDialectRules(IReadOnlyDictionary<string, string> mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        /// <summary>
        /// Gets the dialect for this fixture rules implementation.
        /// </summary>
        public SqlDialect Dialect => SqlDialect.Pgsql;

        /// <summary>
        /// Gets the maximum identifier length for this fixture rules implementation.
        /// </summary>
        public int MaxIdentifierLength => 63;

        /// <summary>
        /// Gets the scalar type defaults reused by this fixture rules implementation.
        /// </summary>
        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        /// <summary>
        /// Shortens identifiers by applying the configured mapping.
        /// </summary>
        public string ShortenIdentifier(string identifier)
        {
            return _mapping.TryGetValue(identifier, out var updated) ? updated : identifier;
        }
    }
}

/// <summary>
/// Test fixture for tracked-change schema (<c>tracked_changes_*</c>) collisions after shortening. Two
/// distinct project schemas can yield tracked-change schemas that shorten to the same identifier even when
/// the source project schemas do not collide; the tracked-change schemas must be registered for detection.
/// </summary>
[TestFixture]
public class Given_Tracked_Change_Schema_Shortening_Collision
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
                new TrackedChangeSchemaCollisionPass(),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );

        var dialectRules = new TrackedChangeSchemaCollisionDialectRules(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tracked_changes_LongProjectAlpha"] = "tc_collision",
                ["tracked_changes_LongProjectBeta"] = "tc_collision",
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
    /// It should fail with a tracked-change schema collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_tracked_change_schema_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().StartWith("Identifier shortening collisions detected: ");
        _exception!.Message.Should().Contain("schema name collision");
        _exception!.Message.Should().Contain("tracked_changes_LongProjectAlpha -> tc_collision");
        _exception!.Message.Should().Contain("tracked_changes_LongProjectBeta -> tc_collision");
    }

    /// <summary>
    /// Fixture pass that seeds two tracked-change tables whose schemas shorten to the same identifier.
    /// </summary>
    private sealed class TrackedChangeSchemaCollisionPass : IRelationalModelSetPass
    {
        /// <summary>
        /// Adds two tracked-change tables in distinct tracked-change schemas designed to collide after
        /// shortening, with distinct table names so only the schema collision fires.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            context.TrackedChangeInventory.Add(
                CreateTrackedChangeTable("tracked_changes_LongProjectAlpha", "TrackedAlpha")
            );
            context.TrackedChangeInventory.Add(
                CreateTrackedChangeTable("tracked_changes_LongProjectBeta", "TrackedBeta")
            );
        }

        /// <summary>
        /// Builds a minimal tracked-change table in the given schema.
        /// </summary>
        private static TrackedChangeTableInfo CreateTrackedChangeTable(string schema, string tableName)
        {
            return new TrackedChangeTableInfo(
                new DbTableName(new DbSchemaName(schema), tableName),
                TrackedChangeTableKind.Resource,
                new DbTableName(new DbSchemaName("edfi"), tableName),
                Array.Empty<TrackedChangeColumnInfo>(),
                Array.Empty<TrackedChangeSystemColumnInfo>(),
                Array.Empty<DbColumnName>(),
                Array.Empty<TrackedChangeDescriptorJoinInfo>(),
                Array.Empty<TrackedChangePersonJoinInfo>()
            );
        }
    }

    /// <summary>
    /// Dialect rules that map specific tracked-change schema identifiers to a shared collision value.
    /// </summary>
    private sealed class TrackedChangeSchemaCollisionDialectRules : ISqlDialectRules
    {
        private readonly IReadOnlyDictionary<string, string> _mapping;
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        /// <summary>
        /// Initializes a new instance with a deterministic identifier mapping.
        /// </summary>
        public TrackedChangeSchemaCollisionDialectRules(IReadOnlyDictionary<string, string> mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        /// <summary>
        /// Gets the dialect for this fixture rules implementation.
        /// </summary>
        public SqlDialect Dialect => SqlDialect.Pgsql;

        /// <summary>
        /// Gets the maximum identifier length for this fixture rules implementation.
        /// </summary>
        public int MaxIdentifierLength => 63;

        /// <summary>
        /// Gets the scalar type defaults reused by this fixture rules implementation.
        /// </summary>
        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        /// <summary>
        /// Shortens identifiers by applying the configured mapping.
        /// </summary>
        public string ShortenIdentifier(string identifier)
        {
            return _mapping.TryGetValue(identifier, out var updated) ? updated : identifier;
        }
    }
}

/// <summary>
/// Test fixture for tracked-change primary-key constraint collisions after shortening. Two tracked-change
/// tables in the same schema with distinct names generate distinct <c>PK_&lt;schema&gt;_&lt;table&gt;</c>
/// constraint names that can still shorten to the same identifier; those generated names must be registered
/// for collision detection so the failure surfaces during model derivation rather than at DDL execution.
/// </summary>
[TestFixture]
public class Given_Tracked_Change_Primary_Key_Shortening_Collision
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
                new TrackedChangePrimaryKeyCollisionPass(),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );

        var dialectRules = new TrackedChangeConstraintCollisionDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PK_tracked_changes_edfi_TrackedAlpha"] = "PK_collision",
                ["PK_tracked_changes_edfi_TrackedBeta"] = "PK_collision",
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
    /// It should fail with a tracked-change primary-key constraint collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_tracked_change_primary_key_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().StartWith("Identifier shortening collisions detected: ");
        _exception!.Message.Should().Contain("constraint name collision");
        _exception!.Message.Should().Contain("PK_tracked_changes_edfi_TrackedAlpha -> PK_collision");
        _exception!.Message.Should().Contain("PK_tracked_changes_edfi_TrackedBeta -> PK_collision");
    }

    /// <summary>
    /// Fixture pass that seeds two same-schema tracked-change tables whose generated primary-key constraint
    /// names shorten to the same identifier, with distinct table names so only the constraint collision fires.
    /// </summary>
    private sealed class TrackedChangePrimaryKeyCollisionPass : IRelationalModelSetPass
    {
        /// <summary>
        /// Adds the two colliding tracked-change tables to the inventory.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            context.TrackedChangeInventory.Add(CreateTrackedChangeTable("TrackedAlpha"));
            context.TrackedChangeInventory.Add(CreateTrackedChangeTable("TrackedBeta"));
        }

        /// <summary>
        /// Builds a minimal tracked-change table in <c>tracked_changes_edfi</c> with a single primary-key
        /// column so the primary-key constraint name is generated and registered.
        /// </summary>
        private static TrackedChangeTableInfo CreateTrackedChangeTable(string tableName)
        {
            return new TrackedChangeTableInfo(
                new DbTableName(new DbSchemaName("tracked_changes_edfi"), tableName),
                TrackedChangeTableKind.Resource,
                new DbTableName(new DbSchemaName("edfi"), tableName),
                Array.Empty<TrackedChangeColumnInfo>(),
                Array.Empty<TrackedChangeSystemColumnInfo>(),
                new DbColumnName[] { new("Id") },
                Array.Empty<TrackedChangeDescriptorJoinInfo>(),
                Array.Empty<TrackedChangePersonJoinInfo>()
            );
        }
    }
}

/// <summary>
/// Test fixture for tracked-change <c>CreatedAt</c> default-constraint collisions after shortening on SQL
/// Server. Two tracked-change tables generate distinct <c>DF_&lt;schema&gt;_&lt;table&gt;_CreatedAt</c>
/// default-constraint names that can shorten to the same identifier; those generated names must be registered
/// for collision detection.
/// </summary>
[TestFixture]
public class Given_Tracked_Change_Default_Constraint_Shortening_Collision
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
                new TrackedChangeDefaultConstraintCollisionPass(),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );

        var dialectRules = new TrackedChangeConstraintCollisionDialectRules(
            SqlDialect.Mssql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DF_tracked_changes_edfi_TrackedAlpha_CreatedAt"] = "DF_collision",
                ["DF_tracked_changes_edfi_TrackedBeta_CreatedAt"] = "DF_collision",
            }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Mssql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with a tracked-change default-constraint collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_tracked_change_default_constraint_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().StartWith("Identifier shortening collisions detected: ");
        _exception!.Message.Should().Contain("constraint name collision");
        _exception!
            .Message.Should()
            .Contain("DF_tracked_changes_edfi_TrackedAlpha_CreatedAt -> DF_collision");
        _exception!.Message.Should().Contain("DF_tracked_changes_edfi_TrackedBeta_CreatedAt -> DF_collision");
    }

    /// <summary>
    /// Fixture pass that seeds two same-schema tracked-change tables whose generated <c>CreatedAt</c>
    /// default-constraint names shorten to the same identifier, with distinct table names so only the
    /// constraint collision fires.
    /// </summary>
    private sealed class TrackedChangeDefaultConstraintCollisionPass : IRelationalModelSetPass
    {
        /// <summary>
        /// Adds the two colliding tracked-change tables to the inventory.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            context.TrackedChangeInventory.Add(CreateTrackedChangeTable("TrackedAlpha"));
            context.TrackedChangeInventory.Add(CreateTrackedChangeTable("TrackedBeta"));
        }

        /// <summary>
        /// Builds a minimal tracked-change table in <c>tracked_changes_edfi</c> with a <c>CreatedAt</c>
        /// system column so the named default constraint is generated and registered.
        /// </summary>
        private static TrackedChangeTableInfo CreateTrackedChangeTable(string tableName)
        {
            return new TrackedChangeTableInfo(
                new DbTableName(new DbSchemaName("tracked_changes_edfi"), tableName),
                TrackedChangeTableKind.Resource,
                new DbTableName(new DbSchemaName("edfi"), tableName),
                Array.Empty<TrackedChangeColumnInfo>(),
                new TrackedChangeSystemColumnInfo[]
                {
                    new(
                        TrackedChangeSystemColumnRole.CreatedAt,
                        new DbColumnName("CreatedAt"),
                        null,
                        false,
                        false
                    ),
                },
                Array.Empty<DbColumnName>(),
                Array.Empty<TrackedChangeDescriptorJoinInfo>(),
                Array.Empty<TrackedChangePersonJoinInfo>()
            );
        }
    }
}

/// <summary>
/// Dialect rules that map specific tracked-change constraint identifiers to a shared collision value while
/// leaving all other identifiers unchanged. Shared by the tracked-change primary-key and default-constraint
/// collision fixtures.
/// </summary>
internal sealed class TrackedChangeConstraintCollisionDialectRules : ISqlDialectRules
{
    private readonly IReadOnlyDictionary<string, string> _mapping;
    private static readonly SqlScalarTypeDefaults PgsqlDefaults = new PgsqlDialectRules().ScalarTypeDefaults;
    private static readonly SqlScalarTypeDefaults MssqlDefaults = new MssqlDialectRules().ScalarTypeDefaults;

    /// <summary>
    /// Initializes a new instance for the given dialect with a deterministic identifier mapping.
    /// </summary>
    public TrackedChangeConstraintCollisionDialectRules(
        SqlDialect dialect,
        IReadOnlyDictionary<string, string> mapping
    )
    {
        Dialect = dialect;
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
    }

    /// <summary>
    /// Gets the dialect for this fixture rules implementation.
    /// </summary>
    public SqlDialect Dialect { get; }

    /// <summary>
    /// Gets the maximum identifier length for the configured dialect.
    /// </summary>
    public int MaxIdentifierLength => Dialect == SqlDialect.Mssql ? 128 : 63;

    /// <summary>
    /// Gets the scalar type defaults for the configured dialect.
    /// </summary>
    public SqlScalarTypeDefaults ScalarTypeDefaults =>
        Dialect == SqlDialect.Mssql ? MssqlDefaults : PgsqlDefaults;

    /// <summary>
    /// Shortens identifiers by applying the configured mapping.
    /// </summary>
    public string ShortenIdentifier(string identifier)
    {
        return _mapping.TryGetValue(identifier, out var updated) ? updated : identifier;
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

    /// <summary>
    /// Fixture pass that seeds an abstract union view with arms whose source columns collide after shortening.
    /// </summary>
    private sealed class UnionArmSourceColumnCollisionPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _abstractResource;
        private readonly ResourceKeyEntry _memberAlpha;
        private readonly ResourceKeyEntry _memberBeta;

        /// <summary>
        /// Initializes a new instance, resolving resource keys for abstract and member resources.
        /// </summary>
        public UnionArmSourceColumnCollisionPass(EffectiveSchemaSet effectiveSchemaSet)
        {
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

        /// <summary>
        /// Adds an abstract union view whose arm source columns are designed to collide after shortening.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
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

    /// <summary>
    /// Dialect rules that map specific union arm source column identifiers to a shared collision value.
    /// </summary>
    private sealed class UnionArmColumnCollisionDialectRules : ISqlDialectRules
    {
        private readonly IReadOnlyDictionary<string, string> _mapping;
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        /// <summary>
        /// Initializes a new instance with a deterministic identifier mapping.
        /// </summary>
        public UnionArmColumnCollisionDialectRules(IReadOnlyDictionary<string, string> mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        /// <summary>
        /// Gets the dialect for this fixture rules implementation.
        /// </summary>
        public SqlDialect Dialect => SqlDialect.Pgsql;

        /// <summary>
        /// Gets the maximum identifier length for this fixture rules implementation.
        /// </summary>
        public int MaxIdentifierLength => 63;

        /// <summary>
        /// Gets the scalar type defaults reused by this fixture rules implementation.
        /// </summary>
        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        /// <summary>
        /// Shortens identifiers by applying the configured mapping.
        /// </summary>
        public string ShortenIdentifier(string identifier)
        {
            return _mapping.TryGetValue(identifier, out var updated) ? updated : identifier;
        }
    }
}
