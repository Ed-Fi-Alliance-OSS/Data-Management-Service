// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

// ═══════════════════════════════════════════════════════════════════
// People Auth View Availability Tests
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_People_Auth_View_Availability
{
    private const string StudentViewName = "EducationOrganizationIdToStudentDocumentId";

    [Test]
    public void It_should_emit_people_views_when_shared_availability_is_satisfied()
    {
        var modelSet = AuthPeopleViewsFixture.Build(SqlDialect.Pgsql);
        var availability = AuthObjectDefinitions.GetPeopleAuthViewAvailability(
            modelSet.AuthEdOrgHierarchy,
            modelSet.ConcreteResourcesInNameOrder
        );

        availability.IsAvailable.Should().BeTrue();
        var ddl = EmitPgsql(modelSet);
        ddl.Should().Contain(StudentViewName);
        // The ReadChanges auth views share the same availability guard (DMS-1178).
        ddl.Should().Contain(StudentViewName + "IncludingDeletes");
    }

    [Test]
    public void It_should_not_emit_people_views_when_auth_hierarchy_is_missing_or_empty()
    {
        var modelSet = AuthPeopleViewsFixture.Build(SqlDialect.Pgsql);

        AssertPeopleViewsUnavailableWithoutMissingAssociations(modelSet with { AuthEdOrgHierarchy = null });
        AssertPeopleViewsUnavailableWithoutMissingAssociations(
            modelSet with
            {
                AuthEdOrgHierarchy = new AuthEdOrgHierarchy([]),
            }
        );
    }

    [Test]
    public void It_should_not_emit_people_views_when_required_association_resources_are_missing()
    {
        var modelSet = AuthPeopleViewsFixture.Build(SqlDialect.Pgsql);
        var missingResourceName = AuthObjectDefinitions.RequiredPeopleAuthAssociationResourceNames[0];
        var modelSetWithMissingAssociation = modelSet with
        {
            ConcreteResourcesInNameOrder =
            [
                .. modelSet.ConcreteResourcesInNameOrder.Where(concreteResource =>
                    concreteResource.ResourceKey.Resource.ResourceName != missingResourceName
                ),
            ],
        };
        var availability = AuthObjectDefinitions.GetPeopleAuthViewAvailability(
            modelSetWithMissingAssociation.AuthEdOrgHierarchy,
            modelSetWithMissingAssociation.ConcreteResourcesInNameOrder
        );

        availability.HasAuthEdOrgHierarchy.Should().BeTrue();
        availability.MissingAssociationResourceNames.Should().Equal(missingResourceName);
        availability.IsAvailable.Should().BeFalse();
        var ddl = EmitPgsql(modelSetWithMissingAssociation);
        ddl.Should().NotContain(StudentViewName);
        ddl.Should().NotContain("IncludingDeletes").And.NotContain("DeletedResponsibility");
    }

    [Test]
    public void It_should_not_emit_readchanges_views_when_tracked_change_tables_are_missing()
    {
        // The ReadChanges views additionally join tracked_changes_edfi tables; without the
        // tracked-change inventory the people views still emit but the ReadChanges views must not,
        // or the DDL would reference tables it never creates.
        var modelSet = AuthPeopleViewsFixture.Build(SqlDialect.Pgsql) with
        {
            TrackedChangeTablesInNameOrder = [],
        };

        AuthObjectDefinitions
            .HasReadChangesTrackedChangeTables(modelSet.TrackedChangeTablesInNameOrder)
            .Should()
            .BeFalse();
        var ddl = EmitPgsql(modelSet);
        ddl.Should().Contain(StudentViewName);
        ddl.Should().NotContain("IncludingDeletes").And.NotContain("DeletedResponsibility");
    }

    [Test]
    public void It_should_not_emit_readchanges_views_when_one_tracked_change_table_is_missing()
    {
        // HasReadChangesTrackedChangeTables requires all five association tables; a single missing
        // table must suppress every ReadChanges view, not just the arms that join it.
        var modelSet = AuthPeopleViewsFixture.Build(SqlDialect.Pgsql);
        var missingTableName = AuthObjectDefinitions.RequiredPeopleAuthAssociationResourceNames[^1];
        var modelSetWithMissingTrackedTable = modelSet with
        {
            TrackedChangeTablesInNameOrder =
            [
                .. modelSet.TrackedChangeTablesInNameOrder.Where(trackedChangeTable =>
                    trackedChangeTable.Table.Name != missingTableName
                ),
            ],
        };
        modelSetWithMissingTrackedTable
            .TrackedChangeTablesInNameOrder.Should()
            .HaveCount(modelSet.TrackedChangeTablesInNameOrder.Count - 1);

        AuthObjectDefinitions
            .HasReadChangesTrackedChangeTables(modelSetWithMissingTrackedTable.TrackedChangeTablesInNameOrder)
            .Should()
            .BeFalse();
        var ddl = EmitPgsql(modelSetWithMissingTrackedTable);
        ddl.Should().Contain(StudentViewName);
        ddl.Should().NotContain("IncludingDeletes").And.NotContain("DeletedResponsibility");
    }

    [Test]
    public void It_should_not_count_tracked_change_tables_from_other_schemas()
    {
        // The guard matches on schema AND name: the views join tracked_changes_edfi.* tables, so a
        // same-named association table in another tracked-change schema must not satisfy it.
        var modelSet = AuthPeopleViewsFixture.Build(SqlDialect.Pgsql);
        var movedTableName = AuthObjectDefinitions.RequiredPeopleAuthAssociationResourceNames[0];
        var trackedChangeTables = modelSet
            .TrackedChangeTablesInNameOrder.Select(trackedChangeTable =>
                trackedChangeTable.Table.Name == movedTableName
                    ? trackedChangeTable with
                    {
                        Table = trackedChangeTable.Table with
                        {
                            Schema = new DbSchemaName("tracked_changes_ext"),
                        },
                    }
                    : trackedChangeTable
            )
            .ToList();

        AuthObjectDefinitions.HasReadChangesTrackedChangeTables(trackedChangeTables).Should().BeFalse();
    }

    private static void AssertPeopleViewsUnavailableWithoutMissingAssociations(
        DerivedRelationalModelSet modelSet
    )
    {
        var availability = AuthObjectDefinitions.GetPeopleAuthViewAvailability(
            modelSet.AuthEdOrgHierarchy,
            modelSet.ConcreteResourcesInNameOrder
        );

        availability.HasAuthEdOrgHierarchy.Should().BeFalse();
        availability.MissingAssociationResourceNames.Should().BeEmpty();
        availability.IsAvailable.Should().BeFalse();
        EmitPgsql(modelSet).Should().NotContain(StudentViewName);
    }

    private static string EmitPgsql(DerivedRelationalModelSet modelSet)
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);

        return emitter.Emit(modelSet);
    }
}

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
    public void It_should_emit_document_stamping_trigger_function()
    {
        _ddl.Should().Contain("CREATE OR REPLACE FUNCTION");
        _ddl.Should().Contain("RETURNS TRIGGER");
    }

    [Test]
    public void It_should_emit_document_stamping_trigger_with_delete_prevision_in_drop_then_create_pattern()
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
        _ddl.Should().Contain("$func$ LANGUAGE plpgsql");
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
    public void It_should_emit_document_stamping_create_or_alter_trigger()
    {
        _ddl.Should().Contain("CREATE OR ALTER TRIGGER");
    }

    [Test]
    public void It_should_emit_document_stamping_after_insert_update_delete_prevision()
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
public class Given_RelationalModelDdlEmitter_With_Pgsql_DocumentStamping
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = PgsqlDocumentStampingFixture.Build();

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_copy_existing_document_stamps_for_root_inserts()
    {
        var insertBranch = ExtractPlpgsqlSegment(
            GetRootStampFunctionBody(),
            "IF TG_OP = 'INSERT' THEN",
            "ELSIF TG_OP = 'UPDATE' THEN"
        );

        insertBranch.Should().Contain("SELECT \"ContentVersion\", \"ContentLastModifiedAt\"");
        insertBranch.Should().Contain("FROM \"dms\".\"Document\"");
        insertBranch.Should().Contain("WHERE \"DocumentId\" = NEW.\"DocumentId\"");
        insertBranch.Should().Contain("INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt");
        insertBranch.Should().Contain("NEW.\"ContentVersion\" := _stampedContentVersion;");
        insertBranch.Should().Contain("NEW.\"ContentLastModifiedAt\" := _stampedContentLastModifiedAt;");
        insertBranch.Should().NotContain("nextval");
    }

    [Test]
    public void It_should_mirror_child_write_stamps_from_returning_rows()
    {
        var functionBody = GetStampFunctionBody("SchoolAddress");

        var cteStart = functionBody.IndexOf("WITH stamped AS (", StringComparison.Ordinal);
        var returningStart = functionBody.IndexOf(
            "RETURNING \"DocumentId\", \"ContentVersion\", \"ContentLastModifiedAt\"",
            cteStart,
            StringComparison.Ordinal
        );
        var mirrorUpdateStart = functionBody.IndexOf(
            "UPDATE \"edfi\".\"School\" r",
            StringComparison.Ordinal
        );
        var mirrorSetStart = functionBody.IndexOf(
            "SET \"ContentVersion\" = stamped.\"ContentVersion\", \"ContentLastModifiedAt\" = stamped.\"ContentLastModifiedAt\"",
            StringComparison.Ordinal
        );
        var mirrorFromStart = functionBody.IndexOf("FROM stamped", StringComparison.Ordinal);
        var mirrorWhereStart = functionBody.IndexOf(
            "WHERE r.\"DocumentId\" = stamped.\"DocumentId\";",
            StringComparison.Ordinal
        );

        cteStart
            .Should()
            .BeGreaterOrEqualTo(0, "the child stamp function must capture stamped document rows in a CTE");
        returningStart.Should().BeGreaterThan(cteStart);
        mirrorUpdateStart.Should().BeGreaterThan(returningStart);
        mirrorSetStart.Should().BeGreaterThan(mirrorUpdateStart);
        mirrorFromStart.Should().BeGreaterThan(mirrorSetStart);
        mirrorWhereStart.Should().BeGreaterThan(mirrorFromStart);
    }

    [Test]
    public void It_should_skip_child_delete_stamping_when_the_root_mirror_row_is_absent()
    {
        var deleteBranch = ExtractPlpgsqlBlock(
            GetStampFunctionBody("SchoolAddress"),
            "IF TG_OP = 'DELETE' THEN"
        );

        deleteBranch.Should().Contain("AND EXISTS (");
        deleteBranch.Should().Contain("FROM \"edfi\".\"School\" r");
        deleteBranch.Should().Contain("WHERE r.\"DocumentId\" = OLD.\"School_DocumentId\"");
    }

    [Test]
    public void It_should_allocate_document_stamps_for_root_updates()
    {
        var updateBranch = ExtractPlpgsqlBlock(GetRootStampFunctionBody(), "ELSIF TG_OP = 'UPDATE' THEN");

        updateBranch.Should().Contain("UPDATE \"dms\".\"Document\"");
        updateBranch
            .Should()
            .Contain(
                "SET \"ContentVersion\" = nextval('\"dms\".\"ChangeVersionSequence\"'), \"ContentLastModifiedAt\" = now()"
            );
        updateBranch.Should().Contain("WHERE \"DocumentId\" = NEW.\"DocumentId\"");
        updateBranch
            .Should()
            .Contain(
                "RETURNING \"ContentVersion\", \"ContentLastModifiedAt\" INTO STRICT _stampedContentVersion, _stampedContentLastModifiedAt;"
            );
        updateBranch.Should().Contain("NEW.\"ContentVersion\" := _stampedContentVersion;");
        updateBranch.Should().Contain("NEW.\"ContentLastModifiedAt\" := _stampedContentLastModifiedAt;");
    }

    [Test]
    public void It_should_not_capture_stamp_variables_on_paths_that_never_read_them()
    {
        // The stamp locals exist only so root INSERT/UPDATE paths can assign NEW mirror
        // columns. The root DELETE path and the child CTE path never read them, so they
        // must not capture into them (and child functions must not declare them at all).
        _ddl.Should().NotContain("_stampedDocumentId");

        var rootDeleteBranch = ExtractPlpgsqlBlock(GetRootStampFunctionBody(), "IF TG_OP = 'DELETE' THEN");
        rootDeleteBranch.Should().NotContain("RETURNING");

        var childFunctionBody = GetStampFunctionBody("SchoolAddress");
        childFunctionBody.Should().NotContain("_stampedContentVersion");
        childFunctionBody.Should().NotContain("DECLARE");
    }

    [Test]
    public void It_should_gate_identity_stamping_on_null_safe_root_identity_diffs()
    {
        _ddl.Should()
            .Contain("IF TG_OP = 'UPDATE' AND (OLD.\"SchoolId\" IS DISTINCT FROM NEW.\"SchoolId\") THEN");
        _ddl.Should().Contain("\"IdentityVersion\" = nextval('\"dms\".\"ChangeVersionSequence\"')");
        _ddl.Should().Contain("\"IdentityLastModifiedAt\" = now()");
        _ddl.Should().Contain("WHERE \"DocumentId\" = NEW.\"DocumentId\";");
    }

    [Test]
    public void It_should_short_circuit_no_op_updates_by_comparing_stored_root_columns()
    {
        _ddl.Should()
            .Contain(
                "IF TG_OP = 'UPDATE' AND NOT (OLD.\"DocumentId\" IS DISTINCT FROM NEW.\"DocumentId\" OR OLD.\"SchoolId\" IS DISTINCT FROM NEW.\"SchoolId\") THEN"
            );
        _ddl.Should().Contain("RETURN NEW;");
    }

    [Test]
    public void It_should_stamp_child_writes_using_the_root_document_locator()
    {
        _ddl.Should()
            .NotContain(
                """
                IF TG_OP = 'UPDATE' THEN
                    UPDATE "dms"."Document"
                    SET "ContentVersion" = nextval('"dms"."ChangeVersionSequence"'), "ContentLastModifiedAt" = now()
                    WHERE "DocumentId" = NEW."School_DocumentId";
                END IF;
                """
            );
        _ddl.Should().Contain("WHERE \"DocumentId\" = NEW.\"School_DocumentId\"");
    }

    [Test]
    public void It_should_stamp_root_extension_inserts_without_reusing_root_insert_defaults()
    {
        var functionStart = _ddl.IndexOf(
            "CREATE OR REPLACE FUNCTION \"edfi\".\"TF_TR_SchoolExtension_Stamp\"()",
            StringComparison.Ordinal
        );
        var triggerStart =
            functionStart >= 0
                ? _ddl.IndexOf(
                    "CREATE TRIGGER \"TR_SchoolExtension_Stamp\"",
                    functionStart,
                    StringComparison.Ordinal
                )
                : -1;

        functionStart.Should().BeGreaterOrEqualTo(0);
        triggerStart.Should().BeGreaterThan(functionStart);

        var functionBody = _ddl.Substring(functionStart, triggerStart - functionStart);

        functionBody
            .Should()
            .Contain(
                "IF TG_OP = 'UPDATE' AND NOT (OLD.\"DocumentId\" IS DISTINCT FROM NEW.\"DocumentId\" OR OLD.\"ExtensionData\" IS DISTINCT FROM NEW.\"ExtensionData\") THEN"
            );
        functionBody.Should().Contain("RETURN NEW;");
        functionBody.Should().Contain("UPDATE \"dms\".\"Document\"");
        functionBody
            .Should()
            .Contain(
                "\"ContentVersion\" = nextval('\"dms\".\"ChangeVersionSequence\"'), \"ContentLastModifiedAt\" = now()"
            );
        functionBody.Should().Contain("WHERE \"DocumentId\" = NEW.\"DocumentId\"");
        functionBody.Should().NotContain("IF TG_OP = 'UPDATE' THEN");
    }

    [Test]
    public void It_should_short_circuit_no_op_updates_by_comparing_child_stored_columns()
    {
        _ddl.Should()
            .Contain(
                "IF TG_OP = 'UPDATE' AND NOT (OLD.\"CollectionItemId\" IS DISTINCT FROM NEW.\"CollectionItemId\" OR OLD.\"School_DocumentId\" IS DISTINCT FROM NEW.\"School_DocumentId\" OR OLD.\"StreetNumberName\" IS DISTINCT FROM NEW.\"StreetNumberName\") THEN"
            );
    }

    [Test]
    public void It_should_use_row_level_nextval_stamping_shape_for_distinct_multi_row_versions()
    {
        _ddl.Should().Contain("FOR EACH ROW");
        _ddl.Should().Contain("SET \"ContentVersion\" = nextval('\"dms\".\"ChangeVersionSequence\"')");
    }

    [Test]
    public void It_should_short_circuit_referential_identity_maintenance_on_same_value_root_updates()
    {
        var functionStart = _ddl.IndexOf(
            "CREATE OR REPLACE FUNCTION \"edfi\".\"TF_TR_School_ReferentialIdentity\"()",
            StringComparison.Ordinal
        );
        functionStart.Should().BeGreaterOrEqualTo(0);

        var triggerCreateStart = _ddl.IndexOf(
            "CREATE TRIGGER \"TR_School_ReferentialIdentity\"",
            functionStart,
            StringComparison.Ordinal
        );
        triggerCreateStart.Should().BeGreaterThan(functionStart);

        var functionBody = _ddl.Substring(functionStart, triggerCreateStart - functionStart);

        var guardIndex = functionBody.IndexOf(
            "IF TG_OP = 'INSERT' OR (OLD.\"SchoolId\" IS DISTINCT FROM NEW.\"SchoolId\") THEN",
            StringComparison.Ordinal
        );
        var deleteIndex = functionBody.IndexOf(
            "DELETE FROM \"dms\".\"ReferentialIdentity\"",
            StringComparison.Ordinal
        );
        var insertIndex = functionBody.IndexOf(
            "INSERT INTO \"dms\".\"ReferentialIdentity\"",
            StringComparison.Ordinal
        );

        guardIndex
            .Should()
            .BeGreaterOrEqualTo(0, "RI trigger must guard the DELETE/INSERT block on identity diffs");
        deleteIndex.Should().BeGreaterThan(guardIndex);
        insertIndex.Should().BeGreaterThan(guardIndex);
    }

    private string GetRootStampFunctionBody()
    {
        var functionStart = _ddl.IndexOf(
            "CREATE OR REPLACE FUNCTION \"edfi\".\"TF_TR_School_Stamp\"()",
            StringComparison.Ordinal
        );
        functionStart.Should().BeGreaterOrEqualTo(0);

        var triggerStart = _ddl.IndexOf(
            "CREATE TRIGGER \"TR_School_Stamp\"",
            functionStart,
            StringComparison.Ordinal
        );

        triggerStart.Should().BeGreaterThan(functionStart);

        return _ddl.Substring(functionStart, triggerStart - functionStart);
    }

    private string GetStampFunctionBody(string tableName)
    {
        var functionStart = _ddl.IndexOf(
            $"CREATE OR REPLACE FUNCTION \"edfi\".\"TF_TR_{tableName}_Stamp\"()",
            StringComparison.Ordinal
        );
        functionStart.Should().BeGreaterOrEqualTo(0);

        var triggerStart = _ddl.IndexOf(
            $"CREATE TRIGGER \"TR_{tableName}_Stamp\"",
            functionStart,
            StringComparison.Ordinal
        );

        triggerStart.Should().BeGreaterThan(functionStart);

        return _ddl.Substring(functionStart, triggerStart - functionStart);
    }

    private static string ExtractPlpgsqlBlock(string functionBody, string marker)
    {
        var blockStart = functionBody.IndexOf(marker, StringComparison.Ordinal);
        blockStart
            .Should()
            .BeGreaterOrEqualTo(
                0,
                "the root stamp function must have a branch that captures stamps for {0}",
                marker
            );

        var blockEnd = functionBody.IndexOf("END IF;", blockStart, StringComparison.Ordinal);
        blockEnd.Should().BeGreaterThan(blockStart);

        return functionBody.Substring(blockStart, blockEnd - blockStart + "END IF;".Length);
    }

    private static string ExtractPlpgsqlSegment(string functionBody, string marker, string endMarker)
    {
        var segmentStart = functionBody.IndexOf(marker, StringComparison.Ordinal);
        segmentStart
            .Should()
            .BeGreaterOrEqualTo(
                0,
                "the root stamp function must have a branch that captures stamps for {0}",
                marker
            );

        var segmentEnd = functionBody.IndexOf(endMarker, segmentStart, StringComparison.Ordinal);
        segmentEnd.Should().BeGreaterThan(segmentStart);

        return functionBody.Substring(segmentStart, segmentEnd - segmentStart);
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Pgsql_MultiColumnReferentialIdentity
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = PgsqlMultiColumnReferentialIdentityFixture.Build();

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_compose_identity_diff_terms_with_or_for_multi_column_identities()
    {
        _ddl.Should()
            .Contain(
                "IF TG_OP = 'INSERT' OR (OLD.\"PartA\" IS DISTINCT FROM NEW.\"PartA\" OR OLD.\"PartB\" IS DISTINCT FROM NEW.\"PartB\") THEN"
            );
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Descriptor_Valued_ReferentialIdentity
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_hash_descriptor_uri_instead_of_descriptor_document_id(SqlDialect dialect)
    {
        var dialectInstance = SqlDialectFactory.Create(dialect);
        var emitter = new RelationalModelDdlEmitter(dialectInstance);
        var modelSet = DescriptorValuedReferentialIdentityFixture.Build(dialect);

        var ddl = emitter.Emit(modelSet);

        if (dialect == SqlDialect.Pgsql)
        {
            ddl.Should()
                .Contain(
                    "'$.programTypeDescriptor=' || lower((SELECT descriptor.\"Uri\" FROM \"dms\".\"Descriptor\" descriptor WHERE descriptor.\"DocumentId\" = NEW.\"ProgramTypeDescriptor_DescriptorId\"))"
                );
            ddl.Should()
                .Contain(
                    "'$.graduationPlanTypeDescriptor=' || lower((SELECT descriptor.\"Uri\" FROM \"dms\".\"Descriptor\" descriptor WHERE descriptor.\"DocumentId\" = NEW.\"GraduationPlanTypeDescriptor_DescriptorId\"))"
                );
            ddl.Should()
                .NotContain("'$.programTypeDescriptor=' || NEW.\"ProgramTypeDescriptor_DescriptorId\"::text");
            ddl.Should()
                .NotContain(
                    "'$.graduationPlanTypeDescriptor=' || NEW.\"GraduationPlanTypeDescriptor_DescriptorId\"::text"
                );
        }
        else
        {
            ddl.Should()
                .Contain(
                    "N'$.programTypeDescriptor=' + LOWER((SELECT descriptor.[Uri] FROM [dms].[Descriptor] descriptor WHERE descriptor.[DocumentId] = i.[ProgramTypeDescriptor_DescriptorId]))"
                );
            ddl.Should()
                .Contain(
                    "N'$.graduationPlanTypeDescriptor=' + LOWER((SELECT descriptor.[Uri] FROM [dms].[Descriptor] descriptor WHERE descriptor.[DocumentId] = i.[GraduationPlanTypeDescriptor_DescriptorId]))"
                );
            ddl.Should()
                .NotContain(
                    "N'$.programTypeDescriptor=' + CAST(i.[ProgramTypeDescriptor_DescriptorId] AS nvarchar(max))"
                );
            ddl.Should()
                .NotContain(
                    "N'$.graduationPlanTypeDescriptor=' + CAST(i.[GraduationPlanTypeDescriptor_DescriptorId] AS nvarchar(max))"
                );
        }
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Mssql_DocumentStamping
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = MssqlDocumentStampingFixture.Build();

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_capture_content_stamps_with_output_and_mirror_to_target_table()
    {
        var triggerBody = GetSchoolStampTriggerBody();

        var declareStart = triggerBody.IndexOf("DECLARE @stamped TABLE (", StringComparison.Ordinal);
        var documentIdColumnStart = triggerBody.IndexOf(
            "[DocumentId] bigint NOT NULL PRIMARY KEY",
            StringComparison.Ordinal
        );
        var contentVersionColumnStart = triggerBody.IndexOf(
            "[ContentVersion] bigint NOT NULL",
            StringComparison.Ordinal
        );
        var contentLastModifiedColumnStart = triggerBody.IndexOf(
            "[ContentLastModifiedAt] datetime2(7) NOT NULL",
            StringComparison.Ordinal
        );
        var outputStart = triggerBody.IndexOf(
            "OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt] INTO @stamped",
            StringComparison.Ordinal
        );
        var mirrorUpdateStart = triggerBody.IndexOf("UPDATE r", StringComparison.Ordinal);
        var mirrorSetVersionStart = triggerBody.IndexOf(
            "SET r.[ContentVersion] = s.[ContentVersion],",
            StringComparison.Ordinal
        );
        var mirrorSetLastModifiedStart = triggerBody.IndexOf(
            "r.[ContentLastModifiedAt] = s.[ContentLastModifiedAt]",
            StringComparison.Ordinal
        );
        var mirrorFromStart = triggerBody.IndexOf("FROM [edfi].[School] r", StringComparison.Ordinal);
        var mirrorJoinStart = triggerBody.IndexOf(
            "INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];",
            StringComparison.Ordinal
        );

        declareStart
            .Should()
            .BeGreaterOrEqualTo(
                0,
                "the School stamp trigger must allocate a capture table for stamped document rows"
            );
        documentIdColumnStart.Should().BeGreaterThan(declareStart);
        contentVersionColumnStart.Should().BeGreaterThan(documentIdColumnStart);
        contentLastModifiedColumnStart.Should().BeGreaterThan(contentVersionColumnStart);
        outputStart.Should().BeGreaterThan(contentLastModifiedColumnStart);
        mirrorUpdateStart.Should().BeGreaterThan(outputStart);
        mirrorSetVersionStart.Should().BeGreaterThan(mirrorUpdateStart);
        mirrorSetLastModifiedStart.Should().BeGreaterThan(mirrorSetVersionStart);
        mirrorFromStart.Should().BeGreaterThan(mirrorSetLastModifiedStart);
        mirrorJoinStart.Should().BeGreaterThan(mirrorFromStart);
    }

    [Test]
    public void It_should_copy_existing_document_stamps_for_root_inserts()
    {
        var triggerBody = GetSchoolStampTriggerBody();

        triggerBody
            .Should()
            .Contain("INSERT INTO @stamped ([DocumentId], [ContentVersion], [ContentLastModifiedAt])");
        triggerBody.Should().Contain("SELECT d.[DocumentId], d.[ContentVersion], d.[ContentLastModifiedAt]");
        triggerBody.Should().Contain("FROM [dms].[Document] d");
        triggerBody.Should().Contain("INNER JOIN inserted i ON d.[DocumentId] = i.[DocumentId]");
        triggerBody.Should().Contain("LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]");
        triggerBody.Should().Contain("WHERE del.[DocumentId] IS NULL;");
    }

    [Test]
    public void It_should_not_allocate_a_second_sequence_value_when_mirroring()
    {
        var mirrorUpdate = ExtractMirrorUpdateStatement(GetSchoolStampTriggerBody());

        mirrorUpdate.Should().NotContain("NEXT VALUE FOR [dms].[ChangeVersionSequence]");
    }

    [Test]
    public void It_should_use_a_deduped_affected_docs_workset_for_content_stamping()
    {
        _ddl.Should().Contain(";WITH affectedDocs AS (");
        _ddl.Should().Contain("LEFT JOIN deleted del ON del.[DocumentId] = i.[DocumentId]");
        _ddl.Should().Contain("WHERE del.[DocumentId] IS NOT NULL AND (");
        _ddl.Should().Contain("LEFT JOIN inserted i ON i.[DocumentId] = del.[DocumentId]");
        _ddl.Should().Contain("INNER JOIN affectedDocs a ON d.[DocumentId] = a.[DocumentId]");
        _ddl.Should().Contain("LEFT JOIN deleted del ON del.[CollectionItemId] = i.[CollectionItemId]");
        _ddl.Should().Contain("LEFT JOIN inserted i ON i.[CollectionItemId] = del.[CollectionItemId]");
        _ddl.Should().Contain("INNER JOIN affectedDocs a ON d.[DocumentId] = a.[School_DocumentId]");
    }

    [Test]
    public void It_should_skip_child_delete_stamping_when_the_root_mirror_row_is_absent()
    {
        var childTriggerBody = GetStampTriggerBody("SchoolAddress");

        childTriggerBody
            .Should()
            .Contain(
                "INNER JOIN [edfi].[School] stampTarget ON stampTarget.[DocumentId] = a.[School_DocumentId];"
            );
    }

    [Test]
    public void It_should_guard_mirror_updates_against_empty_stamped_worksets()
    {
        // Without this guard the mirror self-UPDATE re-fires the trigger even when
        // @stamped is empty, which recurses to the nesting limit on databases with
        // RECURSIVE_TRIGGERS ON (statement triggers fire on 0 affected rows).
        foreach (var tableName in new[] { "School", "SchoolAddress" })
        {
            var triggerBody = GetStampTriggerBody(tableName);

            var guardStart = triggerBody.IndexOf(
                "IF EXISTS (SELECT 1 FROM @stamped)",
                StringComparison.Ordinal
            );
            guardStart
                .Should()
                .BeGreaterOrEqualTo(
                    0,
                    "the {0} stamp trigger must skip the mirror update for empty stamped worksets",
                    tableName
                );

            var mirrorUpdateStart = triggerBody.IndexOf("UPDATE r", guardStart, StringComparison.Ordinal);
            mirrorUpdateStart.Should().BeGreaterThan(guardStart);
        }
    }

    [Test]
    public void It_should_use_disjoint_affected_docs_branches_for_root_triggers()
    {
        // Root tables key inserted/deleted by the PK, so changed updates land only in the
        // inserted-side branch and the deleted-side branch reduces to a pure anti-join.
        // Disjoint branches allow UNION ALL, so the per-column diff disjunction runs once
        // per row instead of twice plus a dedup sort.
        var rootTriggerBody = GetSchoolStampTriggerBody();
        rootTriggerBody.Should().Contain("UNION ALL");
        rootTriggerBody.Should().NotContain("WHERE i.[DocumentId] IS NULL OR ");

        // Child triggers map many rows to one root document, so UNION's dedup is load-bearing.
        var childTriggerBody = GetStampTriggerBody("SchoolAddress");
        childTriggerBody.Should().NotContain("UNION ALL");
        childTriggerBody.Should().Contain("WHERE i.[CollectionItemId] IS NULL OR ");
    }

    [Test]
    public void It_should_use_next_value_for_directly_in_the_set_based_update()
    {
        _ddl.Should().Contain("SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence]");
        _ddl.Should().Contain("SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence]");
        _ddl.Should().NotContain("DECLARE @contentVersion");
        _ddl.Should().NotContain("DECLARE @identityVersion");
    }

    [Test]
    public void It_should_gate_identity_stamping_with_update_prefilter_and_null_safe_value_diff()
    {
        _ddl.Should().Contain("IF EXISTS (SELECT 1 FROM deleted) AND (UPDATE([SchoolId]))");
        _ddl.Should()
            .Contain(
                "SET d.[IdentityVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence], d.[IdentityLastModifiedAt] = sysutcdatetime()"
            );
        _ddl.Should()
            .Contain(
                "(i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL))"
            );
    }

    [Test]
    public void It_should_not_attempt_identity_gating_on_child_locator_columns()
    {
        _ddl.Should().NotContain("UPDATE([School_DocumentId])");
    }

    [Test]
    public void It_should_filter_no_op_updates_by_comparing_stored_row_values()
    {
        _ddl.Should()
            .Contain(
                "WHERE del.[DocumentId] IS NOT NULL AND ((i.[DocumentId] <> del.[DocumentId] OR (i.[DocumentId] IS NULL AND del.[DocumentId] IS NOT NULL) OR (i.[DocumentId] IS NOT NULL AND del.[DocumentId] IS NULL)) OR (i.[SchoolId] <> del.[SchoolId] OR (i.[SchoolId] IS NULL AND del.[SchoolId] IS NOT NULL) OR (i.[SchoolId] IS NOT NULL AND del.[SchoolId] IS NULL)))"
            );
        _ddl.Should()
            .Contain(
                "WHERE del.[CollectionItemId] IS NULL OR (i.[CollectionItemId] <> del.[CollectionItemId] OR (i.[CollectionItemId] IS NULL AND del.[CollectionItemId] IS NOT NULL) OR (i.[CollectionItemId] IS NOT NULL AND del.[CollectionItemId] IS NULL)) OR (i.[School_DocumentId] <> del.[School_DocumentId] OR (i.[School_DocumentId] IS NULL AND del.[School_DocumentId] IS NOT NULL) OR (i.[School_DocumentId] IS NOT NULL AND del.[School_DocumentId] IS NULL)) OR (CAST(i.[StreetNumberName] AS varbinary(max)) <> CAST(del.[StreetNumberName] AS varbinary(max)) OR (i.[StreetNumberName] IS NULL AND del.[StreetNumberName] IS NOT NULL) OR (i.[StreetNumberName] IS NOT NULL AND del.[StreetNumberName] IS NULL))"
            );
    }

    [Test]
    public void It_should_gate_referential_identity_maintenance_with_changed_docs_value_diff()
    {
        var triggerStart = _ddl.IndexOf(
            "CREATE OR ALTER TRIGGER [edfi].[TR_School_ReferentialIdentity]",
            StringComparison.Ordinal
        );
        triggerStart.Should().BeGreaterOrEqualTo(0);

        var nextTriggerStart = _ddl.IndexOf(
            "CREATE OR ALTER TRIGGER",
            triggerStart + 1,
            StringComparison.Ordinal
        );
        var triggerBody =
            nextTriggerStart >= 0
                ? _ddl.Substring(triggerStart, nextTriggerStart - triggerStart)
                : _ddl[triggerStart..];

        triggerBody.Should().Contain("ELSE IF (UPDATE([SchoolId]))");
        triggerBody.Should().Contain("DECLARE @changedDocs TABLE");

        var worksetStart = triggerBody.IndexOf("INSERT INTO @changedDocs", StringComparison.Ordinal);
        worksetStart.Should().BeGreaterOrEqualTo(0);

        var worksetEnd = triggerBody.IndexOf(';', worksetStart);
        worksetEnd
            .Should()
            .BeGreaterThan(
                worksetStart,
                "@changedDocs INSERT must terminate before any subsequent statement"
            );

        var worksetStatement = triggerBody.Substring(worksetStart, worksetEnd - worksetStart);
        worksetStatement
            .Should()
            .Contain(
                "i.[SchoolId] <> d.[SchoolId]",
                "value-diff filter must live inside the @changedDocs INSERT WHERE clause"
            );

        var riDeleteIndex = triggerBody.IndexOf(
            "WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs)",
            StringComparison.Ordinal
        );
        riDeleteIndex
            .Should()
            .BeGreaterThan(
                worksetEnd,
                "RI DELETE must consume the @changedDocs workset after it is populated"
            );
    }

    private string GetSchoolStampTriggerBody()
    {
        return GetStampTriggerBody("School");
    }

    private string GetStampTriggerBody(string tableName)
    {
        var triggerStart = _ddl.IndexOf(
            $"CREATE OR ALTER TRIGGER [edfi].[TR_{tableName}_Stamp]",
            StringComparison.Ordinal
        );
        triggerStart.Should().BeGreaterOrEqualTo(0);

        var triggerEnd = _ddl.IndexOf("\nGO", triggerStart, StringComparison.Ordinal);
        triggerEnd.Should().BeGreaterThan(triggerStart);

        return _ddl.Substring(triggerStart, triggerEnd - triggerStart);
    }

    private static string ExtractMirrorUpdateStatement(string triggerBody)
    {
        var mirrorUpdateStart = triggerBody.IndexOf("UPDATE r", StringComparison.Ordinal);
        mirrorUpdateStart
            .Should()
            .BeGreaterOrEqualTo(
                0,
                "the School stamp trigger must mirror captured stamps to the target table"
            );

        const string mirrorJoin = "INNER JOIN @stamped s ON s.[DocumentId] = r.[DocumentId];";
        var mirrorJoinStart = triggerBody.IndexOf(mirrorJoin, mirrorUpdateStart, StringComparison.Ordinal);
        mirrorJoinStart.Should().BeGreaterThan(mirrorUpdateStart);

        return triggerBody.Substring(
            mirrorUpdateStart,
            mirrorJoinStart - mirrorUpdateStart + mirrorJoin.Length
        );
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_MssqlIdentityPropagationTrigger
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        var emitter = new RelationalModelDdlEmitter(dialect);
        var modelSet = MssqlIdentityPropagationFixture.Build();

        _ddl = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_identity_propagation_as_after_update()
    {
        // INSTEAD OF UPDATE was rejected by SQL Server: the trigger body's referrer
        // UPDATE re-enters the owning table's trigger and SQL Server raises error 570
        // (INSTEAD OF triggers do not support direct recursion). AFTER UPDATE lets the
        // base row update apply first; the trigger body only needs to reconcile
        // referrer stored projected identity columns.
        _ddl.Should().Contain("CREATE OR ALTER TRIGGER [edfi].[TR_School_PropagateIdentity]");
        _ddl.Should().Contain("ON [edfi].[School]");
        _ddl.Should().Contain("AFTER UPDATE");
        _ddl.Should().NotContain("INSTEAD OF UPDATE");
    }

    [Test]
    public void It_should_propagate_referrer_identity_columns_when_identity_changed()
    {
        _ddl.Should().Contain("UPDATE r");
        _ddl.Should().Contain("FROM [edfi].[Enrollment] r");
        _ddl.Should().Contain("INNER JOIN deleted d ON r.[School_DocumentId] = d.[DocumentId]");
        _ddl.Should()
            .Contain(
                "AND ((r.[School_SchoolId] = d.[SchoolId]) OR (r.[School_SchoolId] IS NULL AND d.[SchoolId] IS NULL));"
            );
    }

    [Test]
    public void It_should_not_emit_a_self_update_block_on_the_trigger_table()
    {
        // The trigger is AFTER UPDATE: the base row update is already applied by
        // SQL Server before the body runs. Emitting `UPDATE <self>` here would
        // re-fire the trigger and (under INSTEAD OF) caused error 570.
        _ddl.Should().NotContain("FROM [edfi].[School] t");
        _ddl.Should()
            .NotContain("SET t.[SchoolId] = i.[SchoolId], t.[NameOfInstitution] = i.[NameOfInstitution]");
    }
}

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
            new TriggerKindParameters.DocumentStamping(),
            MirrorStampTargetTable: tableName
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

internal static class PgsqlDocumentStampingFixture
{
    internal static DerivedRelationalModelSet Build()
    {
        var dialect = SqlDialect.Pgsql;
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var childTableName = new DbTableName(schema, "SchoolAddress");
        var collectionItemIdColumn = new DbColumnName("CollectionItemId");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var contentVersionColumn = new DbColumnName("ContentVersion");
        var contentLastModifiedAtColumn = new DbColumnName("ContentLastModifiedAt");
        var extensionDataColumn = new DbColumnName("ExtensionData");
        var childDocumentIdColumn = new DbColumnName("School_DocumentId");
        var streetNumberNameColumn = new DbColumnName("StreetNumberName");
        var extensionTableName = new DbTableName(schema, "SchoolExtension");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var rootTable = new DbTableModel(
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
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.schoolId", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    contentVersionColumn,
                    ColumnKind.MirroredContentVersion,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    IsWritable = false,
                },
                new DbColumnModel(
                    contentLastModifiedAtColumn,
                    ColumnKind.MirroredContentLastModifiedAt,
                    new RelationalScalarType(ScalarKind.DateTime),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    IsWritable = false,
                },
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [documentIdColumn],
                [documentIdColumn],
                [],
                []
            ),
        };

        var childTable = new DbTableModel(
            childTableName,
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(collectionItemIdColumn, ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    collectionItemIdColumn,
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    childDocumentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    streetNumberNameColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 150),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression("$.addresses[*].streetNumberName", []),
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [collectionItemIdColumn],
                [childDocumentIdColumn],
                [childDocumentIdColumn],
                []
            ),
        };

        var rootExtensionTable = new DbTableModel(
            extensionTableName,
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
                    new RelationalScalarType(ScalarKind.String, MaxLength: 150),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression("$._ext.sample.extensionData", []),
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [documentIdColumn],
                [documentIdColumn],
                [documentIdColumn],
                []
            ),
        };

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, childTable, rootExtensionTable],
            [],
            []
        );

        IReadOnlyList<DbTriggerInfo> triggers =
        [
            new(
                new DbTriggerName("TR_School_Stamp"),
                tableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping(),
                MirrorStampTargetTable: tableName
            ),
            new(
                new DbTriggerName("TR_SchoolAddress_Stamp"),
                childTableName,
                [childDocumentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping(),
                MirrorStampTargetTable: tableName
            ),
            new(
                new DbTriggerName("TR_SchoolExtension_Stamp"),
                extensionTableName,
                [documentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping(),
                MirrorStampTargetTable: tableName
            ),
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                tableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [
                        new IdentityElementMapping(
                            schoolIdColumn,
                            "$.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ]
                )
            ),
        ];

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
            triggers
        );
    }
}

internal static class MssqlDocumentStampingFixture
{
    internal static DerivedRelationalModelSet Build()
    {
        var dialect = SqlDialect.Mssql;
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "School");
        var childTableName = new DbTableName(schema, "SchoolAddress");
        var collectionItemIdColumn = new DbColumnName("CollectionItemId");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var contentVersionColumn = new DbColumnName("ContentVersion");
        var contentLastModifiedAtColumn = new DbColumnName("ContentLastModifiedAt");
        var childDocumentIdColumn = new DbColumnName("School_DocumentId");
        var streetNumberNameColumn = new DbColumnName("StreetNumberName");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var rootTable = new DbTableModel(
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
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.schoolId", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    contentVersionColumn,
                    ColumnKind.MirroredContentVersion,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    IsWritable = false,
                },
                new DbColumnModel(
                    contentLastModifiedAtColumn,
                    ColumnKind.MirroredContentLastModifiedAt,
                    new RelationalScalarType(ScalarKind.DateTime),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    IsWritable = false,
                },
            ],
            []
        );

        var childTable = new DbTableModel(
            childTableName,
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(collectionItemIdColumn, ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    collectionItemIdColumn,
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    childDocumentIdColumn,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    streetNumberNameColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 150),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression("$.addresses[*].streetNumberName", []),
                    TargetResource: null
                ),
            ],
            []
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable, childTable],
            [],
            []
        );

        IReadOnlyList<DbTriggerInfo> triggers =
        [
            new(
                new DbTriggerName("TR_School_Stamp"),
                tableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.DocumentStamping(),
                MirrorStampTargetTable: tableName
            ),
            new(
                new DbTriggerName("TR_SchoolAddress_Stamp"),
                childTableName,
                [childDocumentIdColumn],
                [],
                new TriggerKindParameters.DocumentStamping(),
                MirrorStampTargetTable: tableName
            ),
            new(
                new DbTriggerName("TR_School_ReferentialIdentity"),
                tableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "School",
                    [
                        new IdentityElementMapping(
                            schoolIdColumn,
                            "$.schoolId",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ]
                )
            ),
        ];

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
            triggers
        );
    }
}

internal static class PgsqlMultiColumnReferentialIdentityFixture
{
    internal static DerivedRelationalModelSet Build()
    {
        var dialect = SqlDialect.Pgsql;
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "Composite");
        var documentIdColumn = new DbColumnName("DocumentId");
        var partAColumn = new DbColumnName("PartA");
        var partBColumn = new DbColumnName("PartB");
        var resource = new QualifiedResourceName("Ed-Fi", "Composite");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var rootTable = new DbTableModel(
            tableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_Composite", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
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
                    partAColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.partA", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    partBColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.partB", []),
                    TargetResource: null
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [documentIdColumn],
                [documentIdColumn],
                [],
                []
            ),
        };

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );

        IReadOnlyList<DbTriggerInfo> triggers =
        [
            new(
                new DbTriggerName("TR_Composite_ReferentialIdentity"),
                tableName,
                [documentIdColumn],
                [partAColumn, partBColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "Composite",
                    [
                        new IdentityElementMapping(
                            partAColumn,
                            "$.partA",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                        new IdentityElementMapping(
                            partBColumn,
                            "$.partB",
                            new RelationalScalarType(ScalarKind.Int32)
                        ),
                    ]
                )
            ),
        ];

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
            triggers
        );
    }
}

internal static class DescriptorValuedReferentialIdentityFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var tableName = new DbTableName(schema, "ProgramOffering");
        var documentIdColumn = new DbColumnName("DocumentId");
        var programTypeDescriptorColumn = new DbColumnName("ProgramTypeDescriptor_DescriptorId");
        var graduationPlanTypeDescriptorColumn = new DbColumnName(
            "GraduationPlanTypeDescriptor_DescriptorId"
        );
        var resource = new QualifiedResourceName("Ed-Fi", "ProgramOffering");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var rootTable = new DbTableModel(
            tableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_ProgramOffering", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
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
                    programTypeDescriptorColumn,
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.programTypeDescriptor", []),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "ProgramTypeDescriptor")
                ),
                new DbColumnModel(
                    graduationPlanTypeDescriptorColumn,
                    ColumnKind.DescriptorFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.graduationPlanTypeDescriptor", []),
                    TargetResource: new QualifiedResourceName("Ed-Fi", "GraduationPlanTypeDescriptor")
                ),
            ],
            []
        );

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            [],
            []
        );

        IReadOnlyList<DbTriggerInfo> triggers =
        [
            new(
                new DbTriggerName("TR_ProgramOffering_ReferentialIdentity"),
                tableName,
                [documentIdColumn],
                [programTypeDescriptorColumn, graduationPlanTypeDescriptorColumn],
                new TriggerKindParameters.ReferentialIdentityMaintenance(
                    1,
                    "Ed-Fi",
                    "ProgramOffering",
                    [
                        new IdentityElementMapping(
                            programTypeDescriptorColumn,
                            "$.programTypeDescriptor",
                            new RelationalScalarType(ScalarKind.Int64),
                            IsDescriptorReference: true
                        ),
                        new IdentityElementMapping(
                            graduationPlanTypeDescriptorColumn,
                            "$.graduationPlanTypeDescriptor",
                            new RelationalScalarType(ScalarKind.Int64),
                            IsDescriptorReference: true
                        ),
                    ]
                )
            ),
        ];

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
            triggers
        );
    }
}

internal static class MssqlIdentityPropagationFixture
{
    internal static DerivedRelationalModelSet Build()
    {
        var dialect = SqlDialect.Mssql;
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var nameOfInstitutionColumn = new DbColumnName("NameOfInstitution");
        var resource = new QualifiedResourceName("Ed-Fi", "School");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var schoolTableName = new DbTableName(schema, "School");
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
                    SourceJsonPath: new JsonPathExpression("$.schoolId", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    nameOfInstitutionColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 150),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression("$.nameOfInstitution", []),
                    TargetResource: null
                ),
            ],
            []
        );

        IReadOnlyList<DbTriggerInfo> triggers =
        [
            new(
                new DbTriggerName("TR_School_PropagateIdentity"),
                schoolTableName,
                [documentIdColumn],
                [schoolIdColumn],
                new TriggerKindParameters.MssqlIdentityPropagationTrigger([
                    new PropagationReferrerTarget(
                        new DbTableName(schema, "Enrollment"),
                        new DbColumnName("School_DocumentId"),
                        [new TriggerColumnMapping(schoolIdColumn, new DbColumnName("School_SchoolId"))]
                    ),
                ])
            ),
        ];

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
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
            triggers
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
// Child-collection identity propagation: MSSQL emits propagation
// trigger including the child referrer, PG emits composite FK with
// ON UPDATE CASCADE instead of any propagation trigger.
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_DdlEmitter_On_Mssql_With_Child_Collection_Referrer
{
    private string _mssqlDdl = default!;

    [SetUp]
    public void Setup()
    {
        _mssqlDdl = EmitDdlForFixture(SqlDialect.Mssql);
    }

    [Test]
    public void It_should_emit_TR_School_PropagateIdentity()
    {
        _mssqlDdl.Should().Contain("TR_School_PropagateIdentity");
    }

    [Test]
    public void It_should_update_child_table_in_propagation_body()
    {
        _mssqlDdl.Should().Contain("UPDATE r");
        _mssqlDdl.Should().Contain("FROM [edfi].[BusRouteAddress] r");
        _mssqlDdl.Should().Contain("INNER JOIN deleted d ON r.[School_DocumentId] = d.[DocumentId]");
    }

    [Test]
    public void It_should_set_child_stored_identity_columns_from_inserted()
    {
        _mssqlDdl.Should().Contain("r.[School_EducationOrganizationId] = i.[EducationOrganizationId]");
        _mssqlDdl.Should().Contain("r.[School_SchoolId] = i.[SchoolId]");
    }

    /// <summary>
    /// Builds a minimal <see cref="DerivedRelationalModelSet"/> for both dialects that mirrors
    /// the shape exercised by the trigger-inventory derivation tests: a School root with a
    /// composite identity (EducationOrganizationId, SchoolId), a BusRoute root referrer, and
    /// a BusRouteAddress child collection that also stores the propagated School identity
    /// columns. The MSSQL variant carries the <see cref="TriggerKindParameters.MssqlIdentityPropagationTrigger"/>
    /// trigger with both referrer updates; the PG variant has no propagation trigger and instead
    /// uses a composite FK on the child table with <c>ON UPDATE CASCADE</c> against School.
    /// </summary>
    internal static string EmitDdlForFixture(SqlDialect dialect)
    {
        var sqlDialect = SqlDialectFactory.Create(dialect);
        var emitter = new RelationalModelDdlEmitter(sqlDialect);
        var modelSet = ChildCollectionReferrerFixture.Build(dialect);
        return emitter.Emit(modelSet);
    }
}

[TestFixture]
public class Given_DdlEmitter_On_Pgsql_With_Child_Collection_Referrer
{
    private string _pgsqlDdl = default!;

    [SetUp]
    public void Setup()
    {
        _pgsqlDdl = Given_DdlEmitter_On_Mssql_With_Child_Collection_Referrer.EmitDdlForFixture(
            SqlDialect.Pgsql
        );
    }

    [Test]
    public void It_should_not_emit_PropagateIdentity_trigger_on_Pgsql()
    {
        _pgsqlDdl.Should().NotContain("PropagateIdentity");
    }

    [Test]
    public void It_should_emit_composite_FK_with_ON_UPDATE_CASCADE_for_child_table()
    {
        // The composite FK on BusRouteAddress referencing School(DocumentId, EducationOrganizationId, SchoolId)
        // must carry ON UPDATE CASCADE on PG — this is the PG path that replaces the trigger fallback.
        // Anchor the assertion to BusRouteAddress so it cannot be satisfied vacuously by the
        // root BusRoute → School FK that the fixture also emits.
        _pgsqlDdl
            .Should()
            .Contain(
                """
                        ALTER TABLE "edfi"."BusRouteAddress"
                        ADD CONSTRAINT "FK_BusRouteAddress_School"
                        FOREIGN KEY ("School_DocumentId", "School_EducationOrganizationId", "School_SchoolId")
                        REFERENCES "edfi"."School" ("DocumentId", "EducationOrganizationId", "SchoolId")
                        ON DELETE NO ACTION
                        ON UPDATE CASCADE;
                """
            );
    }
}

internal static class ChildCollectionReferrerFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var documentIdColumn = new DbColumnName("DocumentId");
        var educationOrgIdColumn = new DbColumnName("EducationOrganizationId");
        var schoolIdColumn = new DbColumnName("SchoolId");
        var nameOfInstitutionColumn = new DbColumnName("NameOfInstitution");
        var schoolFkColumn = new DbColumnName("School_DocumentId");
        var schoolEdOrgIdColumn = new DbColumnName("School_EducationOrganizationId");
        var schoolSchoolIdColumn = new DbColumnName("School_SchoolId");
        var busRouteNameColumn = new DbColumnName("BusRouteName");
        var streetColumn = new DbColumnName("StreetNumberName");

        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var busRouteResource = new QualifiedResourceName("Ed-Fi", "BusRoute");
        var schoolResourceKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);
        var busRouteResourceKey = new ResourceKeyEntry(2, busRouteResource, "1.0.0", false);

        var schoolTableName = new DbTableName(schema, "School");
        var busRouteTableName = new DbTableName(schema, "BusRoute");
        var busRouteAddressTableName = new DbTableName(schema, "BusRouteAddress");

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
                    educationOrgIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.educationOrganizationId", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.schoolId", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    nameOfInstitutionColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 150),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression("$.nameOfInstitution", []),
                    TargetResource: null
                ),
            ],
            []
        );

        var busRouteTable = new DbTableModel(
            busRouteTableName,
            new JsonPathExpression("$", []),
            new TableKey("PK_BusRoute", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
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
                    busRouteNameColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.busRouteName", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolFkColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.schoolReference", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolEdOrgIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolSchoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            BuildBusRouteConstraints(dialect, schoolTableName)
        );

        var busRouteAddressTable = new DbTableModel(
            busRouteAddressTableName,
            new JsonPathExpression("$.addresses[*]", []),
            new TableKey(
                "PK_BusRouteAddress",
                [
                    new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
                    new DbKeyColumn(streetColumn, ColumnKind.Scalar),
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
                    streetColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 150),
                    IsNullable: false,
                    SourceJsonPath: new JsonPathExpression("$.addresses[*].streetNumberName", []),
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolFkColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolEdOrgIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    schoolSchoolIdColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            BuildBusRouteAddressConstraints(dialect, schoolTableName)
        );

        var schoolModel = new RelationalResourceModel(
            schoolResource,
            schema,
            ResourceStorageKind.RelationalTables,
            schoolTable,
            [schoolTable],
            [],
            []
        );

        var busRouteModel = new RelationalResourceModel(
            busRouteResource,
            schema,
            ResourceStorageKind.RelationalTables,
            busRouteTable,
            [busRouteTable, busRouteAddressTable],
            [],
            []
        );

        IReadOnlyList<DbTriggerInfo> triggers =
            dialect == SqlDialect.Mssql
                ?
                [
                    new DbTriggerInfo(
                        new DbTriggerName("TR_School_PropagateIdentity"),
                        schoolTableName,
                        [documentIdColumn],
                        [educationOrgIdColumn, schoolIdColumn],
                        new TriggerKindParameters.MssqlIdentityPropagationTrigger([
                            new PropagationReferrerTarget(
                                busRouteTableName,
                                schoolFkColumn,
                                [
                                    new TriggerColumnMapping(educationOrgIdColumn, schoolEdOrgIdColumn),
                                    new TriggerColumnMapping(schoolIdColumn, schoolSchoolIdColumn),
                                ]
                            ),
                            new PropagationReferrerTarget(
                                busRouteAddressTableName,
                                schoolFkColumn,
                                [
                                    new TriggerColumnMapping(educationOrgIdColumn, schoolEdOrgIdColumn),
                                    new TriggerColumnMapping(schoolIdColumn, schoolSchoolIdColumn),
                                ]
                            ),
                        ])
                    ),
                ]
                : [];

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                2,
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
                [schoolResourceKey, busRouteResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                new ConcreteResourceModel(
                    busRouteResourceKey,
                    ResourceStorageKind.RelationalTables,
                    busRouteModel
                ),
                new ConcreteResourceModel(
                    schoolResourceKey,
                    ResourceStorageKind.RelationalTables,
                    schoolModel
                ),
            ],
            [],
            [],
            [],
            triggers
        );
    }

    /// <summary>
    /// BusRoute's reference to School: on PG this is a composite FK with ON UPDATE CASCADE
    /// (so cascade-based propagation replaces the trigger fallback); on MSSQL it is a
    /// single-column FK on the document id, since MSSQL relies on the propagation trigger
    /// emitted on School to reconcile the stored identity columns.
    /// </summary>
    private static IReadOnlyList<TableConstraint> BuildBusRouteConstraints(
        SqlDialect dialect,
        DbTableName schoolTableName
    )
    {
        var schoolFkColumn = new DbColumnName("School_DocumentId");
        var schoolEdOrgIdColumn = new DbColumnName("School_EducationOrganizationId");
        var schoolSchoolIdColumn = new DbColumnName("School_SchoolId");
        var documentIdColumn = new DbColumnName("DocumentId");
        var educationOrgIdColumn = new DbColumnName("EducationOrganizationId");
        var schoolIdColumn = new DbColumnName("SchoolId");

        return dialect == SqlDialect.Pgsql
            ?
            [
                new TableConstraint.ForeignKey(
                    "FK_BusRoute_School",
                    [schoolFkColumn, schoolEdOrgIdColumn, schoolSchoolIdColumn],
                    schoolTableName,
                    [documentIdColumn, educationOrgIdColumn, schoolIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.Cascade
                ),
            ]
            :
            [
                new TableConstraint.ForeignKey(
                    "FK_BusRoute_School",
                    [schoolFkColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ];
    }

    /// <summary>
    /// BusRouteAddress's reference to School mirrors the BusRoute case: composite cascade on
    /// PG, single-column FK on MSSQL (with the trigger doing the propagation work).
    /// </summary>
    private static IReadOnlyList<TableConstraint> BuildBusRouteAddressConstraints(
        SqlDialect dialect,
        DbTableName schoolTableName
    )
    {
        var schoolFkColumn = new DbColumnName("School_DocumentId");
        var schoolEdOrgIdColumn = new DbColumnName("School_EducationOrganizationId");
        var schoolSchoolIdColumn = new DbColumnName("School_SchoolId");
        var documentIdColumn = new DbColumnName("DocumentId");
        var educationOrgIdColumn = new DbColumnName("EducationOrganizationId");
        var schoolIdColumn = new DbColumnName("SchoolId");

        return dialect == SqlDialect.Pgsql
            ?
            [
                new TableConstraint.ForeignKey(
                    "FK_BusRouteAddress_School",
                    [schoolFkColumn, schoolEdOrgIdColumn, schoolSchoolIdColumn],
                    schoolTableName,
                    [documentIdColumn, educationOrgIdColumn, schoolIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.Cascade
                ),
            ]
            :
            [
                new TableConstraint.ForeignKey(
                    "FK_BusRouteAddress_School",
                    [schoolFkColumn],
                    schoolTableName,
                    [documentIdColumn],
                    ReferentialAction.NoAction,
                    ReferentialAction.NoAction
                ),
            ];
    }
}

// ═══════════════════════════════════════════════════════════════════
// Tracked-Change Rendering Tests (DMS-1179)
// ═══════════════════════════════════════════════════════════════════

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_TrackedChange_Attached_Resource_Pgsql
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        _ddl = new RelationalModelDdlEmitter(dialect).Emit(
            TrackedChangeTriggerFixture.BuildAttachedResource(SqlDialect.Pgsql)
        );
    }

    [Test]
    public void It_should_insert_a_tombstone_in_the_delete_branch()
    {
        _ddl.Should().Contain("IF TG_OP = 'DELETE' THEN");
        _ddl.Should().Contain("INSERT INTO \"tracked_changes_edfi\".");
        _ddl.IndexOf("INSERT INTO \"tracked_changes_edfi\".", StringComparison.Ordinal)
            .Should()
            .BeLessThan(_ddl.IndexOf("RETURN OLD;", StringComparison.Ordinal));
    }

    [Test]
    public void It_should_insert_a_key_change_row_inside_the_identity_diff_branch()
    {
        _ddl.Should().Contain("_stampedContentVersion");
        _ddl.Should().Contain("IS DISTINCT FROM");

        // The tracked-change CREATE TABLE earlier in the script legitimately names New* columns,
        // so slice to the trigger-function section to prove the key-change INSERT was rendered.
        var triggerSection = _ddl[_ddl.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)..];
        triggerSection.Should().Contain("\"NewBeginDate\"");

        // The key-change INSERT must appear AFTER the IdentityVersion UPDATE within the DDL.
        triggerSection
            .IndexOf("\"IdentityVersion\"", StringComparison.Ordinal)
            .Should()
            .BeLessThan(triggerSection.IndexOf("\"NewBeginDate\"", StringComparison.Ordinal));
    }

    [Test]
    public void It_should_not_gate_key_change_eligibility_on_update_of_column_lists()
    {
        // AC: PostgreSQL must not use UPDATE OF target-list checks for key-change
        // row eligibility — the null-safe identity-workset diff is the only gate.
        // Mirrors the MSSQL fixture's NotContain("UPDATE(") assertion.
        _ddl.Should().NotContain("UPDATE OF");
    }

    [Test]
    public void It_should_place_tombstone_insert_after_delete_branch_content_stamp_update()
    {
        // Locate the delete-branch marker, the content-stamp UPDATE that follows it,
        // and the tombstone INSERT — the INSERT must come after the UPDATE.
        var deleteBranchIdx = _ddl.IndexOf("IF TG_OP = 'DELETE' THEN", StringComparison.Ordinal);
        deleteBranchIdx.Should().BeGreaterThanOrEqualTo(0, "delete branch must be present");

        // The UPDATE on dms."Document" inside the delete branch
        var stampUpdateIdx = _ddl.IndexOf(
            "UPDATE \"dms\".\"Document\"",
            deleteBranchIdx,
            StringComparison.Ordinal
        );
        stampUpdateIdx.Should().BeGreaterThanOrEqualTo(0, "content-stamp UPDATE must follow delete branch");

        var tombstoneIdx = _ddl.IndexOf(
            "INSERT INTO \"tracked_changes_edfi\".",
            stampUpdateIdx,
            StringComparison.Ordinal
        );
        tombstoneIdx
            .Should()
            .BeGreaterThan(stampUpdateIdx, "tombstone INSERT must appear after the content-stamp UPDATE");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_TrackedChange_Attached_Resource_Mssql
{
    private string _ddl = default!;

    [SetUp]
    public void Setup()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Mssql);
        _ddl = new RelationalModelDdlEmitter(dialect).Emit(
            TrackedChangeTriggerFixture.BuildAttachedResource(SqlDialect.Mssql)
        );
    }

    [Test]
    public void It_should_insert_a_tombstone_in_a_delete_only_branch()
    {
        _ddl.Should().Contain("IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)");
        _ddl.Should().Contain("INSERT INTO [tracked_changes_edfi].");
    }

    [Test]
    public void It_should_capture_identity_changed_docs_and_not_gate_on_update_function()
    {
        _ddl.Should().Contain("DECLARE @identityChangedDocs TABLE");
        _ddl.Should()
            .Contain("OUTPUT inserted.[DocumentId], inserted.[ContentVersion] INTO @identityChangedDocs");
        _ddl.Should().NotContain("UPDATE("); // AC: no UPDATE(col) gating on attached Resource triggers
    }

    [Test]
    public void It_should_place_tombstone_branch_after_mirror_update_block()
    {
        // The mirror-update guard block must appear before the tombstone DELETE-only branch.
        var mirrorUpdateIdx = _ddl.IndexOf("IF EXISTS (SELECT 1 FROM @stamped)", StringComparison.Ordinal);
        mirrorUpdateIdx.Should().BeGreaterThanOrEqualTo(0, "mirror-update guard must be present");

        var tombstoneBranchIdx = _ddl.IndexOf(
            "IF EXISTS (SELECT 1 FROM deleted) AND NOT EXISTS (SELECT 1 FROM inserted)",
            StringComparison.Ordinal
        );
        tombstoneBranchIdx
            .Should()
            .BeGreaterThan(mirrorUpdateIdx, "tombstone branch must appear after the mirror-update block");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_TrackedChange_ConcreteAbstract
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_tombstone_but_no_key_change(SqlDialect sqlDialect)
    {
        var dialect = SqlDialectFactory.Create(sqlDialect);
        var ddl = new RelationalModelDdlEmitter(dialect).Emit(
            TrackedChangeTriggerFixture.BuildAttachedConcreteAbstract(sqlDialect)
        );

        ddl.Should().Contain("tracked_changes_edfi");
        ddl.Should().NotContain("@identityChangedDocs");

        // The tombstone itself never emits New* columns, so their absence from the trigger
        // section proves no key-change INSERT was rendered. (The tracked-change CREATE TABLE
        // earlier in the script legitimately names New* columns.)
        var triggerSectionStart =
            sqlDialect == SqlDialect.Pgsql
                ? ddl.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
                : ddl.IndexOf("CREATE OR ALTER TRIGGER", StringComparison.Ordinal);
        triggerSectionStart.Should().BeGreaterThanOrEqualTo(0);
        var triggerSection = ddl[triggerSectionStart..];
        triggerSection.Should().NotContain("\"NewBeginDate\"").And.NotContain("[NewBeginDate]");
        triggerSection
            .Should()
            .Contain(
                sqlDialect == SqlDialect.Pgsql
                    ? "INSERT INTO \"tracked_changes_edfi\"."
                    : "INSERT INTO [tracked_changes_edfi]."
            );

        if (sqlDialect == SqlDialect.Mssql)
        {
            ddl.Should().Contain("UPDATE("); // existing IdentityVersion stamp shape preserved
        }
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_TrackedChange_Resource_Without_Identity_Columns
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_throw_on_emission(SqlDialect sqlDialect)
    {
        var dialect = SqlDialectFactory.Create(sqlDialect);

        var act = () =>
            new RelationalModelDdlEmitter(dialect).Emit(
                TrackedChangeTriggerFixture.BuildAttachedResourceWithEmptyIdentity(sqlDialect)
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*IdentityProjectionColumns*");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_Attachment_To_Unknown_Tracked_Table
{
    [Test]
    public void It_should_throw_on_emission()
    {
        var dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        var modelSet = TrackedChangeTriggerFixture.BuildAttachedResource(SqlDialect.Pgsql) with
        {
            TrackedChangeTablesInNameOrder = [],
        };

        var act = () => new RelationalModelDdlEmitter(dialect).Emit(modelSet);

        act.Should().Throw<InvalidOperationException>().WithMessage("*tracked-change table*");
    }
}

[TestFixture]
public class Given_RelationalModelDdlEmitter_With_NonRoot_Attachment
{
    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_throw_because_attachment_requires_root_trigger(SqlDialect sqlDialect)
    {
        var dialect = SqlDialectFactory.Create(sqlDialect);

        var act = () =>
            new RelationalModelDdlEmitter(dialect).Emit(
                TrackedChangeTriggerFixture.BuildNonRootAttachedResource(sqlDialect)
            );

        act.Should().Throw<InvalidOperationException>().WithMessage("*root*");
    }
}

/// <summary>
/// Builds <see cref="DerivedRelationalModelSet"/> instances whose single DocumentStamping trigger is
/// attached to a tracked-change table (<see cref="TriggerKindParameters.DocumentStamping.ChangeTracking"/>),
/// reusing the Grade-shaped source table and tracked-change inventory from
/// <see cref="TrackedChangeEmitterFixture"/>. Table models for the person-join targets
/// (edfi.StudentSectionAssociation, edfi.Student) are not required because the emitted SQL references
/// them by name only.
/// </summary>
internal static class TrackedChangeTriggerFixture
{
    internal static DerivedRelationalModelSet BuildAttachedResource(SqlDialect dialect)
    {
        return Build(
            dialect,
            TrackedChangeTableKind.Resource,
            [new DbColumnName("BeginDate"), new DbColumnName("SchoolId_Unified")]
        );
    }

    internal static DerivedRelationalModelSet BuildAttachedConcreteAbstract(SqlDialect dialect)
    {
        return Build(
            dialect,
            TrackedChangeTableKind.ConcreteAbstract,
            [new DbColumnName("BeginDate"), new DbColumnName("SchoolId_Unified")]
        );
    }

    internal static DerivedRelationalModelSet BuildAttachedResourceWithEmptyIdentity(SqlDialect dialect)
    {
        return Build(dialect, TrackedChangeTableKind.Resource, []);
    }

    /// <summary>
    /// Builds a model set whose stamping trigger has a <see cref="TrackedChangeAttachment"/> but
    /// whose <see cref="DbTriggerInfo.MirrorStampTargetTable"/> points to a parent table rather than
    /// the trigger's own table, making it non-root per
    /// <see cref="RelationalModelDdlEmitter"/>.<c>IsRootDocumentStampingTrigger</c>.
    /// Used to verify that <c>TryBuildTrackedChangePlan</c> rejects non-root attached triggers.
    /// </summary>
    internal static DerivedRelationalModelSet BuildNonRootAttachedResource(SqlDialect dialect)
    {
        return Build(
            dialect,
            TrackedChangeTableKind.Resource,
            [new DbColumnName("BeginDate"), new DbColumnName("SchoolId_Unified")],
            mirrorStampTargetTable: new DbTableName(TrackedChangeEmitterFixture.EdfiSchema, "CourseOffering")
        );
    }

    private static DerivedRelationalModelSet Build(
        SqlDialect dialect,
        TrackedChangeTableKind kind,
        IReadOnlyList<DbColumnName> identityProjectionColumns,
        DbTableName? mirrorStampTargetTable = null
    )
    {
        var schema = TrackedChangeEmitterFixture.EdfiSchema;
        var sourceTable = TrackedChangeEmitterFixture.BuildSourceTableModel();
        var trackedTable = TrackedChangeEmitterFixture.BuildTrackedTable() with { Kind = kind };
        var resource = new QualifiedResourceName("Ed-Fi", "Grade");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);

        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            sourceTable,
            [sourceTable],
            [],
            []
        );

        var trigger = new DbTriggerInfo(
            new DbTriggerName("TR_Grade_DocumentStamping"),
            sourceTable.Table,
            [new DbColumnName("DocumentId")],
            identityProjectionColumns,
            new TriggerKindParameters.DocumentStamping(new TrackedChangeAttachment(trackedTable.Table)),
            MirrorStampTargetTable: mirrorStampTargetTable ?? sourceTable.Table
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
            [trigger],
            TrackedChangeTablesInNameOrder: [trackedTable]
        );
    }
}
