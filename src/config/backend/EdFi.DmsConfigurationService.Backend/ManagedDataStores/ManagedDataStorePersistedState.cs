// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

namespace EdFi.DmsConfigurationService.Backend.ManagedDataStores;

/// <summary>
/// Complete stored lifecycle state. This is internal persistence data, never a management API response.
/// Rehydrate through <see cref="ManagedDataStore.Rehydrate"/> to validate lifecycle invariants.
/// </summary>
public sealed record ManagedDataStorePersistedState(
    int Id,
    long? TenantId,
    string Name,
    DmsDatabaseTemplate Template,
    DmsTargetDatabaseName TargetName,
    Guid ExpectedSourceIdentity,
    ManagedDataStoreEndpoint Endpoint,
    DmsTrustedArtifactPin? PinnedArtifact,
    ManagedDataStoreStatus Status,
    int? DataStoreId,
    int? HistoricalDataStoreId,
    string? HistoricalDataStoreName,
    string CreateJobId,
    string? DeleteJobId,
    int LastAttempt,
    ManagedDataStoreFailure? LastFailure,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletedAt,
    DateTimeOffset LastRefreshed,
    DateTimeOffset LastModifiedDate
);
