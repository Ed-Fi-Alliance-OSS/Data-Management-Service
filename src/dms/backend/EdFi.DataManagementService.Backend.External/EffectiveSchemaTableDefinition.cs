// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Shared identifier and query conventions for the provisioned
/// <c>dms.EffectiveSchema</c> singleton table.
/// </summary>
public static class EffectiveSchemaTableDefinition
{
    public static readonly DbTableName Table = new(new DbSchemaName("dms"), "EffectiveSchema");

    public static readonly DbColumnName EffectiveSchemaSingletonId = new("EffectiveSchemaSingletonId");
    public static readonly DbColumnName ApiSchemaFormatVersion = new("ApiSchemaFormatVersion");
    public static readonly DbColumnName EffectiveSchemaHash = new("EffectiveSchemaHash");
    public static readonly DbColumnName ResourceKeyCount = new("ResourceKeyCount");
    public static readonly DbColumnName ResourceKeySeedHash = new("ResourceKeySeedHash");
    public static readonly DbColumnName AppliedAt = new("AppliedAt");

    public static IReadOnlyList<DbColumnName> FingerprintProjectionColumns { get; } =
    [
        EffectiveSchemaSingletonId,
        ApiSchemaFormatVersion,
        EffectiveSchemaHash,
        ResourceKeyCount,
        ResourceKeySeedHash,
    ];

    public static string TableDisplayName => $"{Table.Schema.Value}.{Table.Name}";

    public static string RenderExistsCommandText(SqlDialect dialect)
    {
        var schemaLiteral = RenderSqlLiteral(Table.Schema.Value);
        var tableLiteral = RenderSqlLiteral(Table.Name);

        return dialect switch
        {
            SqlDialect.Pgsql =>
                $"SELECT 1 FROM information_schema.tables WHERE table_schema = {schemaLiteral} AND table_name = {tableLiteral}",
            SqlDialect.Mssql =>
                $"SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = {schemaLiteral} AND TABLE_NAME = {tableLiteral}",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    public static string RenderReadFingerprintCommandText(SqlDialect dialect)
    {
        var columnList = string.Join(
            ", ",
            FingerprintProjectionColumns.Select(column =>
                SqlIdentifierQuoter.QuoteIdentifier(dialect, column)
            )
        );

        var qualifiedTable = SqlIdentifierQuoter.QuoteTableName(dialect, Table);
        var orderByColumn = SqlIdentifierQuoter.QuoteIdentifier(dialect, EffectiveSchemaSingletonId);

        return dialect switch
        {
            SqlDialect.Pgsql =>
                $"SELECT {columnList}\nFROM {qualifiedTable}\nORDER BY {orderByColumn}\nLIMIT 2",
            SqlDialect.Mssql =>
                $"SELECT TOP (2) {columnList}\nFROM {qualifiedTable}\nORDER BY {orderByColumn}",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderSqlLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
