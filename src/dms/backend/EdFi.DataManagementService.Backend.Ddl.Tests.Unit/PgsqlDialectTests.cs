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

[TestFixture]
public class Given_PgsqlDialect_Create_Sequence_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _ddl = dialect.CreateSequenceIfNotExists(new DbSchemaName("dms"), "ChangeVersionSequence", 1);
    }

    [Test]
    public void It_should_use_if_not_exists_syntax()
    {
        _ddl.Should().Be("CREATE SEQUENCE IF NOT EXISTS \"dms\".\"ChangeVersionSequence\" START WITH 1;");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Create_Sequence_With_Custom_Start
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _ddl = dialect.CreateSequenceIfNotExists(new DbSchemaName("dms"), "MySequence", 1000);
    }

    [Test]
    public void It_should_include_custom_start_value()
    {
        _ddl.Should().Contain("START WITH 1000");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Create_Index_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        var columns = new[] { new DbColumnName("SchoolId"), new DbColumnName("LocalEducationAgencyId") };
        _ddl = dialect.CreateIndexIfNotExists(table, "IX_School_LEA", columns);
    }

    [Test]
    public void It_should_use_if_not_exists_syntax()
    {
        _ddl.Should().Contain("IF NOT EXISTS");
    }

    [Test]
    public void It_should_qualify_index_name_with_schema()
    {
        _ddl.Should().Contain("\"edfi\".\"IX_School_LEA\"");
    }

    [Test]
    public void It_should_include_all_columns()
    {
        _ddl.Should().Contain("(\"SchoolId\", \"LocalEducationAgencyId\")");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Create_Unique_Index_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        var columns = new[] { new DbColumnName("SchoolId") };
        _ddl = dialect.CreateIndexIfNotExists(table, "UX_School_SchoolId", columns, isUnique: true);
    }

    [Test]
    public void It_should_include_unique_keyword()
    {
        _ddl.Should().Contain("CREATE UNIQUE INDEX");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Add_Foreign_Key_Constraint
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation");
        var targetTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var columns = new[] { new DbColumnName("School_DocumentId"), new DbColumnName("School_SchoolId") };
        var targetColumns = new[] { new DbColumnName("DocumentId"), new DbColumnName("SchoolId") };
        _ddl = dialect.AddForeignKeyConstraint(
            table,
            "FK_StudentSchoolAssociation_School",
            columns,
            targetTable,
            targetColumns,
            ReferentialAction.NoAction,
            ReferentialAction.Cascade
        );
    }

    [Test]
    public void It_should_use_do_block_for_idempotency()
    {
        _ddl.Should().Contain("DO $$");
    }

    [Test]
    public void It_should_check_constraint_existence()
    {
        _ddl.Should().Contain("IF NOT EXISTS");
        _ddl.Should().Contain("pg_constraint");
    }

    [Test]
    public void It_should_include_foreign_key_clause()
    {
        _ddl.Should().Contain("FOREIGN KEY");
    }

    [Test]
    public void It_should_include_references_clause()
    {
        _ddl.Should().Contain("REFERENCES \"edfi\".\"School\"");
    }

    [Test]
    public void It_should_include_on_delete_action()
    {
        _ddl.Should().Contain("ON DELETE NO ACTION");
    }

    [Test]
    public void It_should_include_on_update_action()
    {
        _ddl.Should().Contain("ON UPDATE CASCADE");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Add_Unique_Constraint
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        var columns = new[] { new DbColumnName("DocumentId"), new DbColumnName("SchoolId") };
        _ddl = dialect.AddUniqueConstraint(table, "UQ_School_Identity", columns);
    }

    [Test]
    public void It_should_use_do_block_for_idempotency()
    {
        _ddl.Should().Contain("DO $$");
    }

    [Test]
    public void It_should_check_constraint_existence()
    {
        _ddl.Should().Contain("IF NOT EXISTS");
        _ddl.Should().Contain("pg_constraint");
    }

    [Test]
    public void It_should_include_unique_keyword()
    {
        _ddl.Should().Contain("UNIQUE");
    }

    [Test]
    public void It_should_include_all_columns()
    {
        _ddl.Should().Contain("(\"DocumentId\", \"SchoolId\")");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Add_Check_Constraint
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation");
        _ddl = dialect.AddCheckConstraint(
            table,
            "CK_StudentSchoolAssociation_SchoolRef",
            "(School_DocumentId IS NULL) = (School_SchoolId IS NULL)"
        );
    }

    [Test]
    public void It_should_use_do_block_for_idempotency()
    {
        _ddl.Should().Contain("DO $$");
    }

    [Test]
    public void It_should_include_check_keyword()
    {
        _ddl.Should().Contain("CHECK");
    }

    [Test]
    public void It_should_include_check_expression()
    {
        _ddl.Should().Contain("(School_DocumentId IS NULL) = (School_SchoolId IS NULL)");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Render_Column_Definition
{
    private string _definition = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _definition = dialect.RenderColumnDefinition(
            new DbColumnName("SchoolId"),
            "bigint",
            isNullable: false
        );
    }

    [Test]
    public void It_should_quote_column_name()
    {
        _definition.Should().StartWith("\"SchoolId\"");
    }

    [Test]
    public void It_should_include_type()
    {
        _definition.Should().Contain("bigint");
    }

    [Test]
    public void It_should_include_not_null()
    {
        _definition.Should().Contain("NOT NULL");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Render_Nullable_Column_Definition
{
    private string _definition = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _definition = dialect.RenderColumnDefinition(
            new DbColumnName("MiddleName"),
            "varchar(75)",
            isNullable: true
        );
    }

    [Test]
    public void It_should_include_null()
    {
        _definition.Should().Contain("NULL");
        _definition.Should().NotContain("NOT NULL");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Render_Column_Definition_With_Default
{
    private string _definition = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _definition = dialect.RenderColumnDefinition(
            new DbColumnName("CreatedAt"),
            "timestamp with time zone",
            isNullable: false,
            defaultExpression: "CURRENT_TIMESTAMP"
        );
    }

    [Test]
    public void It_should_include_default_clause()
    {
        _definition.Should().Contain("DEFAULT CURRENT_TIMESTAMP");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Render_Primary_Key_Clause
{
    private string _clause = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        var columns = new[] { new DbColumnName("DocumentId"), new DbColumnName("Ordinal") };
        _clause = dialect.RenderPrimaryKeyClause(columns);
    }

    [Test]
    public void It_should_start_with_primary_key()
    {
        _clause.Should().StartWith("PRIMARY KEY");
    }

    [Test]
    public void It_should_include_all_columns_quoted()
    {
        _clause.Should().Contain("\"DocumentId\", \"Ordinal\"");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Render_Referential_Actions
{
    private PgsqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new PgsqlDialect(new PgsqlDialectRules());
    }

    [Test]
    public void It_should_render_no_action()
    {
        _dialect.RenderReferentialAction(ReferentialAction.NoAction).Should().Be("NO ACTION");
    }

    [Test]
    public void It_should_render_cascade()
    {
        _dialect.RenderReferentialAction(ReferentialAction.Cascade).Should().Be("CASCADE");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Create_Extension_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _ddl = dialect.CreateExtensionIfNotExists("pgcrypto");
    }

    [Test]
    public void It_should_use_create_extension_if_not_exists_syntax()
    {
        _ddl.Should().Be("CREATE EXTENSION IF NOT EXISTS \"pgcrypto\";");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Create_Extension_With_Null_Name
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        try
        {
            var dialect = new PgsqlDialect(new PgsqlDialectRules());
            dialect.CreateExtensionIfNotExists(null!);
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_throw_argument_null_exception()
    {
        _exception.Should().BeOfType<ArgumentNullException>();
    }
}

[TestFixture]
public class Given_PgsqlDialect_Create_Uuidv5_Function
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new PgsqlDialect(new PgsqlDialectRules());
        _ddl = dialect.CreateUuidv5Function(new DbSchemaName("dms"));
    }

    [Test]
    public void It_should_use_create_or_replace_pattern()
    {
        _ddl.Should().Contain("CREATE OR REPLACE FUNCTION");
    }

    [Test]
    public void It_should_qualify_function_name_with_schema()
    {
        _ddl.Should().Contain("\"dms\".\"uuidv5\"");
    }

    [Test]
    public void It_should_accept_uuid_and_text_parameters()
    {
        _ddl.Should().Contain("namespace_uuid uuid").And.Contain("name_text text");
    }

    [Test]
    public void It_should_return_uuid()
    {
        _ddl.Should().Contain("RETURNS uuid");
    }

    [Test]
    public void It_should_use_plpgsql_language()
    {
        _ddl.Should().Contain("LANGUAGE plpgsql");
    }

    [Test]
    public void It_should_use_sha1_digest()
    {
        _ddl.Should().Contain("digest(").And.Contain("'sha1'");
    }

    [Test]
    public void It_should_convert_namespace_to_bytes_via_hex_decode()
    {
        _ddl.Should().Contain("decode(replace(namespace_uuid::text, '-', ''), 'hex')");
    }

    [Test]
    public void It_should_convert_name_to_utf8()
    {
        _ddl.Should().Contain("convert_to(name_text, 'UTF8')");
    }

    [Test]
    public void It_should_set_version_5_on_byte_6()
    {
        _ddl.Should().Contain("set_byte(hash, 6,").And.Contain("x'50'");
    }

    [Test]
    public void It_should_set_variant_rfc4122_on_byte_8()
    {
        _ddl.Should().Contain("set_byte(hash, 8,").And.Contain("x'80'");
    }

    [Test]
    public void It_should_be_marked_immutable()
    {
        _ddl.Should().Contain("IMMUTABLE");
    }

    [Test]
    public void It_should_be_marked_strict()
    {
        _ddl.Should().Contain("STRICT");
    }

    [Test]
    public void It_should_be_marked_parallel_safe()
    {
        _ddl.Should().Contain("PARALLEL SAFE");
    }

    [Test]
    public void It_should_extract_first_16_bytes_of_hash()
    {
        _ddl.Should().Contain("substring(hash from 1 for 16)");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Literal rendering tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_PgsqlDialect_Rendering_Binary_Literal
{
    private PgsqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new PgsqlDialect(new PgsqlDialectRules());
    }

    [Test]
    public void It_should_render_bytea_hex_literal()
    {
        var result = _dialect.RenderBinaryLiteral([0xAB, 0xCD, 0xEF]);
        result.Should().Be("'\\xABCDEF'::bytea");
    }

    [Test]
    public void It_should_render_empty_byte_array()
    {
        var result = _dialect.RenderBinaryLiteral([]);
        result.Should().Be("'\\x'::bytea");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Boolean_Literal
{
    private PgsqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new PgsqlDialect(new PgsqlDialectRules());
    }

    [Test]
    public void It_should_render_true()
    {
        _dialect.RenderBooleanLiteral(true).Should().Be("true");
    }

    [Test]
    public void It_should_render_false()
    {
        _dialect.RenderBooleanLiteral(false).Should().Be("false");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_String_Literal
{
    private PgsqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new PgsqlDialect(new PgsqlDialectRules());
    }

    [Test]
    public void It_should_render_simple_string()
    {
        _dialect.RenderStringLiteral("hello").Should().Be("'hello'");
    }

    [Test]
    public void It_should_escape_single_quotes()
    {
        _dialect.RenderStringLiteral("it's").Should().Be("'it''s'");
    }

    [Test]
    public void It_should_not_use_nvarchar_prefix()
    {
        _dialect.RenderStringLiteral("test").Should().NotStartWith("N");
    }
}

[TestFixture]
public class Given_PgsqlDialect_Rendering_Numeric_Literals
{
    private PgsqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new PgsqlDialect(new PgsqlDialectRules());
    }

    [Test]
    public void It_should_render_smallint()
    {
        _dialect.RenderSmallintLiteral(42).Should().Be("42");
    }

    [Test]
    public void It_should_render_integer()
    {
        _dialect.RenderIntegerLiteral(12345).Should().Be("12345");
    }

    [Test]
    public void It_should_render_zero()
    {
        _dialect.RenderIntegerLiteral(0).Should().Be("0");
    }
}
