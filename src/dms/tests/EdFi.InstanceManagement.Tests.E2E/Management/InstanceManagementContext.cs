// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Context object to track test data across scenarios
/// </summary>
public class InstanceManagementContext
{
    /// <summary>
    /// Vendor ID created during tests
    /// </summary>
    public int? VendorId { get; set; }

    /// <summary>
    /// List of instance IDs created during tests
    /// </summary>
    public List<int> InstanceIds { get; } = [];

    /// <summary>
    /// Application ID created during tests
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
        InstanceIds.Clear();
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
