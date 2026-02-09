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
    private static readonly DbSchemaName DmsSchema = new("dms");

    /// <summary>
    /// Generates the complete core <c>dms.*</c> DDL script for the configured dialect.
    /// </summary>
    /// <returns>
    /// A deterministic, canonicalized SQL string containing all core schema objects.
    /// </returns>
    public string Emit()
    {
        var writer = new SqlWriter(dialect);

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

        writer.AppendLine(dialect.CreateSchemaIfNotExists(DmsSchema));
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
        writer.AppendLine(dialect.CreateSequenceIfNotExists(DmsSchema, "ChangeVersionSequence"));
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
        // TODO: Phase 2 implementation
        writer.AppendLine("-- dms.Descriptor");
        writer.AppendLine();
    }

    private void EmitDocumentTable(SqlWriter writer)
    {
        // TODO: Phase 2 implementation
        writer.AppendLine("-- dms.Document");
        writer.AppendLine();
    }

    private void EmitDocumentCacheTable(SqlWriter writer)
    {
        // TODO: Phase 2 implementation
        writer.AppendLine("-- dms.DocumentCache");
        writer.AppendLine();
    }

    private void EmitDocumentChangeEventTable(SqlWriter writer)
    {
        // TODO: Phase 2 implementation
        writer.AppendLine("-- dms.DocumentChangeEvent");
        writer.AppendLine();
    }

    private void EmitEffectiveSchemaTable(SqlWriter writer)
    {
        // TODO: Phase 2 implementation
        writer.AppendLine("-- dms.EffectiveSchema");
        writer.AppendLine();
    }

    private void EmitReferentialIdentityTable(SqlWriter writer)
    {
        // TODO: Phase 2 implementation
        writer.AppendLine("-- dms.ReferentialIdentity");
        writer.AppendLine();
    }

    private void EmitResourceKeyTable(SqlWriter writer)
    {
        // TODO: Phase 2 implementation
        writer.AppendLine("-- dms.ResourceKey");
        writer.AppendLine();
    }

    private void EmitSchemaComponentTable(SqlWriter writer)
    {
        // TODO: Phase 2 implementation
        writer.AppendLine("-- dms.SchemaComponent");
        writer.AppendLine();
    }

    // ── Phase 4: Foreign Keys ───────────────────────────────────────────

    private void EmitForeignKeys(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 4: Foreign Keys");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // TODO: Phase 2 implementation
        // Ordered by (table name, constraint name).
    }

    // ── Phase 5: Indexes ────────────────────────────────────────────────

    private void EmitIndexes(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 5: Indexes");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // TODO: Phase 2 implementation
        // Explicit design-doc indexes + FK-support indexes.
        // Ordered by (table name, index name).
    }

    // ── Phase 6: Triggers ───────────────────────────────────────────────

    private void EmitTriggers(SqlWriter writer)
    {
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 6: Triggers");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        // TODO: Phase 2 implementation
        // Journaling trigger on dms.Document (PG and MSSQL variants).
        // Ordered by (table name, trigger name).
    }
}
