// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// The backend identity-equality contract: the explicit collation DDL emits on generated string columns
/// that store or copy identity values, paired with the in-process comparer that approximates it.
/// DDL generation and runtime composition both obtain it from <see cref="ISqlDialectRules.IdentityEquality"/>,
/// so the declared column collation and the runtime comparer cannot be chosen independently.
/// </summary>
/// <param name="IdentityTextCollation">
/// The collation emitted on identity text columns, or <c>null</c> when the dialect's storage already
/// provides the contract without an explicit collation.
/// </param>
/// <param name="IdentityTextComparer">
/// The comparer that approximates identity text equality under the declared collation. It is not a
/// collation emulator: where the database has resolved an identity, the database verdict remains
/// authoritative.
/// </param>
public sealed record IdentityEqualityContract(
    string? IdentityTextCollation,
    StringComparer IdentityTextComparer
)
{
    /// <summary>
    /// SQL Server: identity text columns are emitted with <c>SQL_Latin1_General_CP1_CI_AS</c>,
    /// independent of the database default collation, and compared with
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    public static IdentityEqualityContract SqlServer { get; } =
        new("SQL_Latin1_General_CP1_CI_AS", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PostgreSQL: identity text columns keep the case-sensitive default storage and are compared with
    /// <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public static IdentityEqualityContract Postgresql { get; } = new(null, StringComparer.Ordinal);

    /// <summary>
    /// Selects the runtime comparer for a column's values from its derived identity-text role only:
    /// a column flagged <see cref="DbColumnModel.UsesSqlServerIdentityCollation"/> uses the SQL Server
    /// identity comparer, and every other column uses <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public static StringComparer ComparerFor(DbColumnModel column)
    {
        ArgumentNullException.ThrowIfNull(column);

        return column.UsesSqlServerIdentityCollation
            ? SqlServer.IdentityTextComparer
            : StringComparer.Ordinal;
    }
}
