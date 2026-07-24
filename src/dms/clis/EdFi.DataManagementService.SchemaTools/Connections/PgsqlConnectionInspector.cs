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

    public ConnectionEndpointIdentity ClassifyEndpoint(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        string? host = builder.Host;
        if (string.IsNullOrEmpty(host))
        {
            return new ConnectionEndpointIdentity(
                ConnectionEndpointKinds.Missing,
                ConnectionEndpointProtocols.Default,
                Host: null,
                Port: null,
                Instance: null,
                HasAlternateRouting: false
            );
        }

        // Npgsql accepts a comma-separated multi-host list (PostgreSQL's own failover/load-balancing form).
        // It is not a single local endpoint; classify it as multi-host without picking one host.
        if (host.Contains(','))
        {
            return new ConnectionEndpointIdentity(
                ConnectionEndpointKinds.MultiHost,
                ConnectionEndpointProtocols.Tcp,
                Host: null,
                Port: null,
                Instance: null,
                HasAlternateRouting: false
            );
        }

        // Npgsql exposes Port with its canonical default (5432) when the string omits it. PostgreSQL has no
        // named instances, and multi-host is the only alternate-routing form (handled above).
        return new ConnectionEndpointIdentity(
            ConnectionEndpointKinds.SingleHost,
            ConnectionEndpointProtocols.Tcp,
            host,
            builder.Port,
            Instance: null,
            HasAlternateRouting: false
        );
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
