// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// Database providers the harness measures.
/// </summary>
public enum PerfProvider
{
    Postgresql,
    Mssql,
}

/// <summary>
/// Parsing for <see cref="PerfProvider" /> names as they appear in configuration and artifacts.
/// </summary>
public static class PerfProviders
{
    public static PerfProvider Parse(string providerName) =>
        providerName.Trim().ToLowerInvariant() switch
        {
            "postgresql" => PerfProvider.Postgresql,
            "mssql" => PerfProvider.Mssql,
            _ => throw new ArgumentException(
                $"Unknown provider '{providerName}'. Expected 'postgresql' or 'mssql'.",
                nameof(providerName)
            ),
        };

    /// <summary>
    /// The canonical lowercase name recorded in result artifacts.
    /// </summary>
    public static string ArtifactName(PerfProvider provider) =>
        provider switch
        {
            PerfProvider.Postgresql => "postgresql",
            PerfProvider.Mssql => "mssql",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
}
