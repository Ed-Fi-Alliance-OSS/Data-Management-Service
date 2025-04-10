// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Core.External.Interface;

/// <summary>
/// The repository DMS Core uses to access a authorization data.
/// </summary>
public interface IAuthorizationRepository
{
    public Task<long[]> GetAncestorEducationOrganizationIds(long[] educationOrganizationIds, TraceId traceId);
    public Task<JsonElement> GetEducationOrganizationsForStudent(string studentUniqueId, TraceId traceId);
}
