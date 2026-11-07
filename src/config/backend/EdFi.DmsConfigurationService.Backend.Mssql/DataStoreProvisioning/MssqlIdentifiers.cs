// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// The quoting rules of spec 6.8 for the provisioner's SQL Server statements: identifiers in square brackets
/// with an embedded closing bracket doubled; the few non-parameterizable literals (<c>RESTORE … FROM DISK</c>,
/// <c>MOVE … TO</c>, <c>sp_getapplock @Resource</c> where it is inlined) as Unicode <c>N'…'</c> literals with
/// embedded single quotes doubled. Every catalog read is parameterized and never uses these; the statements
/// that do are built only from validated components (a
/// <see cref="Backend.DataStoreProvisioning.DmsDatabaseName"/>, an operation id, a configured path).
/// </summary>
internal static class MssqlIdentifiers
{
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }

    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
