// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Configuration settings for instance management E2E tests
/// </summary>
public static class TestConfiguration
{
    /// <summary>
    /// Base URL for the DMS API
    /// </summary>
    public static string DmsApiUrl =>
        Environment.GetEnvironmentVariable("DMS_API_URL") ?? "http://localhost:8080";

    /// <summary>
    /// Base URL for the Configuration Service
    /// </summary>
    public static string ConfigServiceUrl =>
        Environment.GetEnvironmentVariable("CONFIG_SERVICE_URL") ?? "http://localhost:8081";

    /// <summary>
    /// Default route qualifier segments from environment
    /// </summary>
    public static string[] RouteQualifierSegments => ["districtId", "schoolYear"];

    /// <summary>
    /// Tenant name for multi-tenant Configuration Service calls.
    /// When specified, this value is passed as a "Tenant" header to all Configuration Service API calls.
    /// </summary>
    public static string? TenantName => Environment.GetEnvironmentVariable("CONFIG_SERVICE_TENANT");
}
