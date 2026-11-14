// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>
/// Internal v1 destination identity. Never expose it in management JSON or logs.
/// Equality assumes a stable single-server endpoint; it does not prove physical identity through DNS retargeting.
/// Credentials, target database, application name and timeouts are deliberately absent.
/// </summary>
public sealed record ManagedDataStoreEndpoint(
    string Provider,
    string Transport,
    string Host,
    int? Port,
    string? Instance,
    string MaintenanceDatabase
)
{
    public int Version { get; } = 1;

    public override string ToString() => nameof(ManagedDataStoreEndpoint);

    /// <summary>Fixed-order, unambiguous encoding for persistence and target reservations.</summary>
    public string CanonicalEncoding =>
        JsonSerializer.Serialize(
            new object?[] { Version, Provider, Transport, Host, Port, Instance, MaintenanceDatabase }
        );

    /// <summary>
    /// Requires the structured canonical form produced by the provider parsers. Direct repository
    /// callers must not create separate reservation keys for alternate spellings of the same locator.
    /// </summary>
    public bool IsCanonical()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host != Host.Trim() || Host.Any(char.IsControl))
        {
            return false;
        }

        bool validShape = this switch
        {
            {
                Provider: "postgresql",
                Transport: "tcp" or "unix",
                Port: > 0 and <= 65535,
                Instance: null,
                MaintenanceDatabase: "postgres"
            } => true,
            { Provider: "mssql", Transport: "tcp", MaintenanceDatabase: "master" } => (
                Port is > 0 and <= 65535 && Instance is null
            )
                || (
                    Port is null
                    && !string.IsNullOrWhiteSpace(Instance)
                    && Instance.All(c =>
                        char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-'
                    )
                ),
            _ => false,
        };
        if (!validShape)
        {
            return false;
        }

        if (Transport == "unix")
        {
            // LibpqConnectionSettings recognizes '/' paths and '@' abstract sockets. Its single-host
            // parser trims entries and splits commas, but preserves socket case, brackets and colons.
            return Host[0] is '/' or '@' && !Host.Contains(',');
        }

        if (IPAddress.TryParse(Host, out var address))
        {
            return Host == address.ToString();
        }

        return Uri.CheckHostName(Host) == UriHostNameType.Dns
            && Host.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-');
    }

    public static string CatalogProvider(string provisionerProvider) =>
        provisionerProvider switch
        {
            "postgresql" => "postgresql",
            "mssql" => "sqlserver",
            _ => throw new ArgumentException(
                "Unsupported managed target provider.",
                nameof(provisionerProvider)
            ),
        };
}
