// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The delivered PostgreSQL security reads (F-P9b), text for text: <c>Get-PostgresqlTemplateOwnershipQuerySql</c>
/// without a schema list (the consumer's full scope: every schema outside the system schemas) and
/// <c>Get-PostgresqlPrivilegeQuerySql</c>. Both judge against <c>current_user</c>, the connected administrator.
/// Their rows are split here as <c>Assert-PostgresqlArtifactOwnership</c> and
/// <c>Assert-PostgresqlArtifactPrivilege</c> split them: the identity is the last field and may contain the
/// separator, so the split is bounded.
/// </summary>
internal static class PostgresqlSecurityQueries
{
    private const string StandaloneTypeFilter =
        "t.typtype IN ('b','p','c','d','e','r','m') AND (t.typtype <> 'c' OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c WHERE c.oid = t.typrelid AND c.relkind = 'c')) AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type elem WHERE elem.typarray = t.oid)";

    /// <summary>Every schema, relation, function, and standalone type the administrator does not own, as <c>kind|owner|identity</c>.</summary>
    public const string Ownership =
        "SELECT x.kind || '|' || pg_catalog.pg_get_userbyid(x.owner) || '|' || x.identity FROM (\n"
        + "  SELECT 'schema' AS kind, n.nspowner AS owner, pg_catalog.format('%I', n.nspname) AS identity, n.nspname AS ns FROM pg_catalog.pg_namespace n\n"
        + "  UNION ALL SELECT 'relation', c.relowner, pg_catalog.format('%I.%I', n.nspname, c.relname), n.nspname\n"
        + "    FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n"
        + "  UNION ALL SELECT 'function', p.proowner, pg_catalog.format('%I.%I(%s)', n.nspname, p.proname, pg_catalog.pg_get_function_identity_arguments(p.oid)), n.nspname\n"
        + "    FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace\n"
        + "  UNION ALL SELECT 'type', t.typowner, pg_catalog.format('%I.%I', n.nspname, t.typname), n.nspname\n"
        + "    FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace\n"
        + "    WHERE "
        + StandaloneTypeFilter
        + "\n"
        + ") x\n"
        + "WHERE x.ns !~ '^pg_' AND x.ns <> 'information_schema' AND x.owner <> (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = current_user)\n"
        + "ORDER BY 1;";

    /// <summary>Every privilege held outside the system schemas, defaults expanded, as <c>kind|privilege|owner|grantee|identity</c>.</summary>
    public const string Privileges =
        "SELECT a.kind || '|' || a.privilege || '|' || pg_catalog.format('%I', pg_catalog.pg_get_userbyid(a.owner)) || '|' ||\n"
        + "  CASE WHEN a.grantee = 0 THEN '-' ELSE pg_catalog.format('%I', pg_catalog.pg_get_userbyid(a.grantee)) END || '|' || a.identity FROM (\n"
        + "  SELECT 'schema' AS kind, n.nspowner AS owner, pg_catalog.format('%I', n.nspname) AS identity, n.nspname AS ns, x.grantee, x.privilege_type AS privilege\n"
        + "    FROM pg_catalog.pg_namespace n, LATERAL pg_catalog.aclexplode(COALESCE(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) x\n"
        + "  UNION ALL SELECT 'relation', c.relowner, pg_catalog.format('%I.%I', n.nspname, c.relname), n.nspname, x.grantee, x.privilege_type\n"
        + "    FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace,\n"
        + "    LATERAL pg_catalog.aclexplode(COALESCE(c.relacl, pg_catalog.acldefault((CASE WHEN c.relkind = 'S' THEN 's' ELSE 'r' END)::\"char\", c.relowner))) x\n"
        + "    WHERE c.relkind IN ('r','p','v','m','S','f')\n"
        + "  UNION ALL SELECT 'column', c.relowner, pg_catalog.format('%I.%I.%I', n.nspname, c.relname, att.attname), n.nspname, x.grantee, x.privilege_type\n"
        + "    FROM pg_catalog.pg_attribute att JOIN pg_catalog.pg_class c ON c.oid = att.attrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace,\n"
        + "    LATERAL pg_catalog.aclexplode(att.attacl) x\n"
        + "    WHERE att.attacl IS NOT NULL AND att.attnum > 0 AND NOT att.attisdropped\n"
        + "  UNION ALL SELECT 'function', p.proowner, pg_catalog.format('%I.%I(%s)', n.nspname, p.proname, pg_catalog.pg_get_function_identity_arguments(p.oid)), n.nspname, x.grantee, x.privilege_type\n"
        + "    FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace,\n"
        + "    LATERAL pg_catalog.aclexplode(COALESCE(p.proacl, pg_catalog.acldefault('f', p.proowner))) x\n"
        + "  UNION ALL SELECT 'type', t.typowner, pg_catalog.format('%I.%I', n.nspname, t.typname), n.nspname, x.grantee, x.privilege_type\n"
        + "    FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace,\n"
        + "    LATERAL pg_catalog.aclexplode(COALESCE(t.typacl, pg_catalog.acldefault('T', t.typowner))) x\n"
        + "    WHERE "
        + StandaloneTypeFilter
        + "\n"
        + ") a\n"
        + "WHERE a.ns !~ '^pg_' AND a.ns <> 'information_schema'\n"
        + "ORDER BY 1;";

    /// <summary>Splits a <c>kind|owner|identity</c> row into at most three fields; the identity must be non-blank.</summary>
    public static bool TryParseOwnershipRow(
        string? row,
        [NotNullWhen(true)] out DmsPostgresqlOwnershipRow? entry
    )
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(row))
        {
            return false;
        }

        string[] fields = row.Trim().Split('|', 3);
        if (fields.Length != 3 || string.IsNullOrWhiteSpace(fields[2]))
        {
            return false;
        }

        entry = new DmsPostgresqlOwnershipRow(fields[0], fields[1], fields[2]);
        return true;
    }

    /// <summary>Splits a <c>kind|privilege|owner|grantee|identity</c> row into at most five fields; the identity must be non-blank.</summary>
    public static bool TryParsePrivilegeRow(
        string? row,
        [NotNullWhen(true)] out DmsPostgresqlPrivilegeRow? entry
    )
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(row))
        {
            return false;
        }

        string[] fields = row.Trim().Split('|', 5);
        if (fields.Length != 5 || string.IsNullOrWhiteSpace(fields[4]))
        {
            return false;
        }

        entry = new DmsPostgresqlPrivilegeRow(fields[0], fields[1], fields[2], fields[3], fields[4]);
        return true;
    }
}
