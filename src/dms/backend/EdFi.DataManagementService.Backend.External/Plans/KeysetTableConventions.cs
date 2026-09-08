// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External.Plans;

/// <summary>
/// Deterministic conventions for read-plan keyset table contracts.
/// </summary>
public static class KeysetTableConventions
{
    private static readonly DbColumnName DocumentIdColumnName = new("DocumentId");

    private static readonly KeysetTableContract PgsqlKeysetTableContract = new(
        Table: new SqlRelationRef.TempTable("page"),
        DocumentIdColumnName: DocumentIdColumnName
    );

    private static readonly KeysetTableContract MssqlKeysetTableContract = new(
        Table: new SqlRelationRef.TempTable("#page"),
        DocumentIdColumnName: DocumentIdColumnName
    );

    /// <summary>
    /// Returns the dialect-specific keyset table contract used by hydration executors.
    /// </summary>
    /// <param name="dialect">SQL dialect.</param>
    public static KeysetTableContract GetKeysetTableContract(SqlDialect dialect)
    {
        return dialect switch
        {
            SqlDialect.Pgsql => PgsqlKeysetTableContract,
            SqlDialect.Mssql => MssqlKeysetTableContract,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported SQL dialect."),
        };
    }
}
