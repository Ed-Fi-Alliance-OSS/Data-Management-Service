// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.InstanceManagement.Tests.E2E.UnitTests;

/// <summary>
/// Builds a valid Instance E2E fixture environment snapshot (matching the contract published by
/// <c>Invoke-WithInstanceE2ETestProcessContext</c>) for use in pure parsing/hydration unit tests. Tests
/// clone the dictionary and mutate individual keys to exercise validation failures.
/// </summary>
internal static class FixtureEnvironmentBuilder
{
    public const string Tenant1Name = "Tenant_255901";
    public const string Tenant2Name = "Tenant_255902";
    public const string Tenant1Key = "key-255901";
    public const string Tenant1Secret = "secret-255901";
    public const string Tenant2Key = "key-255902";
    public const string Tenant2Secret = "secret-255902";

    public const string RouteManifestJson = """
        [
          {"tenant":"Tenant_255901","districtId":"255901","schoolYear":"2024","databaseOrdinal":1,"databaseName":"db1","dataStoreId":201,"districtContextId":401,"schoolYearContextId":402},
          {"tenant":"Tenant_255901","districtId":"255901","schoolYear":"2025","databaseOrdinal":2,"databaseName":"db2","dataStoreId":202,"districtContextId":403,"schoolYearContextId":404},
          {"tenant":"Tenant_255902","districtId":"255902","schoolYear":"2024","databaseOrdinal":3,"databaseName":"db3","dataStoreId":203,"districtContextId":405,"schoolYearContextId":406}
        ]
        """;

    public static Dictionary<string, string?> Valid() =>
        new(StringComparer.Ordinal)
        {
            ["INSTANCE_E2E_DATABASE_ENGINE"] = "postgresql",
            ["INSTANCE_E2E_DATABASE_1_NAME"] = "db1",
            ["INSTANCE_E2E_DATABASE_2_NAME"] = "db2",
            ["INSTANCE_E2E_DATABASE_3_NAME"] = "db3",
            ["INSTANCE_E2E_ROUTE_MANIFEST"] = RouteManifestJson,
            ["INSTANCE_E2E_FIXTURE_TENANT_1_NAME"] = Tenant1Name,
            ["INSTANCE_E2E_FIXTURE_TENANT_1_VENDOR_ID"] = "101",
            ["INSTANCE_E2E_FIXTURE_TENANT_1_APPLICATION_ID"] = "301",
            ["INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_KEY"] = Tenant1Key,
            ["INSTANCE_E2E_FIXTURE_TENANT_1_CLIENT_SECRET"] = Tenant1Secret,
            ["INSTANCE_E2E_FIXTURE_TENANT_2_NAME"] = Tenant2Name,
            ["INSTANCE_E2E_FIXTURE_TENANT_2_VENDOR_ID"] = "102",
            ["INSTANCE_E2E_FIXTURE_TENANT_2_APPLICATION_ID"] = "302",
            ["INSTANCE_E2E_FIXTURE_TENANT_2_CLIENT_KEY"] = Tenant2Key,
            ["INSTANCE_E2E_FIXTURE_TENANT_2_CLIENT_SECRET"] = Tenant2Secret,
            ["INSTANCE_E2E_FIXTURE_DATASTORE_IDS"] = "201,202,203",
        };

    public static Dictionary<string, string?> With(string key, string? value)
    {
        var environment = Valid();
        environment[key] = value;
        return environment;
    }
}
