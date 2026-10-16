// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// What the database health check found to probe.
/// </summary>
public enum HealthCheckTargetStatus
{
    /// <summary>
    /// No data store is loaded. Normal at multi-tenant startup and while a tenant has no data store yet.
    /// </summary>
    NoDataStoresLoaded,

    /// <summary>
    /// Data stores are loaded, but none has a connection string: a data store misconfiguration.
    /// </summary>
    NoConnectionString,

    /// <summary>
    /// A data store with a connection string was found; <see cref="HealthCheckTarget.ConnectionString"/> is set.
    /// </summary>
    Available,
}

/// <summary>
/// The result of resolving the database the health check should probe.
/// </summary>
/// <param name="Status">Which of the three states was found</param>
/// <param name="ConnectionString">The connection string to probe, set only when <paramref name="Status"/> is Available</param>
/// <param name="DataStoreCount">The number of loaded data stores, reported when none has a connection string</param>
public sealed record HealthCheckTarget(
    HealthCheckTargetStatus Status,
    string? ConnectionString,
    int DataStoreCount
)
{
    public static HealthCheckTarget NoDataStoresLoaded { get; } =
        new(HealthCheckTargetStatus.NoDataStoresLoaded, null, 0);

    public static HealthCheckTarget NoConnectionString(int dataStoreCount) =>
        new(HealthCheckTargetStatus.NoConnectionString, null, dataStoreCount);

    public static HealthCheckTarget Available(string connectionString) =>
        new(HealthCheckTargetStatus.Available, connectionString, 0);
}
