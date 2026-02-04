// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_PgsqlDialect_Construction_With_Wrong_Dialect_Rules
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        try
        {
            _ = new PgsqlDialect(new MssqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_throw_argument_exception()
    {
        _exception.Should().BeOfType<ArgumentException>();
    }

    [Test]
    public void It_should_indicate_expected_dialect()
    {
        _exception!.Message.Should().Contain("PostgreSQL");
    }
}

[TestFixture]
public class Given_PgsqlDialect_With_Simple_Identifier
{
    private string _quoted = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _quoted = dialect.QuoteIdentifier("School");
    }

    [Test]
    public void It_should_quote_with_double_quotes()
    {
        _quoted.Should().Be("\"School\"");
    }
}

[TestFixture]
public class Given_PgsqlDialect_With_Identifier_Containing_Double_Quote
{
    private string _quoted = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _quoted = dialect.QuoteIdentifier("My\"Table");
    }

    [Test]
    public void It_should_escape_embedded_double_quotes()
    {
        _quoted.Should().Be("\"My\"\"Table\"");
    }
}

[TestFixture]
public class Given_PgsqlDialect_With_Table_Name
{
    private string _qualified = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        _qualified = dialect.QualifyTable(table);
    }

    [Test]
    public void It_should_quote_both_schema_and_table()
    {
        _qualified.Should().Be("\"edfi\".\"School\"");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Document_Id_Type
{
    private string _type = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _type = dialect.DocumentIdColumnType;
    }

    [Test]
    public void It_should_be_bigint()
    {
        _type.Should().Be("bigint");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Ordinal_Type
{
    private string _type = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _type = dialect.OrdinalColumnType;
    }

    [Test]
    public void It_should_be_integer()
    {
        _type.Should().Be("integer");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_String_Type_With_Length
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.String, MaxLength: 255);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_varchar_with_length()
    {
        _rendered.Should().Be("varchar(255)");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_String_Type_Without_Length
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.String);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_varchar_without_length()
    {
        _rendered.Should().Be("varchar");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Int32_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Int32);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_integer()
    {
        _rendered.Should().Be("integer");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Int64_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Int64);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_bigint()
    {
        _rendered.Should().Be("bigint");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Decimal_Type_With_Precision
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Decimal, Decimal: (18, 4));
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_numeric_with_precision_and_scale()
    {
        _rendered.Should().Be("numeric(18,4)");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Decimal_Type_Without_Precision
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Decimal);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_numeric_without_precision()
    {
        _rendered.Should().Be("numeric");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Boolean_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Boolean);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_boolean()
    {
        _rendered.Should().Be("boolean");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Date_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Date);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_date()
    {
        _rendered.Should().Be("date");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_DateTime_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.DateTime);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_timestamp_with_time_zone()
    {
        _rendered.Should().Be("timestamp with time zone");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Time_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Time);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_time()
    {
        _rendered.Should().Be("time");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Create_Schema_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _ddl = dialect.CreateSchemaIfNotExists(new DbSchemaName("edfi"));
    }

    [Test]
    public void It_should_use_if_not_exists_syntax()
    {
        _ddl.Should().Be("CREATE SCHEMA IF NOT EXISTS \"edfi\";");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Create_Table_Header
{
    private string _header = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        _header = dialect.CreateTableHeader(table);
    }

    [Test]
    public void It_should_use_if_not_exists_syntax()
    {
        _header.Should().Be("CREATE TABLE IF NOT EXISTS \"edfi\".\"School\"");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Drop_Trigger_If_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        _ddl = dialect.DropTriggerIfExists(table, "TR_School_UpdateTimestamp");
    }

    [Test]
    public void It_should_use_drop_trigger_if_exists_on_table()
    {
        _ddl.Should().Be("DROP TRIGGER IF EXISTS \"TR_School_UpdateTimestamp\" ON \"edfi\".\"School\";");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Ddl_Patterns
{
    private PgsqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new PgsqlDialect(new PgsqlDialectRules());
    }

    [Test]
    public void It_should_use_drop_then_create_for_triggers()
    {
        _dialect.TriggerCreationPattern.Should().Be(DdlPattern.DropThenCreate);
    }

    [Test]
    public void It_should_use_create_or_replace_for_functions()
    {
        _dialect.FunctionCreationPattern.Should().Be(DdlPattern.CreateOrReplace);
    }

    [Test]
    public void It_should_use_create_or_replace_for_views()
    {
        _dialect.ViewCreationPattern.Should().Be(DdlPattern.CreateOrReplace);
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rules_Access
{
    private PgsqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new PgsqlDialect(new PgsqlDialectRules());
    }

    [Test]
    public void It_should_expose_underlying_rules()
    {
        _dialect.Rules.Should().NotBeNull();
        _dialect.Rules.Dialect.Should().Be(SqlDialect.Pgsql);
    }

    [Test]
    public void It_should_have_correct_max_identifier_length()
    {
        _dialect.Rules.MaxIdentifierLength.Should().Be(63);
    }
}
