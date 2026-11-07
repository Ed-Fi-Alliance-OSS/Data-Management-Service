// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The catalog reads of the PostgreSQL engine. The inventory statements are the PostgreSQL branches of the
/// delivered <c>Template-RestoreCore.psm1</c> builders, text for text (<c>Get-InventorySchemaQuerySql</c> for
/// the <c>InventoryEnumeration</c> purpose, <c>Get-InventoryObjectQuerySql</c>, and
/// <c>Get-InventoryDatabaseObjectQuerySql</c>, with the <c>pgcrypto</c> allow-list and the standalone-type filter
/// expanded), so the inventory the provisioner hashes is the inventory the producer hashed; their
/// pipe-separated rows are split here exactly as <c>ConvertFrom-InventoryQueryRow</c> splits them. The other
/// reads select typed columns. Every statement is read-only and parameter-free.
/// </summary>
internal static class PostgresqlInventoryQueries
{
    private const string AllowedExtensionList = "'pgcrypto'";

    private const string DatabaseLevelAllowedExtensionList = "'plpgsql', 'pgcrypto'";

    /// <summary><c>Get-PostgresqlStandaloneTypeFilterSql</c>: the types a script can create on its own, over alias <c>t</c>.</summary>
    private const string StandaloneTypeFilter =
        "t.typtype IN ('b','p','c','d','e','r','m') AND (t.typtype <> 'c' OR EXISTS (SELECT 1 FROM pg_catalog.pg_class c WHERE c.oid = t.typrelid AND c.relkind = 'c')) AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_type elem WHERE elem.typarray = t.oid)";

    /// <summary>One schema name per row over the inventory-enumeration scope (includes <c>public</c>).</summary>
    public const string SchemaNames =
        "SELECT nspname FROM pg_catalog.pg_namespace WHERE nspname !~ '^pg_' AND nspname NOT IN ('information_schema') ORDER BY nspname;";

    /// <summary>Every inventoried object as a <c>schema|name|type</c> row.</summary>
    public const string Objects =
        "SELECT n.nspname || '|' || c.relname || '|' ||\n"
        + "  CASE c.relkind WHEN 'r' THEN 'table' WHEN 'p' THEN 'table' WHEN 'v' THEN 'view' WHEN 'm' THEN 'materializedview' WHEN 'S' THEN 'sequence' WHEN 'f' THEN 'foreigntable' END\n"
        + "FROM pg_catalog.pg_class c\n"
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n"
        + "WHERE c.relkind IN ('r','p','v','m','S','f')\n"
        + "  AND n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid\n"
        + "                  WHERE d.classid = 'pg_catalog.pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e' AND e.extname IN ("
        + AllowedExtensionList
        + "))\n"
        + "UNION ALL\n"
        + "SELECT n.nspname || '|' || p.proname || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')' || '|' ||\n"
        + "  CASE p.prokind WHEN 'f' THEN 'function' WHEN 'p' THEN 'procedure' WHEN 'a' THEN 'aggregate' WHEN 'w' THEN 'window' END\n"
        + "FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace\n"
        + "WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid\n"
        + "                  WHERE d.classid = 'pg_catalog.pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e' AND e.extname IN ("
        + AllowedExtensionList
        + "))\n"
        + "UNION ALL\n"
        + "SELECT n.nspname || '|' || c.relname || '.' || t.tgname || '|' || 'trigger'\n"
        + "FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n"
        + "WHERE NOT t.tgisinternal AND n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "UNION ALL\n"
        + "SELECT n.nspname || '|' || c.relname || '.' || r.rulename || '|' || 'rule'\n"
        + "FROM pg_catalog.pg_rewrite r JOIN pg_catalog.pg_class c ON c.oid = r.ev_class JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n"
        + "WHERE r.rulename <> '_RETURN' AND n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "UNION ALL\n"
        + "SELECT n.nspname || '|' || c.relname || '.' || pol.polname || '|' || 'policy'\n"
        + "FROM pg_catalog.pg_policy pol JOIN pg_catalog.pg_class c ON c.oid = pol.polrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n"
        + "WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "UNION ALL\n"
        + "SELECT n.nspname || '|' || c.relname || '.' || con.conname || '|' ||\n"
        + "  CASE con.contype WHEN 'p' THEN 'primarykey' WHEN 'u' THEN 'unique' WHEN 'f' THEN 'foreignkey' WHEN 'c' THEN 'check' WHEN 'x' THEN 'exclusion' END\n"
        + "FROM pg_catalog.pg_constraint con JOIN pg_catalog.pg_class c ON c.oid = con.conrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n"
        + "WHERE con.contype IN ('p','u','f','c','x') AND n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "UNION ALL\n"
        + "SELECT n.nspname || '|' || t.relname || '.' || c.relname || '|' || CASE WHEN i.indisunique THEN 'uniqueindex' ELSE 'index' END\n"
        + "FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class c ON c.oid = i.indexrelid JOIN pg_catalog.pg_class t ON t.oid = i.indrelid\n"
        + "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n"
        + "WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint con WHERE con.conindid = c.oid AND con.contype IN ('p','u','x'))\n"
        + "  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid\n"
        + "                  WHERE d.classid = 'pg_catalog.pg_class'::regclass AND d.objid = t.oid AND d.deptype = 'e' AND e.extname IN ("
        + AllowedExtensionList
        + "))\n"
        + "UNION ALL\n"
        + "SELECT n.nspname || '|' || t.typname || '|' ||\n"
        + "  CASE t.typtype WHEN 'b' THEN 'basetype' WHEN 'p' THEN 'shelltype' WHEN 'c' THEN 'compositetype' WHEN 'd' THEN 'domain' WHEN 'e' THEN 'enum' WHEN 'r' THEN 'rangetype' WHEN 'm' THEN 'multirangetype' END\n"
        + "FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace\n"
        + "WHERE "
        + StandaloneTypeFilter
        + "\n"
        + "  AND n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid\n"
        + "                  WHERE d.classid = 'pg_catalog.pg_type'::regclass AND d.objid = t.oid AND d.deptype = 'e' AND e.extname IN ("
        + AllowedExtensionList
        + "))\n"
        + "UNION ALL\n"
        + "SELECT n.nspname || '|' || x.objname || '|' || x.kind\n"
        + "FROM (SELECT collnamespace AS ns, collname::text AS objname, 'collation'::text AS kind, oid, 'pg_catalog.pg_collation'::regclass AS catalog FROM pg_catalog.pg_collation\n"
        + "      UNION ALL SELECT connamespace, conname::text, 'conversion', oid, 'pg_catalog.pg_conversion'::regclass FROM pg_catalog.pg_conversion\n"
        + "      UNION ALL SELECT oprnamespace, oprname::text, 'operator', oid, 'pg_catalog.pg_operator'::regclass FROM pg_catalog.pg_operator\n"
        + "      UNION ALL SELECT opcnamespace, opcname::text, 'operatorclass', oid, 'pg_catalog.pg_opclass'::regclass FROM pg_catalog.pg_opclass\n"
        + "      UNION ALL SELECT opfnamespace, opfname::text, 'operatorfamily', oid, 'pg_catalog.pg_opfamily'::regclass FROM pg_catalog.pg_opfamily\n"
        + "      UNION ALL SELECT stxnamespace, stxname::text, 'statistics', oid, 'pg_catalog.pg_statistic_ext'::regclass FROM pg_catalog.pg_statistic_ext\n"
        + "      UNION ALL SELECT cfgnamespace, cfgname::text, 'textsearchconfiguration', oid, 'pg_catalog.pg_ts_config'::regclass FROM pg_catalog.pg_ts_config\n"
        + "      UNION ALL SELECT dictnamespace, dictname::text, 'textsearchdictionary', oid, 'pg_catalog.pg_ts_dict'::regclass FROM pg_catalog.pg_ts_dict\n"
        + "      UNION ALL SELECT prsnamespace, prsname::text, 'textsearchparser', oid, 'pg_catalog.pg_ts_parser'::regclass FROM pg_catalog.pg_ts_parser\n"
        + "      UNION ALL SELECT tmplnamespace, tmplname::text, 'textsearchtemplate', oid, 'pg_catalog.pg_ts_template'::regclass FROM pg_catalog.pg_ts_template) x\n"
        + "JOIN pg_catalog.pg_namespace n ON n.oid = x.ns\n"
        + "WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'\n"
        + "  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid\n"
        + "                  WHERE d.classid = x.catalog AND d.objid = x.oid AND d.deptype = 'e' AND e.extname IN ("
        + AllowedExtensionList
        + "))\n"
        + "ORDER BY 1;";

    /// <summary>Every user-created database-level object as a <c>kind|name</c> row.</summary>
    public static readonly string DatabaseLevelObjects =
        "SELECT 'extension|' || extname FROM pg_catalog.pg_extension WHERE extname NOT IN ("
        + DatabaseLevelAllowedExtensionList
        + ")\n"
        + "UNION ALL SELECT 'eventtrigger|' || evtname FROM pg_catalog.pg_event_trigger\n"
        + "UNION ALL SELECT 'publication|' || pubname FROM pg_catalog.pg_publication\n"
        + "UNION ALL SELECT 'subscription|' || subname FROM pg_catalog.pg_subscription WHERE subdbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())\n"
        + "UNION ALL SELECT 'foreigndatawrapper|' || fdwname FROM pg_catalog.pg_foreign_data_wrapper\n"
        + "UNION ALL SELECT 'foreignserver|' || srvname FROM pg_catalog.pg_foreign_server\n"
        + "UNION ALL SELECT 'usermapping|' || usename || '@' || srvname FROM pg_catalog.pg_user_mappings\n"
        + "UNION ALL SELECT 'language|' || l.lanname FROM pg_catalog.pg_language l WHERE l.oid >= 16384 AND "
        + NotAllowedExtensionObject("pg_catalog.pg_language", "l.oid")
        + "\n"
        + "UNION ALL SELECT 'cast|' || pg_catalog.format_type(c.castsource, NULL) || '->' || pg_catalog.format_type(c.casttarget, NULL) FROM pg_catalog.pg_cast c WHERE c.oid >= 16384 AND "
        + NotAllowedExtensionObject("pg_catalog.pg_cast", "c.oid")
        + "\n"
        + "UNION ALL SELECT 'transform|' || pg_catalog.format_type(t.trftype, NULL) || '/' || l.lanname FROM pg_catalog.pg_transform t JOIN pg_catalog.pg_language l ON l.oid = t.trflang WHERE t.oid >= 16384 AND "
        + NotAllowedExtensionObject("pg_catalog.pg_transform", "t.oid")
        + "\n"
        + "UNION ALL SELECT 'accessmethod|' || a.amname FROM pg_catalog.pg_am a WHERE a.oid >= 16384 AND "
        + NotAllowedExtensionObject("pg_catalog.pg_am", "a.oid")
        + "\n"
        + "UNION ALL SELECT 'largeobject|' || oid::text FROM pg_catalog.pg_largeobject_metadata\n"
        + "UNION ALL SELECT 'defaultprivilege|' || pg_catalog.pg_get_userbyid(defaclrole) || '/' || defaclobjtype::text FROM pg_catalog.pg_default_acl\n"
        + "ORDER BY 1;";

    /// <summary>The <c>dms.EffectiveSchema</c> columns the validator compares; two rows are enough to prove the singleton rule broken.</summary>
    public const string EffectiveSchemaRows =
        "SELECT \"ApiSchemaFormatVersion\", \"EffectiveSchemaHash\", \"ResourceKeyCount\", \"ResourceKeySeedHash\" FROM \"dms\".\"EffectiveSchema\" LIMIT 2;";

    /// <summary>The delivered identity read (<c>Get-SourceIdentitySelectSql</c>) as text, bounded by the spec's <c>LIMIT 2</c>.</summary>
    public const string SourceIdentityRows =
        "SELECT \"SourceIdentity\"::text FROM \"dms\".\"DataStoreIdentity\" LIMIT 2;";

    /// <summary>Every <c>DocumentJson</c> column in the <c>dms</c> schema with its type (<c>Get-DocumentJsonColumnTypeQuerySql</c>, columns separated).</summary>
    public const string DocumentJsonColumns =
        "SELECT table_name, data_type FROM information_schema.columns WHERE table_schema = 'dms' AND column_name = 'DocumentJson' ORDER BY table_name;";

    /// <summary>The live server's version number (<c>180006</c> for 18.6).</summary>
    public const string ServerVersionNum = "SELECT current_setting('server_version_num')::int;";

    /// <summary>Whether the two tables the Populated predicate reads exist.</summary>
    public const string PopulatedPredicateTablesExist =
        "SELECT to_regclass('\"dms\".\"Document\"') IS NOT NULL AND to_regclass('\"dms\".\"ResourceKey\"') IS NOT NULL;";

    /// <summary>The Populated predicate (bootstrap restore consumer): at least one non-descriptor, non-school-year document.</summary>
    public const string HasPopulatedDocument =
        "SELECT EXISTS (SELECT 1 FROM \"dms\".\"Document\" d JOIN \"dms\".\"ResourceKey\" rk ON rk.\"ResourceKeyId\" = d.\"ResourceKeyId\" WHERE rk.\"ResourceName\" NOT LIKE '%Descriptor' AND rk.\"ResourceName\" NOT ILIKE '%SchoolYear%');";

    /// <summary>Splits a <c>schema|name|type</c> row as <c>ConvertFrom-InventoryQueryRow</c> does: exactly three fields, each non-blank after trimming.</summary>
    public static bool TryParseObjectRow(string? row, [NotNullWhen(true)] out DmsInventoryObject? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(row))
        {
            return false;
        }

        string[] fields = row.Split('|');
        if (fields.Length != 3)
        {
            return false;
        }

        string schemaName = fields[0].Trim();
        string objectName = fields[1].Trim();
        string objectType = fields[2].Trim();
        if (schemaName.Length == 0 || objectName.Length == 0 || objectType.Length == 0)
        {
            return false;
        }

        entry = new DmsInventoryObject(schemaName, objectName, objectType);
        return true;
    }

    /// <summary>Splits a <c>kind|name</c> row; the name may itself contain the separator.</summary>
    public static bool TryParseDatabaseLevelObjectRow(
        string? row,
        [NotNullWhen(true)] out DmsDatabaseLevelObject? entry
    )
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(row))
        {
            return false;
        }

        string[] fields = row.Trim().Split('|', 2);
        if (fields.Length != 2 || fields[0].Length == 0 || fields[1].Length == 0)
        {
            return false;
        }

        entry = new DmsDatabaseLevelObject(fields[0], fields[1]);
        return true;
    }

    private static string NotAllowedExtensionObject(string catalog, string objectId) =>
        "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid WHERE d.classid = '"
        + catalog
        + "'::regclass AND d.objid = "
        + objectId
        + " AND d.deptype = 'e' AND e.extname IN ("
        + DatabaseLevelAllowedExtensionList
        + "))";
}
