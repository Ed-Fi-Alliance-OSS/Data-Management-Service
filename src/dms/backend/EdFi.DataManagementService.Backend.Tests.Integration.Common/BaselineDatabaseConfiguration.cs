// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Configuration;

namespace EdFi.DataManagementService.Backend.Tests.Integration.Common;

/// <summary>
/// Resolves the relational-test admin connection string for callers that lease
/// per-test isolated databases from a generated-DDL baseline. Resolution order:
/// the ConnectionStrings__DatabaseConnection environment variable wins, then the
/// DatabaseConnection entry of appsettings.json / appsettings.Test.json in the
/// consumer's working directory, otherwise throws.
/// </summary>
public static class BaselineDatabaseConfiguration
{
    private const string EnvironmentVariableName = "ConnectionStrings__DatabaseConnection";
    private const string ConfigurationConnectionStringName = "DatabaseConnection";

    private static IConfiguration? _configuration;

    public static string DatabaseConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariableName)
        ?? Config().GetConnectionString(ConfigurationConnectionStringName)
        ?? throw new InvalidOperationException(
            $"Connection string '{ConfigurationConnectionStringName}' is not configured. "
                + $"Set the {EnvironmentVariableName} environment variable, or add a "
                + "DatabaseConnection entry to appsettings.json / appsettings.Test.json."
        );

    private static IConfiguration Config()
    {
        if (_configuration is not null)
        {
            return _configuration;
        }

        var builder = new ConfigurationBuilder();
        if (File.Exists("appsettings.json"))
        {
            builder.AddJsonFile("appsettings.json");
        }
        if (File.Exists("appsettings.Test.json"))
        {
            builder.AddJsonFile("appsettings.Test.json");
        }
        _configuration = builder.Build();
        return _configuration;
    }
}
