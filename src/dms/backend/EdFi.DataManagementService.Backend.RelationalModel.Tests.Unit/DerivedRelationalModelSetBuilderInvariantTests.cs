// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Duplicate_Concrete_Resources_In_Derived_Model_Set
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[] { new DuplicateConcreteResourcesPass(effectiveSchemaSet) }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_duplicate_concrete_resource_entries()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate concrete resources");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }

    private sealed class DuplicateConcreteResourcesPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _resourceKey;
        private readonly RelationalResourceModel _model;

        public DuplicateConcreteResourcesPass(EffectiveSchemaSet effectiveSchemaSet)
        {
            _resourceKey = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "School"
            );
            _model = DerivedRelationalModelSetInvariantTestHelpers.CreateMinimalModel(_resourceKey.Resource);
        }

        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.ConcreteResourcesInNameOrder.Add(
                new ConcreteResourceModel(_resourceKey, ResourceStorageKind.RelationalTables, _model)
            );
            context.ConcreteResourcesInNameOrder.Add(
                new ConcreteResourceModel(_resourceKey, ResourceStorageKind.RelationalTables, _model)
            );
        }
    }
}

[TestFixture]
public class Given_Duplicate_Index_Names_In_Derived_Model_Set
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[] { new DuplicateIndexNamesPass() }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_duplicate_index_names_per_table()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate index names");
        _exception.Message.Should().Contain("edfi.School:IX_School");
    }

    private sealed class DuplicateIndexNamesPass : IRelationalModelSetPass
    {
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var schema = new DbSchemaName("edfi");
            var table = new DbTableName(schema, "School");

            context.IndexInventory.Add(
                new DbIndexInfo(new DbIndexName("IX_School"), table, [], false, DbIndexKind.ForeignKeySupport)
            );
            context.IndexInventory.Add(
                new DbIndexInfo(new DbIndexName("IX_School"), table, [], false, DbIndexKind.ForeignKeySupport)
            );
        }
    }
}

[TestFixture]
public class Given_Duplicate_Trigger_Names_In_Derived_Model_Set
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[] { new DuplicateTriggerNamesPass() }
        );

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_duplicate_trigger_names_per_table()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate trigger names");
        _exception.Message.Should().Contain("edfi.School:TR_School");
    }

    private sealed class DuplicateTriggerNamesPass : IRelationalModelSetPass
    {
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var schema = new DbSchemaName("edfi");
            var table = new DbTableName(schema, "School");

            context.TriggerInventory.Add(
                new DbTriggerInfo(new DbTriggerName("TR_School"), table, DbTriggerKind.DocumentStamping, [])
            );
            context.TriggerInventory.Add(
                new DbTriggerInfo(new DbTriggerName("TR_School"), table, DbTriggerKind.DocumentStamping, [])
            );
        }
    }
}

internal static class DerivedRelationalModelSetInvariantTestHelpers
{
    public static ResourceKeyEntry FindResourceKey(
        EffectiveSchemaSet effectiveSchemaSet,
        string projectName,
        string resourceName
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);

        return effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Single(entry =>
            entry.Resource.ProjectName == projectName && entry.Resource.ResourceName == resourceName
        );
    }

    public static RelationalResourceModel CreateMinimalModel(QualifiedResourceName resource)
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
            new DbTableName(schema, resource.ResourceName),
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
            [table],
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );
    }
}
