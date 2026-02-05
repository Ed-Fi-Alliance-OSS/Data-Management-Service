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

internal static class ReservedIdentifierAssertions
{
    private const string Identifier = ReservedIdentifierFixture.Identifier;

    public static void AssertQuotedIdentifiers(string sql, SqlDialect dialect)
    {
        var quoted = dialect == SqlDialect.Pgsql ? $"\"{Identifier}\"" : $"[{Identifier}]";

        sql.Should().Contain(quoted);
        sql.Should().Contain("CREATE TABLE");
        Regex.IsMatch(sql, @"CREATE\s+(UNIQUE\s+)?INDEX").Should().BeTrue();
        sql.Should().Contain("CREATE TRIGGER");
        sql.Should().Contain($"CONSTRAINT {quoted}");

        AssertNoUnquotedIdentifier(sql, dialect);
    }

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

internal static class ReservedIdentifierFixture
{
    public const string Identifier = "Select";

    public static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName(Identifier);
        var table = new DbTableName(schema, Identifier);
        var column = new DbColumnName(Identifier);
        var jsonScope = new JsonPathExpression("$", Array.Empty<JsonPathSegment>());
        var resource = new QualifiedResourceName("TestProject", "TestResource");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var tableModel = new DbTableModel(
            table,
            jsonScope,
            new TableKey(new[] { new DbKeyColumn(column, ColumnKind.ParentKeyPart) }),
            new[] { new DbColumnModel(column, ColumnKind.ParentKeyPart, null, false, null, null) },
            new[]
            {
                new TableConstraint.ForeignKey(
                    Identifier,
                    new[] { column },
                    table,
                    new[] { column },
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            }
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            tableModel,
            new[] { tableModel },
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                new byte[] { 0x01 },
                new[] { new SchemaComponentInfo("test", "TestProject", "1.0.0", false) },
                new[] { resourceKey }
            ),
            dialect,
            new[] { new ProjectSchemaInfo("test", "TestProject", "1.0.0", false, schema) },
            new[]
            {
                new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel),
            },
            Array.Empty<AbstractIdentityTableInfo>(),
            Array.Empty<AbstractUnionViewInfo>(),
            new[]
            {
                new DbIndexInfo(
                    new DbIndexName(Identifier),
                    table,
                    new[] { column },
                    true,
                    DbIndexKind.Explicit
                ),
            },
            new[]
            {
                new DbTriggerInfo(
                    new DbTriggerName(Identifier),
                    table,
                    DbTriggerKind.DocumentStamping,
                    new[] { column }
                ),
            }
        );
    }
}
