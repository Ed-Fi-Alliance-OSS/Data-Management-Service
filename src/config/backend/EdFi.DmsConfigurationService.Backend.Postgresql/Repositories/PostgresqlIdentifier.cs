// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;

internal static class PostgresqlIdentifier
{
    public static string Quote(string identifier)
    {
        string escapedIdentifier = identifier.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escapedIdentifier}\"";
    }

    public static string Qualify(string tableAlias, string identifier) => $"{tableAlias}.{Quote(identifier)}";

    public static string OrderBy(string identifier, bool isDescending, string? tableAlias = null)
    {
        string orderedIdentifier = tableAlias is null ? Quote(identifier) : Qualify(tableAlias, identifier);
        string direction = isDescending ? "DESC" : "ASC";

        return $"ORDER BY {orderedIdentifier} {direction}";
    }
}
