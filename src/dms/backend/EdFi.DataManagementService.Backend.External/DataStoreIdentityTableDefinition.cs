// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Shared identifier and query conventions for the provisioned
/// <c>dms.DataStoreIdentity</c> singleton table.
/// </summary>
public static class DataStoreIdentityTableDefinition
{
    public static readonly DbTableName Table = new(new DbSchemaName("dms"), "DataStoreIdentity");

    public static readonly DbColumnName DataStoreIdentitySingletonId = new("DataStoreIdentitySingletonId");

    public static readonly DbColumnName SourceIdentity = new("SourceIdentity");

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

    public static string RenderReadSourceIdentityCommandText(SqlDialect dialect)
    {
        var qualifiedTable = SqlIdentifierQuoter.QuoteTableName(dialect, Table);
        var singletonColumn = SqlIdentifierQuoter.QuoteIdentifier(dialect, DataStoreIdentitySingletonId);
        var sourceIdentityColumn = SqlIdentifierQuoter.QuoteIdentifier(dialect, SourceIdentity);

        return dialect switch
        {
            SqlDialect.Pgsql => $"SELECT {sourceIdentityColumn}::text AS {sourceIdentityColumn}\n"
                + $"FROM {qualifiedTable}\n"
                + $"WHERE {singletonColumn} = 1\n"
                + $"LIMIT 2",
            SqlDialect.Mssql =>
                $"SELECT TOP (2) CONVERT(varchar(64), {sourceIdentityColumn}) AS {sourceIdentityColumn}\n"
                    + $"FROM {qualifiedTable}\n"
                    + $"WHERE {singletonColumn} = 1",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }

    private static string RenderSqlLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
