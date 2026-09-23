// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// A job this process has just claimed (spec D-3). <see cref="FencingToken"/> and <see cref="LeaseOwner"/>
/// authorize every later ownership-dependent write; all times are database UTC.
/// </summary>
public sealed record ClaimedJob(
    long Id,
    string JobId,
    long? TenantId,
    string JobType,
    short PayloadVersion,
    string PayloadJson,
    int AttemptCount,
    long FencingToken,
    string LeaseOwner,
    DateTime LeaseExpiresAt,
    DateTime CreatedAt,
    DateTime NextAttemptAt,
    DateTime DatabaseUtcNow
);
