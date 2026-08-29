// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// A CMS record created by the current scenario, paired with the tenant that owns it so cleanup can select
/// the correct tenant-scoped client.
/// </summary>
public readonly record struct OwnedRecord(string Tenant, int Id);

/// <summary>
/// Context object to track test data across scenarios
/// </summary>
public class InstanceManagementContext
{
    /// <summary>
    /// Vendor ID created during tests (legacy single-tenant support)
    /// </summary>
    public int? VendorId { get; set; }

    /// <summary>
    /// Vendor IDs per tenant (tenantName -> vendorId)
    /// </summary>
    public Dictionary<string, int> VendorIdsByTenant { get; } = new();

    /// <summary>
    /// List of tenant names created during tests
    /// </summary>
    public List<string> TenantNames { get; } = [];

    /// <summary>
    /// Currently selected tenant for explicit tenant operations
    /// </summary>
    public string? CurrentTenant { get; set; }

    /// <summary>
    /// Config service clients per tenant (tenantName -> ConfigServiceClient)
    /// </summary>
    public Dictionary<string, ConfigServiceClient> ConfigClientsByTenant { get; } = new();

    /// <summary>
    /// List of data store IDs created during tests
    /// </summary>
    public List<int> DataStoreIds { get; } = [];

    /// <summary>
    /// Maps data store ID to the tenant it belongs to
    /// </summary>
    public Dictionary<int, string> DataStoreIdToTenant { get; } = new();

    /// <summary>
    /// Mapping from route qualifier (e.g., "255901/2024") to data store ID
    /// </summary>
    public Dictionary<string, int> RouteQualifierToDataStoreId { get; } = new();

    /// <summary>
    /// Application IDs per tenant (tenantName -> applicationId)
    /// </summary>
    public Dictionary<string, int> ApplicationIdsByTenant { get; } = new();

    /// <summary>
    /// Application credentials per tenant (tenantName -> (key, secret))
    /// </summary>
    public Dictionary<string, (string Key, string Secret)> CredentialsByTenant { get; } = new();

    /// <summary>
    /// Application ID created during tests (legacy single-tenant support)
    /// </summary>
    public int? ApplicationId { get; set; }

    /// <summary>
    /// Application client key for DMS authentication
    /// </summary>
    public string? ClientKey { get; set; }

    /// <summary>
    /// Application client secret for DMS authentication
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Config Service access token
    /// </summary>
    public string? ConfigToken { get; set; }

    /// <summary>
    /// DMS access token
    /// </summary>
    public string? DmsToken { get; set; }

    /// <summary>
    /// Descriptor locations created during tests (key: identifier, value: location URL)
    /// </summary>
    public Dictionary<string, string> DescriptorLocations { get; } = new();

    /// <summary>
    /// Newest change versions captured during a scenario (variableName -> value), for delta assertions.
    /// </summary>
    public Dictionary<string, long> CapturedChangeVersions { get; } = new();

    /// <summary>
    /// Resource ids captured during a scenario (variableName -> id), for a later by-id request. Kept
    /// apart from <see cref="DescriptorLocations"/> because a bare id is not a location and cannot be
    /// requested as one.
    /// </summary>
    public Dictionary<string, string> CapturedIds { get; } = new();

    /// <summary>
    /// Last HTTP response for assertions
    /// </summary>
    public HttpResponseMessage? LastResponse { get; set; }

    /// <summary>
    /// DMS API client instance (managed for proper disposal)
    /// </summary>
    public DmsApiClient? DmsClient { get; set; }

    /// <summary>
    /// Immutable suite-owned fixture application IDs hydrated for this scenario. These are knowledge only:
    /// per-scenario cleanup must never delete them.
    /// </summary>
    public IReadOnlySet<int> FixtureApplicationIds { get; set; } = new HashSet<int>();

    /// <summary>
    /// Immutable suite-owned fixture data-store IDs hydrated for this scenario (never scenario-owned).
    /// </summary>
    public IReadOnlySet<int> FixtureDataStoreIds { get; set; } = new HashSet<int>();

    /// <summary>
    /// Immutable suite-owned fixture vendor IDs hydrated for this scenario (never scenario-owned).
    /// </summary>
    public IReadOnlySet<int> FixtureVendorIds { get; set; } = new HashSet<int>();

    /// <summary>
    /// Applications created by the current scenario via real CMS operations. Only these are deleted by
    /// per-scenario cleanup, and only after independently excluding every immutable fixture ID.
    /// </summary>
    public List<OwnedRecord> ScenarioOwnedApplications { get; } = [];

    /// <summary>
    /// Data stores created by the current scenario via real CMS operations.
    /// </summary>
    public List<OwnedRecord> ScenarioOwnedDataStores { get; } = [];

    /// <summary>
    /// Vendors created by the current scenario via real CMS operations.
    /// </summary>
    public List<OwnedRecord> ScenarioOwnedVendors { get; } = [];

    /// <summary>
    /// Records an application the current scenario created so cleanup deletes it (unless it is a fixture ID).
    /// </summary>
    public void MarkApplicationScenarioOwned(string tenant, int applicationId) =>
        AddOwned(ScenarioOwnedApplications, tenant, applicationId);

    /// <summary>
    /// Records a data store the current scenario created so cleanup deletes it (unless it is a fixture ID).
    /// </summary>
    public void MarkDataStoreScenarioOwned(string tenant, int dataStoreId) =>
        AddOwned(ScenarioOwnedDataStores, tenant, dataStoreId);

    /// <summary>
    /// Records a vendor the current scenario created so cleanup deletes it (unless it is a fixture ID).
    /// </summary>
    public void MarkVendorScenarioOwned(string tenant, int vendorId) =>
        AddOwned(ScenarioOwnedVendors, tenant, vendorId);

    private static void AddOwned(List<OwnedRecord> owned, string tenant, int id)
    {
        var record = new OwnedRecord(tenant, id);
        if (!owned.Contains(record))
        {
            owned.Add(record);
        }
    }

    /// <summary>
    /// Reset context for new scenario
    /// </summary>
    public void Reset()
    {
        FixtureApplicationIds = new HashSet<int>();
        FixtureDataStoreIds = new HashSet<int>();
        FixtureVendorIds = new HashSet<int>();
        ScenarioOwnedApplications.Clear();
        ScenarioOwnedDataStores.Clear();
        ScenarioOwnedVendors.Clear();
        VendorId = null;
        VendorIdsByTenant.Clear();
        TenantNames.Clear();
        CurrentTenant = null;
        ConfigClientsByTenant.Clear();
        DataStoreIds.Clear();
        DataStoreIdToTenant.Clear();
        RouteQualifierToDataStoreId.Clear();
        ApplicationIdsByTenant.Clear();
        CredentialsByTenant.Clear();
        ApplicationId = null;
        ClientKey = null;
        ClientSecret = null;
        ConfigToken = null;
        DmsToken = null;
        DescriptorLocations.Clear();
        CapturedChangeVersions.Clear();
        CapturedIds.Clear();
        LastResponse = null;
        DmsClient?.Dispose();
        DmsClient = null;
    }
}
