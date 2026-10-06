// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E;

/// <summary>One route of the Instance Management fixture: a tenant's data store and its route qualifiers.</summary>
public sealed record FixtureRoute(string Tenant, string DistrictId, string SchoolYear, int DataStoreId)
{
    public string Qualifier => $"{DistrictId}/{SchoolYear}";
}

/// <summary>
/// The Instance Management E2E environment this project runs against, read from the variables
/// <c>Invoke-WithInstanceE2ETestProcessContext</c> in <c>build-dms.ps1</c> exports. A missing or malformed value
/// fails the whole run with a setup message: this project never skips.
/// </summary>
public sealed class ProjectionE2EEnvironment
{
    private const string SetupFailure = "DMS projection reader E2E setup failed";

    internal ProjectionE2EEnvironment(
        Uri dmsBaseUrl,
        Uri configServiceUrl,
        string adminClientId,
        string adminClientSecret,
        string databaseEngine,
        string routeTwoConnectionString,
        IReadOnlyList<FixtureRoute> routes,
        IReadOnlyDictionary<string, int> vendorIdsByTenant
    )
    {
        DmsBaseUrl = dmsBaseUrl;
        ConfigServiceUrl = configServiceUrl;
        AdminClientId = adminClientId;
        AdminClientSecret = adminClientSecret;
        DatabaseEngine = databaseEngine;
        RouteTwoConnectionString = routeTwoConnectionString;
        Routes = routes;
        VendorIdsByTenant = vendorIdsByTenant;
    }

    public Uri DmsBaseUrl { get; }

    public Uri ConfigServiceUrl { get; }

    public string AdminClientId { get; }

    public string AdminClientSecret { get; }

    /// <summary><c>postgresql</c> or <c>mssql</c>.</summary>
    public string DatabaseEngine { get; }

    /// <summary>The connection string registered for route database 2. Secret-bearing: never logged.</summary>
    public string RouteTwoConnectionString { get; }

    public IReadOnlyList<FixtureRoute> Routes { get; }

    public IReadOnlyDictionary<string, int> VendorIdsByTenant { get; }

    public FixtureRoute Route(string qualifier) =>
        Routes.SingleOrDefault(route => route.Qualifier == qualifier)
        ?? throw new InvalidOperationException($"{SetupFailure}: the fixture has no route {qualifier}.");

    public static ProjectionE2EEnvironment Read()
    {
        Uri dmsBaseUrl = RequiredUrl("INSTANCE_E2E_DMS_BASE_URL");
        Uri configServiceUrl = RequiredUrl("INSTANCE_E2E_CONFIG_SERVICE_URL");
        string adminClientId = Required("INSTANCE_E2E_CONFIG_ADMIN_CLIENT_ID");
        string adminClientSecret = Required("INSTANCE_E2E_CONFIG_ADMIN_CLIENT_SECRET");
        string databaseEngine = Required("INSTANCE_E2E_DATABASE_ENGINE");
        if (databaseEngine is not ("postgresql" or "mssql"))
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: INSTANCE_E2E_DATABASE_ENGINE must be 'postgresql' or 'mssql'."
            );
        }

        Dictionary<string, int> vendorIdsByTenant = new(StringComparer.Ordinal);
        foreach (int ordinal in new[] { 1, 2 })
        {
            string tenant = Required($"INSTANCE_E2E_FIXTURE_TENANT_{ordinal}_NAME");
            vendorIdsByTenant[tenant] = RequiredInt($"INSTANCE_E2E_FIXTURE_TENANT_{ordinal}_VENDOR_ID");
        }

        return new ProjectionE2EEnvironment(
            dmsBaseUrl,
            configServiceUrl,
            adminClientId,
            adminClientSecret,
            databaseEngine,
            Required("INSTANCE_E2E_DATABASE_2_CONNECTION_STRING"),
            ReadRoutes(Required("INSTANCE_E2E_ROUTE_MANIFEST")),
            vendorIdsByTenant
        );
    }

    private static List<FixtureRoute> ReadRoutes(string manifest)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(manifest);
            return
            [
                .. document
                    .RootElement.EnumerateArray()
                    .Select(route => new FixtureRoute(
                        route.GetProperty("tenant").GetString()!,
                        route.GetProperty("districtId").GetString()!,
                        route.GetProperty("schoolYear").GetString()!,
                        route.GetProperty("dataStoreId").GetInt32()
                    )),
            ];
        }
        catch (Exception exception)
            when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: INSTANCE_E2E_ROUTE_MANIFEST is not the route manifest build-dms.ps1 exports.",
                exception
            );
        }
    }

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{SetupFailure}: required environment variable '{name}' is not set. Run this project through "
                    + "'./build-dms.ps1 InstanceE2ETest', which provisions the Instance Management fixture and "
                    + "exports it to the test process."
            );
        }

        return value;
    }

    private static Uri RequiredUrl(string name) =>
        Uri.TryCreate(Required(name).TrimEnd('/') + "/", UriKind.Absolute, out Uri? url)
            ? url
            : throw new InvalidOperationException($"{SetupFailure}: '{name}' is not an absolute URL.");

    private static int RequiredInt(string name) =>
        int.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new InvalidOperationException($"{SetupFailure}: '{name}' is not an integer.");
}
