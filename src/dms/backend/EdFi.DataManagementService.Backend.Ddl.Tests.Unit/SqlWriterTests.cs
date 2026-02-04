// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_SqlWriter_With_Empty_Content
{
    private string _output = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var writer = new SqlWriter(dialect);
        _output = writer.ToString();
    }

    [Test]
    public void It_should_return_empty_string()
    {
        _output.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_SqlWriter_With_Single_Line
{
    private string _output = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var writer = new SqlWriter(dialect);
        writer.AppendLine("SELECT 1;");
        _output = writer.ToString();
    }

    [Test]
    public void It_should_end_with_newline()
    {
        _output.Should().Be("SELECT 1;\n");
    }
}

[TestFixture]
public class Given_SqlWriter_With_Single_Indent_Level
{
    private string _output = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var writer = new SqlWriter(dialect);
        writer.AppendLine("CREATE TABLE");
        using (writer.Indent())
        {
            writer.AppendLine("col1 int");
        }
        _output = writer.ToString();
    }

    [Test]
    public void It_should_apply_four_spaces_for_indent()
    {
        _output.Should().Contain("    col1 int");
    }

    [Test]
    public void It_should_not_indent_after_scope_disposal()
    {
        _output.Should().StartWith("CREATE TABLE");
    }
}

[TestFixture]
public class Given_SqlWriter_With_Nested_Indentation
{
    private string _output = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var writer = new SqlWriter(dialect);
        writer.AppendLine("CREATE TABLE test");
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine("col1 int,");
            using (writer.Indent())
            {
                writer.AppendLine("-- nested comment");
            }
            writer.AppendLine("col2 varchar(10)");
        }
        writer.AppendLine(");");
        _output = writer.ToString();
    }

    [Test]
    public void It_should_apply_eight_spaces_for_double_indent()
    {
        _output.Should().Contain("        -- nested comment");
    }

    [Test]
    public void It_should_return_to_single_indent_after_inner_scope()
    {
        _output.Should().Contain("    col2 varchar(10)");
    }

    [Test]
    public void It_should_have_no_indent_after_all_scopes_disposed()
    {
        _output.Should().EndWith(");\n");
        _output.Split('\n').Last(l => l.Length > 0).Should().Be(");");
    }
}

[TestFixture]
public class Given_SqlWriter_With_Windows_Line_Endings_In_Append
{
    private string _output = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var writer = new SqlWriter(dialect);
        writer.Append("line1\r\n");
        writer.Append("line2\r");
        writer.AppendLine("line3");
        _output = writer.ToString();
    }

    [Test]
    public void It_should_normalize_to_unix_line_endings()
    {
        _output.Should().NotContain("\r");
        _output.Should().Contain("\n");
    }
}

[TestFixture]
public class Given_SqlWriter_With_Trailing_Whitespace
{
    private string _output = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var writer = new SqlWriter(dialect);
        writer.AppendLine("SELECT 1   ");
        writer.AppendLine("FROM dual\t");
        _output = writer.ToString();
    }

    [Test]
    public void It_should_trim_trailing_spaces()
    {
        var lines = _output.Split('\n');
        lines[0].Should().Be("SELECT 1");
    }

    [Test]
    public void It_should_trim_trailing_tabs()
    {
        var lines = _output.Split('\n');
        lines[1].Should().Be("FROM dual");
    }
}

[TestFixture]
public class Given_SqlWriter_With_Append_Without_Line
{
    private string _output = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var writer = new SqlWriter(dialect);
        writer.Append("SELECT ");
        writer.Append("1");
        writer.AppendLine(";");
        _output = writer.ToString();
    }

    [Test]
    public void It_should_concatenate_appends_on_same_line()
    {
        _output.Should().Be("SELECT 1;\n");
    }
}

[TestFixture]
public class Given_SqlWriter_With_Quoted_Identifier
{
    private string _pgsqlOutput = default!;
    private string _mssqlOutput = default!;

    [SetUp]
    public void Setup()
    {
        var pgsqlDialect = new PgsqlDialect(new PgsqlDialectRules());
        var pgsqlWriter = new SqlWriter(pgsqlDialect);
        pgsqlWriter.AppendQuoted("MyTable");
        _pgsqlOutput = pgsqlWriter.ToString();

        var mssqlDialect = new MssqlDialect(new MssqlDialectRules());
        var mssqlWriter = new SqlWriter(mssqlDialect);
        mssqlWriter.AppendQuoted("MyTable");
        _mssqlOutput = mssqlWriter.ToString();
    }

    [Test]
    public void It_should_quote_with_double_quotes_for_pgsql()
    {
        _pgsqlOutput.Should().Be("\"MyTable\"");
    }

    [Test]
    public void It_should_quote_with_brackets_for_mssql()
    {
        _mssqlOutput.Should().Be("[MyTable]");
    }
}

[TestFixture]
public class Given_SqlWriter_With_Table_Name
{
    private string _pgsqlOutput = default!;
    private string _mssqlOutput = default!;

    [SetUp]
    public void Setup()
    {
        var table = new DbTableName(new DbSchemaName("edfi"), "School");

        var pgsqlDialect = new PgsqlDialect(new PgsqlDialectRules());
        var pgsqlWriter = new SqlWriter(pgsqlDialect);
        pgsqlWriter.AppendTable(table);
        _pgsqlOutput = pgsqlWriter.ToString();

        var mssqlDialect = new MssqlDialect(new MssqlDialectRules());
        var mssqlWriter = new SqlWriter(mssqlDialect);
        mssqlWriter.AppendTable(table);
        _mssqlOutput = mssqlWriter.ToString();
    }

    [Test]
    public void It_should_qualify_with_double_quotes_for_pgsql()
    {
        _pgsqlOutput.Should().Be("\"edfi\".\"School\"");
    }

    [Test]
    public void It_should_qualify_with_brackets_for_mssql()
    {
        _mssqlOutput.Should().Be("[edfi].[School]");
    }
}

[TestFixture]
public class Given_SqlWriter_With_Column_Type
{
    private string _pgsqlStringOutput = default!;
    private string _mssqlStringOutput = default!;
    private string _pgsqlDecimalOutput = default!;
    private string _mssqlDecimalOutput = default!;

    [SetUp]
    public void Setup()
    {
        var stringType = new RelationalScalarType(ScalarKind.String, MaxLength: 100);
        var decimalType = new RelationalScalarType(ScalarKind.Decimal, Decimal: (18, 4));

        var pgsqlDialect = new PgsqlDialect(new PgsqlDialectRules());
        var pgsqlWriter1 = new SqlWriter(pgsqlDialect);
        pgsqlWriter1.AppendColumnType(stringType);
        _pgsqlStringOutput = pgsqlWriter1.ToString();

        var pgsqlWriter2 = new SqlWriter(pgsqlDialect);
        pgsqlWriter2.AppendColumnType(decimalType);
        _pgsqlDecimalOutput = pgsqlWriter2.ToString();

        var mssqlDialect = new MssqlDialect(new MssqlDialectRules());
        var mssqlWriter1 = new SqlWriter(mssqlDialect);
        mssqlWriter1.AppendColumnType(stringType);
        _mssqlStringOutput = mssqlWriter1.ToString();

        var mssqlWriter2 = new SqlWriter(mssqlDialect);
        mssqlWriter2.AppendColumnType(decimalType);
        _mssqlDecimalOutput = mssqlWriter2.ToString();
    }

    [Test]
    public void It_should_render_varchar_for_pgsql_string()
    {
        _pgsqlStringOutput.Should().Be("varchar(100)");
    }

    [Test]
    public void It_should_render_nvarchar_for_mssql_string()
    {
        _mssqlStringOutput.Should().Be("nvarchar(100)");
    }

    [Test]
    public void It_should_render_numeric_for_pgsql_decimal()
    {
        _pgsqlDecimalOutput.Should().Be("numeric(18,4)");
    }

    [Test]
    public void It_should_render_decimal_for_mssql_decimal()
    {
        _mssqlDecimalOutput.Should().Be("decimal(18,4)");
    }
}

[TestFixture]
public class Given_SqlWriter_Determinism_With_Same_Input_Sequence
{
    private string _output1 = default!;
    private string _output2 = default!;

    [SetUp]
    public void Setup()
    {
        _output1 = GenerateSampleDdl();
        _output2 = GenerateSampleDdl();
    }

    private static string GenerateSampleDdl()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var writer = new SqlWriter(dialect);

        writer.AppendLine("CREATE TABLE IF NOT EXISTS \"edfi\".\"School\"");
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine("\"DocumentId\" bigint NOT NULL,");
            writer.AppendLine("\"SchoolId\" integer NOT NULL,");
            writer.AppendLine("\"NameOfInstitution\" varchar(75) NOT NULL,");
            writer.AppendLine("CONSTRAINT \"PK_School\" PRIMARY KEY (\"DocumentId\")");
        }
        writer.AppendLine(");");

        return writer.ToString();
    }

    [Test]
    public void It_should_produce_byte_for_byte_identical_output()
    {
        _output1.Should().Be(_output2);
    }
}

[TestFixture]
public class Given_SqlWriter_Clear_Method
{
    private SqlWriter _writer = default!;
    private int _lengthBeforeClear;
    private int _lengthAfterClear;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _writer = new SqlWriter(dialect);
        _writer.AppendLine("SELECT 1;");
        _lengthBeforeClear = _writer.Length;
        _writer.Clear();
        _lengthAfterClear = _writer.Length;
    }

    [Test]
    public void It_should_have_content_before_clear()
    {
        _lengthBeforeClear.Should().BeGreaterThan(0);
    }

    [Test]
    public void It_should_be_empty_after_clear()
    {
        _lengthAfterClear.Should().Be(0);
    }

    [Test]
    public void It_should_return_empty_string_after_clear()
    {
        _writer.ToString().Should().BeEmpty();
    }
}
