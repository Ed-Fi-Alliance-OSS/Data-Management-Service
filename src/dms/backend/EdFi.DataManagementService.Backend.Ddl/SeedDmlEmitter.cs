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
public sealed class SeedDmlEmitter(ISqlDialect dialect)
{
    private readonly ISqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

    // Table names are owned by CoreDdlEmitter (which creates them); we reference them here.
    private static readonly DbTableName _resourceKeyTable = CoreDdlEmitter._resourceKeyTable;
    private static readonly DbTableName _effectiveSchemaTable = CoreDdlEmitter._effectiveSchemaTable;
    private static readonly DbTableName _schemaComponentTable = CoreDdlEmitter._schemaComponentTable;

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

        var writer = new SqlWriter(_dialect);

        writer.AppendLine("-- ==========================================================");
        writer.AppendLine("-- Phase 7: Seed Data (insert-if-missing + validation)");
        writer.AppendLine("-- ==========================================================");
        writer.AppendLine();

        EmitEffectiveSchemaHashPreflight(writer, effectiveSchema.EffectiveSchemaHash);
        EmitResourceKeySeeds(writer, effectiveSchema.ResourceKeysInIdOrder);
        EmitResourceKeyValidation(writer, effectiveSchema.ResourceKeysInIdOrder);
        EmitEffectiveSchemaInsert(writer, effectiveSchema);
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

    private void EmitEffectiveSchemaHashPreflight(SqlWriter writer, string effectiveSchemaHash)
    {
        string Q(string id) => _dialect.QuoteIdentifier(id);
        var table = _dialect.QualifyTable(_effectiveSchemaTable);
        var hashLiteral = _dialect.RenderStringLiteral(effectiveSchemaHash);

        writer.AppendLine("-- Preflight: fail fast if database is provisioned for a different schema hash");

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine("DO $$");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine("IF EXISTS (");
                using (writer.Indent())
                {
                    writer.AppendLine($"SELECT 1 FROM {table}");
                    writer.AppendLine($"WHERE {Q("EffectiveSchemaSingletonId")} = 1");
                    writer.AppendLine($"AND {Q("EffectiveSchemaHash")} <> {hashLiteral}");
                }
                writer.AppendLine(") THEN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"RAISE EXCEPTION 'EffectiveSchemaHash mismatch: database is provisioned for a different schema hash (expected: %)', {hashLiteral};"
                    );
                }
                writer.AppendLine("END IF;");
            }
            writer.AppendLine("END $$;");
        }
        else
        {
            writer.AppendLine("IF EXISTS (");
            using (writer.Indent())
            {
                writer.AppendLine($"SELECT 1 FROM {table}");
                writer.AppendLine($"WHERE {Q("EffectiveSchemaSingletonId")} = 1");
                writer.AppendLine($"AND {Q("EffectiveSchemaHash")} <> {hashLiteral}");
            }
            writer.AppendLine(")");
            using (writer.Indent())
            {
                writer.AppendLine(
                    $"THROW 50000, N'EffectiveSchemaHash mismatch: database is provisioned for a different schema hash', 1;"
                );
            }
        }
        writer.AppendLine();
    }

    private void EmitResourceKeySeeds(SqlWriter writer, IReadOnlyList<ResourceKeyEntry> resourceKeys)
    {
        if (resourceKeys.Count == 0)
        {
            return;
        }

        string Q(string id) => _dialect.QuoteIdentifier(id);
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
                    $"INSERT INTO {table} ({Q("ResourceKeyId")}, {Q("ProjectName")}, {Q("ResourceName")}, {Q("ResourceVersion")})"
                );
                writer.AppendLine(
                    $"VALUES ({idLiteral}, {projectLiteral}, {resourceLiteral}, {versionLiteral})"
                );
                writer.AppendLine($"ON CONFLICT ({Q("ResourceKeyId")}) DO NOTHING;");
            }
            else
            {
                writer.AppendLine(
                    $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE {Q("ResourceKeyId")} = {idLiteral})"
                );
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"INSERT INTO {table} ({Q("ResourceKeyId")}, {Q("ProjectName")}, {Q("ResourceName")}, {Q("ResourceVersion")})"
                    );
                    writer.AppendLine(
                        $"VALUES ({idLiteral}, {projectLiteral}, {resourceLiteral}, {versionLiteral});"
                    );
                }
            }
        }
        writer.AppendLine();
    }

    private void EmitResourceKeyValidation(SqlWriter writer, IReadOnlyList<ResourceKeyEntry> resourceKeys)
    {
        if (resourceKeys.Count == 0)
        {
            return;
        }

        string Q(string id) => _dialect.QuoteIdentifier(id);
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
                    writer.AppendLine("SELECT 1 FROM (VALUES");
                    using (writer.Indent())
                    {
                        for (var i = 0; i < resourceKeys.Count; i++)
                        {
                            var rk = resourceKeys[i];
                            var comma = i < resourceKeys.Count - 1 ? "," : "";
                            writer.AppendLine(
                                $"({_dialect.RenderSmallintLiteral(rk.ResourceKeyId)}::smallint, {_dialect.RenderStringLiteral(rk.Resource.ProjectName)}, {_dialect.RenderStringLiteral(rk.Resource.ResourceName)}, {_dialect.RenderStringLiteral(rk.ResourceVersion)}){comma}"
                            );
                        }
                    }
                    writer.AppendLine(
                        $") AS expected({Q("ResourceKeyId")}, {Q("ProjectName")}, {Q("ResourceName")}, {Q("ResourceVersion")})"
                    );
                    writer.AppendLine($"WHERE expected.{Q("ResourceKeyId")} = rk.{Q("ResourceKeyId")}");
                    writer.AppendLine($"AND expected.{Q("ProjectName")} = rk.{Q("ProjectName")}");
                    writer.AppendLine($"AND expected.{Q("ResourceName")} = rk.{Q("ResourceName")}");
                    writer.AppendLine($"AND expected.{Q("ResourceVersion")} = rk.{Q("ResourceVersion")}");
                }
                writer.AppendLine(");");
                writer.AppendLine("IF _mismatched_count > 0 THEN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        "RAISE EXCEPTION 'dms.ResourceKey contents mismatch: % unexpected or modified rows', _mismatched_count;"
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
                writer.AppendLine("SELECT 1 FROM (VALUES");
                using (writer.Indent())
                {
                    for (var i = 0; i < resourceKeys.Count; i++)
                    {
                        var rk = resourceKeys[i];
                        var comma = i < resourceKeys.Count - 1 ? "," : "";
                        writer.AppendLine(
                            $"({_dialect.RenderSmallintLiteral(rk.ResourceKeyId)}, {_dialect.RenderStringLiteral(rk.Resource.ProjectName)}, {_dialect.RenderStringLiteral(rk.Resource.ResourceName)}, {_dialect.RenderStringLiteral(rk.ResourceVersion)}){comma}"
                        );
                    }
                }
                writer.AppendLine(
                    $") AS expected({Q("ResourceKeyId")}, {Q("ProjectName")}, {Q("ResourceName")}, {Q("ResourceVersion")})"
                );
                writer.AppendLine($"WHERE expected.{Q("ResourceKeyId")} = rk.{Q("ResourceKeyId")}");
                writer.AppendLine($"AND expected.{Q("ProjectName")} = rk.{Q("ProjectName")}");
                writer.AppendLine($"AND expected.{Q("ResourceName")} = rk.{Q("ResourceName")}");
                writer.AppendLine($"AND expected.{Q("ResourceVersion")} = rk.{Q("ResourceVersion")}");
            }
            writer.AppendLine(");");
            writer.AppendLine("IF @mismatched_count > 0");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine(
                    "DECLARE @rk_content_msg nvarchar(200) = CONCAT(N'dms.ResourceKey contents mismatch: ', CAST(@mismatched_count AS nvarchar(10)), N' unexpected or modified rows');"
                );
                writer.AppendLine("THROW 50000, @rk_content_msg, 1;");
            }
            writer.AppendLine("END");
        }
        writer.AppendLine();
    }

    private void EmitEffectiveSchemaInsert(SqlWriter writer, EffectiveSchemaInfo effectiveSchema)
    {
        string Q(string id) => _dialect.QuoteIdentifier(id);
        var table = _dialect.QualifyTable(_effectiveSchemaTable);

        var columns =
            $"{Q("EffectiveSchemaSingletonId")}, {Q("ApiSchemaFormatVersion")}, {Q("EffectiveSchemaHash")}, {Q("ResourceKeyCount")}, {Q("ResourceKeySeedHash")}";

        var values = string.Join(
            ", ",
            _dialect.RenderSmallintLiteral(1),
            _dialect.RenderStringLiteral(effectiveSchema.ApiSchemaFormatVersion),
            _dialect.RenderStringLiteral(effectiveSchema.EffectiveSchemaHash),
            _dialect.RenderIntegerLiteral(effectiveSchema.ResourceKeyCount),
            _dialect.RenderBinaryLiteral(effectiveSchema.ResourceKeySeedHash)
        );

        writer.AppendLine("-- EffectiveSchema singleton insert-if-missing");

        if (_dialect.Rules.Dialect == SqlDialect.Pgsql)
        {
            writer.AppendLine($"INSERT INTO {table} ({columns})");
            writer.AppendLine($"VALUES ({values})");
            writer.AppendLine($"ON CONFLICT ({Q("EffectiveSchemaSingletonId")}) DO NOTHING;");
        }
        else
        {
            writer.AppendLine(
                $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE {Q("EffectiveSchemaSingletonId")} = 1)"
            );
            using (writer.Indent())
            {
                writer.AppendLine($"INSERT INTO {table} ({columns})");
                writer.AppendLine($"VALUES ({values});");
            }
        }
        writer.AppendLine();
    }

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

        string Q(string id) => _dialect.QuoteIdentifier(id);
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
                    $"INSERT INTO {table} ({Q("EffectiveSchemaHash")}, {Q("ProjectEndpointName")}, {Q("ProjectName")}, {Q("ProjectVersion")}, {Q("IsExtensionProject")})"
                );
                writer.AppendLine(
                    $"VALUES ({hashLiteral}, {endpointLiteral}, {projectLiteral}, {versionLiteral}, {extensionLiteral})"
                );
                writer.AppendLine(
                    $"ON CONFLICT ({Q("EffectiveSchemaHash")}, {Q("ProjectEndpointName")}) DO NOTHING;"
                );
            }
            else
            {
                writer.AppendLine(
                    $"IF NOT EXISTS (SELECT 1 FROM {table} WHERE {Q("EffectiveSchemaHash")} = {hashLiteral} AND {Q("ProjectEndpointName")} = {endpointLiteral})"
                );
                using (writer.Indent())
                {
                    writer.AppendLine(
                        $"INSERT INTO {table} ({Q("EffectiveSchemaHash")}, {Q("ProjectEndpointName")}, {Q("ProjectName")}, {Q("ProjectVersion")}, {Q("IsExtensionProject")})"
                    );
                    writer.AppendLine(
                        $"VALUES ({hashLiteral}, {endpointLiteral}, {projectLiteral}, {versionLiteral}, {extensionLiteral});"
                    );
                }
            }
        }
        writer.AppendLine();
    }

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

        string Q(string id) => _dialect.QuoteIdentifier(id);
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
            }
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                // Count check
                writer.AppendLine(
                    $"SELECT COUNT(*) INTO _actual_count FROM {table} WHERE {Q("EffectiveSchemaHash")} = {hashLiteral};"
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
                writer.AppendLine($"WHERE sc.{Q("EffectiveSchemaHash")} = {hashLiteral}");
                writer.AppendLine("AND NOT EXISTS (");
                using (writer.Indent())
                {
                    writer.AppendLine("SELECT 1 FROM (VALUES");
                    using (writer.Indent())
                    {
                        for (var i = 0; i < schemaComponents.Count; i++)
                        {
                            var sc = schemaComponents[i];
                            var comma = i < schemaComponents.Count - 1 ? "," : "";
                            writer.AppendLine(
                                $"({_dialect.RenderStringLiteral(sc.ProjectEndpointName)}, {_dialect.RenderStringLiteral(sc.ProjectName)}, {_dialect.RenderStringLiteral(sc.ProjectVersion)}, {_dialect.RenderBooleanLiteral(sc.IsExtensionProject)}){comma}"
                            );
                        }
                    }
                    writer.AppendLine(
                        $") AS expected({Q("ProjectEndpointName")}, {Q("ProjectName")}, {Q("ProjectVersion")}, {Q("IsExtensionProject")})"
                    );
                    writer.AppendLine(
                        $"WHERE expected.{Q("ProjectEndpointName")} = sc.{Q("ProjectEndpointName")}"
                    );
                    writer.AppendLine($"AND expected.{Q("ProjectName")} = sc.{Q("ProjectName")}");
                    writer.AppendLine($"AND expected.{Q("ProjectVersion")} = sc.{Q("ProjectVersion")}");
                    writer.AppendLine(
                        $"AND expected.{Q("IsExtensionProject")} = sc.{Q("IsExtensionProject")}"
                    );
                }
                writer.AppendLine(");");
                writer.AppendLine("IF _mismatched_count > 0 THEN");
                using (writer.Indent())
                {
                    writer.AppendLine(
                        "RAISE EXCEPTION 'dms.SchemaComponent contents mismatch: % unexpected or modified rows', _mismatched_count;"
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
            writer.AppendLine();

            // Count check
            writer.AppendLine(
                $"SELECT @sc_actual_count = COUNT(*) FROM {table} WHERE {Q("EffectiveSchemaHash")} = {hashLiteral};"
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
            writer.AppendLine($"WHERE sc.{Q("EffectiveSchemaHash")} = {hashLiteral}");
            writer.AppendLine("AND NOT EXISTS (");
            using (writer.Indent())
            {
                writer.AppendLine("SELECT 1 FROM (VALUES");
                using (writer.Indent())
                {
                    for (var i = 0; i < schemaComponents.Count; i++)
                    {
                        var sc = schemaComponents[i];
                        var comma = i < schemaComponents.Count - 1 ? "," : "";
                        writer.AppendLine(
                            $"({_dialect.RenderStringLiteral(sc.ProjectEndpointName)}, {_dialect.RenderStringLiteral(sc.ProjectName)}, {_dialect.RenderStringLiteral(sc.ProjectVersion)}, {_dialect.RenderBooleanLiteral(sc.IsExtensionProject)}){comma}"
                        );
                    }
                }
                writer.AppendLine(
                    $") AS expected({Q("ProjectEndpointName")}, {Q("ProjectName")}, {Q("ProjectVersion")}, {Q("IsExtensionProject")})"
                );
                writer.AppendLine(
                    $"WHERE expected.{Q("ProjectEndpointName")} = sc.{Q("ProjectEndpointName")}"
                );
                writer.AppendLine($"AND expected.{Q("ProjectName")} = sc.{Q("ProjectName")}");
                writer.AppendLine($"AND expected.{Q("ProjectVersion")} = sc.{Q("ProjectVersion")}");
                writer.AppendLine($"AND expected.{Q("IsExtensionProject")} = sc.{Q("IsExtensionProject")}");
            }
            writer.AppendLine(");");
            writer.AppendLine("IF @sc_mismatched_count > 0");
            writer.AppendLine("BEGIN");
            using (writer.Indent())
            {
                writer.AppendLine(
                    "DECLARE @sc_content_msg nvarchar(200) = CONCAT(N'dms.SchemaComponent contents mismatch: ', CAST(@sc_mismatched_count AS nvarchar(10)), N' unexpected or modified rows');"
                );
                writer.AppendLine("THROW 50000, @sc_content_msg, 1;");
            }
            writer.AppendLine("END");
        }
        writer.AppendLine();
    }
}
