// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.SchemaTools.Connections;

/// <summary>
/// The non-secret canonical coordinates of a connection string, as resolved by the exact runtime provider.
/// It deliberately carries NO password (or any other secret): it is safe to serialize to stdout, logs, and
/// diagnostics. Port is null for providers that do not expose a separate port (SQL Server encodes it inside
/// the data source).
/// </summary>
public sealed record ConnectionTarget(string? Database, string? Host, int? Port, string? Username);

/// <summary>
/// The single exact-provider connection-string authority shared by the <c>connection validate</c> and
/// <c>connection inspect</c> verbs and by the <c>ddl provision</c> endpoint override. Implementations parse
/// with the exact runtime provider builder (Npgsql / Microsoft.Data.SqlClient), so alias canonicalization,
/// last-wins duplicate synonyms, and rejection of unsupported keywords match runtime exactly - there is no
/// second alias table or hand-rolled extraction vocabulary.
/// </summary>
public interface IConnectionInspector
{
    /// <summary>
    /// Parses the connection string with the exact runtime provider and returns its non-secret canonical
    /// coordinates. Throws when the provider rejects the string (an unsupported keyword or a wrong-engine
    /// value).
    /// </summary>
    ConnectionTarget Parse(string connectionString);

    /// <summary>
    /// Returns a connection string identical to the input except the endpoint (host/port) is replaced. The
    /// rewrite is applied through the exact provider builder, so every other option - credentials, SSL,
    /// pooling, timeouts, a password containing ';' or '=' - is preserved and correctly re-quoted, and the
    /// provider (not a text scanner) owns which keyword carries the endpoint.
    /// </summary>
    string ApplyEndpointOverride(string connectionString, string host, int port);
}

/// <summary>
/// The single engine-token boundary and inspector selector. Accepts the two supported engines
/// case-insensitively (so publicly documented variants such as <c>MSSQL</c> / <c>PostgreSQL</c> resolve to
/// the canonical token) and rejects anything else - including surrounding-whitespace variants, which are
/// usage errors rather than engines.
/// </summary>
public static class ConnectionInspectors
{
    /// <summary>
    /// Returns the canonical engine token (<c>postgresql</c> / <c>mssql</c>) for a case-insensitive match, or
    /// <c>null</c> for an unsupported value (including a surrounding-whitespace variant).
    /// </summary>
    public static string? CanonicalizeEngine(string engine)
    {
        if (string.Equals(engine, "postgresql", StringComparison.OrdinalIgnoreCase))
        {
            return "postgresql";
        }
        if (string.Equals(engine, "mssql", StringComparison.OrdinalIgnoreCase))
        {
            return "mssql";
        }
        return null;
    }

    /// <summary>
    /// Returns the inspector for the supplied engine token, or <c>null</c> when the engine is unsupported.
    /// </summary>
    public static IConnectionInspector? ForEngine(string engine) =>
        CanonicalizeEngine(engine) switch
        {
            "postgresql" => new PgsqlConnectionInspector(),
            "mssql" => new MssqlConnectionInspector(),
            _ => null,
        };
}
