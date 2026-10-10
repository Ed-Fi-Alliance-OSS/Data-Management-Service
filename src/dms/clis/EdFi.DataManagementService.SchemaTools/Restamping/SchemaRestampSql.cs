// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.SchemaTools.Restamping;

internal static class SchemaRestampSql
{
    private static readonly DbTableName ResourceKey = new(new DbSchemaName("dms"), "ResourceKey");
    private static readonly DbTableName SchemaComponent = new(new DbSchemaName("dms"), "SchemaComponent");

    internal static IReadOnlyList<string> LockCommands(SqlDialect dialect)
    {
        DbTableName[] tables = [EffectiveSchemaTableDefinition.Table, ResourceKey, SchemaComponent];
        return tables.Select(table => LockCommand(dialect, table)).ToArray();
    }

    private static string LockCommand(SqlDialect dialect, DbTableName table)
    {
        var name = SqlIdentifierQuoter.QuoteTableName(dialect, table);
        return dialect switch
        {
            SqlDialect.Pgsql => $"LOCK TABLE {name} IN SHARE ROW EXCLUSIVE MODE",
            SqlDialect.Mssql => $"SELECT * FROM {name} WITH (TABLOCKX, HOLDLOCK)",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect)),
        };
    }

    internal static string ReadFingerprint(SqlDialect dialect) =>
        EffectiveSchemaTableDefinition.RenderReadFingerprintCommandText(dialect);

    internal static string ReadResourceKeys(SqlDialect dialect) =>
        $"SELECT {Columns(dialect, "ResourceKeyId", "ProjectName", "ResourceName", "ResourceVersion")} FROM {Table(dialect, ResourceKey)} ORDER BY {Column(dialect, "ResourceKeyId")}";

    internal static string ReadComponents(SqlDialect dialect) =>
        $"SELECT {Columns(dialect, "EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")} FROM {Table(dialect, SchemaComponent)} ORDER BY {Column(dialect, "ProjectEndpointName")}";

    internal static string DeleteComponents(SqlDialect dialect) =>
        $"DELETE FROM {Table(dialect, SchemaComponent)} WHERE {Column(dialect, "EffectiveSchemaHash")} = @oldHash";

    internal static string UpdateFingerprint(SqlDialect dialect) =>
        $"UPDATE {Table(dialect, EffectiveSchemaTableDefinition.Table)} SET {Column(dialect, "EffectiveSchemaHash")} = @targetHash WHERE {Column(dialect, "EffectiveSchemaSingletonId")} = @singletonId AND {Column(dialect, "EffectiveSchemaHash")} = @oldHash";

    internal static string InsertComponent(SqlDialect dialect) =>
        $"INSERT INTO {Table(dialect, SchemaComponent)} ({Columns(dialect, "EffectiveSchemaHash", "ProjectEndpointName", "ProjectName", "ProjectVersion", "IsExtensionProject")}) VALUES (@targetHash, @endpoint, @projectName, @projectVersion, @isExtension)";

    private static string Table(SqlDialect dialect, DbTableName table) =>
        SqlIdentifierQuoter.QuoteTableName(dialect, table);

    private static string Column(SqlDialect dialect, string name) =>
        SqlIdentifierQuoter.QuoteIdentifier(dialect, new DbColumnName(name));

    private static string Columns(SqlDialect dialect, params string[] names) =>
        string.Join(", ", names.Select(name => Column(dialect, name)));
}
