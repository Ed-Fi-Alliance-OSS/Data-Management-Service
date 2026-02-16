// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for DDL emission with reserved identifiers using Pgsql rules.
/// </summary>
[TestFixture]
public class Given_Pgsql_Ddl_Emission_With_Reserved_Identifiers
{
    private string _sql = string.Empty;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var dialectRules = new PgsqlDialectRules();
        var modelSet = ReservedIdentifierFixture.Build(dialectRules.Dialect);
        var emitter = new RelationalModelDdlEmitter(dialectRules);

        _sql = emitter.Emit(modelSet);
    }

    /// <summary>
    /// It should quote all identifiers in Pgsql DDL.
    /// </summary>
    [Test]
    public void It_should_quote_all_identifiers()
    {
        ReservedIdentifierAssertions.AssertQuotedIdentifiers(_sql, SqlDialect.Pgsql);
    }
}

/// <summary>
/// Test fixture for DDL emission with reserved identifiers using Mssql rules.
/// </summary>
[TestFixture]
public class Given_Mssql_Ddl_Emission_With_Reserved_Identifiers
{
    private string _sql = string.Empty;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var dialectRules = new MssqlDialectRules();
        var modelSet = ReservedIdentifierFixture.Build(dialectRules.Dialect);
        var emitter = new RelationalModelDdlEmitter(dialectRules);

        _sql = emitter.Emit(modelSet);
    }

    /// <summary>
    /// It should quote all identifiers in Mssql DDL.
    /// </summary>
    [Test]
    public void It_should_quote_all_identifiers()
    {
        ReservedIdentifierAssertions.AssertQuotedIdentifiers(_sql, SqlDialect.Mssql);
    }
}

/// <summary>
/// Assertion helpers for validating that dialect-specific DDL output quotes reserved identifiers.
/// </summary>
internal static class ReservedIdentifierAssertions
{
    private const string Identifier = ReservedIdentifierFixture.Identifier;

    /// <summary>
    /// Asserts that the emitted DDL contains quoted occurrences of the reserved identifier for the given dialect
    /// and that no unquoted occurrences remain.
    /// </summary>
    public static void AssertQuotedIdentifiers(string sql, SqlDialect dialect)
    {
        var quoted = dialect == SqlDialect.Pgsql ? $"\"{Identifier}\"" : $"[{Identifier}]";

        sql.Should().Contain(quoted);
        sql.Should().Contain("CREATE TABLE");
        Regex.IsMatch(sql, @"CREATE\s+(UNIQUE\s+)?INDEX").Should().BeTrue();
        Regex.IsMatch(sql, @"CREATE\s+(OR\s+(REPLACE|ALTER)\s+)?TRIGGER").Should().BeTrue();
        sql.Should().Contain($"CONSTRAINT {quoted}");

        AssertNoUnquotedIdentifier(sql, dialect);
    }

    /// <summary>
    /// Asserts that the emitted DDL contains no unquoted occurrences of the reserved identifier for the given dialect.
    /// </summary>
    private static void AssertNoUnquotedIdentifier(string sql, SqlDialect dialect)
    {
        var pattern = dialect switch
        {
            SqlDialect.Pgsql => $@"(?<!\"")\b{Regex.Escape(Identifier)}\b(?!\"")",
            SqlDialect.Mssql => $@"(?<!\[)\b{Regex.Escape(Identifier)}\b(?!\])",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };

        Regex.IsMatch(sql, pattern).Should().BeFalse($"Expected all {Identifier} identifiers to be quoted.");
    }
}

/// <summary>
/// Builds a minimal derived model set containing reserved identifiers to validate quoting behavior.
/// </summary>
internal static class ReservedIdentifierFixture
{
    public const string Identifier = "Select";

    /// <summary>
    /// Builds a minimal <see cref="DerivedRelationalModelSet"/> with schema, table, column, constraint, index,
    /// and trigger names that use a reserved identifier.
    /// </summary>
    public static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName(Identifier);
        var table = new DbTableName(schema, Identifier);
        var column = new DbColumnName(Identifier);
        var jsonScope = new JsonPathExpression("$", []);
        var resource = new QualifiedResourceName("TestProject", "TestResource");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var tableModel = new DbTableModel(
            table,
            jsonScope,
            new TableKey($"PK_{table.Name}", [new DbKeyColumn(column, ColumnKind.ParentKeyPart)]),
            [new DbColumnModel(column, ColumnKind.ParentKeyPart, null, false, null, null)],
            [
                new TableConstraint.ForeignKey(
                    Identifier,
                    [column],
                    table,
                    [column],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            tableModel,
            [tableModel],
            [],
            []
        );

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [
                    new SchemaComponentInfo(
                        "test",
                        "TestProject",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("test", "TestProject", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [new DbIndexInfo(new DbIndexName(Identifier), table, [column], true, DbIndexKind.Explicit)],
            [
                new DbTriggerInfo(
                    new DbTriggerName(Identifier),
                    table,
                    DbTriggerKind.DocumentStamping,
                    [column],
                    []
                ),
            ]
        );
    }
}
