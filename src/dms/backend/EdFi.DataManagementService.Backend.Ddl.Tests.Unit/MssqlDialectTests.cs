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
public class Given_MssqlDialect_Rendering_Unbounded_String_Type
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
    public void It_should_render_nvarchar_max()
    {
        _rendered.Should().Be("nvarchar(max)");
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

[TestFixture]
public class Given_MssqlDialect_Create_Sequence_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _ddl = dialect.CreateSequenceIfNotExists(new DbSchemaName("dms"), "ChangeVersionSequence", 1);
    }

    [Test]
    public void It_should_use_sys_sequences_check()
    {
        _ddl.Should().Contain("sys.sequences");
    }

    [Test]
    public void It_should_check_schema_and_sequence_name()
    {
        _ddl.Should().Contain("N'dms'");
        _ddl.Should().Contain("N'ChangeVersionSequence'");
    }

    [Test]
    public void It_should_include_create_sequence()
    {
        _ddl.Should().Contain("CREATE SEQUENCE [dms].[ChangeVersionSequence]");
    }

    [Test]
    public void It_should_include_start_with()
    {
        _ddl.Should().Contain("START WITH 1");
    }
}

[TestFixture]
public class Given_MssqlDialect_Create_Index_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        var columns = new[] { new DbColumnName("SchoolId"), new DbColumnName("LocalEducationAgencyId") };
        _ddl = dialect.CreateIndexIfNotExists(table, "IX_School_LEA", columns);
    }

    [Test]
    public void It_should_use_sys_indexes_check()
    {
        _ddl.Should().Contain("sys.indexes");
    }

    [Test]
    public void It_should_check_schema_table_and_index_name()
    {
        _ddl.Should().Contain("N'edfi'");
        _ddl.Should().Contain("N'School'");
        _ddl.Should().Contain("N'IX_School_LEA'");
    }

    [Test]
    public void It_should_include_create_index()
    {
        _ddl.Should().Contain("CREATE INDEX [IX_School_LEA]");
    }

    [Test]
    public void It_should_include_all_columns()
    {
        _ddl.Should().Contain("([SchoolId], [LocalEducationAgencyId])");
    }
}

[TestFixture]
public class Given_MssqlDialect_Create_Unique_Index_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
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
public class Given_MssqlDialect_Add_Foreign_Key_Constraint
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
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
    public void It_should_use_sys_foreign_keys_check()
    {
        _ddl.Should().Contain("sys.foreign_keys");
    }

    [Test]
    public void It_should_check_constraint_name()
    {
        _ddl.Should().Contain("N'FK_StudentSchoolAssociation_School'");
    }

    [Test]
    public void It_should_include_foreign_key_clause()
    {
        _ddl.Should().Contain("FOREIGN KEY");
    }

    [Test]
    public void It_should_include_references_clause()
    {
        _ddl.Should().Contain("REFERENCES [edfi].[School]");
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
public class Given_MssqlDialect_Add_Unique_Constraint
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "School");
        var columns = new[] { new DbColumnName("DocumentId"), new DbColumnName("SchoolId") };
        _ddl = dialect.AddUniqueConstraint(table, "UQ_School_Identity", columns);
    }

    [Test]
    public void It_should_use_sys_key_constraints_check()
    {
        _ddl.Should().Contain("sys.key_constraints");
    }

    [Test]
    public void It_should_check_for_unique_constraint_type()
    {
        _ddl.Should().Contain("type = 'UQ'");
    }

    [Test]
    public void It_should_include_unique_keyword()
    {
        _ddl.Should().Contain("UNIQUE");
    }

    [Test]
    public void It_should_include_all_columns()
    {
        _ddl.Should().Contain("([DocumentId], [SchoolId])");
    }
}

[TestFixture]
public class Given_MssqlDialect_Add_Check_Constraint
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        var table = new DbTableName(new DbSchemaName("edfi"), "StudentSchoolAssociation");
        _ddl = dialect.AddCheckConstraint(
            table,
            "CK_StudentSchoolAssociation_SchoolRef",
            "(School_DocumentId IS NULL) = (School_SchoolId IS NULL)"
        );
    }

    [Test]
    public void It_should_use_sys_check_constraints_check()
    {
        _ddl.Should().Contain("sys.check_constraints");
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
public class Given_MssqlDialect_Render_Column_Definition
{
    private string _definition = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _definition = dialect.RenderColumnDefinition(
            new DbColumnName("SchoolId"),
            "bigint",
            isNullable: false
        );
    }

    [Test]
    public void It_should_quote_column_name()
    {
        _definition.Should().StartWith("[SchoolId]");
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
public class Given_MssqlDialect_Render_Nullable_Column_Definition
{
    private string _definition = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _definition = dialect.RenderColumnDefinition(
            new DbColumnName("MiddleName"),
            "nvarchar(75)",
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
public class Given_MssqlDialect_Render_Column_Definition_With_Default
{
    private string _definition = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _definition = dialect.RenderColumnDefinition(
            new DbColumnName("CreatedAt"),
            "datetime2(7)",
            isNullable: false,
            defaultExpression: "GETUTCDATE()"
        );
    }

    [Test]
    public void It_should_include_default_clause()
    {
        _definition.Should().Contain("DEFAULT GETUTCDATE()");
    }
}

[TestFixture]
public class Given_MssqlDialect_Render_Primary_Key_Clause
{
    private string _clause = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
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
        _clause.Should().Contain("[DocumentId], [Ordinal]");
    }
}

[TestFixture]
public class Given_MssqlDialect_Render_Referential_Actions
{
    private MssqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new MssqlDialect(new MssqlDialectRules());
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
public class Given_MssqlDialect_Create_Extension_If_Not_Exists
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _ddl = dialect.CreateExtensionIfNotExists("pgcrypto");
    }

    [Test]
    public void It_should_return_empty_string()
    {
        _ddl.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_MssqlDialect_Create_Extension_With_Null_Name
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        try
        {
            var dialect = new MssqlDialect(new MssqlDialectRules());
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
public class Given_MssqlDialect_Create_Uuidv5_Function
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = new MssqlDialect(new MssqlDialectRules());
        _ddl = dialect.CreateUuidv5Function(new DbSchemaName("dms"));
    }

    [Test]
    public void It_should_use_create_or_alter_pattern()
    {
        _ddl.Should().Contain("CREATE OR ALTER FUNCTION");
    }

    [Test]
    public void It_should_qualify_function_name_with_schema()
    {
        _ddl.Should().Contain("[dms].[uuidv5]");
    }

    [Test]
    public void It_should_accept_uniqueidentifier_and_nvarchar_parameters()
    {
        _ddl.Should().Contain("@namespace_uuid uniqueidentifier").And.Contain("@name_text nvarchar(max)");
    }

    [Test]
    public void It_should_return_uniqueidentifier()
    {
        _ddl.Should().Contain("RETURNS uniqueidentifier");
    }

    [Test]
    public void It_should_use_hashbytes_sha1()
    {
        _ddl.Should().Contain("HASHBYTES('SHA1'");
    }

    [Test]
    public void It_should_convert_namespace_to_big_endian_by_swapping_bytes()
    {
        // Verifies the mixed-endian to big-endian byte swap for the namespace
        _ddl.Should()
            .Contain("SUBSTRING(@ns_bytes, 4, 1)")
            .And.Contain("SUBSTRING(@ns_bytes, 3, 1)")
            .And.Contain("SUBSTRING(@ns_bytes, 2, 1)")
            .And.Contain("SUBSTRING(@ns_bytes, 1, 1)");
    }

    [Test]
    public void It_should_convert_result_back_to_mixed_endian()
    {
        // Verifies the big-endian to mixed-endian byte swap for the result
        _ddl.Should()
            .Contain("SUBSTRING(@result, 4, 1)")
            .And.Contain("SUBSTRING(@result, 3, 1)")
            .And.Contain("SUBSTRING(@result, 2, 1)")
            .And.Contain("SUBSTRING(@result, 1, 1)");
    }

    [Test]
    public void It_should_set_version_5_on_byte_6()
    {
        // Byte 6 (0-indexed) = SUBSTRING position 7 (1-indexed)
        _ddl.Should().Contain("SUBSTRING(@result, 7, 1)").And.Contain("0x50");
    }

    [Test]
    public void It_should_set_variant_rfc4122_on_byte_8()
    {
        // Byte 8 (0-indexed) = SUBSTRING position 9 (1-indexed)
        _ddl.Should().Contain("SUBSTRING(@result, 9, 1)").And.Contain("0x80");
    }

    [Test]
    public void It_should_include_schemabinding()
    {
        _ddl.Should().Contain("WITH SCHEMABINDING");
    }

    [Test]
    public void It_should_convert_name_to_utf8_bytes_via_collation()
    {
        _ddl.Should()
            .Contain(
                "CAST(CAST(@name_text AS varchar(max) COLLATE Latin1_General_100_CI_AS_SC_UTF8) AS varbinary(max))"
            );
    }
}

// ═══════════════════════════════════════════════════════════════════
// Literal rendering tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_MssqlDialect_Rendering_Binary_Literal
{
    private MssqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new MssqlDialect(new MssqlDialectRules());
    }

    [Test]
    public void It_should_render_hex_literal()
    {
        var result = _dialect.RenderBinaryLiteral([0xAB, 0xCD, 0xEF]);
        result.Should().Be("0xABCDEF");
    }

    [Test]
    public void It_should_render_empty_byte_array()
    {
        var result = _dialect.RenderBinaryLiteral([]);
        result.Should().Be("0x");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_Boolean_Literal
{
    private MssqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new MssqlDialect(new MssqlDialectRules());
    }

    [Test]
    public void It_should_render_true_as_1()
    {
        _dialect.RenderBooleanLiteral(true).Should().Be("1");
    }

    [Test]
    public void It_should_render_false_as_0()
    {
        _dialect.RenderBooleanLiteral(false).Should().Be("0");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_String_Literal
{
    private MssqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new MssqlDialect(new MssqlDialectRules());
    }

    [Test]
    public void It_should_render_nvarchar_string()
    {
        _dialect.RenderStringLiteral("hello").Should().Be("N'hello'");
    }

    [Test]
    public void It_should_escape_single_quotes()
    {
        _dialect.RenderStringLiteral("it's").Should().Be("N'it''s'");
    }

    [Test]
    public void It_should_use_nvarchar_prefix()
    {
        _dialect.RenderStringLiteral("test").Should().StartWith("N'");
    }
}

[TestFixture]
public class Given_MssqlDialect_Rendering_Numeric_Literals
{
    private MssqlDialect _dialect = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = new MssqlDialect(new MssqlDialectRules());
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
