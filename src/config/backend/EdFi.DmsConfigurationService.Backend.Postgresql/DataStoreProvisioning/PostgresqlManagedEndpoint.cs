// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.DataStoreProvisioning;

/// <summary>Pure parsing of an already resolved snapshot; never opens a connection or resolves a secret.</summary>
public static class PostgresqlManagedEndpoint
{
    public static bool TryParse(
        string connectionString,
        [NotNullWhen(true)] out ManagedDataStoreEndpoint? endpoint
    )
    {
        endpoint = null;
        try
        {
            NpgsqlConnectionStringBuilder builder = new(connectionString);
            string originalHost = builder.Host?.Trim() ?? string.Empty;
            if (originalHost.StartsWith('['))
            {
                // Check before the shared libpq parser removes brackets; only IPv6 brackets are equivalent.
                int closingBracket = originalHost.IndexOf(']');
                if (
                    closingBracket < 0
                    || !IPAddress.TryParse(originalHost.AsSpan(1, closingBracket - 1), out var ipv6)
                    || ipv6.AddressFamily != AddressFamily.InterNetworkV6
                )
                {
                    return false;
                }
            }
            var hosts = LibpqConnectionSettings.ParseEndpoints(builder.Host!, builder.Port);
            if (hosts.Count != 1 || hosts[0].Port is < 1 or > 65535)
            {
                return false;
            }
            string host = hosts[0].Host;
            if (host.Length == 0 || host.Any(char.IsControl))
            {
                return false;
            }
            bool socket = LibpqConnectionSettings.IsUnixSocket(host);
            if (!socket)
            {
                if (IPAddress.TryParse(host, out var address))
                {
                    host = address.ToString();
                }
                else if (
                    Uri.CheckHostName(host) == UriHostNameType.Dns
                    && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
                )
                {
                    host = host.ToLowerInvariant();
                }
                else
                {
                    return false;
                }
            }
            endpoint = new(
                "postgresql",
                socket ? "unix" : "tcp",
                host,
                hosts[0].Port,
                null,
                PostgresqlAdminConnection.MaintenanceDatabaseName
            );
            return true;
        }
        catch (Exception exception)
            when (exception
                    is ArgumentException
                        or InvalidOperationException
                        or FormatException
                        or OverflowException
            )
        {
            // Provider exceptions may echo input. The boundary returns only a safe refusal.
            return false;
        }
    }
}
