// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_MssqlDialect_Construction_With_Wrong_Dialect_Rules
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        try
        {
            _ = new MssqlDialect(new PgsqlDialectRules());
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
        _exception!.Message.Should().Contain("SQL Server");
    }
}

[TestFixture]
public class Given_MssqlDialect_With_Simple_Identifier
{
    private string _quoted = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _quoted = dialect.QuoteIdentifier("School");
    }

    [Test]
    public void It_should_quote_with_brackets()
    {
        _quoted.Should().Be("[School]");
    }
}

[TestFixture]
public class Given_MssqlDialect_With_Identifier_Containing_Right_Bracket
{
    private string _quoted = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _quoted = dialect.QuoteIdentifier("My]Table");
    }

    [Test]
    public void It_should_escape_embedded_right_brackets()
    {
        _quoted.Should().Be("[My]]Table]");
    }
}

[TestFixture]
public class Given_MssqlDialect_With_Table_Name
{
    private string _qualified = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        _qualified = dialect.QualifyTable(table);
    }

    [Test]
    public void It_should_quote_both_schema_and_table()
    {
        _qualified.Should().Be("[edfi].[School]");
    }
}

[TestFixture]
public class Given_MssqlDialect_Document_Id_Type
{
    private string _type = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _type = dialect.DocumentIdColumnType;
    }

    [Test]
    public void It_should_be_bigint()
    {
        _type.Should().Be("bigint");
    }
}

[TestFixture]
public class Given_MssqlDialect_Ordinal_Type
{
    private string _type = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _type = dialect.OrdinalColumnType;
    }

    [Test]
    public void It_should_be_int()
    {
        _type.Should().Be("int");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_String_Type_With_Length
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.String, MaxLength: 255);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_nvarchar_with_length()
    {
        _rendered.Should().Be("nvarchar(255)");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_String_Type_Without_Length
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.String);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_nvarchar_without_length()
    {
        _rendered.Should().Be("nvarchar");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_Int32_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Int32);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_int()
    {
        _rendered.Should().Be("int");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_Int64_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
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
public class Given_MssqlDialect_Rendering_Decimal_Type_With_Precision
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Decimal, Decimal: (18, 4));
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_decimal_with_precision_and_scale()
    {
        _rendered.Should().Be("decimal(18,4)");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_Decimal_Type_Without_Precision
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Decimal);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_decimal_without_precision()
    {
        _rendered.Should().Be("decimal");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_Boolean_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Boolean);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_bit()
    {
        _rendered.Should().Be("bit");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_Date_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
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
public class Given_MssqlDialect_Rendering_DateTime_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.DateTime);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_datetime2_with_precision()
    {
        _rendered.Should().Be("datetime2(7)");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_Time_Type
{
    private string _rendered = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var scalarType = new RelationalScalarType(ScalarKind.Time);
        _rendered = dialect.RenderColumnType(scalarType);
    }

    [Test]
    public void It_should_render_time_with_precision()
    {
        _rendered.Should().Be("time(7)");
    }
}

[TestFixture]
public class Given_MssqlDialect_Create_Schema_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _ddl = dialect.CreateSchemaIfNotExists(new DbSchemaName("edfi"));
    }

    [Test]
    public void It_should_use_sys_schemas_check()
    {
        _ddl.Should().Contain("sys.schemas");
    }

    [Test]
    public void It_should_use_exec_for_create()
    {
        _ddl.Should().Contain("EXEC(");
    }

    [Test]
    public void It_should_include_schema_name_in_literal()
    {
        _ddl.Should().Contain("N'edfi'");
    }
}

[TestFixture]
public class Given_MssqlDialect_Create_Schema_With_Quote_In_Name
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _ddl = dialect.CreateSchemaIfNotExists(new DbSchemaName("test'schema"));
    }

    [Test]
    public void It_should_escape_single_quotes_in_literal()
    {
        _ddl.Should().Contain("N'test''schema'");
    }
}

[TestFixture]
public class Given_MssqlDialect_Create_Table_Header
{
    private string _header = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        _header = dialect.CreateTableHeader(table);
    }

    [Test]
    public void It_should_use_object_id_check()
    {
        _header.Should().Contain("OBJECT_ID");
    }

    [Test]
    public void It_should_check_for_user_table()
    {
        _header.Should().Contain("N'U'");
    }

    [Test]
    public void It_should_include_create_table()
    {
        _header.Should().Contain("CREATE TABLE [edfi].[School]");
    }
}

[TestFixture]
public class Given_MssqlDialect_Drop_Trigger_If_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        _ddl = dialect.DropTriggerIfExists(table, "TR_School_UpdateTimestamp");
    }

    [Test]
    public void It_should_use_drop_trigger_if_exists_with_schema()
    {
        _ddl.Should().Be("DROP TRIGGER IF EXISTS [edfi].[TR_School_UpdateTimestamp];");
    }
}

[TestFixture]
public class Given_MssqlDialect_Ddl_Patterns
{
    private MssqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new MssqlDialect(new MssqlDialectRules());
    }

    [Test]
    public void It_should_use_create_or_alter_for_triggers()
    {
        _dialect.TriggerCreationPattern.Should().Be(DdlPattern.CreateOrAlter);
    }

    [Test]
    public void It_should_use_create_or_alter_for_functions()
    {
        _dialect.FunctionCreationPattern.Should().Be(DdlPattern.CreateOrAlter);
    }

    [Test]
    public void It_should_use_create_or_alter_for_views()
    {
        _dialect.ViewCreationPattern.Should().Be(DdlPattern.CreateOrAlter);
    }
}

[TestFixture]
public class Given_MssqlDialect_Rules_Access
{
    private MssqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new MssqlDialect(new MssqlDialectRules());
    }

    [Test]
    public void It_should_expose_underlying_rules()
    {
        _dialect.Rules.Should().NotBeNull();
        _dialect.Rules.Dialect.Should().Be(SqlDialect.Mssql);
    }

    [Test]
    public void It_should_have_correct_max_identifier_length()
    {
        _dialect.Rules.MaxIdentifierLength.Should().Be(128);
    }
}
