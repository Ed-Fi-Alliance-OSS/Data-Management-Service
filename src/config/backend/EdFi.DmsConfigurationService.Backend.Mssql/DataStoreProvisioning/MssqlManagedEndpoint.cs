// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// Pure parsing of an already resolved snapshot. Requires explicit TCP to avoid implicit protocol selection;
/// supports one host with a port or named instance, never both. Named instance discovery assumes stable routing.
/// Hidden client aliases, failover routing and DNS retargeting remain unsupported deployment topologies;
/// this parser cannot discover them or establish a physical server identity.
/// </summary>
public static class MssqlManagedEndpoint
{
    public static bool TryParse(
        string connectionString,
        [NotNullWhen(true)] out ManagedDataStoreEndpoint? endpoint
    )
    {
        endpoint = null;
        try
        {
            SqlConnectionStringBuilder builder = new(connectionString);
            if (
                !string.IsNullOrEmpty(builder.FailoverPartner)
                || builder.MultiSubnetFailover
                || builder.UserInstance
                || builder.ApplicationIntent != ApplicationIntent.ReadWrite
                || !string.IsNullOrEmpty(builder.AttachDBFilename)
                || !builder.DataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
            )
            {
                return false;
            }
            string locator = builder.DataSource[4..];
            string? instance = null;
            int? port = 1433;
            int separator = locator.IndexOf('\\');
            if (separator >= 0)
            {
                instance = locator[(separator + 1)..];
                locator = locator[..separator];
                if (
                    instance.Length == 0
                    || !instance.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                )
                {
                    return false;
                }
                instance = instance.ToLowerInvariant();
                port = null;
            }
            else if ((separator = locator.IndexOf(',')) >= 0)
            {
                if (
                    !int.TryParse(
                        locator[(separator + 1)..],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int parsedPort
                    ) || parsedPort is < 1 or > 65535
                )
                {
                    return false;
                }
                port = parsedPort;
                locator = locator[..separator];
            }
            string host = locator;
            if (host.StartsWith('[') && host.EndsWith(']'))
            {
                if (
                    !IPAddress.TryParse(host.AsSpan(1, host.Length - 2), out var ipv6)
                    || ipv6.AddressFamily != AddressFamily.InterNetworkV6
                )
                {
                    return false;
                }
                host = ipv6.ToString();
            }
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
            endpoint = new(
                "mssql",
                "tcp",
                host,
                port,
                instance,
                MssqlAdminConnection.MaintenanceDatabaseName
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
            return false;
        }
    }
}
