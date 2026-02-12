// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for schema-scoped index collisions in PostgreSQL after shortening.
/// </summary>
[TestFixture]
public class Given_Index_Name_Shortening_Collision_Across_Tables_For_Pgsql
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
                new IndexShorteningCollisionAcrossTablesPass(),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );
        var dialectRules = new MappedDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["IX_LongAlpha"] = "IX_Collision",
                ["IX_LongBeta"] = "IX_Collision",
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
    /// It should fail with a schema-scoped index collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_schema_scoped_index_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier shortening collisions detected");
        _exception.Message.Should().Contain("index name collision");
        _exception.Message.Should().Contain("schema 'edfi'");
    }
}

/// <summary>
/// Test fixture for table-scoped index collisions in SQL Server after shortening.
/// </summary>
[TestFixture]
public class Given_Index_Name_Shortening_Collision_Across_Tables_For_Mssql
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
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new IndexShorteningCollisionAcrossTablesPass(),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );
        var dialectRules = new MappedDialectRules(
            SqlDialect.Mssql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["IX_LongAlpha"] = "IX_Collision",
                ["IX_LongBeta"] = "IX_Collision",
            }
        );

        try
        {
            _result = builder.Build(effectiveSchemaSet, SqlDialect.Mssql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should allow duplicate shortened index names across tables.
    /// </summary>
    [Test]
    public void It_should_allow_duplicate_shortened_index_names_across_tables()
    {
        _exception.Should().BeNull();
        _result.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for table-scoped trigger collisions in PostgreSQL after shortening.
/// </summary>
[TestFixture]
public class Given_Trigger_Name_Shortening_Collision_Across_Tables_For_Pgsql
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
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new TriggerShorteningCollisionAcrossTablesPass(),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );
        var dialectRules = new MappedDialectRules(
            SqlDialect.Pgsql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TR_LongAlpha"] = "TR_Collision",
                ["TR_LongBeta"] = "TR_Collision",
            }
        );

        try
        {
            _result = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, dialectRules);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    /// <summary>
    /// It should allow duplicate shortened trigger names across tables.
    /// </summary>
    [Test]
    public void It_should_allow_duplicate_shortened_trigger_names_across_tables()
    {
        _exception.Should().BeNull();
        _result.Should().NotBeNull();
    }
}

/// <summary>
/// Test fixture for schema-scoped trigger collisions in SQL Server after shortening.
/// </summary>
[TestFixture]
public class Given_Trigger_Name_Shortening_Collision_Across_Tables_For_Mssql
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
                new TriggerShorteningCollisionAcrossTablesPass(),
                new ApplyDialectIdentifierShorteningPass(),
            }
        );
        var dialectRules = new MappedDialectRules(
            SqlDialect.Mssql,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TR_LongAlpha"] = "TR_Collision",
                ["TR_LongBeta"] = "TR_Collision",
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
    /// It should fail with a schema-scoped trigger collision.
    /// </summary>
    [Test]
    public void It_should_fail_with_schema_scoped_trigger_collision()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Identifier shortening collisions detected");
        _exception.Message.Should().Contain("trigger name collision");
        _exception.Message.Should().Contain("schema 'edfi'");
    }
}

file sealed class IndexShorteningCollisionAcrossTablesPass : IRelationalModelSetPass
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
                new DbIndexName("IX_LongAlpha"),
                tableAlpha,
                [],
                false,
                DbIndexKind.ForeignKeySupport
            )
        );
        context.IndexInventory.Add(
            new DbIndexInfo(
                new DbIndexName("IX_LongBeta"),
                tableBeta,
                [],
                false,
                DbIndexKind.ForeignKeySupport
            )
        );
    }
}

file sealed class TriggerShorteningCollisionAcrossTablesPass : IRelationalModelSetPass
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
                new DbTriggerName("TR_LongAlpha"),
                tableAlpha,
                DbTriggerKind.DocumentStamping,
                [],
                []
            )
        );
        context.TriggerInventory.Add(
            new DbTriggerInfo(
                new DbTriggerName("TR_LongBeta"),
                tableBeta,
                DbTriggerKind.DocumentStamping,
                [],
                []
            )
        );
    }
}

file sealed class MappedDialectRules : ISqlDialectRules
{
    private readonly SqlDialect _dialect;
    private readonly IReadOnlyDictionary<string, string> _mapping;
    private readonly SqlScalarTypeDefaults _defaults;

    public MappedDialectRules(SqlDialect dialect, IReadOnlyDictionary<string, string> mapping)
    {
        _dialect = dialect;
        _mapping = mapping;
        _defaults = dialect switch
        {
            SqlDialect.Pgsql => new PgsqlDialectRules().ScalarTypeDefaults,
            SqlDialect.Mssql => new MssqlDialectRules().ScalarTypeDefaults,
            _ => new PgsqlDialectRules().ScalarTypeDefaults,
        };
    }

    /// <summary>
    /// Gets dialect.
    /// </summary>
    public SqlDialect Dialect => _dialect;

    /// <summary>
    /// Gets max identifier length.
    /// </summary>
    public int MaxIdentifierLength =>
        _dialect switch
        {
            SqlDialect.Pgsql => 63,
            SqlDialect.Mssql => 128,
            _ => 128,
        };

    /// <summary>
    /// Gets scalar type defaults.
    /// </summary>
    public SqlScalarTypeDefaults ScalarTypeDefaults => _defaults;

    /// <summary>
    /// Shorten identifier.
    /// </summary>
    public string ShortenIdentifier(string identifier)
    {
        return _mapping.TryGetValue(identifier, out var updated) ? updated : identifier;
    }
}
