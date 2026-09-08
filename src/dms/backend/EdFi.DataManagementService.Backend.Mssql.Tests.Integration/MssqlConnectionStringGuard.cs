// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

internal static class MssqlConnectionStringGuard
{
    private const string MssqlAdminEnvironmentVariableName = "ConnectionStrings__MssqlAdmin";

    public static void RequireConfiguredForCiOrSkipLocally(string localSkipMessage)
    {
        if (IsGitHubActions() && !HasMssqlAdminEnvironmentVariable())
        {
            Assert.Fail(
                $"{MssqlAdminEnvironmentVariableName} must be exported before MSSQL integration tests run in CI."
            );
        }

        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(localSkipMessage);
        }
    }

    private static bool HasMssqlAdminEnvironmentVariable() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MssqlAdminEnvironmentVariableName));

    private static bool IsGitHubActions() =>
        string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase
        );
}
