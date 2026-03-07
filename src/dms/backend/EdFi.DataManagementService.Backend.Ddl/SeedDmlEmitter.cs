// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Ddl;

/// <summary>
/// Emits deterministic seed DML for the core <c>dms.*</c> tables.
/// <para>
/// This includes insert-if-missing statements and inline validation for
/// <c>dms.ResourceKey</c>, <c>dms.EffectiveSchema</c>, and <c>dms.SchemaComponent</c>.
/// </para>
/// </summary>
/// <remarks>
/// The in-SQL validation emitted by <c>EmitResourceKeyValidation</c> and
/// <c>EmitSchemaComponentValidation</c> is the defense-in-depth counterpart of
/// the C# preflight validation in
/// <see cref="EdFi.DataManagementService.SchemaTools.Provisioning.SeedValidator"/>.
/// Both validate the same seed tables using the same columns:
/// <list type="bullet">
///   <item>ResourceKey: ResourceKeyId, ProjectName, ResourceName, ResourceVersion</item>
///   <item>SchemaComponent: ProjectEndpointName, ProjectName, ProjectVersion, IsExtensionProject</item>
/// </list>
/// The in-SQL path runs inside the DDL transaction; SeedValidator runs before it as a fail-fast.
/// <para>The in-SQL content mismatch messages include sampled key IDs/names and direct
/// users to the <c>ddl provision</c> CLI command for detailed row-level diffs provided
/// by <see cref="EdFi.DataManagementService.SchemaTools.Provisioning.SeedValidator"/>.</para>
/// <para><b>Important:</b> Changes to validation columns or comparison logic must be
/// reflected in both locations to keep the dual-path strategy consistent.</para>
/// </remarks>
public sealed class SeedDmlEmitter(ISqlDialect dialect)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

    /// <summary>
    /// SQL Server limits VALUES table constructors to 1000 rows.
    /// Chunks are capped at 999 to stay safely under the limit.
    /// </summary>
    private const int MaxValuesRows = 999;

    private static readonly DbTableName _resourceKeyTable = DmsTableNames.ResourceKey;
    private static readonly DbTableName _effectiveSchemaTable = EffectiveSchemaTableDefinition.Table;
    private static readonly DbColumnName _effectiveSchemaSingletonIdColumn =
        EffectiveSchemaTableDefinition.EffectiveSchemaSingletonId;
    private static readonly DbColumnName _apiSchemaFormatVersionColumn =
        EffectiveSchemaTableDefinition.ApiSchemaFormatVersion;
    private static readonly DbColumnName _effectiveSchemaHashColumn =
        EffectiveSchemaTableDefinition.EffectiveSchemaHash;
    private static readonly DbColumnName _resourceKeyCountColumn =
        EffectiveSchemaTableDefinition.ResourceKeyCount;
    private static readonly DbColumnName _resourceKeySeedHashColumn =
        EffectiveSchemaTableDefinition.ResourceKeySeedHash;
    private static readonly DbTableName _schemaComponentTable = DmsTableNames.SchemaComponent;

    /// <summary>
    /// Emits only the preflight hash-check SQL (Phase 0), without the Phase 7 seed DML.
    /// Used by <see cref="FullDdlEmitter"/> to place the preflight before core DDL.
    /// </summary>
    public string EmitPreflightOnly(string effectiveSchemaHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveSchemaHash);

        var writer = new SqlWriter(_dialect);
        EmitEffectiveSchemaHashPreflight(writer, effectiveSchemaHash);
        return writer.ToString();
    }

    /// <summary>
    /// Generates the seed DML script (Phase 7) for the configured dialect.
    /// </summary>
    /// <param name="effectiveSchema">
    /// The effective schema info providing resource keys, schema components,
    /// and fingerprint data for deterministic seed DML emission.
    /// </param>
    /// <returns>
    /// A deterministic, canonicalized SQL string containing seed data
    /// (insert-if-missing + validation).
    /// </returns>
    public string Emit(EffectiveSchemaInfo effectiveSchema)
    {
        ArgumentNullException.ThrowIfNull(effectiveSchema);

        var effectiveSchemaIssues = EffectiveSchemaFingerprintContract
            .GetExpectedValidationIssues(effectiveSchema)
            .Select(issue => $"Expected provisioning metadata invalid: {issue}")
            .ToList();

        if (effectiveSchemaIssues.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot emit seed DML because dms.EffectiveSchema metadata is invalid: {string.Join("; ", effectiveSchemaIssues)}"
            );
        }

        var writer = new SqlWriter(_dialect);

        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 7: Seed Data (insert-if-missing + validation)");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        EmitResourceKeySeeds(writer, effectiveSchema.ResourceKeysInIdOrder);
        EmitResourceKeyValidation(writer, effectiveSchema.ResourceKeysInIdOrder);
        EmitEffectiveSchemaInsert(writer, effectiveSchema);
        EmitEffectiveSchemaValidation(writer, effectiveSchema);
        EmitSchemaComponentSeeds(
            writer,
            effectiveSchema.EffectiveSchemaHash,
            effectiveSchema.SchemaComponentsInEndpointOrder
        );
        EmitSchemaComponentValidation(
            writer,
            effectiveSchema.EffectiveSchemaHash,
            effectiveSchema.SchemaComponentsInEndpointOrder
        );

        return writer.ToString();
    }

    /// <summary>
    /// Emits a preflight check that fails fast when the database is provisioned for a different effective schema hash.
    /// </summary>
    private void EmitEffectiveSchemaHashPreflight(SqlWriter writer, string effectiveSchemaHash)
    {
        var table = _dialect.QualifyTable(_effectiveSchemaTable);
        var hashLiteral = _dialect.RenderStringLiteral(effectiveSchemaHash);
        var tableObjectIdLiteral = RenderTableObjectIdLiteral(_effectiveSchemaTable);

        writer.AppendLine("-- Preflight: fail fast if database is provisioned for a different schema hash");

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            // Preflight runs before core DDL; on a fresh database, the EffectiveSchema table may not exist yet.
            var regclassLiteral = _dialect.QualifyTable(_effectiveSchemaTable).Replace("'", "''");

            writer.AppendLine("DO $$");
            writer.AppendLine("DECLARE");
            using (writer.Indent())
            {
                writer.AppendLine("_stored_hash text;");
            }
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine($"IF to_regclass('{regclassLiteral}') IS NOT NULL THEN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"SELECT {Quote(_effectiveSchemaHashColumn)} INTO _stored_hash FROM {table}"
                    );
                    writer.AppendLine($"WHERE {Quote(_effectiveSchemaSingletonIdColumn)} = 1;");
                    writer.AppendLine($"IF _stored_hash IS NOT NULL AND _stored_hash <> {hashLiteral} THEN");
                    using (writer.Indent())
                    {
                        writer.AppendLine(
                            $"RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database has ''%'' but expected ''%''', _stored_hash, {hashLiteral};"
                        );
                    }
                    writer.AppendLine("END IF;");
                }
                writer.AppendLine("END IF;");
            }
            writer.AppendLine("END $$;");
        }
        else
        {
            writer.AppendLine("DECLARE @preflight_stored_hash nvarchar(200);");
            writer.AppendLine();
            writer.AppendLine($"IF OBJECT_ID(N'{tableObjectIdLiteral}', N'U') IS NOT NULL");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"SELECT @preflight_stored_hash = {Quote(_effectiveSchemaHashColumn)} FROM {table}"
                );
                writer.AppendLine($"WHERE {Quote(_effectiveSchemaSingletonIdColumn)} = 1;");
                writer.AppendLine(
                    $"IF @preflight_stored_hash IS NOT NULL AND @preflight_stored_hash <> {hashLiteral}"
                );
                writer.AppendLine("BEGIN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"DECLARE @preflight_msg nvarchar(500) = CONCAT(N'EffectiveSchemaHash mismatch: database has ''', @preflight_stored_hash, N''' but expected ''', {hashLiteral}, N'''');"
                    );
                    writer.AppendLine("THROW 50000, @preflight_msg, 1;");
                }
                writer.AppendLine("END");
            }
            writer.AppendLine("END");
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits insert-if-missing seed rows for <c>dms.ResourceKey</c>.
    /// </summary>
    private void EmitResourceKeySeeds(SqlWriter writer, IReadOnlyList<ResourceKeyEntry> resourceKeys)
    {
        if (resourceKeys.Count == 0)
        {
            return;
        }

        var table = _dialect.QualifyTable(_resourceKeyTable);

        writer.AppendLine("-- ResourceKey seed inserts (insert-if-missing)");

        foreach (var rk in resourceKeys)
        {
            var idLiteral = _dialect.RenderSmallintLiteral(rk.ResourceKeyId);
            var projectLiteral = _dialect.RenderStringLiteral(rk.Resource.ProjectName);
            var resourceLiteral = _dialect.RenderStringLiteral(rk.Resource.ResourceName);
            var versionLiteral = _dialect.RenderStringLiteral(rk.ResourceVersion);

            if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
            {
                writer.AppendLine(
                    $"INSERT INTO {table} ({Quote("ResourceKeyId")}, {Quote("ProjectName")}, {Quote("ResourceName")}, {Quote("ResourceVersion")})"
                );
                writer.AppendLine(
                    $"VALUES ({idLiteral}, {projectLiteral}, {resourceLiteral}, {versionLiteral})"
                );
                writer.AppendLine($"ON CONFLICT ({Quote("ResourceKeyId")}) DO NOTHING;");
            }
            else
            {
                writer.AppendLine(
                    $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE {Quote("ResourceKeyId")} = {idLiteral})"
                );
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"INSERT INTO {table} ({Quote("ResourceKeyId")}, {Quote("ProjectName")}, {Quote("ResourceName")}, {Quote("ResourceVersion")})"
                    );
                    writer.AppendLine(
                        $"VALUES ({idLiteral}, {projectLiteral}, {resourceLiteral}, {versionLiteral});"
                    );
                }
            }
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a deterministic exact-match validation block for <c>dms.ResourceKey</c> (row count + expected content).
    /// </summary>
    private void EmitResourceKeyValidation(SqlWriter writer, IReadOnlyList<ResourceKeyEntry> resourceKeys)
    {
        if (resourceKeys.Count == 0)
        {
            return;
        }

        var table = _dialect.QualifyTable(_resourceKeyTable);
        var expectedCount = _dialect.RenderIntegerLiteral(resourceKeys.Count);

        writer.AppendLine("-- ResourceKey full-table validation (count + content)");

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine("DO $$");
            writer.AppendLine("DECLARE");
            using (writer.Indent())
            {
                writer.AppendLine("_actual_count integer;");
                writer.AppendLine("_mismatched_count integer;");
                writer.AppendLine("_mismatched_ids text;");
            }
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                // Count check
                writer.AppendLine($"SELECT COUNT(*) INTO _actual_count FROM {table};");
                writer.AppendLine($"IF _actual_count <> {expectedCount} THEN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"RAISE EXCEPTION 'dms.ResourceKey count mismatch: expected {expectedCount}, found %', _actual_count;"
                    );
                }
                writer.AppendLine("END IF;");
                writer.AppendLine();

                // Content check
                writer.AppendLine("SELECT COUNT(*) INTO _mismatched_count");
                writer.AppendLine($"FROM {table} rk");
                writer.AppendLine("WHERE NOT EXISTS (");
                using (writer.Indent())
                {
                    EmitResourceKeyExpectedValuesSubquery(writer, resourceKeys);
                }
                writer.AppendLine(");");
                writer.AppendLine("IF _mismatched_count > 0 THEN");
                using (writer.Indent())
                {
                    // Collect up to 10 mismatched ResourceKeyIds for diagnostics
                    writer.AppendLine(
                        "SELECT string_agg(sub.id, ', ' ORDER BY sub.id_num) INTO _mismatched_ids"
                    );
                    writer.AppendLine("FROM (");
                    using (writer.Indent())
                    {
                        writer.AppendLine(
                            $"SELECT rk.{Quote("ResourceKeyId")}::text AS id, rk.{Quote("ResourceKeyId")} AS id_num"
                        );
                        writer.AppendLine($"FROM {table} rk");
                        writer.AppendLine("WHERE NOT EXISTS (");
                        using (writer.Indent())
                        {
                            EmitResourceKeyExpectedValuesSubquery(writer, resourceKeys);
                        }
                        writer.AppendLine(")");
                        writer.AppendLine($"ORDER BY rk.{Quote("ResourceKeyId")}");
                        writer.AppendLine("LIMIT 10");
                    }
                    writer.AppendLine(") sub;");
                    writer.AppendLine(
                        "RAISE EXCEPTION 'dms.ResourceKey contents mismatch: % unexpected or modified rows (ResourceKeyIds: %). Run ddl provision for detailed row-level diff.', _mismatched_count, _mismatched_ids;"
                    );
                }
                writer.AppendLine("END IF;");
            }
            writer.AppendLine("END $$;");
        }
        else
        {
            writer.AppendLine("DECLARE @actual_count integer;");
            writer.AppendLine("DECLARE @mismatched_count integer;");
            writer.AppendLine("DECLARE @rk_mismatched_ids nvarchar(max);");
            writer.AppendLine();

            // Count check
            writer.AppendLine($"SELECT @actual_count = COUNT(*) FROM {table};");
            writer.AppendLine($"IF @actual_count <> {expectedCount}");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"DECLARE @rk_count_msg nvarchar(200) = CONCAT(N'dms.ResourceKey count mismatch: expected {expectedCount}, found ', CAST(@actual_count AS nvarchar(10)));"
                );
                writer.AppendLine("THROW 50000, @rk_count_msg, 1;");
            }
            writer.AppendLine("END");
            writer.AppendLine();

            // Content check
            writer.AppendLine("SELECT @mismatched_count = COUNT(*)");
            writer.AppendLine($"FROM {table} rk");
            writer.AppendLine("WHERE NOT EXISTS (");
            using (writer.Indent())
            {
                EmitResourceKeyExpectedValuesSubquery(writer, resourceKeys);
            }
            writer.AppendLine(");");
            writer.AppendLine("IF @mismatched_count > 0");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                // Collect up to 10 mismatched ResourceKeyIds for diagnostics
                writer.AppendLine(
                    $"SELECT @rk_mismatched_ids = STRING_AGG(sub.{Quote("ResourceKeyId")}, N', ') WITHIN GROUP (ORDER BY sub.{Quote("ResourceKeyIdNum")})"
                );
                writer.AppendLine("FROM (");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"SELECT TOP 10 CAST(rk.{Quote("ResourceKeyId")} AS nvarchar(10)) AS {Quote("ResourceKeyId")}, rk.{Quote("ResourceKeyId")} AS {Quote("ResourceKeyIdNum")}"
                    );
                    writer.AppendLine($"FROM {table} rk");
                    writer.AppendLine("WHERE NOT EXISTS (");
                    using (writer.Indent())
                    {
                        EmitResourceKeyExpectedValuesSubquery(writer, resourceKeys);
                    }
                    writer.AppendLine(")");
                    writer.AppendLine($"ORDER BY rk.{Quote("ResourceKeyId")}");
                }
                writer.AppendLine(") sub;");
                writer.AppendLine(
                    "DECLARE @rk_content_msg nvarchar(500) = CONCAT(N'dms.ResourceKey contents mismatch: ', CAST(@mismatched_count AS nvarchar(10)), N' unexpected or modified rows (ResourceKeyIds: ', @rk_mismatched_ids, N'). Run ddl provision for detailed row-level diff.');"
                );
                writer.AppendLine("THROW 50000, @rk_content_msg, 1;");
            }
            writer.AppendLine("END");
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the singleton insert-if-missing for <c>dms.EffectiveSchema</c>.
    /// </summary>
    private void EmitEffectiveSchemaInsert(SqlWriter writer, EffectiveSchemaInfo effectiveSchema)
    {
        var table = _dialect.QualifyTable(_effectiveSchemaTable);

        var columns =
            $"{Quote(_effectiveSchemaSingletonIdColumn)}, {Quote(_apiSchemaFormatVersionColumn)}, {Quote(_effectiveSchemaHashColumn)}, {Quote(_resourceKeyCountColumn)}, {Quote(_resourceKeySeedHashColumn)}";

        var values = string.Join(
            ", ",
            _dialect.RenderSmallintLiteral(1),
            _dialect.RenderStringLiteral(effectiveSchema.ApiSchemaFormatVersion),
            _dialect.RenderStringLiteral(effectiveSchema.EffectiveSchemaHash),
            _dialect.RenderSmallintLiteral(checked((short)effectiveSchema.ResourceKeyCount)),
            _dialect.RenderBinaryLiteral(effectiveSchema.ResourceKeySeedHash)
        );

        writer.AppendLine("-- EffectiveSchema singleton insert-if-missing");

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"INSERT INTO {table} ({columns})");
            writer.AppendLine($"VALUES ({values})");
            writer.AppendLine($"ON CONFLICT ({Quote(_effectiveSchemaSingletonIdColumn)}) DO NOTHING;");
        }
        else
        {
            writer.AppendLine(
                $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE {Quote(_effectiveSchemaSingletonIdColumn)} = 1)"
            );
            using (writer.Indent())
            {
                writer.AppendLine($"INSERT INTO {table} ({columns})");
                writer.AppendLine($"VALUES ({values});");
            }
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a validation block that verifies the existing <c>dms.EffectiveSchema</c> row's
    /// <c>ResourceKeyCount</c> and <c>ResourceKeySeedHash</c> match the expected values.
    /// </summary>
    private void EmitEffectiveSchemaValidation(SqlWriter writer, EffectiveSchemaInfo effectiveSchema)
    {
        var table = _dialect.QualifyTable(_effectiveSchemaTable);
        var expectedCount = _dialect.RenderSmallintLiteral(checked((short)effectiveSchema.ResourceKeyCount));
        var expectedHash = _dialect.RenderBinaryLiteral(effectiveSchema.ResourceKeySeedHash);

        writer.AppendLine("-- EffectiveSchema validation (ResourceKeyCount + ResourceKeySeedHash)");

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine("DO $$");
            writer.AppendLine("DECLARE");
            using (writer.Indent())
            {
                writer.AppendLine("_stored_count smallint;");
                writer.AppendLine("_stored_hash bytea;");
            }
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"SELECT {Quote(_resourceKeyCountColumn)}, {Quote(_resourceKeySeedHashColumn)} INTO _stored_count, _stored_hash"
                );
                writer.AppendLine($"FROM {table}");
                writer.AppendLine($"WHERE {Quote(_effectiveSchemaSingletonIdColumn)} = 1;");
                writer.AppendLine("IF _stored_count IS NOT NULL THEN");
                using (writer.Indent())
                {
                    writer.AppendLine($"IF _stored_count <> {expectedCount} THEN");
                    using (writer.Indent())
                    {
                        writer.AppendLine(
                            $"RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeyCount mismatch: expected {expectedCount}, found %', _stored_count;"
                        );
                    }
                    writer.AppendLine("END IF;");
                    writer.AppendLine($"IF _stored_hash <> {expectedHash} THEN");
                    using (writer.Indent())
                    {
                        writer.AppendLine(
                            "RAISE EXCEPTION 'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored % but expected %', encode(_stored_hash, 'hex'), encode("
                                + $"{expectedHash}, 'hex');"
                        );
                    }
                    writer.AppendLine("END IF;");
                }
                writer.AppendLine("END IF;");
            }
            writer.AppendLine("END $$;");
        }
        else
        {
            writer.AppendLine("DECLARE @es_stored_count smallint;");
            writer.AppendLine("DECLARE @es_stored_hash varbinary(32);");
            writer.AppendLine();
            writer.AppendLine(
                $"SELECT @es_stored_count = {Quote(_resourceKeyCountColumn)}, @es_stored_hash = {Quote(_resourceKeySeedHashColumn)}"
            );
            writer.AppendLine($"FROM {table}");
            writer.AppendLine($"WHERE {Quote(_effectiveSchemaSingletonIdColumn)} = 1;");
            writer.AppendLine("IF @es_stored_count IS NOT NULL");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine($"IF @es_stored_count <> {expectedCount}");
                writer.AppendLine("BEGIN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"DECLARE @es_count_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeyCount mismatch: expected {expectedCount}, found ', CAST(@es_stored_count AS nvarchar(10)));"
                    );
                    writer.AppendLine("THROW 50000, @es_count_msg, 1;");
                }
                writer.AppendLine("END");
                writer.AppendLine($"IF @es_stored_hash <> {expectedHash}");
                writer.AppendLine("BEGIN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"DECLARE @es_hash_msg nvarchar(200) = CONCAT(N'dms.EffectiveSchema ResourceKeySeedHash mismatch: stored ', CONVERT(nvarchar(66), @es_stored_hash, 1), N' but expected ', CONVERT(nvarchar(66), {expectedHash}, 1));"
                    );
                    writer.AppendLine("THROW 50000, @es_hash_msg, 1;");
                }
                writer.AppendLine("END");
            }
            writer.AppendLine("END");
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits insert-if-missing seed rows for <c>dms.SchemaComponent</c> scoped by <paramref name="effectiveSchemaHash"/>.
    /// </summary>
    private void EmitSchemaComponentSeeds(
        SqlWriter writer,
        string effectiveSchemaHash,
        IReadOnlyList<SchemaComponentInfo> schemaComponents
    )
    {
        if (schemaComponents.Count == 0)
        {
            return;
        }

        var table = _dialect.QualifyTable(_schemaComponentTable);
        var hashLiteral = _dialect.RenderStringLiteral(effectiveSchemaHash);

        writer.AppendLine("-- SchemaComponent seed inserts (insert-if-missing)");

        foreach (var sc in schemaComponents)
        {
            var endpointLiteral = _dialect.RenderStringLiteral(sc.ProjectEndpointName);
            var projectLiteral = _dialect.RenderStringLiteral(sc.ProjectName);
            var versionLiteral = _dialect.RenderStringLiteral(sc.ProjectVersion);
            var extensionLiteral = _dialect.RenderBooleanLiteral(sc.IsExtensionProject);

            if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
            {
                writer.AppendLine(
                    $"INSERT INTO {table} ({Quote("EffectiveSchemaHash")}, {Quote("ProjectEndpointName")}, {Quote("ProjectName")}, {Quote("ProjectVersion")}, {Quote("IsExtensionProject")})"
                );
                writer.AppendLine(
                    $"VALUES ({hashLiteral}, {endpointLiteral}, {projectLiteral}, {versionLiteral}, {extensionLiteral})"
                );
                writer.AppendLine(
                    $"ON CONFLICT ({Quote("EffectiveSchemaHash")}, {Quote("ProjectEndpointName")}) DO NOTHING;"
                );
            }
            else
            {
                writer.AppendLine(
                    $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE {Quote("EffectiveSchemaHash")} = {hashLiteral} AND {Quote("ProjectEndpointName")} = {endpointLiteral})"
                );
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"INSERT INTO {table} ({Quote("EffectiveSchemaHash")}, {Quote("ProjectEndpointName")}, {Quote("ProjectName")}, {Quote("ProjectVersion")}, {Quote("IsExtensionProject")})"
                    );
                    writer.AppendLine(
                        $"VALUES ({hashLiteral}, {endpointLiteral}, {projectLiteral}, {versionLiteral}, {extensionLiteral});"
                    );
                }
            }
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits a deterministic exact-match validation block for <c>dms.SchemaComponent</c> (row count + expected content).
    /// </summary>
    private void EmitSchemaComponentValidation(
        SqlWriter writer,
        string effectiveSchemaHash,
        IReadOnlyList<SchemaComponentInfo> schemaComponents
    )
    {
        if (schemaComponents.Count == 0)
        {
            return;
        }

        var table = _dialect.QualifyTable(_schemaComponentTable);
        var hashLiteral = _dialect.RenderStringLiteral(effectiveSchemaHash);
        var expectedCount = _dialect.RenderIntegerLiteral(schemaComponents.Count);

        writer.AppendLine("-- SchemaComponent exact-match validation (count + content)");

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine("DO $$");
            writer.AppendLine("DECLARE");
            using (writer.Indent())
            {
                writer.AppendLine("_actual_count integer;");
                writer.AppendLine("_mismatched_count integer;");
                writer.AppendLine("_mismatched_names text;");
            }
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                // Count check
                writer.AppendLine(
                    $"SELECT COUNT(*) INTO _actual_count FROM {table} WHERE {Quote("EffectiveSchemaHash")} = {hashLiteral};"
                );
                writer.AppendLine($"IF _actual_count <> {expectedCount} THEN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"RAISE EXCEPTION 'dms.SchemaComponent count mismatch: expected {expectedCount}, found %', _actual_count;"
                    );
                }
                writer.AppendLine("END IF;");
                writer.AppendLine();

                // Content check
                writer.AppendLine("SELECT COUNT(*) INTO _mismatched_count");
                writer.AppendLine($"FROM {table} sc");
                writer.AppendLine($"WHERE sc.{Quote("EffectiveSchemaHash")} = {hashLiteral}");
                writer.AppendLine("AND NOT EXISTS (");
                using (writer.Indent())
                {
                    EmitSchemaComponentExpectedValuesSubquery(writer, schemaComponents);
                }
                writer.AppendLine(");");
                writer.AppendLine("IF _mismatched_count > 0 THEN");
                using (writer.Indent())
                {
                    // Collect up to 10 mismatched ProjectEndpointNames for diagnostics
                    writer.AppendLine(
                        "SELECT string_agg(sub.name, ', ' ORDER BY sub.name) INTO _mismatched_names"
                    );
                    writer.AppendLine("FROM (");
                    using (writer.Indent())
                    {
                        writer.AppendLine($"SELECT sc.{Quote("ProjectEndpointName")} AS name");
                        writer.AppendLine($"FROM {table} sc");
                        writer.AppendLine($"WHERE sc.{Quote("EffectiveSchemaHash")} = {hashLiteral}");
                        writer.AppendLine("AND NOT EXISTS (");
                        using (writer.Indent())
                        {
                            EmitSchemaComponentExpectedValuesSubquery(writer, schemaComponents);
                        }
                        writer.AppendLine(")");
                        writer.AppendLine($"ORDER BY sc.{Quote("ProjectEndpointName")}");
                        writer.AppendLine("LIMIT 10");
                    }
                    writer.AppendLine(") sub;");
                    writer.AppendLine(
                        "RAISE EXCEPTION 'dms.SchemaComponent contents mismatch: % unexpected or modified rows (ProjectEndpointNames: %). Run ddl provision for detailed row-level diff.', _mismatched_count, _mismatched_names;"
                    );
                }
                writer.AppendLine("END IF;");
            }
            writer.AppendLine("END $$;");
        }
        else
        {
            writer.AppendLine("DECLARE @sc_actual_count integer;");
            writer.AppendLine("DECLARE @sc_mismatched_count integer;");
            writer.AppendLine("DECLARE @sc_mismatched_names nvarchar(max);");
            writer.AppendLine();

            // Count check
            writer.AppendLine(
                $"SELECT @sc_actual_count = COUNT(*) FROM {table} WHERE {Quote("EffectiveSchemaHash")} = {hashLiteral};"
            );
            writer.AppendLine($"IF @sc_actual_count <> {expectedCount}");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"DECLARE @sc_count_msg nvarchar(200) = CONCAT(N'dms.SchemaComponent count mismatch: expected {expectedCount}, found ', CAST(@sc_actual_count AS nvarchar(10)));"
                );
                writer.AppendLine("THROW 50000, @sc_count_msg, 1;");
            }
            writer.AppendLine("END");
            writer.AppendLine();

            // Content check
            writer.AppendLine("SELECT @sc_mismatched_count = COUNT(*)");
            writer.AppendLine($"FROM {table} sc");
            writer.AppendLine($"WHERE sc.{Quote("EffectiveSchemaHash")} = {hashLiteral}");
            writer.AppendLine("AND NOT EXISTS (");
            using (writer.Indent())
            {
                EmitSchemaComponentExpectedValuesSubquery(writer, schemaComponents);
            }
            writer.AppendLine(");");
            writer.AppendLine("IF @sc_mismatched_count > 0");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                // Collect up to 10 mismatched ProjectEndpointNames for diagnostics
                writer.AppendLine(
                    $"SELECT @sc_mismatched_names = STRING_AGG(sub.{Quote("ProjectEndpointName")}, N', ') WITHIN GROUP (ORDER BY sub.{Quote("ProjectEndpointName")})"
                );
                writer.AppendLine("FROM (");
                using (writer.Indent())
                {
                    writer.AppendLine($"SELECT TOP 10 sc.{Quote("ProjectEndpointName")}");
                    writer.AppendLine($"FROM {table} sc");
                    writer.AppendLine($"WHERE sc.{Quote("EffectiveSchemaHash")} = {hashLiteral}");
                    writer.AppendLine("AND NOT EXISTS (");
                    using (writer.Indent())
                    {
                        EmitSchemaComponentExpectedValuesSubquery(writer, schemaComponents);
                    }
                    writer.AppendLine(")");
                    writer.AppendLine($"ORDER BY sc.{Quote("ProjectEndpointName")}");
                }
                writer.AppendLine(") sub;");
                writer.AppendLine(
                    "DECLARE @sc_content_msg nvarchar(500) = CONCAT(N'dms.SchemaComponent contents mismatch: ', CAST(@sc_mismatched_count AS nvarchar(10)), N' unexpected or modified rows (ProjectEndpointNames: ', @sc_mismatched_names, N'). Run ddl provision for detailed row-level diff.');"
                );
                writer.AppendLine("THROW 50000, @sc_content_msg, 1;");
            }
            writer.AppendLine("END");
        }
        writer.AppendLine();
    }

    /// <summary>
    /// Emits the VALUES subquery body for ResourceKey NOT EXISTS checks.
    /// Shared between the count query and the ID-collection query.
    /// When the row count exceeds <see cref="MaxValuesRows"/>, the VALUES
    /// clause is split into chunks joined with UNION ALL to stay within
    /// the SQL Server 1000-row table constructor limit.
    /// </summary>
    private void EmitResourceKeyExpectedValuesSubquery(
        SqlWriter writer,
        IReadOnlyList<ResourceKeyEntry> resourceKeys
    )
    {
        var columnList =
            $"{Quote("ResourceKeyId")}, {Quote("ProjectName")}, {Quote("ResourceName")}, {Quote("ResourceVersion")}";

        if (resourceKeys.Count > MaxValuesRows)
        {
            writer.AppendLine("SELECT 1 FROM (");
            using (writer.Indent())
            {
                for (var chunkStart = 0; chunkStart < resourceKeys.Count; chunkStart += MaxValuesRows)
                {
                    if (chunkStart > 0)
                    {
                        writer.AppendLine("UNION ALL");
                    }
                    var chunkEnd = Math.Min(chunkStart + MaxValuesRows, resourceKeys.Count);
                    EmitResourceKeyValuesBlock(writer, resourceKeys, chunkStart, chunkEnd, "chunk");
                }
            }
            writer.AppendLine($") AS expected({columnList})");
        }
        else
        {
            writer.AppendLine("SELECT 1 FROM (VALUES");
            using (writer.Indent())
            {
                EmitResourceKeyValueRows(writer, resourceKeys, 0, resourceKeys.Count);
            }
            writer.AppendLine($") AS expected({columnList})");
        }

        writer.AppendLine($"WHERE expected.{Quote("ResourceKeyId")} = rk.{Quote("ResourceKeyId")}");
        writer.AppendLine($"AND expected.{Quote("ProjectName")} = rk.{Quote("ProjectName")}");
        writer.AppendLine($"AND expected.{Quote("ResourceName")} = rk.{Quote("ResourceName")}");
        writer.AppendLine($"AND expected.{Quote("ResourceVersion")} = rk.{Quote("ResourceVersion")}");
    }

    private void EmitResourceKeyValuesBlock(
        SqlWriter writer,
        IReadOnlyList<ResourceKeyEntry> resourceKeys,
        int start,
        int end,
        string alias
    )
    {
        writer.AppendLine("SELECT * FROM (VALUES");
        using (writer.Indent())
        {
            EmitResourceKeyValueRows(writer, resourceKeys, start, end);
        }
        writer.AppendLine(
            $") AS {alias}({Quote("ResourceKeyId")}, {Quote("ProjectName")}, {Quote("ResourceName")}, {Quote("ResourceVersion")})"
        );
    }

    private void EmitResourceKeyValueRows(
        SqlWriter writer,
        IReadOnlyList<ResourceKeyEntry> resourceKeys,
        int start,
        int end
    )
    {
        for (var i = start; i < end; i++)
        {
            var rk = resourceKeys[i];
            var comma = i < end - 1 ? "," : "";
            var idLiteral = _dialect.RenderSmallintLiteral(rk.ResourceKeyId);
            var idValue = _dialect.Rules.Dialect == SqlDialect.Pgsql ? $"{idLiteral}::smallint" : idLiteral;
            writer.AppendLine(
                $"({idValue}, {_dialect.RenderStringLiteral(rk.Resource.ProjectName)}, {_dialect.RenderStringLiteral(rk.Resource.ResourceName)}, {_dialect.RenderStringLiteral(rk.ResourceVersion)}){comma}"
            );
        }
    }

    /// <summary>
    /// Emits the VALUES subquery body for SchemaComponent NOT EXISTS checks.
    /// Shared between the count query and the ID-collection query.
    /// When the row count exceeds <see cref="MaxValuesRows"/>, the VALUES
    /// clause is split into chunks joined with UNION ALL to stay within
    /// the SQL Server 1000-row table constructor limit.
    /// </summary>
    private void EmitSchemaComponentExpectedValuesSubquery(
        SqlWriter writer,
        IReadOnlyList<SchemaComponentInfo> schemaComponents
    )
    {
        var columnList =
            $"{Quote("ProjectEndpointName")}, {Quote("ProjectName")}, {Quote("ProjectVersion")}, {Quote("IsExtensionProject")}";

        if (schemaComponents.Count > MaxValuesRows)
        {
            writer.AppendLine("SELECT 1 FROM (");
            using (writer.Indent())
            {
                for (var chunkStart = 0; chunkStart < schemaComponents.Count; chunkStart += MaxValuesRows)
                {
                    if (chunkStart > 0)
                    {
                        writer.AppendLine("UNION ALL");
                    }
                    var chunkEnd = Math.Min(chunkStart + MaxValuesRows, schemaComponents.Count);
                    EmitSchemaComponentValuesBlock(writer, schemaComponents, chunkStart, chunkEnd, "chunk");
                }
            }
            writer.AppendLine($") AS expected({columnList})");
        }
        else
        {
            writer.AppendLine("SELECT 1 FROM (VALUES");
            using (writer.Indent())
            {
                EmitSchemaComponentValueRows(writer, schemaComponents, 0, schemaComponents.Count);
            }
            writer.AppendLine($") AS expected({columnList})");
        }

        writer.AppendLine(
            $"WHERE expected.{Quote("ProjectEndpointName")} = sc.{Quote("ProjectEndpointName")}"
        );
        writer.AppendLine($"AND expected.{Quote("ProjectName")} = sc.{Quote("ProjectName")}");
        writer.AppendLine($"AND expected.{Quote("ProjectVersion")} = sc.{Quote("ProjectVersion")}");
        writer.AppendLine($"AND expected.{Quote("IsExtensionProject")} = sc.{Quote("IsExtensionProject")}");
    }

    private void EmitSchemaComponentValuesBlock(
        SqlWriter writer,
        IReadOnlyList<SchemaComponentInfo> schemaComponents,
        int start,
        int end,
        string alias
    )
    {
        writer.AppendLine("SELECT * FROM (VALUES");
        using (writer.Indent())
        {
            EmitSchemaComponentValueRows(writer, schemaComponents, start, end);
        }
        writer.AppendLine(
            $") AS {alias}({Quote("ProjectEndpointName")}, {Quote("ProjectName")}, {Quote("ProjectVersion")}, {Quote("IsExtensionProject")})"
        );
    }

    private void EmitSchemaComponentValueRows(
        SqlWriter writer,
        IReadOnlyList<SchemaComponentInfo> schemaComponents,
        int start,
        int end
    )
    {
        for (var i = start; i < end; i++)
        {
            var sc = schemaComponents[i];
            var comma = i < end - 1 ? "," : "";
            writer.AppendLine(
                $"({_dialect.RenderStringLiteral(sc.ProjectEndpointName)}, {_dialect.RenderStringLiteral(sc.ProjectName)}, {_dialect.RenderStringLiteral(sc.ProjectVersion)}, {_dialect.RenderBooleanLiteral(sc.IsExtensionProject)}){comma}"
            );
        }
    }

    private string Quote(string identifier) => _dialect.QuoteIdentifier(identifier);

    private string Quote(DbColumnName column) => _dialect.QuoteIdentifier(column.Value);

    private static string RenderTableObjectIdLiteral(DbTableName table)
    {
        return $"{table.Schema.Value}.{table.Name}".Replace("'", "''");
    }
}
