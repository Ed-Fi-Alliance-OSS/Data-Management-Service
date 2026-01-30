// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Identifier_Shortening_Collision_In_Derived_Model_Set
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[] { new IdentifierShorteningCollisionPass(effectiveSchemaSet) }
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

    [Test]
    public void It_should_fail_with_identifier_shortening_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier shortening collisions detected");
        _exception.Message.Should().Contain("CollisionName");
        _exception.Message.Should().Contain("LongTableNameAlpha");
        _exception.Message.Should().Contain("LongTableNameBeta");
    }

    private sealed class IdentifierShorteningCollisionPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _resourceOne;
        private readonly ResourceKeyEntry _resourceTwo;

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

        public int Order { get; } = 1;

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
                new[] { table },
                Array.Empty<DocumentReferenceBinding>(),
                Array.Empty<DescriptorEdgeSource>()
            );
        }
    }

    private sealed class CollisionDialectRules : ISqlDialectRules
    {
        private static readonly SqlScalarTypeDefaults Defaults = new PgsqlDialectRules().ScalarTypeDefaults;

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public int MaxIdentifierLength => 8;

        public SqlScalarTypeDefaults ScalarTypeDefaults => Defaults;

        public string ShortenIdentifier(string identifier)
        {
            return identifier.Contains("LongTableName", StringComparison.Ordinal)
                ? "CollisionName"
                : identifier;
        }
    }
}
