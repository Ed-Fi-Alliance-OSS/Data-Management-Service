// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Resolves the SQL Server connection for integration tests from the
/// ConnectionStrings__MssqlAdmin environment variable. Tests skip locally when
/// the variable is absent and fail in GitHub Actions when it is absent.
/// </summary>
public static class MssqlTestConfiguration
{
    private const string AdminEnvironmentVariableName = "ConnectionStrings__MssqlAdmin";

    public const string DatabaseName = "edfi_configurationservice_mssql_integration";

    public static string? AdminConnectionString =>
        Environment.GetEnvironmentVariable(AdminEnvironmentVariableName);

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(AdminConnectionString);

    public static string DatabaseConnectionString =>
        new SqlConnectionStringBuilder(AdminConnectionString)
        {
            InitialCatalog = DatabaseName,
        }.ConnectionString;

    public static IOptions<DatabaseOptions> DatabaseOptions =>
        Options.Create(
            new DatabaseOptions()
            {
                DatabaseConnection = DatabaseConnectionString,
                EncryptionKey = "IntegrationTestEncryptionKey32Chars",
            }
        );

    public static void RequireConfiguredForCiOrSkipLocally(string localSkipMessage)
    {
        if (IsGitHubActions() && !IsConfigured)
        {
            Assert.Fail(
                $"{AdminEnvironmentVariableName} must be exported before MSSQL integration tests run in CI."
            );
        }

        if (!IsConfigured)
        {
            Assert.Ignore(localSkipMessage);
        }
    }

    private static bool IsGitHubActions() =>
        string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );
}
