// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace EdFi.DataManagementService.Core.Configuration;

public enum RelationalProviderMetadataStatus
{
    Missing,
    Unknown,
    Supported,
}

public sealed record RelationalProviderToken
{
    public const string PostgresqlValue = "postgresql";
    public const string SqlServerValue = "sqlserver";

    public static RelationalProviderToken Postgresql { get; } = new(PostgresqlValue);

    public static RelationalProviderToken SqlServer { get; } = new(SqlServerValue);

    private RelationalProviderToken(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool TryNormalize(
        string? providerToken,
        [NotNullWhen(true)] out RelationalProviderToken? normalizedToken
    )
    {
        normalizedToken = null;

        if (string.IsNullOrEmpty(providerToken))
        {
            return false;
        }

        if (string.Equals(providerToken, PostgresqlValue, StringComparison.OrdinalIgnoreCase))
        {
            normalizedToken = Postgresql;
            return true;
        }

        if (string.Equals(providerToken, SqlServerValue, StringComparison.OrdinalIgnoreCase))
        {
            normalizedToken = SqlServer;
            return true;
        }

        return false;
    }

    public override string ToString() => Value;
}
