// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal static class ManifestTestData
{
    internal static EffectiveSchemaInfo BuildEffectiveSchema() =>
        new(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "abc123def456",
            ResourceKeyCount: 2,
            ResourceKeySeedHash:
            [
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
                0xAB,
                0xCD,
                0xEF,
                0x01,
                0x23,
                0x45,
                0x67,
                0x89,
            ],
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.1.0", false, "hash1"),
            ],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "5.1.0", false),
                new ResourceKeyEntry(2, new QualifiedResourceName("Ed-Fi", "Student"), "5.1.0", false),
            ]
        );
}

[TestFixture]
public class Given_DdlManifestEmitter_Emitting_Twice_With_Same_Inputs
{
    private string _first = default!;
    private string _second = default!;

    [SetUp]
    public void Setup()
    {
        var schema = ManifestTestData.BuildEffectiveSchema();
        var entries = new List<DdlManifestEntry>
        {
            new(SqlDialect.Pgsql, "CREATE TABLE foo (id INT);\n"),
            new(SqlDialect.Mssql, "CREATE TABLE bar (id INT);\n"),
        };

        _first = DdlManifestEmitter.Emit(schema, entries);
        _second = DdlManifestEmitter.Emit(schema, entries);
    }

    [Test]
    public void It_should_produce_byte_for_byte_identical_output()
    {
        _first.Should().Be(_second);
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_With_Reversed_Dialect_Order
{
    private string _mssqlFirst = default!;
    private string _pgsqlFirst = default!;

    [SetUp]
    public void Setup()
    {
        var schema = ManifestTestData.BuildEffectiveSchema();

        var mssqlFirstEntries = new List<DdlManifestEntry>
        {
            new(SqlDialect.Mssql, "CREATE TABLE bar (id INT);\n"),
            new(SqlDialect.Pgsql, "CREATE TABLE foo (id INT);\n"),
        };

        var pgsqlFirstEntries = new List<DdlManifestEntry>
        {
            new(SqlDialect.Pgsql, "CREATE TABLE foo (id INT);\n"),
            new(SqlDialect.Mssql, "CREATE TABLE bar (id INT);\n"),
        };

        _mssqlFirst = DdlManifestEmitter.Emit(schema, mssqlFirstEntries);
        _pgsqlFirst = DdlManifestEmitter.Emit(schema, pgsqlFirstEntries);
    }

    [Test]
    public void It_should_produce_identical_output_regardless_of_input_order()
    {
        _mssqlFirst.Should().Be(_pgsqlFirst);
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_ComputeSha256_With_Known_Input
{
    [Test]
    public void It_should_return_correct_lowercase_hex_hash()
    {
        var hash = DdlManifestEmitter.ComputeSha256("CREATE TABLE foo (id INT);\n");

        hash.Should().Be("ef05371c08d966f2229348542ec66ce0eec906e411cdb9b7f4c3cd4c69bd82eb");
    }

    [Test]
    public void It_should_return_64_character_string()
    {
        var hash = DdlManifestEmitter.ComputeSha256("any text");

        hash.Should().HaveLength(64);
    }

    [Test]
    public void It_should_return_lowercase_hex_only()
    {
        var hash = DdlManifestEmitter.ComputeSha256("any text");

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_CountStatements_For_Pgsql
{
    [Test]
    public void It_should_count_simple_semicolon_terminated_statements()
    {
        var sql = "CREATE TABLE foo (id INT);\nCREATE TABLE bar (id INT);\n";

        DdlManifestEmitter.CountStatements(SqlDialect.Pgsql, sql).Should().Be(2);
    }

    [Test]
    public void It_should_not_count_empty_lines()
    {
        var sql = "CREATE TABLE foo (id INT);\n\nCREATE TABLE bar (id INT);\n";

        DdlManifestEmitter.CountStatements(SqlDialect.Pgsql, sql).Should().Be(2);
    }

    [Test]
    public void It_should_count_dollar_quoted_block_as_one_statement()
    {
        var sql = string.Join(
            "\n",
            "CREATE TABLE foo (id INT);",
            "CREATE OR REPLACE FUNCTION my_func() RETURNS TRIGGER AS $$",
            "BEGIN",
            "    INSERT INTO bar VALUES (1);",
            "    RETURN NEW;",
            "END $$;",
            ""
        );

        DdlManifestEmitter.CountStatements(SqlDialect.Pgsql, sql).Should().Be(2);
    }

    [Test]
    public void It_should_count_dollar_quoted_block_closed_with_language_clause_as_one_statement()
    {
        var sql = string.Join(
            "\n",
            "CREATE TABLE foo (id INT);",
            "CREATE OR REPLACE FUNCTION my_func() RETURNS TRIGGER AS $$",
            "BEGIN",
            "    INSERT INTO bar VALUES (1);",
            "    RETURN NEW;",
            "$$ LANGUAGE plpgsql;",
            ""
        );

        DdlManifestEmitter.CountStatements(SqlDialect.Pgsql, sql).Should().Be(2);
    }

    [Test]
    public void It_should_ignore_dollar_sign_inside_string_literals()
    {
        var sql = string.Join(
            "\n",
            "INSERT INTO foo VALUES ('$$.schoolId=');",
            "INSERT INTO bar VALUES (1);",
            ""
        );

        DdlManifestEmitter.CountStatements(SqlDialect.Pgsql, sql).Should().Be(2);
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_CountStatements_For_Mssql
{
    [Test]
    public void It_should_count_simple_semicolon_terminated_statements()
    {
        var sql = "CREATE TABLE foo (id INT);\nCREATE TABLE bar (id INT);\n";

        DdlManifestEmitter.CountStatements(SqlDialect.Mssql, sql).Should().Be(2);
    }

    [Test]
    public void It_should_count_go_delimited_batches_as_one_statement_each()
    {
        var sql = string.Join(
            "\n",
            "CREATE TABLE foo (id INT);",
            "CREATE TABLE bar (id INT);",
            "GO",
            "CREATE TRIGGER my_trigger ON foo",
            "AFTER INSERT AS",
            "BEGIN",
            "    INSERT INTO bar VALUES (1);",
            "END;",
            "GO",
            ""
        );

        // 2 semicolons before first GO + 1 batch after first GO = 3
        DdlManifestEmitter.CountStatements(SqlDialect.Mssql, sql).Should().Be(3);
    }

    [Test]
    public void It_should_not_count_empty_go_batches()
    {
        var sql = string.Join(
            "\n",
            "CREATE TABLE foo (id INT);",
            "GO",
            "GO",
            "CREATE TRIGGER my_trigger ON foo",
            "AFTER INSERT AS",
            "BEGIN",
            "    RETURN;",
            "END;",
            "GO",
            ""
        );

        // 1 semicolon before first GO + 0 (empty batch) + 1 trigger batch = 2
        DdlManifestEmitter.CountStatements(SqlDialect.Mssql, sql).Should().Be(2);
    }

    [Test]
    public void It_should_count_trailing_batch_after_last_go_without_trailing_go_separator()
    {
        var sql = string.Join(
            "\n",
            "CREATE TABLE foo (id INT);",
            "GO",
            "CREATE TRIGGER my_trigger ON foo",
            "AFTER INSERT AS",
            "BEGIN",
            "    RETURN;",
            "END;"
        );

        // 1 semicolon before first GO + 1 trailing batch (no closing GO) = 2
        DdlManifestEmitter.CountStatements(SqlDialect.Mssql, sql).Should().Be(2);
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_CountStatements_With_Empty_Input
{
    [Test]
    public void It_should_return_zero_for_empty_pgsql_input()
    {
        DdlManifestEmitter.CountStatements(SqlDialect.Pgsql, "").Should().Be(0);
    }

    [Test]
    public void It_should_return_zero_for_empty_mssql_input()
    {
        DdlManifestEmitter.CountStatements(SqlDialect.Mssql, "").Should().Be(0);
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_Emit_With_Empty_Entries
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var schema = ManifestTestData.BuildEffectiveSchema();
        _manifest = DdlManifestEmitter.Emit(schema, []);
    }

    [Test]
    public void It_should_produce_valid_json_with_empty_ddl_array()
    {
        var doc = JsonDocument.Parse(_manifest);
        doc.RootElement.GetProperty("ddl").GetArrayLength().Should().Be(0);
    }

    [Test]
    public void It_should_still_include_schema_metadata()
    {
        var doc = JsonDocument.Parse(_manifest);
        doc.RootElement.GetProperty("effective_schema_hash").GetString().Should().Be("abc123def456");
        doc.RootElement.GetProperty("relational_mapping_version").GetString().Should().Be("1.0.0");
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_Emit_With_Single_Dialect
{
    private string _manifest = default!;
    private JsonDocument _doc = default!;

    [SetUp]
    public void Setup()
    {
        var schema = ManifestTestData.BuildEffectiveSchema();
        var entries = new List<DdlManifestEntry> { new(SqlDialect.Pgsql, "CREATE TABLE foo (id INT);\n") };

        _manifest = DdlManifestEmitter.Emit(schema, entries);
        _doc = JsonDocument.Parse(_manifest);
    }

    [Test]
    public void It_should_produce_valid_json()
    {
        var act = () => JsonDocument.Parse(_manifest);

        act.Should().NotThrow();
    }

    [Test]
    public void It_should_have_ddl_array_with_one_element()
    {
        _doc.RootElement.GetProperty("ddl").GetArrayLength().Should().Be(1);
    }

    [Test]
    public void It_should_have_correct_dialect()
    {
        _doc.RootElement.GetProperty("ddl")[0].GetProperty("dialect").GetString().Should().Be("pgsql");
    }

    [Test]
    public void It_should_include_statement_count()
    {
        _doc.RootElement.GetProperty("ddl")[0].GetProperty("statement_count").GetInt32().Should().Be(1);
    }

    [Test]
    public void It_should_include_normalized_sql_sha256()
    {
        _doc.RootElement.GetProperty("ddl")[0]
            .GetProperty("normalized_sql_sha256")
            .GetString()
            .Should()
            .MatchRegex("^[0-9a-f]{64}$");
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_Emit_Json_Format
{
    private string _manifest = default!;

    [SetUp]
    public void Setup()
    {
        var schema = ManifestTestData.BuildEffectiveSchema();
        var entries = new List<DdlManifestEntry>
        {
            new(SqlDialect.Pgsql, "CREATE TABLE foo (id INT);\n"),
            new(SqlDialect.Mssql, "CREATE TABLE bar (id INT);\n"),
        };

        _manifest = DdlManifestEmitter.Emit(schema, entries);
    }

    [Test]
    public void It_should_use_unix_line_endings_only()
    {
        _manifest.Should().NotContain("\r");
    }

    [Test]
    public void It_should_end_with_trailing_newline()
    {
        _manifest.Should().EndWith("\n");
    }

    [Test]
    public void It_should_be_well_formed_json()
    {
        var act = () => JsonDocument.Parse(_manifest);

        act.Should().NotThrow();
    }

    [Test]
    public void It_should_be_indented()
    {
        _manifest.Should().Contain("  ");
    }
}

[TestFixture]
public class Given_DdlManifestEmitter_Emit_Manifest_Schema
{
    private JsonDocument _doc = default!;

    [SetUp]
    public void Setup()
    {
        var schema = ManifestTestData.BuildEffectiveSchema();
        var entries = new List<DdlManifestEntry>
        {
            new(SqlDialect.Pgsql, "CREATE TABLE foo (id INT);\n"),
            new(SqlDialect.Mssql, "CREATE TABLE bar (id INT);\nINSERT INTO bar VALUES (1);\n"),
        };

        var manifest = DdlManifestEmitter.Emit(schema, entries);
        _doc = JsonDocument.Parse(manifest);
    }

    [Test]
    public void It_should_include_effective_schema_hash()
    {
        _doc.RootElement.GetProperty("effective_schema_hash").GetString().Should().Be("abc123def456");
    }

    [Test]
    public void It_should_include_relational_mapping_version()
    {
        _doc.RootElement.GetProperty("relational_mapping_version").GetString().Should().Be("1.0.0");
    }

    [Test]
    public void It_should_have_ddl_array_ordered_by_dialect_name()
    {
        var ddl = _doc.RootElement.GetProperty("ddl");
        ddl.GetArrayLength().Should().Be(2);
        ddl[0].GetProperty("dialect").GetString().Should().Be("mssql");
        ddl[1].GetProperty("dialect").GetString().Should().Be("pgsql");
    }

    [Test]
    public void It_should_include_normalized_sql_sha256_per_dialect()
    {
        var ddl = _doc.RootElement.GetProperty("ddl");
        ddl[0].GetProperty("normalized_sql_sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        ddl[1].GetProperty("normalized_sql_sha256").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Test]
    public void It_should_include_correct_statement_counts()
    {
        var ddl = _doc.RootElement.GetProperty("ddl");

        // mssql: "CREATE TABLE bar (id INT);\nINSERT INTO bar VALUES (1);\n" = 2 statements
        ddl[0].GetProperty("statement_count").GetInt32().Should().Be(2);

        // pgsql: "CREATE TABLE foo (id INT);\n" = 1 statement
        ddl[1].GetProperty("statement_count").GetInt32().Should().Be(1);
    }

    [Test]
    public void It_should_include_correct_sha256_for_known_sql()
    {
        var ddl = _doc.RootElement.GetProperty("ddl");

        // mssql SQL: "CREATE TABLE bar (id INT);\nINSERT INTO bar VALUES (1);\n"
        ddl[0]
            .GetProperty("normalized_sql_sha256")
            .GetString()
            .Should()
            .Be("0e91981e3a5b85b420c798392ef44c8798f73b23ba6b1c1e38a72668b3abf174");

        // pgsql SQL: "CREATE TABLE foo (id INT);\n"
        ddl[1]
            .GetProperty("normalized_sql_sha256")
            .GetString()
            .Should()
            .Be("ef05371c08d966f2229348542ec66ce0eec906e411cdb9b7f4c3cd4c69bd82eb");
    }
}
