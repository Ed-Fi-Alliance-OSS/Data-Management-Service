// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// One pre-registered route (tenant + districtId/schoolYear) mapped to its route-context database and the
/// CMS records the suite-owned fixture created for it.
/// </summary>
public sealed record InstanceFixtureRoute(
    string TenantName,
    string DistrictId,
    string SchoolYear,
    int DatabaseOrdinal,
    string DatabaseName,
    int DataStoreId,
    int DistrictContextId,
    int SchoolYearContextId
)
{
    /// <summary>The route qualifier segment pair as used in URLs and step definitions, e.g. "255901/2024".</summary>
    public string RouteQualifier => $"{DistrictId}/{SchoolYear}";
}

/// <summary>
/// One pre-registered fixture tenant with its vendor, application, and application credentials.
/// </summary>
public sealed record InstanceFixtureTenant(
    string Name,
    int VendorId,
    int ApplicationId,
    string ClientKey,
    string ClientSecret
);

/// <summary>
/// Run-scoped, immutable snapshot of the suite-owned Instance Management E2E fixture, loaded once from the
/// environment contract published by the build orchestration (<c>Invoke-WithInstanceE2ETestProcessContext</c>).
/// It survives <see cref="InstanceManagementContext.Reset"/> because it is process-scoped run state, not
/// per-scenario state; scenarios re-hydrate their context from it. Parsing fails fast on any missing,
/// malformed, inconsistent, duplicate, or secret-bearing manifest data.
/// </summary>
public sealed class InstanceFixtureState
{
    public const string RouteManifestVariableName = "INSTANCE_E2E_ROUTE_MANIFEST";
    public const string DatabaseEngineVariableName = "INSTANCE_E2E_DATABASE_ENGINE";

    private const int ExpectedRouteCount = 3;
    private const int ExpectedTenantCount = 2;

    private static readonly Lazy<InstanceFixtureState> _current = new(() => Parse(ReadEnvironment()));

    private readonly Dictionary<string, InstanceFixtureTenant> _tenantsByName;
    private readonly Dictionary<string, InstanceFixtureRoute> _routesByQualifier;

    private InstanceFixtureState(
        string databaseEngine,
        IReadOnlyList<InstanceFixtureTenant> tenants,
        IReadOnlyList<InstanceFixtureRoute> routes
    )
    {
        DatabaseEngine = databaseEngine;
        Tenants = tenants;
        Routes = routes;
        _tenantsByName = tenants.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _routesByQualifier = routes.ToDictionary(r => r.RouteQualifier, StringComparer.Ordinal);
        ApplicationIds = tenants.Select(t => t.ApplicationId).ToHashSet();
        VendorIds = tenants.Select(t => t.VendorId).ToHashSet();
        DataStoreIds = routes.Select(r => r.DataStoreId).ToHashSet();
        DataStoreContextIds = routes
            .SelectMany(r => new[] { r.DistrictContextId, r.SchoolYearContextId })
            .ToHashSet();
    }

    /// <summary>The engine the fixture databases were provisioned for ("postgresql" or "mssql").</summary>
    public string DatabaseEngine { get; }

    public IReadOnlyList<InstanceFixtureTenant> Tenants { get; }

    public IReadOnlyList<InstanceFixtureRoute> Routes { get; }

    /// <summary>Immutable fixture application IDs that per-scenario cleanup must never delete during the run.</summary>
    public IReadOnlySet<int> ApplicationIds { get; }

    /// <summary>Immutable fixture vendor IDs that per-scenario cleanup must never delete during the run.</summary>
    public IReadOnlySet<int> VendorIds { get; }

    /// <summary>Immutable fixture data-store IDs that per-scenario cleanup must never delete during the run.</summary>
    public IReadOnlySet<int> DataStoreIds { get; }

    /// <summary>Immutable fixture data-store route-context IDs.</summary>
    public IReadOnlySet<int> DataStoreContextIds { get; }

    /// <summary>
    /// True when the fixture environment contract is present. When false, the suite is not running through
    /// the build orchestration and fixture-consuming scenarios cannot run.
    /// </summary>
    public static bool IsAvailable =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RouteManifestVariableName));

    /// <summary>
    /// The run-scoped fixture state, loaded and validated once from the process environment. Throws if the
    /// environment contract is missing or invalid.
    /// </summary>
    public static InstanceFixtureState Current => _current.Value;

    public bool IsFixtureTenant(string tenantName) => _tenantsByName.ContainsKey(tenantName);

    public InstanceFixtureTenant GetTenant(string tenantName) =>
        _tenantsByName.TryGetValue(tenantName, out var tenant)
            ? tenant
            : throw new InvalidOperationException(
                $"'{tenantName}' is not a pre-registered Instance E2E fixture tenant."
            );

    public IReadOnlyList<InstanceFixtureRoute> RoutesForTenant(string tenantName) =>
        [.. Routes.Where(r => string.Equals(r.TenantName, tenantName, StringComparison.OrdinalIgnoreCase))];

    public bool TryGetRoute(string routeQualifier, out InstanceFixtureRoute route) =>
        _routesByQualifier.TryGetValue(routeQualifier, out route!);

    /// <summary>
    /// Parses and validates the fixture state from an environment-variable snapshot. Pure: it reads only the
    /// supplied dictionary so parsing/validation can be unit tested without mutating process state.
    /// </summary>
    public static InstanceFixtureState Parse(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var databaseEngine = RequireValue(environment, DatabaseEngineVariableName);
        if (
            !string.Equals(databaseEngine, "postgresql", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(databaseEngine, "mssql", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException(
                $"'{DatabaseEngineVariableName}' must be 'postgresql' or 'mssql' but was '{databaseEngine}'."
            );
        }

        var manifestJson = RequireValue(environment, RouteManifestVariableName);
        var routes = ParseRoutes(manifestJson, environment);
        var tenants = ParseTenants(environment, routes);

        // Belt-and-suspenders: the manifest is contractually secret-free. Confirm no application credential
        // leaked into it before this state (and anything derived from it) is used, without logging the value.
        foreach (var tenant in tenants)
        {
            if (
                manifestJson.Contains(tenant.ClientKey, StringComparison.Ordinal)
                || manifestJson.Contains(tenant.ClientSecret, StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    $"'{RouteManifestVariableName}' must not contain application credentials, but a credential "
                        + $"for tenant '{tenant.Name}' was found in it."
                );
            }
        }

        // Cross-check the flat data-store id list against the manifest's data-store ids.
        var declaredDataStoreIds = ParseIdList(environment, "INSTANCE_E2E_FIXTURE_DATASTORE_IDS");
        var manifestDataStoreIds = routes.Select(r => r.DataStoreId).OrderBy(id => id).ToArray();
        if (!declaredDataStoreIds.OrderBy(id => id).SequenceEqual(manifestDataStoreIds))
        {
            throw new InvalidOperationException(
                "'INSTANCE_E2E_FIXTURE_DATASTORE_IDS' must list exactly the data-store ids present in the "
                    + "route manifest."
            );
        }

        return new InstanceFixtureState(databaseEngine, tenants, routes);
    }

    private static IReadOnlyList<InstanceFixtureRoute> ParseRoutes(
        string manifestJson,
        IReadOnlyDictionary<string, string?> environment
    )
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"'{RouteManifestVariableName}' is not valid JSON.", ex);
        }

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != ExpectedRouteCount)
        {
            throw new InvalidOperationException(
                $"'{RouteManifestVariableName}' must be a JSON array of exactly {ExpectedRouteCount} route records."
            );
        }

        var routes = new List<InstanceFixtureRoute>(ExpectedRouteCount);
        foreach (var element in root.EnumerateArray())
        {
            var tenantName = RequireStringProperty(element, "tenant");
            var districtId = RequireStringProperty(element, "districtId");
            var schoolYear = RequireStringProperty(element, "schoolYear");
            var databaseOrdinal = RequireIntProperty(element, "databaseOrdinal");
            var databaseName = RequireStringProperty(element, "databaseName");
            var dataStoreId = RequireIntProperty(element, "dataStoreId");
            var districtContextId = RequireIntProperty(element, "districtContextId");
            var schoolYearContextId = RequireIntProperty(element, "schoolYearContextId");

            if (databaseOrdinal is < 1 or > ExpectedRouteCount)
            {
                throw new InvalidOperationException(
                    $"Route manifest databaseOrdinal must be between 1 and {ExpectedRouteCount} but was {databaseOrdinal}."
                );
            }

            var expectedDatabaseName = RequireValue(
                environment,
                $"INSTANCE_E2E_DATABASE_{databaseOrdinal}_NAME"
            );
            if (!string.Equals(databaseName, expectedDatabaseName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Route manifest databaseName '{databaseName}' for ordinal {databaseOrdinal} does not match "
                        + $"'INSTANCE_E2E_DATABASE_{databaseOrdinal}_NAME'."
                );
            }

            RequirePositive(dataStoreId, "dataStoreId");
            RequirePositive(districtContextId, "districtContextId");
            RequirePositive(schoolYearContextId, "schoolYearContextId");
            if (districtContextId == schoolYearContextId)
            {
                throw new InvalidOperationException(
                    $"Route '{districtId}/{schoolYear}' has identical district and school-year context ids."
                );
            }

            routes.Add(
                new InstanceFixtureRoute(
                    tenantName,
                    districtId,
                    schoolYear,
                    databaseOrdinal,
                    databaseName,
                    dataStoreId,
                    districtContextId,
                    schoolYearContextId
                )
            );
        }

        if (routes.Select(r => r.DatabaseOrdinal).Distinct().Count() != ExpectedRouteCount)
        {
            throw new InvalidOperationException("Route manifest database ordinals must be distinct.");
        }
        if (
            routes.Select(r => r.RouteQualifier).Distinct(StringComparer.Ordinal).Count()
            != ExpectedRouteCount
        )
        {
            throw new InvalidOperationException("Route manifest route qualifiers must be distinct.");
        }
        if (routes.Select(r => r.DataStoreId).Distinct().Count() != ExpectedRouteCount)
        {
            throw new InvalidOperationException("Route manifest data-store ids must be distinct.");
        }
        var contextIds = routes
            .SelectMany(r => new[] { r.DistrictContextId, r.SchoolYearContextId })
            .ToArray();
        if (contextIds.Distinct().Count() != contextIds.Length)
        {
            throw new InvalidOperationException("Route manifest route-context ids must be distinct.");
        }

        return routes;
    }

    private static IReadOnlyList<InstanceFixtureTenant> ParseTenants(
        IReadOnlyDictionary<string, string?> environment,
        IReadOnlyList<InstanceFixtureRoute> routes
    )
    {
        var tenants = new List<InstanceFixtureTenant>(ExpectedTenantCount);
        for (var tenantIndex = 1; tenantIndex <= ExpectedTenantCount; tenantIndex++)
        {
            var prefix = $"INSTANCE_E2E_FIXTURE_TENANT_{tenantIndex}_";
            tenants.Add(
                new InstanceFixtureTenant(
                    RequireValue(environment, $"{prefix}NAME"),
                    RequireInt(environment, $"{prefix}VENDOR_ID"),
                    RequireInt(environment, $"{prefix}APPLICATION_ID"),
                    RequireValue(environment, $"{prefix}CLIENT_KEY"),
                    RequireValue(environment, $"{prefix}CLIENT_SECRET")
                )
            );
        }

        if (
            tenants.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != ExpectedTenantCount
        )
        {
            throw new InvalidOperationException("Fixture tenant names must be distinct.");
        }
        if (tenants.Select(t => t.VendorId).Distinct().Count() != ExpectedTenantCount)
        {
            throw new InvalidOperationException("Fixture tenant vendor ids must be distinct.");
        }
        if (tenants.Select(t => t.ApplicationId).Distinct().Count() != ExpectedTenantCount)
        {
            throw new InvalidOperationException("Fixture tenant application ids must be distinct.");
        }

        var tenantNames = tenants.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (!tenantNames.Contains(route.TenantName))
            {
                throw new InvalidOperationException(
                    $"Route manifest references tenant '{route.TenantName}', which is not a declared fixture tenant."
                );
            }
        }
        foreach (var tenant in tenants)
        {
            if (
                !routes.Any(r => string.Equals(r.TenantName, tenant.Name, StringComparison.OrdinalIgnoreCase))
            )
            {
                throw new InvalidOperationException(
                    $"Fixture tenant '{tenant.Name}' owns no route in the manifest."
                );
            }
        }

        return tenants;
    }

    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
    {
        var names = new List<string>
        {
            DatabaseEngineVariableName,
            RouteManifestVariableName,
            "INSTANCE_E2E_FIXTURE_DATASTORE_IDS",
        };
        for (var ordinal = 1; ordinal <= ExpectedRouteCount; ordinal++)
        {
            names.Add($"INSTANCE_E2E_DATABASE_{ordinal}_NAME");
        }
        for (var tenantIndex = 1; tenantIndex <= ExpectedTenantCount; tenantIndex++)
        {
            var prefix = $"INSTANCE_E2E_FIXTURE_TENANT_{tenantIndex}_";
            names.AddRange([
                $"{prefix}NAME",
                $"{prefix}VENDOR_ID",
                $"{prefix}APPLICATION_ID",
                $"{prefix}CLIENT_KEY",
                $"{prefix}CLIENT_SECRET",
            ]);
        }

        return names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
    }

    private static string RequireValue(IReadOnlyDictionary<string, string?> environment, string name)
    {
        if (!environment.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required Instance E2E fixture environment variable '{name}' is not set."
            );
        }

        return value;
    }

    private static int RequireInt(IReadOnlyDictionary<string, string?> environment, string name)
    {
        var value = RequireValue(environment, name);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException($"Environment variable '{name}' must be an integer.");
        }

        RequirePositive(parsed, name);
        return parsed;
    }

    private static IReadOnlyList<int> ParseIdList(
        IReadOnlyDictionary<string, string?> environment,
        string name
    )
    {
        var value = RequireValue(environment, name);
        var ids = new List<int>();
        foreach (
            var token in value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{name}' must be a comma-separated list of integers."
                );
            }

            ids.Add(parsed);
        }

        if (ids.Count == 0)
        {
            throw new InvalidOperationException($"Environment variable '{name}' must list at least one id.");
        }
        if (ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidOperationException(
                $"Environment variable '{name}' must not contain duplicate ids."
            );
        }

        return ids;
    }

    private static string RequireStringProperty(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
        )
        {
            throw new InvalidOperationException(
                $"Route manifest record is missing required string property '{propertyName}'."
            );
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Route manifest property '{propertyName}' must not be blank."
            );
        }

        return value;
    }

    private static int RequireIntProperty(JsonElement element, string propertyName)
    {
        if (
            !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value)
        )
        {
            throw new InvalidOperationException(
                $"Route manifest record is missing required integer property '{propertyName}'."
            );
        }

        return value;
    }

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"'{name}' must be a positive integer but was {value}.");
        }
    }
}
