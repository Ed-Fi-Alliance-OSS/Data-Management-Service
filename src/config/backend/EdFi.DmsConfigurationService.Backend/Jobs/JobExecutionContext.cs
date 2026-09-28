// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Services;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// What a handler knows about the execution it runs in (spec §4.1): the public job identifier, the job's tenant, the
/// attempt number out of the maximum, and the fence for writes that require continued ownership.
/// </summary>
public sealed record JobExecutionContext(
    string JobId,
    TenantContext TenantContext,
    int Attempt,
    int MaxAttempts,
    IJobFence Fence
);
