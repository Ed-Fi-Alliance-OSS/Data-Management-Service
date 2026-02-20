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
