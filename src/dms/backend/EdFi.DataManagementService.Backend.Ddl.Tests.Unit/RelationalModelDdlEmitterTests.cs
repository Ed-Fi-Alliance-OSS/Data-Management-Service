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
