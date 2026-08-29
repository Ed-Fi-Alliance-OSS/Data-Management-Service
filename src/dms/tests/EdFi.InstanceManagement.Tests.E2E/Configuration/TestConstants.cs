// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;

namespace EdFi.InstanceManagement.Tests.E2E.Configuration;

/// <summary>
/// Resolves the route-context database identity for the pre-registered instances from the
/// environment contract the suite-owned fixture publishes before the tests run. The database names and
/// connection strings are engine-correct and opaque: they are produced by the setup/build orchestration
/// (PostgreSQL or SQL Server) and consumed here verbatim, so no fixed database name, default password,
/// provider SQL, or connection-string construction lives in test code.
/// </summary>
public static class TestConstants
{
    private const int MinimumDatabaseIndex = 1;
    private const int MaximumDatabaseIndex = 4;

    /// <summary>
    /// Gets the database name for the given 1-based route database ordinal, read verbatim from
    /// <c>INSTANCE_E2E_DATABASE_{index}_NAME</c>.
    /// </summary>
    public static string GetDatabaseName(int index) =>
        ReadRequiredDatabaseValue(index, "INSTANCE_E2E_DATABASE_{0}_NAME");

    /// <summary>
    /// Gets the engine-correct connection string for the given 1-based route database ordinal, read
    /// verbatim from <c>INSTANCE_E2E_DATABASE_{index}_CONNECTION_STRING</c>. The value is a secret-bearing
    /// Docker-network connection string; never log it.
    /// </summary>
    public static string GetConnectionString(int index) =>
        ReadRequiredDatabaseValue(index, "INSTANCE_E2E_DATABASE_{0}_CONNECTION_STRING");

    private static string ReadRequiredDatabaseValue(int index, string variableNameFormat)
    {
        if (index is < MinimumDatabaseIndex or > MaximumDatabaseIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"Only route databases {MinimumDatabaseIndex}-{MaximumDatabaseIndex} are available."
            );
        }

        var variableName = string.Format(CultureInfo.InvariantCulture, variableNameFormat, index);
        var value = Environment.GetEnvironmentVariable(variableName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required environment variable '{variableName}' is not set. The Instance Management E2E "
                    + "suite must be run through the build orchestration that publishes the route-context "
                    + "database environment contract."
            );
        }

        return value;
    }
}
