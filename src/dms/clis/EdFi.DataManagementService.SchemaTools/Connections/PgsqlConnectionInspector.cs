// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;

namespace EdFi.DataManagementService.SchemaTools.Connections;

/// <summary>
/// PostgreSQL <see cref="IConnectionInspector"/> backed by <see cref="NpgsqlConnectionStringBuilder"/> - the
/// exact runtime provider builder. The constructor invokes the setter, which throws on any keyword Npgsql
/// does not recognize and canonicalizes aliases (Server -> Host; User Id / UID / Userid -> Username;
/// DB -> Database) with last-wins semantics.
/// </summary>
public sealed class PgsqlConnectionInspector : IConnectionInspector
{
    public ConnectionTarget Parse(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new ConnectionTarget(
            Database: NullIfEmpty(builder.Database),
            Host: NullIfEmpty(builder.Host),
            // Npgsql exposes Port with its canonical default (5432) when the string omits it.
            Port: builder.Port,
            Username: NullIfEmpty(builder.Username)
        );
    }

    public string ApplyEndpointOverride(string connectionString, string host, int port)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Host = host, Port = port };
        return builder.ConnectionString;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
