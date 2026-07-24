// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.DataManagementService.SchemaTools.Connections;

/// <summary>
/// The non-secret CLASSIFICATION of a connection string's endpoint. This is deliberately distinct from two
/// other concepts:
/// <list type="bullet">
/// <item>provider validity - whether the exact runtime provider accepts the string at all (owned by
/// <see cref="IConnectionInspector.Parse"/>); and</item>
/// <item>local-topology acceptability - whether the endpoint is the single local database the docker-compose
/// stack serves (decided by a later consumer, not here).</item>
/// </list>
/// A provider-valid connection can still be a shape the local topology cannot use - a named instance, a
/// multi-host list, a non-TCP protocol, or one carrying alternate routing (SQL Server <c>Failover Partner</c>).
/// Those are coherent classifications here, never parse failures. It carries no secret (no password).
/// </summary>
public sealed record ConnectionEndpointIdentity(
    string Kind,
    string Protocol,
    string? Host,
    int? Port,
    string? Instance,
    bool HasAlternateRouting
);

/// <summary>
/// Endpoint classification kinds. Values are the exact (camelCase) tokens serialized by the
/// <c>connection inspect</c> verb.
/// </summary>
public static class ConnectionEndpointKinds
{
    /// <summary>No host/server is specified.</summary>
    public const string Missing = "missing";

    /// <summary>Exactly one TCP host (possibly with an explicit port).</summary>
    public const string SingleHost = "singleHost";

    /// <summary>A PostgreSQL multi-host (comma-separated) list - its own failover/load-balancing form.</summary>
    public const string MultiHost = "multiHost";

    /// <summary>A SQL Server named instance (<c>host\instance</c>).</summary>
    public const string NamedInstance = "namedInstance";

    /// <summary>A shape the local-topology check cannot interpret as a single TCP host (e.g. a non-TCP protocol).</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Endpoint transport protocols. Values are the exact (camelCase) tokens serialized by the
/// <c>connection inspect</c> verb.
/// </summary>
public static class ConnectionEndpointProtocols
{
    /// <summary>No explicit protocol prefix (the provider's default).</summary>
    public const string Default = "default";

    /// <summary>TCP (an explicit <c>tcp:</c> prefix).</summary>
    public const string Tcp = "tcp";

    /// <summary>SQL Server named pipes (<c>np:</c>).</summary>
    public const string NamedPipes = "namedPipes";

    /// <summary>SQL Server shared memory / local procedure call (<c>lpc:</c>).</summary>
    public const string SharedMemory = "sharedMemory";

    /// <summary>
    /// SQL Server Dedicated Administrator Connection (<c>admin:</c>) - a special administrative channel, not a
    /// normal data endpoint, so it is never a local CMS endpoint (retained rather than erased into <c>tcp</c>).
    /// </summary>
    public const string Admin = "admin";

    /// <summary>
    /// A recognized-but-unclassified protocol prefix (the deprecated <c>via:</c>) or a TCP data source whose
    /// port suffix cannot be interpreted (empty, non-numeric, out of range, or ambiguous).
    /// </summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The single internal interpreter of a SQL Server <see cref="Microsoft.Data.SqlClient.SqlConnectionStringBuilder.DataSource"/>
/// value. SqlClient canonicalizes the connection-string KEYWORDS (Server / Data Source / Addr / ... -> DataSource)
/// but does not split the resulting value into protocol, host, instance, and port - so this interpreter owns
/// that finite grammar: <c>[protocol:]server[\instance][,port]</c>. There is no PowerShell endpoint parser.
/// </summary>
public static class SqlServerEndpointClassifier
{
    /// <summary>
    /// Classifies the provider-returned data source (and whether the connection carries a non-blank
    /// <c>Failover Partner</c>) into a non-secret <see cref="ConnectionEndpointIdentity"/>.
    /// </summary>
    public static ConnectionEndpointIdentity Classify(string? dataSource, bool hasAlternateRouting)
    {
        string trimmed = (dataSource ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return new ConnectionEndpointIdentity(
                ConnectionEndpointKinds.Missing,
                ConnectionEndpointProtocols.Default,
                Host: null,
                Port: null,
                Instance: null,
                hasAlternateRouting
            );
        }

        // Optional protocol prefix: tcp: / np: / lpc: / admin: / via:. A colon that is not one of these
        // recognized prefixes (e.g. inside an IPv6 literal) is left as part of the server value.
        string protocol = ConnectionEndpointProtocols.Default;
        string remainder = trimmed;
        int colonIndex = trimmed.IndexOf(':');
        if (colonIndex > 0)
        {
            string prefix = trimmed[..colonIndex].Trim().ToLowerInvariant();
            string? mapped = prefix switch
            {
                "tcp" => ConnectionEndpointProtocols.Tcp,
                "admin" => ConnectionEndpointProtocols.Admin,
                "np" => ConnectionEndpointProtocols.NamedPipes,
                "lpc" => ConnectionEndpointProtocols.SharedMemory,
                "via" => ConnectionEndpointProtocols.Unknown,
                _ => null,
            };
            if (mapped is not null)
            {
                protocol = mapped;
                remainder = trimmed[(colonIndex + 1)..].Trim();
            }
        }

        // Only TCP (an explicit tcp: prefix or the default) can be a single local endpoint. Named pipes,
        // shared memory, the deprecated via:, and the Dedicated Administrator Connection (admin:) are
        // coherent-but-unsupported shapes: retained as their own protocol, never erased into tcp, and never a
        // local CMS endpoint.
        if (protocol is not (ConnectionEndpointProtocols.Default or ConnectionEndpointProtocols.Tcp))
        {
            return Unsupported(protocol);
        }

        // TCP / default grammar: server[\instance][,port]. The port follows a comma; the instance follows a
        // backslash and precedes the comma. A port suffix that is empty, non-numeric, outside 1-65535, or
        // ambiguous (more than one comma) is malformed and cannot be a TCP endpoint - classify it as an
        // unsupported shape rather than silently dropping the port to null, which would make a bare host and a
        // malformed one indistinguishable to the later locality check.
        string serverAndInstance = remainder;
        int? explicitPort = null;
        int commaIndex = remainder.IndexOf(',');
        if (commaIndex >= 0)
        {
            serverAndInstance = remainder[..commaIndex].Trim();
            string portToken = remainder[(commaIndex + 1)..].Trim();
            if (
                portToken.Contains(',')
                || !int.TryParse(
                    portToken,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int parsedPort
                )
                || parsedPort < 1
                || parsedPort > 65535
            )
            {
                return Unsupported(ConnectionEndpointProtocols.Unknown);
            }
            explicitPort = parsedPort;
        }

        string? host = serverAndInstance;
        string? instance = null;
        int backslashIndex = serverAndInstance.IndexOf('\\');
        if (backslashIndex >= 0)
        {
            host = serverAndInstance[..backslashIndex].Trim();
            instance = serverAndInstance[(backslashIndex + 1)..].Trim();
            if (instance.Length == 0)
            {
                // A backslash delimiter with no instance name is malformed routing syntax ("host\" or
                // "host\,1433"), not a bare host; it must not collapse into the local single-host identity.
                return Unsupported(ConnectionEndpointProtocols.Unknown);
            }
        }
        if (host is { Length: 0 })
        {
            host = null;
        }

        // A port with no server (e.g. ",1433") is not a usable endpoint. A missing endpoint has no meaningful
        // transport, so it always reports the default protocol.
        if (host is null)
        {
            return new ConnectionEndpointIdentity(
                ConnectionEndpointKinds.Missing,
                ConnectionEndpointProtocols.Default,
                Host: null,
                Port: null,
                Instance: null,
                hasAlternateRouting
            );
        }

        // A named instance resolves its port dynamically via SQL Server Browser unless one is given, so do
        // not fabricate a default port here.
        if (instance is not null)
        {
            return new ConnectionEndpointIdentity(
                ConnectionEndpointKinds.NamedInstance,
                protocol,
                host,
                explicitPort,
                instance,
                hasAlternateRouting
            );
        }

        // A single TCP host: canonicalize an omitted port to the SQL Server default (1433) so the later
        // locality check compares one concrete value rather than guessing.
        return new ConnectionEndpointIdentity(
            ConnectionEndpointKinds.SingleHost,
            protocol,
            host,
            explicitPort ?? 1433,
            Instance: null,
            hasAlternateRouting
        );

        ConnectionEndpointIdentity Unsupported(string unsupportedProtocol) =>
            new(
                ConnectionEndpointKinds.Unsupported,
                unsupportedProtocol,
                Host: null,
                Port: null,
                Instance: null,
                hasAlternateRouting
            );
    }
}
