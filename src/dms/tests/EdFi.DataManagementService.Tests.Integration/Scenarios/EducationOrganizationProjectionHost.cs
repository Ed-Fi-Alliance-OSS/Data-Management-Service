// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Credentials = EdFi.DataManagementService.Tests.Integration.Doubles.EducationOrganizationProjectionCredentials;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>The two route-qualifier values a data store answers for.</summary>
internal sealed record ProjectionRoute(string DistrictId, string SchoolYear);

/// <summary>
/// The deployment every education-organization projection API scenario runs against: multi-tenant
/// with the <c>districtId</c> and <c>schoolYear</c> route qualifiers, real authorization, and a data
/// store catalog holding one store per target state of tenant A.
/// </summary>
internal static class EducationOrganizationProjectionHost
{
    public const string TenantA = "Tenant_255901";
    public const string TenantB = "Tenant_255902";

    /// <summary>
    /// A tenant whose catalog a scenario makes unloadable after startup, which loads every tenant's
    /// catalog and stops the host if one fails.
    /// </summary>
    public const string OutageTenant = "Tenant_outage";

    public const string RouteQualifierSegments = "districtId,schoolYear";

    public static readonly ProjectionRoute RouteA = new("255901", "2025");
    public static readonly ProjectionRoute RouteB = new("255902", "2024");

    /// <summary>The leased primary, served by this deployment's dialect.</summary>
    public const long PrimaryStoreId = 1;

    /// <summary>Tenant B's store, registered after startup.</summary>
    public const long TenantBStoreId = 2;

    /// <summary>A store of tenant A registered after startup.</summary>
    public const long LateStoreId = 3;

    /// <summary>The primary under a legacy registration with no provider token.</summary>
    public const long MissingProviderStoreId = 4;

    /// <summary>The primary registered under the other dialect's token.</summary>
    public const long OtherProviderStoreId = 5;

    /// <summary>The primary registered under a token the catalog could not normalize.</summary>
    public const long UnknownProviderStoreId = 6;

    public const long NoConnectionStringStoreId = 7;

    /// <summary>A connection string naming a database that does not exist on the test server.</summary>
    public const long AbsentDatabaseStoreId = 8;

    /// <summary>A connection string naming a host that cannot be resolved.</summary>
    public const long UnresolvableHostStoreId = 9;

    public const long MismatchedFingerprintStoreId = 10;
    public const long MalformedFingerprintStoreId = 11;
    public const long NotProvisionedStoreId = 12;

    public static DataStore Store(
        long id,
        string? connectionString,
        ProjectionRoute route,
        RelationalProviderToken? token,
        RelationalProviderMetadataStatus? status = null,
        IReadOnlyDictionary<DataStoreDerivativeType, string>? derivatives = null
    ) =>
        new(
            Id: id,
            DataStoreType: "default",
            Name: $"projection-store-{id}",
            ConnectionString: connectionString,
            RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>
            {
                [new RouteQualifierName("districtId")] = new RouteQualifierValue(route.DistrictId),
                [new RouteQualifierName("schoolYear")] = new RouteQualifierValue(route.SchoolYear),
            },
            RelationalProviderToken: token,
            RelationalProviderMetadataStatus: status
                ?? (
                    token is null
                        ? RelationalProviderMetadataStatus.Missing
                        : RelationalProviderMetadataStatus.Supported
                ),
            DerivativeConnectionStrings: derivatives
        );

    /// <summary>The catalog at startup: tenant A's stores, an empty tenant B, and the outage tenant.</summary>
    public static ProjectionDataStoreCatalog CreateCatalog(
        EducationOrganizationProjectionEngine engine,
        string primaryConnectionString,
        string absentDatabaseConnectionString,
        string unresolvableConnectionString,
        IReadOnlyDictionary<DataStoreDerivativeType, string>? primaryDerivatives
    )
    {
        ProjectionDataStoreCatalog catalog = new(
            new Dictionary<string, IReadOnlyList<DataStore>>
            {
                [TenantA] =
                [
                    Store(
                        PrimaryStoreId,
                        primaryConnectionString,
                        RouteA,
                        engine.OwnToken,
                        derivatives: primaryDerivatives
                    ),
                    Store(MissingProviderStoreId, primaryConnectionString, RouteA, token: null),
                    Store(OtherProviderStoreId, primaryConnectionString, RouteA, engine.OtherToken),
                    Store(
                        UnknownProviderStoreId,
                        primaryConnectionString,
                        RouteA,
                        token: null,
                        RelationalProviderMetadataStatus.Unknown
                    ),
                    Store(NoConnectionStringStoreId, "   ", RouteA, engine.OwnToken),
                    Store(AbsentDatabaseStoreId, absentDatabaseConnectionString, RouteA, engine.OwnToken),
                    Store(UnresolvableHostStoreId, unresolvableConnectionString, RouteA, engine.OwnToken),
                ],
                [TenantB] = [],
                [OutageTenant] = [],
            }
        );
        return catalog;
    }

    /// <summary>The projection settings a fixture runs with, on top of the shipped defaults.</summary>
    public static IReadOnlyDictionary<string, string> HostSettings(
        int readLockTimeoutSeconds,
        int? maxProjectionRows = null,
        int? rateLimitPermits = null
    )
    {
        Dictionary<string, string> settings = new()
        {
            ["AppSettings:EnableEducationOrganizationProjection"] = "true",
            ["AppSettings:EducationOrganizationProjection:ReadLockTimeoutSeconds"] =
                readLockTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        };
        if (maxProjectionRows is { } rows)
        {
            settings["AppSettings:EducationOrganizationProjection:MaxProjectionRows"] = rows.ToString(
                CultureInfo.InvariantCulture
            );
        }

        if (rateLimitPermits is { } permits)
        {
            // One fixed window longer than any test, so the permits are not replenished mid-test.
            settings["RateLimit:PermitLimit"] = permits.ToString(CultureInfo.InvariantCulture);
            settings["RateLimit:QueueLimit"] = "0";
            settings["RateLimit:Window"] = "3600";
        }

        return settings;
    }

    /// <summary>
    /// Which client has an application in which tenant, and the data stores it is assigned. The
    /// projection credentials have none.
    /// </summary>
    private static readonly IReadOnlyDictionary<
        (string ClientId, string Tenant),
        IReadOnlyList<long>
    > _bindings = new Dictionary<(string, string), IReadOnlyList<long>>
    {
        [(Credentials.Projection.ClientId, TenantA)] = [],
        [(Credentials.Projection.ClientId, OutageTenant)] = [],
        [(Credentials.ProjectionTenantB.ClientId, TenantB)] = [],
        [(Credentials.Resource.ClientId, TenantA)] = [PrimaryStoreId],
        [(Credentials.Resource.ClientId, TenantB)] = [TenantBStoreId],
        [(Credentials.IdentityOnly.ClientId, TenantA)] = [],
        [(Credentials.ProjectionWithoutRead.ClientId, TenantA)] = [],
        [(Credentials.ProjectionMisconfigured.ClientId, TenantA)] = [],
        [(Credentials.UnknownClaimSet.ClientId, TenantA)] = [],
    };

    /// <summary>
    /// Replaces authentication and client binding with the credential doubles, and installs the log
    /// recorder and the fingerprint and mapping decorators. Runs after the shared doubles, so these win.
    /// </summary>
    public static void ConfigureServices(
        IServiceCollection services,
        ProjectionLogRecorder logs,
        RedirectingFingerprintReader fingerprints,
        SwitchableMappingSetProvider mapping
    )
    {
        services.RemoveAll<IJwtValidationService>();
        services.AddSingleton<IJwtValidationService>(
            new CredentialJwtValidationService([PrimaryStoreId, TenantBStoreId])
        );
        services.RemoveAll<IApplicationContextProvider>();
        services.AddSingleton<IApplicationContextProvider>(
            new TenantBindingApplicationContextProvider(_bindings, Credentials.BindingUnavailable.ClientId)
        );
        logs.Register(services);
        fingerprints.Register(services);
        mapping.Register(services);
    }
}

/// <summary>Everything a projection scenario reaches through, built by the dialect wrapper.</summary>
internal sealed record EducationOrganizationProjectionContext(
    ApiIntegrationHarness Harness,
    EducationOrganizationProjectionEngine Engine,
    string PrimaryConnectionString,
    string UnresolvableConnectionString,
    ProjectionDataStoreCatalog Catalog,
    ProjectionLogRecorder Logs,
    RedirectingFingerprintReader Fingerprints,
    SwitchableMappingSetProvider Mapping,
    FakeTimeProvider Clock,
    IDerivativeTargetReachability Reachability,
    Func<Task<string>> LeaseDatabaseAsync
);
