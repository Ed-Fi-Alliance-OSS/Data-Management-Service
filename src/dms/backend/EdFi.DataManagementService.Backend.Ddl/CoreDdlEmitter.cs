// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits deterministic DDL for the core <c>dms.*</c> schema objects.
/// <para>
/// This includes tables, constraints, indexes, sequences, and journaling
/// triggers required by the v1 object inventory defined in
/// <c>reference/design/backend-redesign/design-docs/ddl-generation.md</c>.
/// </para>
/// <para>
/// Emission follows a strict phased order to satisfy dependency requirements
/// and ensure deterministic, byte-for-byte stable output:
/// <list type="number">
/// <item>Schemas</item>
/// <item>Sequences</item>
/// <item>Tables (PK / UNIQUE / CHECK inline; no cross-table FKs)</item>
/// <item>Foreign keys (ALTER TABLE ADD CONSTRAINT)</item>
/// <item>Indexes</item>
/// <item>Triggers</item>
/// </list>
/// </para>
/// </summary>
public sealed class CoreDdlEmitter(ISqlDialect dialect)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

    private static readonly DbSchemaName _dmsSchema = new("dms");
    private static readonly DbTableName _descriptorTable = new(_dmsSchema, "Descriptor");
    private static readonly DbTableName _documentTable = new(_dmsSchema, "Document");
    private static readonly DbTableName _documentCacheTable = new(_dmsSchema, "DocumentCache");
    private static readonly DbTableName _documentChangeEventTable = new(_dmsSchema, "DocumentChangeEvent");
    private static readonly DbTableName _effectiveSchemaTable = new(_dmsSchema, "EffectiveSchema");
    private static readonly DbTableName _referentialIdentityTable = new(_dmsSchema, "ReferentialIdentity");
    private static readonly DbTableName _resourceKeyTable = new(_dmsSchema, "ResourceKey");
    private static readonly DbTableName _schemaComponentTable = new(_dmsSchema, "SchemaComponent");

    private static DbColumnName Col(string name) => new(name);

    private string StringType(int maxLength) =>
        $"{_dialect.Rules.ScalarTypeDefaults.StringType}({maxLength})";

    private string DateTimeType => _dialect.Rules.ScalarTypeDefaults.DateTimeType;

    private string BooleanType => _dialect.Rules.ScalarTypeDefaults.BooleanType;

    private string SequenceDefault =>
        _dialect.RenderSequenceDefaultExpression(_dmsSchema, "ChangeVersionSequence");

    /// <summary>
    /// Generates the complete core <c>dms.*</c> DDL script for the configured dialect.
    /// </summary>
    /// <returns>
    /// A deterministic, canonicalized SQL string containing all core schema objects.
    /// </returns>
    public string Emit()
    {
        var writer = new SqlWriter(_dialect);

        EmitSchemas(writer);
        EmitSequences(writer);
        EmitTables(writer);
        EmitForeignKeys(writer);
        EmitIndexes(writer);
        EmitTriggers(writer);

        return writer.ToString();
    }

    // ── Phase 1: Schemas ────────────────────────────────────────────────

    private void EmitSchemas(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 1: Schemas");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        writer.AppendLine(_dialect.CreateSchemaIfNotExists(_dmsSchema));
        writer.AppendLine();
    }

    // ── Phase 2: Sequences ──────────────────────────────────────────────

    private void EmitSequences(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 2: Sequences");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        EmitChangeVersionSequence(writer);
    }

    private void EmitChangeVersionSequence(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateSequenceIfNotExists(_dmsSchema, "ChangeVersionSequence"));
        writer.AppendLine();
    }

    // ── Phase 3: Tables ─────────────────────────────────────────────────

    private void EmitTables(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 3: Tables (PK/UNIQUE/CHECK only, no cross-table FKs)");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // Alphabetical order by table name within the dms schema.
        EmitDescriptorTable(writer);
        EmitDocumentTable(writer);
        EmitDocumentCacheTable(writer);
        EmitDocumentChangeEventTable(writer);
        EmitEffectiveSchemaTable(writer);
        EmitReferentialIdentityTable(writer);
        EmitResourceKeyTable(writer);
        EmitSchemaComponentTable(writer);
    }

    private void EmitDescriptorTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_descriptorTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.DocumentIdColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("Namespace"), StringType(255), false)},"
            );
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("CodeValue"), StringType(50), false)},");
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ShortDescription"), StringType(75), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("Description"), StringType(1024), true)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("Discriminator"), StringType(128), false)},"
            );
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("Uri"), StringType(306), false)},");
            writer.AppendLine(_dialect.RenderNamedPrimaryKeyClause("PK_Descriptor", [Col("DocumentId")]));
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddUniqueConstraint(
                _descriptorTable,
                "UX_Descriptor_Uri_Discriminator",
                [Col("Uri"), Col("Discriminator")]
            )
        );
        writer.AppendLine();
    }

    private void EmitDocumentTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_documentTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.IdentityBigintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentUuid"), _dialect.UuidColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("ContentVersion"), "bigint", false, "DF_Document_ContentVersion", SequenceDefault)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("IdentityVersion"), "bigint", false, "DF_Document_IdentityVersion", SequenceDefault)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("ContentLastModifiedAt"), DateTimeType, false, "DF_Document_ContentLastModifiedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("IdentityLastModifiedAt"), DateTimeType, false, "DF_Document_IdentityLastModifiedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("CreatedAt"), DateTimeType, false, "DF_Document_CreatedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(_dialect.RenderNamedPrimaryKeyClause("PK_Document", [Col("DocumentId")]));
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddUniqueConstraint(_documentTable, "UX_Document_DocumentUuid", [Col("DocumentUuid")])
        );
        writer.AppendLine();
    }

    private void EmitDocumentCacheTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_documentCacheTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.DocumentIdColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentUuid"), _dialect.UuidColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceVersion"), StringType(32), false)},"
            );
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("Etag"), StringType(64), false)},");
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("LastModifiedAt"), DateTimeType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentJson"), _dialect.JsonColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("ComputedAt"), DateTimeType, false, "DF_DocumentCache_ComputedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(_dialect.RenderNamedPrimaryKeyClause("PK_DocumentCache", [Col("DocumentId")]));
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddUniqueConstraint(
                _documentCacheTable,
                "UX_DocumentCache_DocumentUuid",
                [Col("DocumentUuid")]
            )
        );
        writer.AppendLine();

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine(
                _dialect.AddCheckConstraint(
                    _documentCacheTable,
                    "CK_DocumentCache_JsonObject",
                    $"jsonb_typeof({_dialect.QuoteIdentifier("DocumentJson")}) = 'object'"
                )
            );
        }
        else
        {
            writer.AppendLine(
                _dialect.AddCheckConstraint(
                    _documentCacheTable,
                    "CK_DocumentCache_IsJsonObject",
                    $"ISJSON({_dialect.QuoteIdentifier("DocumentJson")}) = 1 AND LEFT(LTRIM({_dialect.QuoteIdentifier("DocumentJson")}), 1) = '{{'"
                )
            );
        }
        writer.AppendLine();
    }

    private void EmitDocumentChangeEventTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_documentChangeEventTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine($"{_dialect.RenderColumnDefinition(Col("ChangeVersion"), "bigint", false)},");
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.DocumentIdColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("CreatedAt"), DateTimeType, false, "DF_DocumentChangeEvent_CreatedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(
                    "PK_DocumentChangeEvent",
                    [Col("ChangeVersion"), Col("DocumentId")]
                )
            );
        }
        writer.AppendLine(");");
        writer.AppendLine();
    }

    private void EmitEffectiveSchemaTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_effectiveSchemaTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("EffectiveSchemaSingletonId"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ApiSchemaFormatVersion"), StringType(64), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("EffectiveSchemaHash"), StringType(64), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyCount"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeySeedHash"), _dialect.RenderBinaryColumnType(32), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinitionWithNamedDefault(Col("AppliedAt"), DateTimeType, false, "DF_EffectiveSchema_AppliedAt", _dialect.CurrentTimestampDefaultExpression)},"
            );
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(
                    "PK_EffectiveSchema",
                    [Col("EffectiveSchemaSingletonId")]
                )
            );
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddCheckConstraint(
                _effectiveSchemaTable,
                "CK_EffectiveSchema_Singleton",
                $"{_dialect.QuoteIdentifier("EffectiveSchemaSingletonId")} = 1"
            )
        );
        writer.AppendLine();

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine(
                _dialect.AddCheckConstraint(
                    _effectiveSchemaTable,
                    "CK_EffectiveSchema_ResourceKeySeedHash_Length",
                    $"octet_length({_dialect.QuoteIdentifier("ResourceKeySeedHash")}) = 32"
                )
            );
            writer.AppendLine();
        }

        writer.AppendLine(
            _dialect.AddUniqueConstraint(
                _effectiveSchemaTable,
                "UX_EffectiveSchema_EffectiveSchemaHash",
                [Col("EffectiveSchemaHash")]
            )
        );
        writer.AppendLine();
    }

    private void EmitReferentialIdentityTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_referentialIdentityTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ReferentialId"), _dialect.UuidColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("DocumentId"), _dialect.DocumentIdColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );

            if (_dialect.Rules.Dialect == SqlDialect.Mssql)
            {
                // MSSQL: PK NONCLUSTERED + inline UNIQUE CLUSTERED
                writer.AppendLine(
                    _dialect.RenderNamedPrimaryKeyClause(
                        "PK_ReferentialIdentity",
                        [Col("ReferentialId")],
                        clustered: false
                    ) + ","
                );
                var clusteredCols = string.Join(
                    ", ",
                    new[] { Col("DocumentId"), Col("ResourceKeyId") }.Select(c =>
                        _dialect.QuoteIdentifier(c.Value)
                    )
                );
                writer.AppendLine(
                    $"CONSTRAINT {_dialect.QuoteIdentifier("UX_ReferentialIdentity_DocumentId_ResourceKeyId")} UNIQUE CLUSTERED ({clusteredCols})"
                );
            }
            else
            {
                writer.AppendLine(
                    _dialect.RenderNamedPrimaryKeyClause("PK_ReferentialIdentity", [Col("ReferentialId")])
                );
            }
        }
        writer.AppendLine(");");
        writer.AppendLine();
    }

    private void EmitResourceKeyTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_resourceKeyTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceKeyId"), _dialect.SmallintColumnType, false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ResourceVersion"), StringType(32), false)},"
            );
            writer.AppendLine(_dialect.RenderNamedPrimaryKeyClause("PK_ResourceKey", [Col("ResourceKeyId")]));
        }
        writer.AppendLine(");");
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddUniqueConstraint(
                _resourceKeyTable,
                "UX_ResourceKey_ProjectName_ResourceName",
                [Col("ProjectName"), Col("ResourceName")]
            )
        );
        writer.AppendLine();
    }

    private void EmitSchemaComponentTable(SqlWriter writer)
    {
        writer.AppendLine(_dialect.CreateTableHeader(_schemaComponentTable));
        writer.AppendLine("(");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("EffectiveSchemaHash"), StringType(64), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectEndpointName"), StringType(128), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectName"), StringType(256), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("ProjectVersion"), StringType(32), false)},"
            );
            writer.AppendLine(
                $"{_dialect.RenderColumnDefinition(Col("IsExtensionProject"), BooleanType, false)},"
            );
            writer.AppendLine(
                _dialect.RenderNamedPrimaryKeyClause(
                    "PK_SchemaComponent",
                    [Col("EffectiveSchemaHash"), Col("ProjectEndpointName")]
                )
            );
        }
        writer.AppendLine(");");
        writer.AppendLine();
    }

    // ── Phase 4: Foreign Keys ───────────────────────────────────────────

    private void EmitForeignKeys(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 4: Foreign Keys");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // Ordered by (table name, constraint name).

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _descriptorTable,
                "FK_Descriptor_Document",
                [Col("DocumentId")],
                _documentTable,
                [Col("DocumentId")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _documentTable,
                "FK_Document_ResourceKey",
                [Col("ResourceKeyId")],
                _resourceKeyTable,
                [Col("ResourceKeyId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _documentCacheTable,
                "FK_DocumentCache_Document",
                [Col("DocumentId")],
                _documentTable,
                [Col("DocumentId")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _documentChangeEventTable,
                "FK_DocumentChangeEvent_Document",
                [Col("DocumentId")],
                _documentTable,
                [Col("DocumentId")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _documentChangeEventTable,
                "FK_DocumentChangeEvent_ResourceKey",
                [Col("ResourceKeyId")],
                _resourceKeyTable,
                [Col("ResourceKeyId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _referentialIdentityTable,
                "FK_ReferentialIdentity_Document",
                [Col("DocumentId")],
                _documentTable,
                [Col("DocumentId")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _referentialIdentityTable,
                "FK_ReferentialIdentity_ResourceKey",
                [Col("ResourceKeyId")],
                _resourceKeyTable,
                [Col("ResourceKeyId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.AddForeignKeyConstraint(
                _schemaComponentTable,
                "FK_SchemaComponent_EffectiveSchemaHash",
                [Col("EffectiveSchemaHash")],
                _effectiveSchemaTable,
                [Col("EffectiveSchemaHash")],
                onDelete: ReferentialAction.Cascade
            )
        );
        writer.AppendLine();
    }

    // ── Phase 5: Indexes ────────────────────────────────────────────────

    private void EmitIndexes(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 5: Indexes");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // Ordered by (table name, index name).

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _descriptorTable,
                "IX_Descriptor_Uri_Discriminator",
                [Col("Uri"), Col("Discriminator")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentTable,
                "IX_Document_ResourceKeyId_DocumentId",
                [Col("ResourceKeyId"), Col("DocumentId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentCacheTable,
                "IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt",
                [Col("ProjectName"), Col("ResourceName"), Col("LastModifiedAt"), Col("DocumentId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentChangeEventTable,
                "IX_DocumentChangeEvent_DocumentId",
                [Col("DocumentId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _documentChangeEventTable,
                "IX_DocumentChangeEvent_ResourceKeyId_ChangeVersion",
                [Col("ResourceKeyId"), Col("ChangeVersion"), Col("DocumentId")]
            )
        );
        writer.AppendLine();

        writer.AppendLine(
            _dialect.CreateIndexIfNotExists(
                _referentialIdentityTable,
                "IX_ReferentialIdentity_DocumentId",
                [Col("DocumentId")]
            )
        );
        writer.AppendLine();
    }

    // ── Phase 6: Triggers ───────────────────────────────────────────────

    private void EmitTriggers(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 6: Triggers");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            EmitPgsqlJournalingTrigger(writer);
        }
        else
        {
            EmitMssqlJournalingTrigger(writer);
        }
    }

    private void EmitPgsqlJournalingTrigger(SqlWriter writer)
    {
        string Q(string id) => _dialect.QuoteIdentifier(id);

        var docTable = _dialect.QualifyTable(_documentTable);
        var changeTable = _dialect.QualifyTable(_documentChangeEventTable);
        var funcName = $"{Q(_dmsSchema.Value)}.{Q("TF_Document_Journal")}";

        // Trigger function
        writer.AppendLine($"CREATE OR REPLACE FUNCTION {funcName}()");
        writer.AppendLine("RETURNS TRIGGER AS $$");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine(
                $"INSERT INTO {changeTable} ({Q("ChangeVersion")}, {Q("DocumentId")}, {Q("ResourceKeyId")}, {Q("CreatedAt")})"
            );
            writer.AppendLine(
                $"SELECT d.{Q("ContentVersion")}, d.{Q("DocumentId")}, d.{Q("ResourceKeyId")}, now()"
            );
            writer.AppendLine("FROM (");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"SELECT DISTINCT ON ({Q("DocumentId")}) {Q("ContentVersion")}, {Q("DocumentId")}, {Q("ResourceKeyId")}"
                );
                writer.AppendLine("FROM new_table");
            }
            writer.AppendLine(") d;");
            writer.AppendLine("RETURN NULL;");
        }
        writer.AppendLine("END;");
        writer.AppendLine("$$ LANGUAGE plpgsql;");
        writer.AppendLine();

        // Drop and recreate trigger
        writer.AppendLine(_dialect.DropTriggerIfExists(_documentTable, "TR_Document_Journal"));
        writer.AppendLine($"CREATE TRIGGER {Q("TR_Document_Journal")}");
        using (writer.Indent())
        {
            writer.AppendLine($"AFTER INSERT OR UPDATE OF {Q("ContentVersion")} ON {docTable}");
            writer.AppendLine("REFERENCING NEW TABLE AS new_table");
            writer.AppendLine("FOR EACH STATEMENT");
            writer.AppendLine($"EXECUTE FUNCTION {funcName}();");
        }
        writer.AppendLine();
    }

    private void EmitMssqlJournalingTrigger(SqlWriter writer)
    {
        string Q(string id) => _dialect.QuoteIdentifier(id);

        var docTable = _dialect.QualifyTable(_documentTable);
        var changeTable = _dialect.QualifyTable(_documentChangeEventTable);
        var triggerName = $"{Q(_dmsSchema.Value)}.{Q("TR_Document_Journal")}";

        writer.AppendLine($"CREATE OR ALTER TRIGGER {triggerName}");
        writer.AppendLine($"ON {docTable}");
        writer.AppendLine("AFTER INSERT, UPDATE");
        writer.AppendLine("AS");
        writer.AppendLine("BEGIN");
        using (writer.Indent())
        {
            writer.AppendLine("SET NOCOUNT ON;");
            writer.AppendLine($"IF UPDATE({Q("ContentVersion")}) OR NOT EXISTS (SELECT 1 FROM deleted)");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"INSERT INTO {changeTable} ({Q("ChangeVersion")}, {Q("DocumentId")}, {Q("ResourceKeyId")}, {Q("CreatedAt")})"
                );
                writer.AppendLine(
                    $"SELECT i.{Q("ContentVersion")}, i.{Q("DocumentId")}, i.{Q("ResourceKeyId")}, sysutcdatetime()"
                );
                writer.AppendLine("FROM inserted i;");
            }
            writer.AppendLine("END");
        }
        writer.AppendLine("END;");
        writer.AppendLine();
    }
}
