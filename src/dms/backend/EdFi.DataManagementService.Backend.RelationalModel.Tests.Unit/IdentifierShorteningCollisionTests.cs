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
                new ApplyDialectIdentifierShorteningRelationalModelSetPass(),
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
        _exception!
            .Message.Should()
            .Be(
                "Identifier shortening collisions detected: "
                    + "table name collision AfterDialectShortening(Pgsql:8-bytes) in schema 'edfi': "
                    + "LongTableNameAlpha -> CollisionName "
                    + "(table edfi.LongTableNameAlpha, resource 'Ed-Fi:School', path '$'), "
                    + "LongTableNameBeta -> CollisionName "
                    + "(table edfi.LongTableNameBeta, resource 'Ed-Fi:SchoolTypeDescriptor', path '$')"
            );
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
                new TableKey([keyColumn]),
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
