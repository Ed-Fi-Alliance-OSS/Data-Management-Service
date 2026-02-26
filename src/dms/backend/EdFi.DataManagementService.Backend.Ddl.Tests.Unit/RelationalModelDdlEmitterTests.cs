// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ═══════════════════════════════════════════════════════════════════
// Phase Ordering Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_And_Foreign_Keys
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = ForeignKeyFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_schemas_first()
    {
        _ddl.Should().Contain("CREATE SCHEMA IF NOT EXISTS");
    }

    [Test]
    public void It_should_emit_foreign_keys_after_tables()
    {
        // RelationalModelDdlEmitter does not emit phase comment markers (unlike CoreDdlEmitter).
        // We verify ordering by finding first occurrence of each DDL construct.
        var schemaIndex = _ddl.IndexOf("CREATE SCHEMA");
        var tableIndex = _ddl.IndexOf("CREATE TABLE");
        var fkIndex = _ddl.IndexOf("ALTER TABLE");

        // First verify each construct is present
        schemaIndex.Should().BeGreaterOrEqualTo(0, "expected CREATE SCHEMA in DDL");
        tableIndex.Should().BeGreaterOrEqualTo(0, "expected CREATE TABLE in DDL");
        fkIndex.Should().BeGreaterOrEqualTo(0, "expected ALTER TABLE in DDL");

        // Then verify ordering: schemas before tables, tables before FKs
        schemaIndex.Should().BeLessThan(tableIndex);
        tableIndex.Should().BeLessThan(fkIndex);
    }

    [Test]
    public void It_should_not_include_foreign_keys_in_create_table()
    {
        var createTableEndIndex = _ddl.IndexOf(");", _ddl.IndexOf("CREATE TABLE"));
        var firstFkIndex = _ddl.IndexOf("FOREIGN KEY");

        firstFkIndex.Should().BeGreaterOrEqualTo(0, "expected at least one FOREIGN KEY in emitted DDL");
        firstFkIndex.Should().BeGreaterThan(createTableEndIndex);
    }

    [Test]
    public void It_should_emit_foreign_keys_with_alter_table()
    {
        _ddl.Should().Contain("ALTER TABLE");
        _ddl.Should().Contain("ADD CONSTRAINT");
        _ddl.Should().Contain("FOREIGN KEY");
        _ddl.Should().Contain("REFERENCES");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_Foreign_Keys
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = ForeignKeyFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_foreign_keys_after_tables()
    {
        var tableIndex = _ddl.IndexOf("CREATE TABLE");
        var fkIndex = _ddl.IndexOf("ALTER TABLE");

        tableIndex.Should().BeGreaterOrEqualTo(0, "expected CREATE TABLE in DDL");
        fkIndex.Should().BeGreaterOrEqualTo(0, "expected ALTER TABLE in DDL");
        tableIndex.Should().BeLessThan(fkIndex);
    }

    [Test]
    public void It_should_not_include_foreign_keys_in_create_table()
    {
        var createTableEndIndex = _ddl.IndexOf(");", _ddl.IndexOf("CREATE TABLE"));
        var firstFkIndex = _ddl.IndexOf("FOREIGN KEY");

        firstFkIndex.Should().BeGreaterOrEqualTo(0, "expected at least one FOREIGN KEY in emitted DDL");
        firstFkIndex.Should().BeGreaterThan(createTableEndIndex);
    }

    [Test]
    public void It_should_emit_schemas_first()
    {
        // MSSQL uses IF NOT EXISTS pattern wrapped in EXEC
        _ddl.Should().Contain("CREATE SCHEMA");
    }

    [Test]
    public void It_should_emit_foreign_keys_with_alter_table()
    {
        _ddl.Should().Contain("ALTER TABLE");
        _ddl.Should().Contain("ADD CONSTRAINT");
        _ddl.Should().Contain("FOREIGN KEY");
        _ddl.Should().Contain("REFERENCES");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Unbounded String Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_And_Unbounded_String
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = UnboundedStringFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_varchar_without_max_suffix()
    {
        _ddl.Should().Contain("\"UnboundedColumn\" varchar NOT NULL");
    }

    [Test]
    public void It_should_not_emit_varchar_max_for_postgresql()
    {
        _ddl.Should().NotContain("varchar(max)");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_Unbounded_String
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = UnboundedStringFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_nvarchar_max_for_unbounded_string()
    {
        _ddl.Should().Contain("[UnboundedColumn] nvarchar(max) NOT NULL");
    }

    [Test]
    public void It_should_not_emit_bare_nvarchar()
    {
        _ddl.Should().NotContain("nvarchar NOT NULL");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Abstract Identity Table Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_And_Abstract_Identity_Table
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = AbstractIdentityTableFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_abstract_identity_table()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"edfi\".\"EducationOrganizationIdentity\"");
    }

    [Test]
    public void It_should_include_discriminator_column()
    {
        _ddl.Should().Contain("\"Discriminator\"");
    }

    [Test]
    public void It_should_include_primary_key()
    {
        _ddl.Should().Contain("PRIMARY KEY");
    }

    [Test]
    public void It_should_include_discriminator_as_not_null()
    {
        // Discriminator column must be NOT NULL to ensure every row identifies its concrete type.
        _ddl.Should().MatchRegex(@"""Discriminator""\s+\w+.*NOT NULL");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_Abstract_Identity_Table
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = AbstractIdentityTableFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_abstract_identity_table()
    {
        // MSSQL uses IF OBJECT_ID for table existence check
        _ddl.Should().Contain("[EducationOrganizationIdentity]");
        _ddl.Should().Contain("IF OBJECT_ID");
    }

    [Test]
    public void It_should_include_discriminator_column()
    {
        _ddl.Should().Contain("[Discriminator]");
    }

    [Test]
    public void It_should_include_primary_key()
    {
        _ddl.Should().Contain("PRIMARY KEY");
    }

    [Test]
    public void It_should_include_discriminator_as_not_null()
    {
        // Discriminator column must be NOT NULL to ensure every row identifies its concrete type.
        _ddl.Should().MatchRegex(@"\[Discriminator\]\s+\w+.*NOT NULL");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Abstract Union View Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_And_Abstract_Union_View
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = AbstractUnionViewFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_create_or_replace_view()
    {
        _ddl.Should().Contain("CREATE OR REPLACE VIEW");
    }

    [Test]
    public void It_should_include_union_all()
    {
        _ddl.Should().Contain("UNION ALL");
    }

    [Test]
    public void It_should_include_all_union_arms()
    {
        // Both concrete table names should appear in the view's FROM clauses
        _ddl.Should().Contain("\"School\"");
        _ddl.Should().Contain("\"LocalEducationAgency\"");
    }

    [Test]
    public void It_should_emit_views_after_tables_and_indexes()
    {
        var tableIndex = _ddl.IndexOf("CREATE TABLE");
        var viewIndex = _ddl.IndexOf("CREATE OR REPLACE VIEW");

        tableIndex.Should().BeGreaterOrEqualTo(0, "expected CREATE TABLE in DDL");
        viewIndex.Should().BeGreaterOrEqualTo(0, "expected CREATE OR REPLACE VIEW in DDL");
        viewIndex.Should().BeGreaterThan(tableIndex);
    }

    [Test]
    public void It_should_emit_discriminator_literal_with_postgresql_cast()
    {
        _ddl.Should().Contain("'School'::varchar");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_Abstract_Union_View
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = AbstractUnionViewFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_create_or_alter_view()
    {
        _ddl.Should().Contain("CREATE OR ALTER VIEW");
    }

    [Test]
    public void It_should_include_union_all()
    {
        _ddl.Should().Contain("UNION ALL");
    }

    [Test]
    public void It_should_include_all_union_arms()
    {
        // Both concrete table names should appear in the view's FROM clauses
        _ddl.Should().Contain("[School]");
        _ddl.Should().Contain("[LocalEducationAgency]");
    }

    [Test]
    public void It_should_emit_discriminator_literal_with_sql_server_cast()
    {
        _ddl.Should().Contain("CAST(N'School' AS nvarchar(");
    }

    [Test]
    public void It_should_emit_views_after_tables()
    {
        var tableIndex = _ddl.IndexOf("CREATE TABLE");
        var viewIndex = _ddl.IndexOf("CREATE OR ALTER VIEW");

        tableIndex.Should().BeGreaterOrEqualTo(0, "expected CREATE TABLE in DDL");
        viewIndex.Should().BeGreaterOrEqualTo(0, "expected CREATE OR ALTER VIEW in DDL");
        viewIndex.Should().BeGreaterThan(tableIndex);
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_Unbounded_String_In_Union_View
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = UnboundedStringUnionViewFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_unbounded_string_cast_with_nvarchar_max()
    {
        // MssqlDialect.RenderColumnType should emit nvarchar(max) for unbounded strings
        _ddl.Should().Contain("CAST(N'TestValue' AS nvarchar(max))");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Abstract Identity Table FK Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_And_Abstract_Identity_Table_FK
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = AbstractIdentityTableFkFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_fk_from_abstract_identity_table_to_dms_document()
    {
        // Design requirement: abstract identity tables must have FK to dms.Document
        _ddl.Should().Contain("FK_TestIdentity_Document");
        _ddl.Should().Contain("REFERENCES \"dms\".\"Document\"");
    }

    [Test]
    public void It_should_emit_abstract_identity_table_fk_in_phase_4()
    {
        // FK emission for abstract identity tables should come after table creation
        var tableIndex = _ddl.IndexOf("CREATE TABLE IF NOT EXISTS \"edfi\".\"TestIdentity\"");
        var fkIndex = _ddl.IndexOf("FK_TestIdentity_Document");

        tableIndex.Should().BeGreaterOrEqualTo(0, "expected identity table CREATE TABLE in DDL");
        fkIndex.Should().BeGreaterOrEqualTo(0, "expected identity table FK in DDL");
        fkIndex.Should().BeGreaterThan(tableIndex);
    }

    [Test]
    public void It_should_emit_fk_with_cascade_delete()
    {
        // Abstract identity table FK to dms.Document must use CASCADE delete
        // so rows are cleaned up when the Document is deleted.
        _ddl.Should().Contain("ON DELETE CASCADE");
    }

    [Test]
    public void It_should_follow_fk_naming_convention()
    {
        // FK constraint should follow FK_{TableName}_{TargetTableName} convention.
        _ddl.Should().MatchRegex(@"CONSTRAINT\s+""FK_TestIdentity_Document""");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_Abstract_Identity_Table_FK
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = AbstractIdentityTableFkFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_fk_from_abstract_identity_table_to_dms_document()
    {
        // Design requirement: abstract identity tables must have FK to dms.Document
        _ddl.Should().Contain("FK_TestIdentity_Document");
        _ddl.Should().Contain("REFERENCES [dms].[Document]");
    }

    [Test]
    public void It_should_emit_abstract_identity_table_fk_in_phase_4()
    {
        // FK emission for abstract identity tables should come after table creation
        var tableIndex = _ddl.IndexOf("CREATE TABLE [edfi].[TestIdentity]");
        var fkIndex = _ddl.IndexOf("FK_TestIdentity_Document");

        tableIndex.Should().BeGreaterOrEqualTo(0, "expected identity table CREATE TABLE in DDL");
        fkIndex.Should().BeGreaterOrEqualTo(0, "expected identity table FK in DDL");
        fkIndex.Should().BeGreaterThan(tableIndex);
    }

    [Test]
    public void It_should_emit_fk_with_cascade_delete()
    {
        // Abstract identity table FK to dms.Document must use CASCADE delete
        // so rows are cleaned up when the Document is deleted.
        _ddl.Should().Contain("ON DELETE CASCADE");
    }

    [Test]
    public void It_should_follow_fk_naming_convention()
    {
        // FK constraint should follow FK_{TableName}_{TargetTableName} convention.
        _ddl.Should().MatchRegex(@"CONSTRAINT\s+\[FK_TestIdentity_Document\]");
    }
}

// ═══════════════════════════════════════════════════════════════════
// All-Or-None CHECK Constraint Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_And_AllOrNone_CHECK_Constraint
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = AllOrNoneCheckFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_check_constraint_with_bidirectional_all_or_none()
    {
        // Bidirectional: (all NULL) OR (all NOT NULL)
        _ddl.Should().Contain("CHK_Reference_AllOrNone");
        _ddl.Should().Contain("CHECK");
    }

    [Test]
    public void It_should_require_fk_null_when_deps_null()
    {
        // First clause: FK IS NULL AND all deps IS NULL
        _ddl.Should().Contain("\"Reference_DocumentId\" IS NULL");
        _ddl.Should().Contain("\"Reference_IdentityPart1\" IS NULL");
        _ddl.Should().Contain("\"Reference_IdentityPart2\" IS NULL");
    }

    [Test]
    public void It_should_require_fk_not_null_when_deps_not_null()
    {
        // Second clause: FK IS NOT NULL AND all deps IS NOT NULL
        _ddl.Should().Contain("\"Reference_DocumentId\" IS NOT NULL");
        _ddl.Should().Contain("\"Reference_IdentityPart1\" IS NOT NULL");
        _ddl.Should().Contain("\"Reference_IdentityPart2\" IS NOT NULL");
    }

    [Test]
    public void It_should_use_or_between_all_null_and_all_not_null_clauses()
    {
        // Format: (all NULL) OR (all NOT NULL)
        // Use [\s\S]* instead of .* to match across newlines in multiline CHECK expressions
        _ddl.Should()
            .MatchRegex(
                @"IS NULL[\s\S]*AND[\s\S]*IS NULL[\s\S]*\)\s*OR\s*\([\s\S]*IS NOT NULL[\s\S]*AND[\s\S]*IS NOT NULL"
            );
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_AllOrNone_CHECK_Constraint
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = AllOrNoneCheckFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_check_constraint_with_bidirectional_all_or_none()
    {
        // Bidirectional: (all NULL) OR (all NOT NULL)
        _ddl.Should().Contain("CHK_Reference_AllOrNone");
        _ddl.Should().Contain("CHECK");
    }

    [Test]
    public void It_should_require_fk_null_when_deps_null()
    {
        // First clause: FK IS NULL AND all deps IS NULL
        _ddl.Should().Contain("[Reference_DocumentId] IS NULL");
        _ddl.Should().Contain("[Reference_IdentityPart1] IS NULL");
        _ddl.Should().Contain("[Reference_IdentityPart2] IS NULL");
    }

    [Test]
    public void It_should_require_fk_not_null_when_deps_not_null()
    {
        // Second clause: FK IS NOT NULL AND all deps IS NOT NULL
        _ddl.Should().Contain("[Reference_DocumentId] IS NOT NULL");
        _ddl.Should().Contain("[Reference_IdentityPart1] IS NOT NULL");
        _ddl.Should().Contain("[Reference_IdentityPart2] IS NOT NULL");
    }

    [Test]
    public void It_should_use_or_between_all_null_and_all_not_null_clauses()
    {
        // Format: (all NULL) OR (all NOT NULL)
        // Use [\s\S]* instead of .* to match across newlines in multiline CHECK expressions
        _ddl.Should()
            .MatchRegex(
                @"IS NULL[\s\S]*AND[\s\S]*IS NULL[\s\S]*\)\s*OR\s*\([\s\S]*IS NOT NULL[\s\S]*AND[\s\S]*IS NOT NULL"
            );
    }
}

// ═══════════════════════════════════════════════════════════════════
// Trigger Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_And_Triggers
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = TriggerFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_trigger_function()
    {
        _ddl.Should().Contain("CREATE OR REPLACE FUNCTION");
        _ddl.Should().Contain("RETURNS TRIGGER");
    }

    [Test]
    public void It_should_emit_trigger_with_drop_then_create_pattern()
    {
        // Design requires DROP + CREATE pattern, not CREATE OR REPLACE TRIGGER (ddl-generation.md:260-262)
        _ddl.Should().Contain("DROP TRIGGER IF EXISTS");
        _ddl.Should().Contain("CREATE TRIGGER");
        _ddl.Should().NotContain("CREATE OR REPLACE TRIGGER");
        _ddl.Should().Contain("EXECUTE FUNCTION");
    }

    [Test]
    public void It_should_emit_plpgsql_language()
    {
        _ddl.Should().Contain("$$ LANGUAGE plpgsql");
    }

    [Test]
    public void It_should_emit_return_new()
    {
        _ddl.Should().Contain("RETURN NEW;");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_Triggers
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = TriggerFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_create_or_alter_trigger()
    {
        _ddl.Should().Contain("CREATE OR ALTER TRIGGER");
    }

    [Test]
    public void It_should_emit_after_insert_update()
    {
        _ddl.Should().Contain("AFTER INSERT, UPDATE");
    }

    [Test]
    public void It_should_emit_set_nocount_on()
    {
        _ddl.Should().Contain("SET NOCOUNT ON;");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Determinism Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_Emitting_Twice
{
    private string _first = default!;
    private string _second = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var modelSet = ForeignKeyFixture.Build(dialect.Rules.Dialect);

        // Use separate emitter instances to prove construction + emission is stateless
        var emitter1 = new RelationalModelDdlEmitter(dialect);
        var emitter2 = new RelationalModelDdlEmitter(dialect);

        _first = emitter1.Emit(modelSet);
        _second = emitter2.Emit(modelSet);
    }

    [Test]
    public void It_should_produce_byte_for_byte_identical_output()
    {
        _first.Should().Be(_second);
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_Emitting_Twice
{
    private string _first = default!;
    private string _second = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var modelSet = ForeignKeyFixture.Build(dialect.Rules.Dialect);

        // Use separate emitter instances to prove construction + emission is stateless
        var emitter1 = new RelationalModelDdlEmitter(dialect);
        var emitter2 = new RelationalModelDdlEmitter(dialect);

        _first = emitter1.Emit(modelSet);
        _second = emitter2.Emit(modelSet);
    }

    [Test]
    public void It_should_produce_byte_for_byte_identical_output()
    {
        _first.Should().Be(_second);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Component-Level Ordering Stability Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Multiple_Schemas_Emitting_Twice
{
    private string _first = default!;
    private string _second = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);

        // Build fixture with multiple schemas to verify ordering stability
        var modelSet = MultiSchemaFixture.Build(dialect.Rules.Dialect);

        var emitter1 = new RelationalModelDdlEmitter(dialect);
        var emitter2 = new RelationalModelDdlEmitter(dialect);

        _first = emitter1.Emit(modelSet);
        _second = emitter2.Emit(modelSet);
    }

    [Test]
    public void It_should_produce_identical_output()
    {
        _first.Should().Be(_second);
    }

    [Test]
    public void It_should_emit_schemas_in_consistent_order()
    {
        // Verify schema creation statements appear in a deterministic order
        var alphaIndex = _first.IndexOf("\"alpha\"");
        var betaIndex = _first.IndexOf("\"beta\"");

        alphaIndex.Should().BeGreaterOrEqualTo(0, "expected alpha schema in DDL");
        betaIndex.Should().BeGreaterOrEqualTo(0, "expected beta schema in DDL");

        // Alpha should appear before beta (alphabetical order)
        alphaIndex.Should().BeLessThan(betaIndex);
    }

    [Test]
    public void It_should_emit_tables_in_consistent_order_within_schema()
    {
        // Verify tables within the same schema are emitted in deterministic order
        var alphaAaaIndex = _first.IndexOf("\"alpha\".\"Aaa\"");
        var alphaBbbIndex = _first.IndexOf("\"alpha\".\"Bbb\"");

        alphaAaaIndex.Should().BeGreaterOrEqualTo(0, "expected alpha.Aaa table in DDL");
        alphaBbbIndex.Should().BeGreaterOrEqualTo(0, "expected alpha.Bbb table in DDL");

        // Aaa should appear before Bbb (alphabetical order by resource name)
        alphaAaaIndex.Should().BeLessThan(alphaBbbIndex);
    }
}

internal static class MultiSchemaFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var alphaSchema = new DbSchemaName("alpha");
        var betaSchema = new DbSchemaName("beta");
        var documentIdColumn = new DbColumnName("DocumentId");

        var alphaAaaResource = new QualifiedResourceName("Alpha", "Aaa");
        var alphaBbbResource = new QualifiedResourceName("Alpha", "Bbb");
        var betaCccResource = new QualifiedResourceName("Beta", "Ccc");

        var alphaAaaKey = new ResourceKeyEntry(1, alphaAaaResource, "1.0.0", false);
        var alphaBbbKey = new ResourceKeyEntry(2, alphaBbbResource, "1.0.0", false);
        var betaCccKey = new ResourceKeyEntry(3, betaCccResource, "1.0.0", false);

        DbTableModel CreateTable(DbSchemaName schema, string tableName)
        {
            var tableDef = new DbTableName(schema, tableName);
            return new DbTableModel(
                tableDef,
                new JsonPathExpression("$", []),
                new TableKey(
                    $"PK_{tableName}",
                    [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
                ),
                [
                    new DbColumnModel(
                        documentIdColumn,
                        ColumnKind.ParentKeyPart,
                        new RelationalScalarType(ScalarKind.Int64),
                        IsNullable: false,
                        SourceJsonPath: null,
                        TargetResource: null
                    ),
                ],
                []
            );
        }

        RelationalResourceModel CreateResourceModel(
            QualifiedResourceName resource,
            DbSchemaName schema,
            string tableName
        )
        {
            var table = CreateTable(schema, tableName);
            return new RelationalResourceModel(
                resource,
                schema,
                ResourceStorageKind.RelationalTables,
                table,
                [table],
                [],
                []
            );
        }

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                3,
                [0x01, 0x02, 0x03],
                [
                    new SchemaComponentInfo(
                        "alpha",
                        "Alpha",
                        "1.0.0",
                        false,
                        "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000"
                    ),
                    new SchemaComponentInfo(
                        "beta",
                        "Beta",
                        "1.0.0",
                        false,
                        "bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000bbbb0000"
                    ),
                ],
                [alphaAaaKey, alphaBbbKey, betaCccKey]
            ),
            dialect,
            [
                new ProjectSchemaInfo("alpha", "Alpha", "1.0.0", false, alphaSchema),
                new ProjectSchemaInfo("beta", "Beta", "1.0.0", false, betaSchema),
            ],
            [
                new ConcreteResourceModel(
                    alphaAaaKey,
                    ResourceStorageKind.RelationalTables,
                    CreateResourceModel(alphaAaaResource, alphaSchema, "Aaa")
                ),
                new ConcreteResourceModel(
                    alphaBbbKey,
                    ResourceStorageKind.RelationalTables,
                    CreateResourceModel(alphaBbbResource, alphaSchema, "Bbb")
                ),
                new ConcreteResourceModel(
                    betaCccKey,
                    ResourceStorageKind.RelationalTables,
                    CreateResourceModel(betaCccResource, betaSchema, "Ccc")
                ),
            ],
            [],
            [],
            [],
            []
        );
    }
}

// ═══════════════════════════════════════════════════════════════════
// Basic Tests (existing)
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_Pgsql_Ddl_Emitter_With_Primary_Key_Constraint_Name
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = PrimaryKeyFixture.Build(dialect.Rules.Dialect, "PK_School");

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_named_primary_key_constraint()
    {
        _sql.Should().Contain("CONSTRAINT \"PK_School\" PRIMARY KEY (\"DocumentId\")");
    }
}

[TestFixture]
public class Given_Mssql_Ddl_Emitter_With_Primary_Key_Constraint_Name
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = PrimaryKeyFixture.Build(dialect.Rules.Dialect, "PK_School");

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_named_primary_key_constraint()
    {
        _sql.Should().Contain("CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])");
    }
}

internal static class PrimaryKeyFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect, string primaryKeyName)
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var columnName = new DbColumnName("DocumentId");
        var keyColumn = new DbKeyColumn(columnName, ColumnKind.ParentKeyPart);
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);
        var table = new DbTableModel(
            tableName,
            new JsonPathExpression("$", []),
            new TableKey(primaryKeyName, [keyColumn]),
            [
                new DbColumnModel(
                    columnName,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );
        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            [table],
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
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            []
        );
    }
}

internal static class ForeignKeyFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var parentTableName = new DbTableName(schema, "School");
        var childTableName = new DbTableName(schema, "SchoolAddress");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var parentTable = new DbTableModel(
            parentTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        var childTable = new DbTableModel(
            childTableName,
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("AddressTypeId"), ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("AddressTypeId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolAddress_School",
                    [documentIdColumn],
                    parentTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            parentTable,
            [parentTable, childTable],
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
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            []
        );
    }
}

internal static class UnboundedStringFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var documentIdColumn = new DbColumnName("DocumentId");
        var unboundedColumn = new DbColumnName("UnboundedColumn");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var table = new DbTableModel(
            tableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    unboundedColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: null), // Unbounded
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            [table],
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
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            []
        );
    }
}

internal static class AbstractIdentityTableFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var identityTableName = new DbTableName(schema, "EducationOrganizationIdentity");
        var documentIdColumn = new DbColumnName("DocumentId");
        var discriminatorColumn = new DbColumnName("Discriminator");
        var resource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", true); // Abstract

        var identityTable = new DbTableModel(
            identityTableName,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_EducationOrganizationIdentity",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    discriminatorColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        var abstractIdentityTable = new AbstractIdentityTableInfo(resourceKey, identityTable);

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [],
            [abstractIdentityTable],
            [],
            [],
            []
        );
    }
}

internal static class AbstractUnionViewFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var schoolTableName = new DbTableName(schema, "School");
        var districtTableName = new DbTableName(schema, "LocalEducationAgency");
        var viewName = new DbTableName(schema, "EducationOrganization");
        var documentIdColumn = new DbColumnName("DocumentId");
        var discriminatorColumn = new DbColumnName("Discriminator");

        var abstractResource = new QualifiedResourceName("Ed-Fi", "EducationOrganization");
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var districtResource = new QualifiedResourceName("Ed-Fi", "LocalEducationAgency");
        var abstractResourceKey = new ResourceKeyEntry(1, abstractResource, "1.0.0", true);
        var schoolResourceKey = new ResourceKeyEntry(2, schoolResource, "1.0.0", false);
        var districtResourceKey = new ResourceKeyEntry(3, districtResource, "1.0.0", false);

        List<AbstractUnionViewOutputColumn> outputColumns =
        [
            new(documentIdColumn, new RelationalScalarType(ScalarKind.Int64), null, null),
            new(discriminatorColumn, new RelationalScalarType(ScalarKind.String, MaxLength: 50), null, null),
        ];

        var schoolArm = new AbstractUnionViewArm(
            schoolResourceKey,
            schoolTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("School"),
            ]
        );

        var districtArm = new AbstractUnionViewArm(
            districtResourceKey,
            districtTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("LocalEducationAgency"),
            ]
        );

        var unionView = new AbstractUnionViewInfo(
            abstractResourceKey,
            viewName,
            outputColumns,
            [schoolArm, districtArm]
        );

        // Create identity table
        var identityTable = new DbTableModel(
            new DbTableName(schema, "EducationOrganizationIdentity"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_EducationOrganizationIdentity",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    discriminatorColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        var abstractIdentityTable = new AbstractIdentityTableInfo(abstractResourceKey, identityTable);

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                3,
                [0x01, 0x02, 0x03],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [abstractResourceKey, schoolResourceKey, districtResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [],
            [abstractIdentityTable],
            [unionView],
            [],
            []
        );
    }
}

internal static class UnboundedStringUnionViewFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var testTableName = new DbTableName(schema, "Test");
        var viewName = new DbTableName(schema, "TestView");
        var documentIdColumn = new DbColumnName("DocumentId");
        var unboundedColumn = new DbColumnName("UnboundedField");

        var abstractResource = new QualifiedResourceName("Ed-Fi", "TestAbstract");
        var concreteResource = new QualifiedResourceName("Ed-Fi", "Test");
        var abstractResourceKey = new ResourceKeyEntry(1, abstractResource, "1.0.0", true);
        var concreteResourceKey = new ResourceKeyEntry(2, concreteResource, "1.0.0", false);

        // Output column with unbounded string (no MaxLength)
        List<AbstractUnionViewOutputColumn> outputColumns =
        [
            new(documentIdColumn, new RelationalScalarType(ScalarKind.Int64), null, null),
            new(unboundedColumn, new RelationalScalarType(ScalarKind.String), null, null), // No MaxLength = unbounded
        ];

        var testArm = new AbstractUnionViewArm(
            concreteResourceKey,
            testTableName,
            [
                new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                new AbstractUnionViewProjectionExpression.StringLiteral("TestValue"),
            ]
        );

        var unionView = new AbstractUnionViewInfo(abstractResourceKey, viewName, outputColumns, [testArm]);

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                1,
                [0x01],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [abstractResourceKey, concreteResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [],
            [],
            [unionView],
            [],
            []
        );
    }
}

// ═══════════════════════════════════════════════════════════════════
// Extension Table Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_And_Extension_Tables
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = ExtensionTableFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_create_core_schema()
    {
        _ddl.Should().Contain("CREATE SCHEMA IF NOT EXISTS \"edfi\"");
    }

    [Test]
    public void It_should_create_extension_schema()
    {
        _ddl.Should().Contain("CREATE SCHEMA IF NOT EXISTS \"sample\"");
    }

    [Test]
    public void It_should_create_extension_table_in_extension_schema()
    {
        _ddl.Should().Contain("CREATE TABLE IF NOT EXISTS \"sample\".\"SchoolExtension\"");
    }

    [Test]
    public void It_should_create_cascade_fk_to_base_table()
    {
        _ddl.Should().Contain("\"FK_SchoolExtension_School\"");
        _ddl.Should().Contain("REFERENCES \"edfi\".\"School\"");
        _ddl.Should().Contain("ON DELETE CASCADE");
    }

    [Test]
    public void It_should_emit_extension_schema_before_extension_tables()
    {
        var sampleSchemaIndex = _ddl.IndexOf("CREATE SCHEMA IF NOT EXISTS \"sample\"");
        var extensionTableIndex = _ddl.IndexOf("CREATE TABLE IF NOT EXISTS \"sample\".\"SchoolExtension\"");

        sampleSchemaIndex
            .Should()
            .BeGreaterOrEqualTo(0, "expected CREATE SCHEMA IF NOT EXISTS \"sample\" in DDL");
        extensionTableIndex
            .Should()
            .BeGreaterOrEqualTo(
                0,
                "expected CREATE TABLE IF NOT EXISTS \"sample\".\"SchoolExtension\" in DDL"
            );
        sampleSchemaIndex.Should().BeLessThan(extensionTableIndex);
    }

    [Test]
    public void It_should_emit_base_table_before_extension_table()
    {
        var baseTableIndex = _ddl.IndexOf("CREATE TABLE IF NOT EXISTS \"edfi\".\"School\"");
        var extensionTableIndex = _ddl.IndexOf("CREATE TABLE IF NOT EXISTS \"sample\".\"SchoolExtension\"");

        baseTableIndex
            .Should()
            .BeGreaterOrEqualTo(0, "expected CREATE TABLE IF NOT EXISTS \"edfi\".\"School\" in DDL");
        extensionTableIndex
            .Should()
            .BeGreaterOrEqualTo(
                0,
                "expected CREATE TABLE IF NOT EXISTS \"sample\".\"SchoolExtension\" in DDL"
            );
        baseTableIndex.Should().BeLessThan(extensionTableIndex);
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_And_Extension_Tables
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = ExtensionTableFixture.Build(dialect.Rules.Dialect);

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_create_extension_table_in_extension_schema()
    {
        // MSSQL uses IF OBJECT_ID() IS NULL pattern for idempotent table creation
        _ddl.Should().Contain("CREATE TABLE [sample].[SchoolExtension]");
        _ddl.Should().Contain("IF OBJECT_ID(N'sample.SchoolExtension', N'U') IS NULL");
    }

    [Test]
    public void It_should_create_cascade_fk_to_base_table()
    {
        _ddl.Should().Contain("[FK_SchoolExtension_School]");
        _ddl.Should().Contain("REFERENCES [edfi].[School]");
        _ddl.Should().Contain("ON DELETE CASCADE");
    }

    [Test]
    public void It_should_emit_base_table_before_extension_table()
    {
        // MSSQL uses IF OBJECT_ID() IS NULL pattern for idempotent table creation
        var baseTableIndex = _ddl.IndexOf("CREATE TABLE [edfi].[School]");
        var extensionTableIndex = _ddl.IndexOf("CREATE TABLE [sample].[SchoolExtension]");

        baseTableIndex.Should().BeGreaterOrEqualTo(0, "expected CREATE TABLE [edfi].[School] in DDL");
        extensionTableIndex
            .Should()
            .BeGreaterOrEqualTo(0, "expected CREATE TABLE [sample].[SchoolExtension] in DDL");
        baseTableIndex.Should().BeLessThan(extensionTableIndex);
    }
}

internal static class ExtensionTableFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var sampleSchema = new DbSchemaName("sample");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var extensionDataColumn = new DbColumnName("ExtensionData");

        // Core table: School
        var schoolTableName = new DbTableName(edfiSchema, "School");
        var schoolTable = new DbTableModel(
            schoolTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        // Extension table: SchoolExtension
        var schoolExtTableName = new DbTableName(sampleSchema, "SchoolExtension");
        var schoolExtTable = new DbTableModel(
            schoolExtTableName,
            new JsonPathExpression("$._ext.sample", []),
            new TableKey("PK_SchoolExtension", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    extensionDataColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 200),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                new TableConstraint.ForeignKey(
                    "FK_SchoolExtension_School",
                    [documentIdColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.Cascade,
                    ReferentialAction.NoAction
                ),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            edfiSchema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable, schoolExtTable],
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
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                    new SchemaComponentInfo(
                        "sample",
                        "Sample",
                        "1.0.0",
                        false,
                        "aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000aaaa0000"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema),
                new ProjectSchemaInfo("sample", "Sample", "1.0.0", false, sampleSchema),
            ],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            []
        );
    }
}

internal static class TriggerFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var documentIdColumn = new DbColumnName("DocumentId");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var table = new DbTableModel(
            tableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            [table],
            [],
            []
        );

        var trigger = new DbTriggerInfo(
            new DbTriggerName("TR_School_DocumentStamping"),
            tableName,
            [documentIdColumn],
            [],
            new TriggerKindParameters.DocumentStamping()
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
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            [trigger]
        );
    }
}

internal static class AbstractIdentityTableFkFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var dmsSchema = new DbSchemaName("dms");
        var documentIdColumn = new DbColumnName("DocumentId");
        var testIdColumn = new DbColumnName("TestId");
        var discriminatorColumn = new DbColumnName("Discriminator");

        var abstractResource = new QualifiedResourceName("Ed-Fi", "Test");
        var abstractResourceKey = new ResourceKeyEntry(1, abstractResource, "1.0.0", true);

        // Abstract identity table with FK to dms.Document
        var identityTableName = new DbTableName(edfiSchema, "TestIdentity");
        var identityTable = new AbstractIdentityTableInfo(
            abstractResourceKey,
            new DbTableModel(
                identityTableName,
                new JsonPathExpression("$", []),
                new TableKey(
                    "PK_TestIdentity",
                    [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
                ),
                [
                    new DbColumnModel(
                        documentIdColumn,
                        ColumnKind.ParentKeyPart,
                        new RelationalScalarType(ScalarKind.Int64),
                        IsNullable: false,
                        SourceJsonPath: null,
                        TargetResource: null
                    ),
                    new DbColumnModel(
                        testIdColumn,
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.Int32),
                        IsNullable: false,
                        SourceJsonPath: null,
                        TargetResource: null
                    ),
                    new DbColumnModel(
                        discriminatorColumn,
                        ColumnKind.Scalar,
                        new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                        IsNullable: false,
                        SourceJsonPath: null,
                        TargetResource: null
                    ),
                ],
                [
                    // FK from abstract identity table to dms.Document (as created by BuildIdentityTableConstraints)
                    new TableConstraint.ForeignKey(
                        "FK_TestIdentity_Document",
                        [documentIdColumn],
                        new DbTableName(dmsSchema, "Document"),
                        [documentIdColumn],
                        ReferentialAction.Cascade,
                        ReferentialAction.NoAction
                    ),
                ]
            )
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
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [abstractResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema)],
            [],
            [identityTable],
            [],
            [],
            []
        );
    }
}

internal static class AllOrNoneCheckFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var resource = new QualifiedResourceName("Ed-Fi", "Test");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var documentIdColumn = new DbColumnName("DocumentId");
        var testIdColumn = new DbColumnName("TestId");
        var referenceFkColumn = new DbColumnName("Reference_DocumentId");
        var identityPart1Column = new DbColumnName("Reference_IdentityPart1");
        var identityPart2Column = new DbColumnName("Reference_IdentityPart2");

        // Root table with a document reference that has an all-or-none check constraint
        var tableName = new DbTableName(edfiSchema, "Test");
        var table = new DbTableModel(
            tableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_Test", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    testIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                // Document reference FK column (nullable for optional reference)
                new DbColumnModel(
                    referenceFkColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                // Propagated identity columns (nullable for optional reference)
                new DbColumnModel(
                    identityPart1Column,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    identityPart2Column,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            [
                // All-or-none check: FK and identity columns must all be null or all be not null
                new TableConstraint.AllOrNoneNullability(
                    "CHK_Reference_AllOrNone",
                    referenceFkColumn,
                    [identityPart1Column, identityPart2Column]
                ),
            ]
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            edfiSchema,
            ResourceStorageKind.RelationalTables,
            table,
            [table],
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
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [resourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema)],
            [new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel)],
            [],
            [],
            [],
            []
        );
    }
}

// ═══════════════════════════════════════════════════════════════════
// UUIDv5 Namespace Guard Test
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Guard test ensuring the DDL emitter's embedded UUIDv5 namespace stays in sync with
/// <c>ReferentialIdCalculator.EdFiUuidv5Namespace</c> in <c>EdFi.DataManagementService.Core</c>.
/// If this test fails, either the emitter or the calculator has been changed independently,
/// which would cause emitted triggers to compute referential IDs that don't match runtime.
/// </summary>
[TestFixture]
public class Given_Uuidv5Namespace_Constant
{
    [Test]
    public void It_should_match_the_canonical_EdFi_UUIDv5_namespace()
    {
        // This value must match ReferentialIdCalculator.EdFiUuidv5Namespace in
        // EdFi.DataManagementService.Core.Extraction.
        const string expected = "edf1edf1-3df1-3df1-3df1-3df1edf1edf1";

        RelationalModelDdlEmitter.Uuidv5Namespace.Should().Be(expected);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Input-Order Permutation Regression Tests
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Regression tests that verify the emitter produces byte-for-byte identical DDL regardless of the
/// order in which resources, schemas, indexes, and triggers appear in the <see cref="DerivedRelationalModelSet"/>.
/// This guards against regressions where dictionary iteration order or JSON-file processing order
/// leaks into the emitted SQL text.
/// </summary>
[TestFixture(SqlDialect.Pgsql, "\"Alpha\"", "\"Zeta\"", "\"edfi\"", "\"tpdm\"")]
[TestFixture(SqlDialect.Mssql, "[Alpha]", "[Zeta]", "[edfi]", "[tpdm]")]
public class Given_RelationalModelDdlEmitter_InputOrder_Is_Irrelevant(
    SqlDialect sqlDialect,
    string quotedAlpha,
    string quotedZeta,
    string quotedEdfi,
    string quotedTpdm
)
{
    private string _zetaFirstDdl = default!;
    private string _alphaFirstDdl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(sqlDialect);
        var emitter = new RelationalModelDdlEmitter(dialect);

        _zetaFirstDdl = emitter.Emit(PermutationInputOrderFixture.BuildZetaFirstOrder(dialect.Rules.Dialect));
        _alphaFirstDdl = emitter.Emit(
            PermutationInputOrderFixture.BuildAlphaFirstOrder(dialect.Rules.Dialect)
        );
    }

    [Test]
    public void It_should_produce_identical_ddl_regardless_of_input_ordering()
    {
        _zetaFirstDdl
            .Should()
            .Be(
                _alphaFirstDdl,
                "DDL output must be byte-for-byte identical regardless of input element ordering"
            );
    }

    [Test]
    public void It_should_emit_alpha_table_before_zeta_table()
    {
        // Use a regex to match CREATE TABLE statements containing the quoted table name,
        // accounting for dialect differences (schema prefix, IF NOT EXISTS clause, etc.).
        var alphaMatch = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE TABLE\b.*{System.Text.RegularExpressions.Regex.Escape(quotedAlpha)}"
        );
        var zetaMatch = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE TABLE\b.*{System.Text.RegularExpressions.Regex.Escape(quotedZeta)}"
        );

        alphaMatch.Success.Should().BeTrue("expected CREATE TABLE for Alpha in DDL");
        zetaMatch.Success.Should().BeTrue("expected CREATE TABLE for Zeta in DDL");
        alphaMatch
            .Index.Should()
            .BeLessThan(zetaMatch.Index, "Alpha must precede Zeta in canonical ordinal order");
    }

    [Test]
    public void It_should_emit_edfi_schema_before_tpdm_schema()
    {
        var edfiMatch = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE SCHEMA\b.*{System.Text.RegularExpressions.Regex.Escape(quotedEdfi)}"
        );
        var tpdmMatch = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE SCHEMA\b.*{System.Text.RegularExpressions.Regex.Escape(quotedTpdm)}"
        );

        edfiMatch.Success.Should().BeTrue("expected CREATE SCHEMA for edfi in DDL");
        tpdmMatch.Success.Should().BeTrue("expected CREATE SCHEMA for tpdm in DDL");
        edfiMatch
            .Index.Should()
            .BeLessThan(tpdmMatch.Index, "edfi schema must precede tpdm schema in canonical order");
    }

    [Test]
    public void It_should_emit_edfi_tables_before_tpdm_tables()
    {
        // Within concrete resource tables, Ed-Fi resources (ProjectName "Ed-Fi") are ordered
        // before TPDM resources (ProjectName "TPDM") because "Ed-Fi" < "TPDM" ordinally.
        // Check that the first edfi-schema table appears before the first tpdm-schema table.
        var firstEdfiTable = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE TABLE\b.*{System.Text.RegularExpressions.Regex.Escape(quotedEdfi)}\."
        );
        var firstTpdmTable = System.Text.RegularExpressions.Regex.Match(
            _zetaFirstDdl,
            @$"CREATE TABLE\b.*{System.Text.RegularExpressions.Regex.Escape(quotedTpdm)}\."
        );

        firstEdfiTable.Success.Should().BeTrue("expected at least one edfi table in DDL");
        firstTpdmTable.Success.Should().BeTrue("expected at least one tpdm table in DDL");

        firstEdfiTable
            .Index.Should()
            .BeLessThan(
                firstTpdmTable.Index,
                "first edfi table must precede first tpdm table in canonical order"
            );
    }
}

/// <summary>
/// Fixture for input-order permutation tests.
/// Two schemas ("edfi" and "tpdm"), three concrete resources ("Alpha", "AlphaChild", "Zeta" in edfi
/// plus "Gamma" in tpdm), abstract identity tables, abstract union views, multiple FKs per table,
/// multiple indexes per table, and multiple triggers per table are provided in opposite orderings
/// across the two <c>Build*</c> methods so the emitter's canonical sort is the only thing that can
/// produce identical output.
/// </summary>
internal static class PermutationInputOrderFixture
{
    private static DerivedRelationalModelSet Build(
        SqlDialect dialect,
        IReadOnlyList<ProjectSchemaInfo> projectSchemaOrder,
        IReadOnlyList<ConcreteResourceModel> resourceOrder,
        IReadOnlyList<AbstractIdentityTableInfo> abstractIdentityTableOrder,
        IReadOnlyList<AbstractUnionViewInfo> abstractUnionViewOrder,
        IReadOnlyList<DbIndexInfo> indexOrder,
        IReadOnlyList<DbTriggerInfo> triggerOrder
    )
    {
        var alphaResource = new QualifiedResourceName("Ed-Fi", "Alpha");
        var alphaKey = new ResourceKeyEntry(1, alphaResource, "1.0.0", false);

        var zetaResource = new QualifiedResourceName("Ed-Fi", "Zeta");
        var zetaKey = new ResourceKeyEntry(2, zetaResource, "1.0.0", false);

        var alphaAbstractResource = new QualifiedResourceName("Ed-Fi", "AlphaAbstract");
        var alphaAbstractKey = new ResourceKeyEntry(3, alphaAbstractResource, "1.0.0", true);

        var zetaAbstractResource = new QualifiedResourceName("Ed-Fi", "ZetaAbstract");
        var zetaAbstractKey = new ResourceKeyEntry(4, zetaAbstractResource, "1.0.0", true);

        var gammaResource = new QualifiedResourceName("TPDM", "Gamma");
        var gammaKey = new ResourceKeyEntry(5, gammaResource, "1.0.0", false);

        var gammaAbstractResource = new QualifiedResourceName("TPDM", "GammaAbstract");
        var gammaAbstractKey = new ResourceKeyEntry(6, gammaAbstractResource, "1.0.0", true);

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "abc123",
                6,
                [0xAB, 0xC1],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                    new SchemaComponentInfo(
                        "tpdm",
                        "TPDM",
                        "1.0.0",
                        false,
                        "tpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdmtpdm"
                    ),
                ],
                [alphaKey, zetaKey, alphaAbstractKey, zetaAbstractKey, gammaKey, gammaAbstractKey]
            ),
            dialect,
            projectSchemaOrder,
            resourceOrder,
            abstractIdentityTableOrder,
            abstractUnionViewOrder,
            indexOrder,
            triggerOrder
        );
    }

    private static ConcreteResourceModel BuildConcreteResource(
        DbSchemaName schema,
        DbColumnName documentIdColumn,
        short keyId,
        string resourceName,
        string projectName = "Ed-Fi",
        TableConstraint.ForeignKey? extraChildFk = null
    )
    {
        var resource = new QualifiedResourceName(projectName, resourceName);
        var key = new ResourceKeyEntry(keyId, resource, "1.0.0", false);
        var parentTableName = new DbTableName(schema, resourceName);
        var childTableName = new DbTableName(schema, $"{resourceName}Child");
        var childOrdinalColumn = new DbColumnName("ChildOrdinal");

        var parentTable = new DbTableModel(
            parentTableName,
            new JsonPathExpression("$", []),
            new TableKey($"PK_{resourceName}", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );

        List<DbColumnModel> childColumns =
        [
            new(
                documentIdColumn,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new(
                childOrdinalColumn,
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        ];

        List<TableConstraint> childConstraints =
        [
            new TableConstraint.ForeignKey(
                $"FK_{resourceName}Child_{resourceName}",
                [documentIdColumn],
                parentTableName,
                [documentIdColumn],
                ReferentialAction.Cascade,
                ReferentialAction.NoAction
            ),
        ];

        if (extraChildFk != null)
        {
            // Add the extra FK column to the child table
            childColumns.Add(
                new DbColumnModel(
                    extraChildFk.Columns[0],
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                )
            );
            childConstraints.Add(extraChildFk);
        }

        var childTable = new DbTableModel(
            childTableName,
            new JsonPathExpression("$.children[*]", []),
            new TableKey(
                $"PK_{resourceName}Child",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(childOrdinalColumn, ColumnKind.Scalar),
                ]
            ),
            childColumns,
            childConstraints
        );

        var model = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            parentTable,
            [parentTable, childTable],
            [],
            []
        );
        return new ConcreteResourceModel(key, ResourceStorageKind.RelationalTables, model);
    }

    private static AbstractIdentityTableInfo BuildAbstractIdentityTable(
        DbSchemaName schema,
        DbColumnName documentIdColumn,
        short keyId,
        string resourceName,
        string projectName = "Ed-Fi"
    )
    {
        var resource = new QualifiedResourceName(projectName, resourceName);
        var key = new ResourceKeyEntry(keyId, resource, "1.0.0", true);
        var table = new DbTableModel(
            new DbTableName(schema, $"{resourceName}Identity"),
            new JsonPathExpression("$", []),
            new TableKey(
                $"PK_{resourceName}Identity",
                [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    documentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    new DbColumnName("Discriminator"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            []
        );
        return new AbstractIdentityTableInfo(key, table);
    }

    private static AbstractUnionViewInfo BuildAbstractUnionView(
        DbSchemaName schema,
        List<AbstractUnionViewOutputColumn> outputColumns,
        short abstractKeyId,
        string abstractResourceName,
        short concreteKeyId,
        string concreteResourceName,
        string projectName = "Ed-Fi"
    )
    {
        var documentIdColumn = new DbColumnName("DocumentId");
        var abstractResource = new QualifiedResourceName(projectName, abstractResourceName);
        var abstractKey = new ResourceKeyEntry(abstractKeyId, abstractResource, "1.0.0", true);
        var concreteKey = new ResourceKeyEntry(
            concreteKeyId,
            new QualifiedResourceName(projectName, concreteResourceName),
            "1.0.0",
            false
        );
        return new AbstractUnionViewInfo(
            abstractKey,
            new DbTableName(schema, abstractResourceName),
            outputColumns,
            [
                new AbstractUnionViewArm(
                    concreteKey,
                    new DbTableName(schema, concreteResourceName),
                    [
                        new AbstractUnionViewProjectionExpression.SourceColumn(documentIdColumn),
                        new AbstractUnionViewProjectionExpression.StringLiteral(concreteResourceName),
                    ]
                ),
            ]
        );
    }

    private static DbIndexInfo BuildIndex(
        DbSchemaName schema,
        string tableName,
        string columnName = "DocumentId"
    )
    {
        return new DbIndexInfo(
            new DbIndexName($"IX_{tableName}_{columnName}"),
            new DbTableName(schema, tableName),
            [new DbColumnName(columnName)],
            false,
            DbIndexKind.ForeignKeySupport
        );
    }

    private static DbTriggerInfo BuildTrigger(
        DbSchemaName schema,
        string tableName,
        string triggerSuffix = "Stamp"
    )
    {
        return new DbTriggerInfo(
            new DbTriggerName($"TR_{tableName}_{triggerSuffix}"),
            new DbTableName(schema, tableName),
            [new DbColumnName("DocumentId")],
            [],
            new TriggerKindParameters.DocumentStamping()
        );
    }

    /// <summary>
    /// Builds a model set with all elements in Zeta-first (non-alphabetical) order.
    /// Schema ordering is tpdm-first to exercise cross-schema sorting.
    /// </summary>
    internal static DerivedRelationalModelSet BuildZetaFirstOrder(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var tpdmSchema = new DbSchemaName("tpdm");
        var docId = new DbColumnName("DocumentId");
        var (alpha, zeta, gamma, alphaIdentity, zetaIdentity, gammaIdentity, alphaView, zetaView, gammaView) =
            BuildAllModels(edfiSchema, tpdmSchema, docId);

        // Intentionally non-alphabetical: Zeta first, then Gamma (tpdm), then Alpha
        return Build(
            dialect,
            projectSchemaOrder:
            [
                new ProjectSchemaInfo("tpdm", "TPDM", "1.0.0", false, tpdmSchema),
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema),
            ],
            resourceOrder: [zeta, gamma, alpha],
            abstractIdentityTableOrder: [zetaIdentity, gammaIdentity, alphaIdentity],
            abstractUnionViewOrder: [zetaView, gammaView, alphaView],
            indexOrder:
            [
                BuildIndex(tpdmSchema, "Gamma"),
                BuildIndex(edfiSchema, "Zeta"),
                BuildIndex(edfiSchema, "Alpha", "ReferentialId"),
                BuildIndex(edfiSchema, "Alpha"),
            ],
            triggerOrder:
            [
                BuildTrigger(tpdmSchema, "Gamma"),
                BuildTrigger(edfiSchema, "Zeta"),
                BuildTrigger(edfiSchema, "Alpha", "Version"),
                BuildTrigger(edfiSchema, "Alpha"),
            ]
        );
    }

    /// <summary>
    /// Builds a model set with the same data as <see cref="BuildZetaFirstOrder"/> but with
    /// all elements in Alpha-first (alphabetical) order.
    /// Schema ordering is edfi-first (alphabetical).
    /// </summary>
    internal static DerivedRelationalModelSet BuildAlphaFirstOrder(SqlDialect dialect)
    {
        var edfiSchema = new DbSchemaName("edfi");
        var tpdmSchema = new DbSchemaName("tpdm");
        var docId = new DbColumnName("DocumentId");
        var (alpha, zeta, gamma, alphaIdentity, zetaIdentity, gammaIdentity, alphaView, zetaView, gammaView) =
            BuildAllModels(edfiSchema, tpdmSchema, docId);

        // Alphabetical order: Alpha first, then Zeta, then Gamma (tpdm)
        return Build(
            dialect,
            projectSchemaOrder:
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, edfiSchema),
                new ProjectSchemaInfo("tpdm", "TPDM", "1.0.0", false, tpdmSchema),
            ],
            resourceOrder: [alpha, zeta, gamma],
            abstractIdentityTableOrder: [alphaIdentity, zetaIdentity, gammaIdentity],
            abstractUnionViewOrder: [alphaView, zetaView, gammaView],
            indexOrder:
            [
                BuildIndex(edfiSchema, "Alpha"),
                BuildIndex(edfiSchema, "Alpha", "ReferentialId"),
                BuildIndex(edfiSchema, "Zeta"),
                BuildIndex(tpdmSchema, "Gamma"),
            ],
            triggerOrder:
            [
                BuildTrigger(edfiSchema, "Alpha"),
                BuildTrigger(edfiSchema, "Alpha", "Version"),
                BuildTrigger(edfiSchema, "Zeta"),
                BuildTrigger(tpdmSchema, "Gamma"),
            ]
        );
    }

    private static (
        ConcreteResourceModel alpha,
        ConcreteResourceModel zeta,
        ConcreteResourceModel gamma,
        AbstractIdentityTableInfo alphaIdentity,
        AbstractIdentityTableInfo zetaIdentity,
        AbstractIdentityTableInfo gammaIdentity,
        AbstractUnionViewInfo alphaView,
        AbstractUnionViewInfo zetaView,
        AbstractUnionViewInfo gammaView
    ) BuildAllModels(DbSchemaName edfiSchema, DbSchemaName tpdmSchema, DbColumnName documentIdColumn)
    {
        var discriminator = new DbColumnName("Discriminator");
        List<AbstractUnionViewOutputColumn> outputColumns =
        [
            new(documentIdColumn, new RelationalScalarType(ScalarKind.Int64), null, null),
            new(discriminator, new RelationalScalarType(ScalarKind.String, MaxLength: 50), null, null),
        ];

        // Cross-resource FK: AlphaChild references Zeta parent table via ZetaDocumentId
        var zetaDocIdColumn = new DbColumnName("ZetaDocumentId");
        var zetaParentTable = new DbTableName(edfiSchema, "Zeta");
        var alphaChildCrossRefFk = new TableConstraint.ForeignKey(
            "FK_AlphaChild_Zeta",
            [zetaDocIdColumn],
            zetaParentTable,
            [documentIdColumn],
            ReferentialAction.NoAction,
            ReferentialAction.NoAction
        );

        return (
            BuildConcreteResource(
                edfiSchema,
                documentIdColumn,
                1,
                "Alpha",
                extraChildFk: alphaChildCrossRefFk
            ),
            BuildConcreteResource(edfiSchema, documentIdColumn, 2, "Zeta"),
            BuildConcreteResource(tpdmSchema, documentIdColumn, 5, "Gamma", projectName: "TPDM"),
            BuildAbstractIdentityTable(edfiSchema, documentIdColumn, 3, "AlphaAbstract"),
            BuildAbstractIdentityTable(edfiSchema, documentIdColumn, 4, "ZetaAbstract"),
            BuildAbstractIdentityTable(tpdmSchema, documentIdColumn, 6, "GammaAbstract", projectName: "TPDM"),
            BuildAbstractUnionView(edfiSchema, outputColumns, 3, "AlphaAbstract", 1, "Alpha"),
            BuildAbstractUnionView(edfiSchema, outputColumns, 4, "ZetaAbstract", 2, "Zeta"),
            BuildAbstractUnionView(
                tpdmSchema,
                outputColumns,
                6,
                "GammaAbstract",
                5,
                "Gamma",
                projectName: "TPDM"
            )
        );
    }
}
