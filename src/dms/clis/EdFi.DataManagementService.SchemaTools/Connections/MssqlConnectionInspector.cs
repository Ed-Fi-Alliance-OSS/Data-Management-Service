// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.SchemaTools.Connections;

/// <summary>
/// SQL Server <see cref="IConnectionInspector"/> backed by <see cref="SqlConnectionStringBuilder"/> - the
/// exact runtime provider builder. Database and Initial Catalog are synonyms (collapsed last-wins and exposed
/// as InitialCatalog); Server / Data Source / Addr / Address / Network Address map to DataSource;
/// User Id / UID map to UserID; unsupported keywords throw.
/// </summary>
public sealed class MssqlConnectionInspector : IConnectionInspector
{
    public ConnectionTarget Parse(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        // SQL Server encodes host and port together inside DataSource ("host,port"; a named instance as
        // "host\instance") and exposes no separate port property, so Port is null and callers split DataSource.
        return new ConnectionTarget(
            Database: NullIfEmpty(builder.InitialCatalog),
            Host: NullIfEmpty(builder.DataSource),
            Port: null,
            Username: NullIfEmpty(builder.UserID)
        );
    }

    public string ApplyEndpointOverride(string connectionString, string host, int port)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { DataSource = $"{host},{port}" };
        return builder.ConnectionString;
    }

    public ConnectionEndpointIdentity ClassifyEndpoint(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        // SqlClient canonicalizes the endpoint KEYWORD to DataSource but leaves the value's internal grammar
        // (protocol/host/instance/port) intact; SqlServerEndpointClassifier is the single interpreter of it.
        // A non-blank Failover Partner is alternate routing (a locally named primary can redirect to a remote
        // physical server), surfaced so the local-topology check can reject it.
        return SqlServerEndpointClassifier.Classify(
            builder.DataSource,
            !string.IsNullOrWhiteSpace(builder.FailoverPartner)
        );
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
