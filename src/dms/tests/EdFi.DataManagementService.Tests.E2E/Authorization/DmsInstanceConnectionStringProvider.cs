// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

internal static class DmsInstanceConnectionStringProvider
{
    private const string ConnectionStringPrefix =
        "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=";

    public static string Create()
    {
        return Create(AppSettings.DmsInstanceDatabaseName);
    }

    internal static string Create(AppSettingsValues settings)
    {
        return Create(settings.DmsInstanceDatabaseName);
    }

    private static string Create(string databaseName)
    {
        return $"{ConnectionStringPrefix}{databaseName};";
    }
}
