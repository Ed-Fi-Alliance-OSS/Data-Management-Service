// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
