// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The catalog reads of the SQL Server engine. The inventory statements are the SQL Server branches of the
/// delivered <c>Template-RestoreCore.psm1</c> builders, text for text (<c>Get-InventorySchemaQuerySql</c> for
/// the <c>InventoryEnumeration</c> purpose with the exclusion lists expanded, <c>Get-InventoryObjectQuerySql</c>,
/// <c>Get-InventoryDatabaseObjectQuerySql</c>, and <c>Get-InventoryPrincipalQuerySql</c>), so the inventory the
/// provisioner hashes is the inventory the producer hashed; their pipe-separated rows are split as
/// <c>ConvertFrom-InventoryQueryRow</c> splits them (<see cref="Backend.DataStoreProvisioning.DmsInventoryRows"/>).
/// The other reads select typed columns. Every statement is read-only and parameter-free.
/// </summary>
internal static class MssqlInventoryQueries
{
    /// <summary>One schema name per row over the inventory-enumeration scope (includes <c>dbo</c>; the built-in <c>guest</c> and fixed-role schemas only while they hold a user object).</summary>
    public const string SchemaNames =
        "SET NOCOUNT ON; SELECT s.name FROM sys.schemas s WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA') AND (s.name NOT IN ('guest', 'db_accessadmin', 'db_backupoperator', 'db_datareader', 'db_datawriter', 'db_ddladmin', 'db_denydatareader', 'db_denydatawriter', 'db_owner', 'db_securityadmin') OR EXISTS (SELECT 1 FROM sys.objects o WHERE o.schema_id = s.schema_id AND o.is_ms_shipped = 0) OR EXISTS (SELECT 1 FROM sys.types t WHERE t.schema_id = s.schema_id AND t.is_user_defined = 1) OR EXISTS (SELECT 1 FROM sys.xml_schema_collections x WHERE x.schema_id = s.schema_id)) ORDER BY s.name;";

    /// <summary>Every inventoried object as a <c>schema|name|type</c> row.</summary>
    public const string Objects =
        "SET NOCOUNT ON;\n"
        + "SELECT s.name + '|' + o.name + '|' +\n"
        + "  CASE o.type WHEN 'U' THEN 'table' WHEN 'V' THEN 'view' WHEN 'P' THEN 'procedure'\n"
        + "    WHEN 'FN' THEN 'function' WHEN 'IF' THEN 'function' WHEN 'TF' THEN 'function' WHEN 'AF' THEN 'aggregate'\n"
        + "    WHEN 'TR' THEN 'trigger' WHEN 'SO' THEN 'sequence' WHEN 'SN' THEN 'synonym' ELSE LOWER(RTRIM(o.type)) END COLLATE DATABASE_DEFAULT\n"
        + "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id\n"
        + "WHERE o.is_ms_shipped = 0\n"
        + "  AND (o.parent_object_id = 0 OR o.type IN ('TR','TA'))\n"
        + "  AND o.type <> 'TT'\n"
        + "  AND s.name NOT IN ('sys','INFORMATION_SCHEMA')\n"
        + "UNION ALL\n"
        + "SELECT (s.name + '|' + p.name + '.' +\n"
        + "  CASE WHEN COALESCE(kc.is_system_named, fk.is_system_named, cc.is_system_named, dc.is_system_named, 0) = 1\n"
        + "    THEN '#system(' + ISNULL(COL_NAME(o.parent_object_id, COALESCE(dc.parent_column_id, NULLIF(cc.parent_column_id, 0))), '') + ')'\n"
        + "    ELSE o.name END + '|' +\n"
        + "  CASE o.type WHEN 'PK' THEN 'primarykey' WHEN 'UQ' THEN 'unique' WHEN 'F' THEN 'foreignkey' WHEN 'C' THEN 'check'\n"
        + "    WHEN 'D' THEN 'default' WHEN 'EC' THEN 'edgeconstraint' END) COLLATE DATABASE_DEFAULT\n"
        + "FROM sys.objects o JOIN sys.objects p ON p.object_id = o.parent_object_id AND p.type = 'U'\n"
        + "JOIN sys.schemas s ON s.schema_id = o.schema_id\n"
        + "LEFT JOIN sys.key_constraints kc ON kc.object_id = o.object_id\n"
        + "LEFT JOIN sys.foreign_keys fk ON fk.object_id = o.object_id\n"
        + "LEFT JOIN sys.check_constraints cc ON cc.object_id = o.object_id\n"
        + "LEFT JOIN sys.default_constraints dc ON dc.object_id = o.object_id\n"
        + "WHERE o.type IN ('PK','UQ','F','C','D','EC') AND o.is_ms_shipped = 0 AND p.is_ms_shipped = 0\n"
        + "  AND s.name NOT IN ('sys','INFORMATION_SCHEMA')\n"
        + "UNION ALL\n"
        + "SELECT (s.name + '|' + p.name + '.' + i.name + '|' +\n"
        + "  CASE WHEN i.is_unique = 1 THEN 'uniqueindex' ELSE 'index' END) COLLATE DATABASE_DEFAULT\n"
        + "FROM sys.indexes i JOIN sys.objects p ON p.object_id = i.object_id AND p.type IN ('U','V')\n"
        + "JOIN sys.schemas s ON s.schema_id = p.schema_id\n"
        + "WHERE i.type <> 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND p.is_ms_shipped = 0\n"
        + "  AND s.name NOT IN ('sys','INFORMATION_SCHEMA')\n"
        + "UNION ALL\n"
        + "SELECT s.name + '|' + t.name + '|' + CASE WHEN t.is_table_type = 1 THEN 'tabletype' ELSE 'type' END\n"
        + "FROM sys.types t JOIN sys.schemas s ON s.schema_id = t.schema_id\n"
        + "WHERE t.is_user_defined = 1\n"
        + "  AND s.name NOT IN ('sys','INFORMATION_SCHEMA')\n"
        + "UNION ALL\n"
        + "SELECT s.name + '|' + x.name + '|' + 'xmlschemacollection'\n"
        + "FROM sys.xml_schema_collections x JOIN sys.schemas s ON s.schema_id = x.schema_id\n"
        + "WHERE s.name NOT IN ('sys','INFORMATION_SCHEMA')\n"
        + "ORDER BY 1;";

    /// <summary>Every user-created database-level object as a <c>kind|name</c> row.</summary>
    public const string DatabaseLevelObjects =
        "SET NOCOUNT ON;\n"
        + "SELECT ('ddltrigger|' + name) COLLATE DATABASE_DEFAULT FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0\n"
        + "UNION ALL\n"
        + "SELECT ('eventnotification|' + name) COLLATE DATABASE_DEFAULT FROM sys.event_notifications WHERE parent_class = 0\n"
        + "UNION ALL\n"
        + "SELECT ('assembly|' + name) COLLATE DATABASE_DEFAULT FROM sys.assemblies WHERE is_user_defined = 1\n"
        + "UNION ALL\n"
        + "SELECT ('certificate|' + name) COLLATE DATABASE_DEFAULT FROM sys.certificates\n"
        + "UNION ALL\n"
        + "SELECT ('asymmetrickey|' + name) COLLATE DATABASE_DEFAULT FROM sys.asymmetric_keys\n"
        + "UNION ALL\n"
        + "SELECT ('symmetrickey|' + name) COLLATE DATABASE_DEFAULT FROM sys.symmetric_keys\n"
        + "UNION ALL\n"
        + "SELECT ('columnmasterkey|' + name) COLLATE DATABASE_DEFAULT FROM sys.column_master_keys\n"
        + "UNION ALL\n"
        + "SELECT ('columnencryptionkey|' + name) COLLATE DATABASE_DEFAULT FROM sys.column_encryption_keys\n"
        + "UNION ALL\n"
        + "SELECT ('databasescopedcredential|' + name) COLLATE DATABASE_DEFAULT FROM sys.database_scoped_credentials\n"
        + "UNION ALL\n"
        + "SELECT ('externaldatasource|' + name) COLLATE DATABASE_DEFAULT FROM sys.external_data_sources\n"
        + "UNION ALL\n"
        + "SELECT ('externalfileformat|' + name) COLLATE DATABASE_DEFAULT FROM sys.external_file_formats\n"
        + "UNION ALL\n"
        + "SELECT ('externallibrary|' + name) COLLATE DATABASE_DEFAULT FROM sys.external_libraries\n"
        + "UNION ALL\n"
        + "SELECT ('partitionfunction|' + name) COLLATE DATABASE_DEFAULT FROM sys.partition_functions\n"
        + "UNION ALL\n"
        + "SELECT ('partitionscheme|' + name) COLLATE DATABASE_DEFAULT FROM sys.partition_schemes\n"
        + "UNION ALL\n"
        + "SELECT ('fulltextcatalog|' + name) COLLATE DATABASE_DEFAULT FROM sys.fulltext_catalogs\n"
        + "UNION ALL\n"
        + "SELECT ('fulltextstoplist|' + name) COLLATE DATABASE_DEFAULT FROM sys.fulltext_stoplists\n"
        + "UNION ALL\n"
        + "SELECT ('searchpropertylist|' + name) COLLATE DATABASE_DEFAULT FROM sys.registered_search_property_lists\n"
        + "UNION ALL\n"
        + "SELECT ('planguide|' + name) COLLATE DATABASE_DEFAULT FROM sys.plan_guides\n"
        + "UNION ALL\n"
        + "SELECT ('databaseauditspecification|' + name) COLLATE DATABASE_DEFAULT FROM sys.database_audit_specifications\n"
        + "UNION ALL\n"
        + "SELECT ('messagetype|' + name) COLLATE DATABASE_DEFAULT FROM sys.service_message_types WHERE message_type_id > 65535\n"
        + "UNION ALL\n"
        + "SELECT ('contract|' + name) COLLATE DATABASE_DEFAULT FROM sys.service_contracts WHERE service_contract_id > 65535\n"
        + "UNION ALL\n"
        + "SELECT ('service|' + name) COLLATE DATABASE_DEFAULT FROM sys.services WHERE service_id > 65535\n"
        + "UNION ALL\n"
        + "SELECT ('route|' + name) COLLATE DATABASE_DEFAULT FROM sys.routes WHERE name <> N'AutoCreatedLocal'\n"
        + "UNION ALL\n"
        + "SELECT ('remoteservicebinding|' + name) COLLATE DATABASE_DEFAULT FROM sys.remote_service_bindings\n"
        + "ORDER BY 1;";

    /// <summary>Every non-built-in database principal, one name per row.</summary>
    public const string Principals =
        "SET NOCOUNT ON; SELECT name FROM sys.database_principals WHERE is_fixed_role = 0 AND name NOT IN ('dbo','guest','sys','INFORMATION_SCHEMA','public') ORDER BY name;";

    /// <summary>The <c>dms.EffectiveSchema</c> columns the validator compares; two rows are enough to prove the singleton rule broken.</summary>
    public const string EffectiveSchemaRows =
        "SET NOCOUNT ON; SELECT TOP (2) [ApiSchemaFormatVersion], [EffectiveSchemaHash], [ResourceKeyCount], [ResourceKeySeedHash] FROM [dms].[EffectiveSchema];";

    /// <summary>
    /// The delivered identity read (<c>Get-SourceIdentitySelectSql</c>) as text, bounded by the spec's
    /// <c>TOP (2)</c>. The text conversion is unbounded rather than the delivered <c>nvarchar(36)</c>: for the
    /// DDL's <c>uniqueidentifier</c> column the result is identical, and a text column holding a GUID followed
    /// by anything else is never truncated into a valid GUID.
    /// </summary>
    public const string SourceIdentityRows =
        "SET NOCOUNT ON; SELECT TOP (2) CONVERT(nvarchar(max), [SourceIdentity]) FROM [dms].[DataStoreIdentity];";

    /// <summary>
    /// Every <c>DocumentJson</c> column in the <c>dms</c> schema with its type, declared length included
    /// (<c>Get-DocumentJsonColumnTypeQuerySql</c>, columns separated).
    /// </summary>
    public const string DocumentJsonColumns =
        "SET NOCOUNT ON; SELECT o.name, t.name + CASE WHEN c.max_length = -1 THEN '(max)' WHEN t.name IN (N'nvarchar', N'nchar') THEN '(' + CONVERT(nvarchar(11), c.max_length / 2) + ')' WHEN t.name IN (N'varchar', N'char', N'varbinary', N'binary') THEN '(' + CONVERT(nvarchar(11), c.max_length) + ')' ELSE '' END FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id JOIN sys.objects o ON o.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE s.name = N'dms' AND c.name = N'DocumentJson' ORDER BY o.name;";

    /// <summary>The live server's major version (<c>17</c> for SQL Server 2025).</summary>
    public const string ProductMajorVersion = "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));";

    /// <summary>The compatibility level of the session's own database (<c>Get-DatabaseCompatibilityLevelQuerySql</c>, by id rather than by a name literal).</summary>
    public const string CompatibilityLevel =
        "SELECT CONVERT(int, compatibility_level) FROM sys.databases WHERE database_id = DB_ID();";

    /// <summary>Whether the two tables the Populated predicate reads exist.</summary>
    public const string PopulatedPredicateTablesExist =
        "SELECT CONVERT(bit, CASE WHEN OBJECT_ID(N'[dms].[Document]') IS NOT NULL AND OBJECT_ID(N'[dms].[ResourceKey]') IS NOT NULL THEN 1 ELSE 0 END);";

    /// <summary>The Populated predicate (bootstrap restore consumer): at least one non-descriptor, non-school-year document.</summary>
    public const string HasPopulatedDocument =
        "SELECT CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM [dms].[Document] d JOIN [dms].[ResourceKey] rk ON rk.[ResourceKeyId] = d.[ResourceKeyId] WHERE rk.[ResourceName] NOT LIKE '%Descriptor' AND rk.[ResourceName] NOT LIKE '%SchoolYear%') THEN 1 ELSE 0 END);";
}
