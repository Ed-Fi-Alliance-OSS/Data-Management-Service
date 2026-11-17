// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_Dialect_Ddl_Boundary_Contracts(SqlDialect dialectKind)
{
    private readonly ISqlDialect _dialect = SqlDialectFactory.Create(dialectKind);
    private static readonly DbTableName _table = new(new DbSchemaName("Owner's"), "Student's");

    [TestCase("index")]
    [TestCase("foreign key")]
    [TestCase("unique constraint")]
    [TestCase("primary key")]
    public void It_rejects_an_empty_column_inventory(string operation)
    {
        Action render = () =>
            _ = operation switch
            {
                "index" => _dialect.CreateIndexIfNotExists(_table, "Index", []),
                "foreign key" => _dialect.AddForeignKeyConstraint(_table, "ForeignKey", [], _table, []),
                "unique constraint" => _dialect.AddUniqueConstraint(_table, "Unique", []),
                "primary key" => _dialect.RenderNamedPrimaryKeyClause("Primary", []),
                _ => throw new InvalidOperationException($"Unsupported test operation: {operation}"),
            };

        render.Should().Throw<ArgumentException>().WithParameterName("columns");
    }

    [Test]
    public void It_rejects_mismatched_foreign_key_column_inventories()
    {
        Action render = () =>
            _dialect.AddForeignKeyConstraint(
                _table,
                "ForeignKey",
                [new("First"), new("Second")],
                _table,
                [new("First")]
            );

        render.Should().Throw<ArgumentException>().WithParameterName("targetColumns");
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void It_rejects_nonpositive_binary_lengths(int length)
    {
        Action render = () => _dialect.RenderBinaryColumnType(length);

        render
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("length")
            .Which.ActualValue.Should()
            .Be(length);
    }

    [Test]
    public void It_treats_empty_included_columns_like_omitted_included_columns()
    {
        var omitted = _dialect.CreateIndexIfNotExists(_table, "Index", [new("First")]);
        var empty = _dialect.CreateIndexIfNotExists(_table, "Index", [new("First")], includeColumns: []);

        empty.Should().Be(omitted).And.NotContain("INCLUDE");
    }

    [Test]
    public void It_escapes_sequence_names_in_identifier_and_literal_positions()
    {
        var sql = _dialect.CreateSequenceIfNotExists(_table.Schema, "Sequence's", startWith: 17);
        var defaultExpression = _dialect.RenderSequenceDefaultExpression(_table.Schema, "Sequence's");

        sql = sql.ReplaceLineEndings("\n");
        if (dialectKind == SqlDialect.Mssql)
        {
            sql.Should()
                .Be(
                    """
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.sequences s
                        JOIN sys.schemas sch ON s.schema_id = sch.schema_id
                        WHERE sch.name = N'Owner''s' AND s.name = N'Sequence''s'
                    )
                    CREATE SEQUENCE [Owner's].[Sequence's] START WITH 17;
                    """.ReplaceLineEndings("\n")
                );
            defaultExpression.Should().Be("(NEXT VALUE FOR [Owner's].[Sequence's])");
        }
        else
        {
            sql.Should().Be("CREATE SEQUENCE IF NOT EXISTS \"Owner's\".\"Sequence's\" START WITH 17;");
            defaultExpression.Should().Be("nextval('\"Owner''s\".\"Sequence''s\"')");
        }
    }

    [Test]
    public void It_escapes_index_names_without_changing_the_indexed_columns()
    {
        var sql = _dialect.CreateIndexIfNotExists(_table, "Index's", [new("Second"), new("First")]);

        sql = sql.ReplaceLineEndings("\n");
        if (dialectKind == SqlDialect.Mssql)
        {
            sql.Should()
                .Be(
                    """
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.indexes i
                        JOIN sys.tables t ON i.object_id = t.object_id
                        JOIN sys.schemas s ON t.schema_id = s.schema_id
                        WHERE s.name = N'Owner''s' AND t.name = N'Student''s' AND i.name = N'Index''s'
                    )
                    CREATE INDEX [Index's] ON [Owner's].[Student's] ([Second], [First]);
                    """.ReplaceLineEndings("\n")
                );
        }
        else
        {
            sql.Should()
                .Be(
                    "CREATE INDEX IF NOT EXISTS \"Index's\" ON \"Owner's\".\"Student's\" (\"Second\", \"First\");"
                );
        }
    }

    [TestCase("foreign key")]
    [TestCase("unique")]
    [TestCase("check")]
    public void It_escapes_constraint_lookup_literals_and_retains_the_quoted_identifiers(string kind)
    {
        var sql = kind switch
        {
            "foreign key" => _dialect.AddForeignKeyConstraint(
                _table,
                "Rule's",
                [new("Second"), new("First")],
                new(new DbSchemaName("edfi"), "Target"),
                [new("TargetSecond"), new("TargetFirst")]
            ),
            "unique" => _dialect.AddUniqueConstraint(_table, "Rule's", [new("Second"), new("First")]),
            "check" => _dialect.AddCheckConstraint(_table, "Rule's", "1 = 1"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        sql = sql.ReplaceLineEndings("\n");
        if (dialectKind == SqlDialect.Mssql)
        {
            sql.Should()
                .Contain("WHERE name = N'Rule''s'")
                .And.Contain("parent_object_id = OBJECT_ID(N'Owner''s.Student''s')")
                .And.Contain("ALTER TABLE [Owner's].[Student's]\nADD CONSTRAINT [Rule's]");
            if (kind == "foreign key")
            {
                sql.Should()
                    .Contain(
                        "FOREIGN KEY ([Second], [First])\nREFERENCES [edfi].[Target] ([TargetSecond], [TargetFirst])"
                    );
            }
        }
        else
        {
            sql.Should()
                .Contain("WHERE conname = 'Rule''s'")
                .And.Contain("AND conrelid = to_regclass('\"Owner''s\".\"Student''s\"')")
                .And.Contain("ALTER TABLE \"Owner's\".\"Student's\"")
                .And.Contain("ADD CONSTRAINT \"Rule's\"");
            if (kind == "foreign key")
            {
                sql.Should()
                    .Contain("FOREIGN KEY (\"Second\", \"First\")")
                    .And.Contain("REFERENCES \"edfi\".\"Target\" (\"TargetSecond\", \"TargetFirst\")");
            }
        }
    }
}

[TestFixture]
public class Given_Mssql_Column_Default_And_Table_Type_Contracts
{
    private readonly MssqlDialect _dialect = new(new MssqlDialectRules());

    [TestCase("int")]
    [TestCase("smallint")]
    [TestCase("tinyint")]
    public void It_accepts_each_supported_integer_table_type(string columnType)
    {
        _dialect
            .CreateUserDefinedTableTypeIfNotExists(new("dms"), "Ids", "Id", columnType)
            .ReplaceLineEndings("\n")
            .Should()
            .EndWith($"CREATE TYPE [dms].[Ids] AS TABLE(\n    [Id] {columnType} NOT NULL\n);");
    }

    [TestCase(true, null, "[Value] bigint NULL")]
    [TestCase(false, null, "[Value] bigint NOT NULL")]
    [TestCase(true, "0", "[Value] bigint NULL DEFAULT 0")]
    [TestCase(false, "0", "[Value] bigint NOT NULL DEFAULT 0")]
    public void It_renders_nullable_columns_and_unnamed_defaults(
        bool nullable,
        string? defaultExpression,
        string expected
    )
    {
        _dialect
            .RenderColumnDefinitionWithNamedDefault(new("Value"), "bigint", nullable, null, defaultExpression)
            .Should()
            .Be(expected);
    }
}
