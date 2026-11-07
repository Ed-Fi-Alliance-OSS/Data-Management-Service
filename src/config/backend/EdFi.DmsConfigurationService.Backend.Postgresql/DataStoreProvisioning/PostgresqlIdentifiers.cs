// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>
/// The quoting rules of spec 6.8 for the provisioner's PostgreSQL statements: identifiers in double quotes
/// with embedded double quotes doubled; the few non-parameterizable literals (<c>COMMENT ON DATABASE … IS</c>)
/// in single quotes with embedded single quotes doubled. Every catalog read is parameterized and never
/// uses these; the statements that do are built only from validated components (a
/// <see cref="Backend.DataStoreProvisioning.DmsDatabaseName"/>, a claim, an operation id).
/// </summary>
internal static class PostgresqlIdentifiers
{
    public static string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
