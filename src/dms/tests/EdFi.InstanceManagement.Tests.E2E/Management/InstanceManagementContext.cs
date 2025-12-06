// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Infrastructure;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

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
    /// List of instance IDs created during tests
    /// </summary>
    public List<int> InstanceIds { get; } = [];

    /// <summary>
    /// Maps instance ID to the tenant it belongs to
    /// </summary>
    public Dictionary<int, string> InstanceIdToTenant { get; } = new();

    /// <summary>
    /// Mapping from route qualifier (e.g., "255901/2024") to instance ID
    /// </summary>
    public Dictionary<string, int> RouteQualifierToInstanceId { get; } = new();

    /// <summary>
    /// Tracks instance ID to database name mapping for infrastructure cleanup
    /// </summary>
    public Dictionary<int, string> InstanceIdToDatabaseName { get; } = new();

    /// <summary>
    /// Infrastructure manager for Kafka/Debezium lifecycle
    /// </summary>
    public InstanceInfrastructureManager? InfrastructureManager { get; set; }

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
    /// Last HTTP response for assertions
    /// </summary>
    public HttpResponseMessage? LastResponse { get; set; }

    /// <summary>
    /// DMS API client instance (managed for proper disposal)
    /// </summary>
    public DmsApiClient? DmsClient { get; set; }

    /// <summary>
    /// Kafka message collector for topic-per-instance validation
    /// </summary>
    public InstanceKafkaMessageCollector? KafkaCollector { get; set; }

    /// <summary>
    /// Messages collected from Kafka, grouped by instance ID
    /// </summary>
    public Dictionary<long, List<KafkaTestMessage>> MessagesByInstance { get; } = new();

    /// <summary>
    /// Reset context for new scenario
    /// </summary>
    public void Reset()
    {
        VendorId = null;
        VendorIdsByTenant.Clear();
        TenantNames.Clear();
        CurrentTenant = null;
        ConfigClientsByTenant.Clear();
        InstanceIds.Clear();
        InstanceIdToTenant.Clear();
        RouteQualifierToInstanceId.Clear();
        InstanceIdToDatabaseName.Clear();
        ApplicationIdsByTenant.Clear();
        CredentialsByTenant.Clear();
        ApplicationId = null;
        ClientKey = null;
        ClientSecret = null;
        ConfigToken = null;
        DmsToken = null;
        DescriptorLocations.Clear();
        LastResponse = null;
        DmsClient?.Dispose();
        DmsClient = null;
        KafkaCollector?.Dispose();
        KafkaCollector = null;
        MessagesByInstance.Clear();
    }
}
