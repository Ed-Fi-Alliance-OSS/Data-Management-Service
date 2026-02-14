// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_Pgsql_Ddl_Emitter_With_Primary_Key_Constraint_Name
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new PgsqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = PrimaryKeyFixture.Build(dialectRules.Dialect, "PK_School");

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
        var dialectRules = new MssqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = PrimaryKeyFixture.Build(dialectRules.Dialect, "PK_School");

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_named_primary_key_constraint()
    {
        _sql.Should().Contain("CONSTRAINT [PK_School] PRIMARY KEY ([DocumentId])");
    }
}

[TestFixture]
public class Given_Pgsql_Ddl_Emitter_With_Trigger_Inventory
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new PgsqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = TriggerFixture.Build(dialectRules.Dialect);

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_triggers_using_TriggerTable_for_all_kinds()
    {
        _sql.Should().Contain("CREATE TRIGGER \"TR_School_Stamp\" ON \"edfi\".\"School\" WHEN (");
        _sql.Should()
            .Contain("CREATE TRIGGER \"TR_Student_ReferentialIdentity\" ON \"edfi\".\"Student\" WHEN (");
        _sql.Should().Contain("CREATE TRIGGER \"TR_School_AbstractIdentity\" ON \"edfi\".\"School\" WHEN (");
        _sql.Should()
            .Contain(
                "CREATE TRIGGER \"TR_EducationOrganizationIdentity_PropagateIdentity\" ON \"edfi\".\"EducationOrganizationIdentity\" EXECUTE FUNCTION \"noop\"();"
            );
    }

    [Test]
    public void It_should_not_use_maintenance_target_table_as_trigger_owner()
    {
        _sql.Should()
            .NotContain(
                "CREATE TRIGGER \"TR_School_AbstractIdentity\" ON \"dms\".\"EducationOrganizationIdentity\""
            );
    }

    [Test]
    public void It_should_emit_null_safe_identity_value_diff_predicates()
    {
        _sql.Should().Contain("(OLD.\"SchoolId\") IS DISTINCT FROM (NEW.\"SchoolId\")");
        _sql.Should().Contain("(OLD.\"StudentUniqueId\") IS DISTINCT FROM (NEW.\"StudentUniqueId\")");
    }
}

[TestFixture]
public class Given_Mssql_Ddl_Emitter_With_Trigger_Inventory
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new MssqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = TriggerFixture.Build(dialectRules.Dialect);

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_emit_schema_qualified_trigger_names_and_use_TriggerTable_for_all_kinds()
    {
        _sql.Should().Contain("CREATE TRIGGER [edfi].[TR_School_Stamp] ON [edfi].[School] AS");
        _sql.Should()
            .Contain("CREATE TRIGGER [edfi].[TR_Student_ReferentialIdentity] ON [edfi].[Student] AS");
        _sql.Should().Contain("CREATE TRIGGER [edfi].[TR_School_AbstractIdentity] ON [edfi].[School] AS");
        _sql.Should()
            .Contain(
                "CREATE TRIGGER [edfi].[TR_EducationOrganizationIdentity_PropagateIdentity] ON [edfi].[EducationOrganizationIdentity] AS"
            );
    }

    [Test]
    public void It_should_not_use_maintenance_target_table_as_trigger_owner()
    {
        _sql.Should()
            .NotContain(
                "CREATE TRIGGER [edfi].[TR_School_AbstractIdentity] ON [dms].[EducationOrganizationIdentity]"
            );
    }

    [Test]
    public void It_should_emit_null_safe_identity_value_diff_predicates()
    {
        _sql.Should().Contain("FULL OUTER JOIN deleted d");
        _sql.Should().Contain("(d.[SchoolId] <> i.[SchoolId])");
        _sql.Should().Contain("(d.[StudentUniqueId] <> i.[StudentUniqueId])");
    }
}

[TestFixture]
public class Given_Mssql_Ddl_Emitter_With_Presence_Gated_Unified_Alias_Trigger_Columns
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new MssqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = UnifiedAliasTriggerFixture.Build(dialectRules.Dialect);

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_compare_presence_gated_canonical_values_not_alias_columns()
    {
        _sql.Should()
            .Contain("CASE WHEN d.[School_DocumentId] IS NULL THEN NULL ELSE d.[SchoolId_Unified] END");
        _sql.Should()
            .Contain("CASE WHEN i.[School_DocumentId] IS NULL THEN NULL ELSE i.[SchoolId_Unified] END");
        _sql.Should().NotContain("(d.[SchoolId] <> i.[SchoolId])");
        _sql.Should().NotContain("UPDATE([SchoolId])");
    }
}

[TestFixture]
public class Given_Pgsql_Ddl_Emitter_With_Presence_Gated_Unified_Alias_Trigger_Columns
{
    private string _sql = default!;

    [SetUp]
    public void Setup()
    {
        var dialectRules = new PgsqlDialectRules();
        var emitter = new RelationalModelDdlEmitter(dialectRules);
        var modelSet = UnifiedAliasTriggerFixture.Build(dialectRules.Dialect);

        _sql = emitter.Emit(modelSet);
    }

    [Test]
    public void It_should_compare_presence_gated_canonical_values_not_alias_columns()
    {
        _sql.Should()
            .Contain(
                "CASE WHEN OLD.\"School_DocumentId\" IS NULL THEN NULL ELSE OLD.\"SchoolId_Unified\" END"
            );
        _sql.Should()
            .Contain(
                "CASE WHEN NEW.\"School_DocumentId\" IS NULL THEN NULL ELSE NEW.\"SchoolId_Unified\" END"
            );
        _sql.Should().Contain("IS DISTINCT FROM");
        _sql.Should().NotContain("(OLD.\"SchoolId\") IS DISTINCT FROM (NEW.\"SchoolId\")");
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
            new JsonPathExpression("$", Array.Empty<JsonPathSegment>()),
            new TableKey(primaryKeyName, [keyColumn]),
            new[]
            {
                new DbColumnModel(
                    columnName,
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            },
            Array.Empty<TableConstraint>()
        );
        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            table,
            [table],
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
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
            Array.Empty<AbstractIdentityTableInfo>(),
            Array.Empty<AbstractUnionViewInfo>(),
            Array.Empty<DbIndexInfo>(),
            Array.Empty<DbTriggerInfo>()
        );
    }
}

internal static class TriggerFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var schoolTable = BuildRootTable(schema, "School", "SchoolId");
        var studentTable = BuildRootTable(schema, "Student", "StudentUniqueId");
        var schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        var studentResource = new QualifiedResourceName("Ed-Fi", "Student");
        var schoolResourceKey = new ResourceKeyEntry(1, schoolResource, "1.0.0", false);
        var studentResourceKey = new ResourceKeyEntry(2, studentResource, "1.0.0", false);
        var maintenanceTargetTable = new DbTableName(
            new DbSchemaName("dms"),
            "EducationOrganizationIdentity"
        );
        var propagationTriggerTable = new DbTableName(schema, "EducationOrganizationIdentity");

        return new DerivedRelationalModelSet(
            new EffectiveSchemaInfo(
                "1.0.0",
                "1.0.0",
                "hash",
                2,
                [0x01, 0x02],
                [
                    new SchemaComponentInfo(
                        "ed-fi",
                        "Ed-Fi",
                        "1.0.0",
                        false,
                        "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
                    ),
                ],
                [schoolResourceKey, studentResourceKey]
            ),
            dialect,
            [new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, schema)],
            [
                BuildResourceModel(schoolResourceKey, schoolResource, schema, schoolTable),
                BuildResourceModel(studentResourceKey, studentResource, schema, studentTable),
            ],
            Array.Empty<AbstractIdentityTableInfo>(),
            Array.Empty<AbstractUnionViewInfo>(),
            Array.Empty<DbIndexInfo>(),
            [
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_Stamp"),
                    schoolTable.Table,
                    DbTriggerKind.DocumentStamping,
                    [new DbColumnName("DocumentId")],
                    [new DbColumnName("SchoolId")]
                ),
                new DbTriggerInfo(
                    new DbTriggerName("TR_Student_ReferentialIdentity"),
                    studentTable.Table,
                    DbTriggerKind.ReferentialIdentityMaintenance,
                    [new DbColumnName("DocumentId")],
                    [new DbColumnName("StudentUniqueId")]
                ),
                new DbTriggerInfo(
                    new DbTriggerName("TR_School_AbstractIdentity"),
                    schoolTable.Table,
                    DbTriggerKind.AbstractIdentityMaintenance,
                    [new DbColumnName("DocumentId")],
                    [new DbColumnName("SchoolId")],
                    MaintenanceTargetTable: maintenanceTargetTable
                ),
                new DbTriggerInfo(
                    new DbTriggerName("TR_EducationOrganizationIdentity_PropagateIdentity"),
                    propagationTriggerTable,
                    DbTriggerKind.IdentityPropagationFallback,
                    [],
                    [],
                    MaintenanceTargetTable: null,
                    PropagationFallback: new DbIdentityPropagationFallbackInfo([
                        new DbIdentityPropagationReferrerAction(
                            schoolTable.Table,
                            new DbColumnName("EducationOrganizationReference_DocumentId"),
                            new DbColumnName("DocumentId"),
                            [
                                new DbIdentityPropagationColumnPair(
                                    new DbColumnName(
                                        "EducationOrganizationReference_EducationOrganizationId"
                                    ),
                                    new DbColumnName("EducationOrganizationId")
                                ),
                            ]
                        ),
                    ])
                ),
            ]
        );
    }

    private static ConcreteResourceModel BuildResourceModel(
        ResourceKeyEntry resourceKey,
        QualifiedResourceName resource,
        DbSchemaName schema,
        DbTableModel rootTable
    )
    {
        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
        );

        return new ConcreteResourceModel(resourceKey, ResourceStorageKind.RelationalTables, relationalModel);
    }

    private static DbTableModel BuildRootTable(
        DbSchemaName schema,
        string tableName,
        string identityColumnName
    )
    {
        var table = new DbTableName(schema, tableName);
        var documentIdColumn = new DbColumnName("DocumentId");
        var identityColumn = new DbColumnName(identityColumnName);

        return new DbTableModel(
            table,
            new JsonPathExpression("$", Array.Empty<JsonPathSegment>()),
            new TableKey($"PK_{tableName}", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
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
                    identityColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Array.Empty<TableConstraint>()
        );
    }
}

internal static class UnifiedAliasTriggerFixture
{
    internal static DerivedRelationalModelSet Build(SqlDialect dialect)
    {
        var schema = new DbSchemaName("edfi");
        var resource = new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation");
        var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);
        var rootTable = BuildRootTable(schema);
        var relationalModel = new RelationalResourceModel(
            resource,
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            [rootTable],
            Array.Empty<DocumentReferenceBinding>(),
            Array.Empty<DescriptorEdgeSource>()
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
            Array.Empty<AbstractIdentityTableInfo>(),
            Array.Empty<AbstractUnionViewInfo>(),
            Array.Empty<DbIndexInfo>(),
            [
                new DbTriggerInfo(
                    new DbTriggerName("TR_StudentSchoolAssociation_Stamp"),
                    rootTable.Table,
                    DbTriggerKind.DocumentStamping,
                    [new DbColumnName("DocumentId")],
                    [new DbColumnName("SchoolId")]
                ),
            ]
        );
    }

    private static DbTableModel BuildRootTable(DbSchemaName schema)
    {
        var table = new DbTableName(schema, "StudentSchoolAssociation");
        var documentIdColumn = new DbColumnName("DocumentId");
        var schoolDocumentIdColumn = new DbColumnName("School_DocumentId");
        var canonicalColumn = new DbColumnName("SchoolId_Unified");
        var aliasColumn = new DbColumnName("SchoolId");

        return new DbTableModel(
            table,
            new JsonPathExpression("$", Array.Empty<JsonPathSegment>()),
            new TableKey(
                "PK_StudentSchoolAssociation",
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
                    schoolDocumentIdColumn,
                    ColumnKind.DocumentFk,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    canonicalColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    aliasColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: null,
                    TargetResource: null
                )
                {
                    Storage = new ColumnStorage.UnifiedAlias(canonicalColumn, schoolDocumentIdColumn),
                },
            ],
            Array.Empty<TableConstraint>()
        );
    }
}
