// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

internal static class DataStoreConnectionStringProvider
{
    // The Docker-network Configuration Service registration connection string is resolved once from
    // the selected engine and environment by the build orchestration and passed to the test process
    // (AppSettings.DataStoreConnectionString). Return it verbatim so the registered data store matches
    // the running engine (dms-postgresql:5432 or dms-mssql,1433) instead of a hardcoded PostgreSQL form.
    public static string Create()
    {
        return AppSettings.DataStoreConnectionString;
    }

    internal static string Create(AppSettingsValues settings)
    {
        return settings.DataStoreConnectionString;
    }
}
