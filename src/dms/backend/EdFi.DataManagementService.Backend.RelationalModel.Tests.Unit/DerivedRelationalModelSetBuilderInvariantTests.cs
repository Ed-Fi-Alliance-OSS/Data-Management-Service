// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for duplicate concrete resources in derived model set.
/// </summary>
[TestFixture]
public class Given_Duplicate_Concrete_Resources_In_Derived_Model_Set
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([
            new DuplicateConcreteResourcesPass(effectiveSchemaSet),
        ]);

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with duplicate concrete resource entries.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_concrete_resource_entries()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate concrete resources");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }

    /// <summary>
    /// Test type duplicate concrete resources pass.
    /// </summary>
    private sealed class DuplicateConcreteResourcesPass : IRelationalModelSetPass
    {
        private readonly ResourceKeyEntry _resourceKey;
        private readonly RelationalResourceModel _model;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public DuplicateConcreteResourcesPass(EffectiveSchemaSet effectiveSchemaSet)
        {
            _resourceKey = DerivedRelationalModelSetInvariantTestHelpers.FindResourceKey(
                effectiveSchemaSet,
                "Ed-Fi",
                "School"
            );
            _model = DerivedRelationalModelSetInvariantTestHelpers.CreateMinimalModel(_resourceKey.Resource);
        }

        /// <summary>
        /// Execute.
        /// </summary>
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

/// <summary>
/// Test fixture for duplicate index names in derived model set.
/// </summary>
[TestFixture]
public class Given_Duplicate_Index_Names_In_Derived_Model_Set
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([new DuplicateIndexNamesAcrossTablesPass()]);

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with duplicate index names per schema.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_index_names_per_schema()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate index names");
        _exception.Message.Should().Contain("edfi:IX_Common");
    }
}

/// <summary>
/// Test fixture for duplicate index names across tables in SQL Server.
/// </summary>
[TestFixture]
public class Given_Duplicate_Index_Names_Across_Tables_For_Mssql
{
    private Exception? _exception;
    private DerivedRelationalModelSet? _result;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([new DuplicateIndexNamesAcrossTablesPass()]);

        try
        {
            _result = builder.Build(effectiveSchemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should allow duplicate index names across tables.
    /// </summary>
    [Test]
    public void It_should_allow_duplicate_index_names_across_tables()
    {
        _exception.Should().BeNull();
        _result.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for duplicate trigger names in derived model set.
/// </summary>
[TestFixture]
public class Given_Duplicate_Trigger_Names_In_Derived_Model_Set
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([new DuplicateTriggerNamesPass()]);

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with duplicate trigger names per table.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_trigger_names_per_table()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate trigger names");
        _exception.Message.Should().Contain("edfi.School:TR_School");
    }

    /// <summary>
    /// Test type duplicate trigger names pass.
    /// </summary>
    private sealed class DuplicateTriggerNamesPass : IRelationalModelSetPass
    {
        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var schema = new DbSchemaName("edfi");
            var table = new DbTableName(schema, "School");

            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School"),
                    table,
                    [],
                    [],
                    new TriggerKindParameters.DocumentStamping()
                )
            );
            context.TriggerInventory.Add(
                new DbTriggerInfo(
                    new DbTriggerName("TR_School"),
                    table,
                    [],
                    [],
                    new TriggerKindParameters.DocumentStamping()
                )
            );
        }
    }
}

/// <summary>
/// Test fixture for duplicate trigger names across tables in PostgreSQL.
/// </summary>
[TestFixture]
public class Given_Duplicate_Trigger_Names_Across_Tables_For_Pgsql
{
    private Exception? _exception;
    private DerivedRelationalModelSet? _result;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([new DuplicateTriggerNamesAcrossTablesPass()]);

        try
        {
            _result = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should allow duplicate trigger names across tables.
    /// </summary>
    [Test]
    public void It_should_allow_duplicate_trigger_names_across_tables()
    {
        _exception.Should().BeNull();
        _result.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for duplicate trigger names across tables in SQL Server.
/// </summary>
[TestFixture]
public class Given_Duplicate_Trigger_Names_Across_Tables_For_Mssql
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        var builder = new DerivedRelationalModelSetBuilder([new DuplicateTriggerNamesAcrossTablesPass()]);

        try
        {
            _ = builder.Build(effectiveSchemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should fail with duplicate trigger names per schema.
    /// </summary>
    [Test]
    public void It_should_fail_with_duplicate_trigger_names_per_schema()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Duplicate trigger names");
        _exception.Message.Should().Contain("edfi:TR_Common");
    }
}

/// <summary>
/// Test type derived relational model set invariant test helpers.
/// </summary>
internal static class DerivedRelationalModelSetInvariantTestHelpers
{
    /// <summary>
    /// Find resource key.
    /// </summary>
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

    /// <summary>
    /// Create minimal model.
    /// </summary>
    public static RelationalResourceModel CreateMinimalModel(QualifiedResourceName resource)
    {
        var schema = new DbSchemaName("edfi");
        var keyColumn = new DbKeyColumn(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart
        );
        DbColumnModel[] columns =
        [
            new DbColumnModel(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        ];
        var tableName = new DbTableName(schema, resource.ResourceName);
        var table = new DbTableModel(
            tableName,
            JsonPathExpressionCompiler.Compile("$"),
            new TableKey($"PK_{tableName.Name}", [keyColumn]),
            columns,
            []
        );

        return new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            [table],
            [],
            []
        );
    }
}

file sealed class DuplicateIndexNamesAcrossTablesPass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var schema = new DbSchemaName("edfi");
        var tableAlpha = new DbTableName(schema, "School");
        var tableBeta = new DbTableName(schema, "Student");

        context.IndexInventory.Add(
            new DbIndexInfo(
                new DbIndexName("IX_Common"),
                tableAlpha,
                [],
                false,
                DbIndexKind.ForeignKeySupport
            )
        );
        context.IndexInventory.Add(
            new DbIndexInfo(new DbIndexName("IX_Common"), tableBeta, [], false, DbIndexKind.ForeignKeySupport)
        );
    }
}

file sealed class DuplicateTriggerNamesAcrossTablesPass : IRelationalModelSetPass
{
    /// <summary>
    /// Execute.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var schema = new DbSchemaName("edfi");
        var tableAlpha = new DbTableName(schema, "School");
        var tableBeta = new DbTableName(schema, "Student");

        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName("TR_Common"),
                tableAlpha,
                [],
                [],
                new TriggerKindParameters.DocumentStamping()
            )
        );
        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName("TR_Common"),
                tableBeta,
                [],
                [],
                new TriggerKindParameters.DocumentStamping()
            )
        );
    }
}
