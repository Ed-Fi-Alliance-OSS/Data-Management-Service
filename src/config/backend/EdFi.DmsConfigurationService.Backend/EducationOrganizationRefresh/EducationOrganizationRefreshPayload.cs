// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>The education-organization refresh job and schedule types (DMS-1441 spec §6.3).</summary>
public static class EducationOrganizationRefreshJobTypes
{
    /// <summary>Refreshes the snapshot of one data store, or of every data store, in the job's tenant.</summary>
    public const string Refresh = "EducationOrganization.Refresh";

    /// <summary>A tenant's recurring schedule, whose occurrences enqueue a refresh of every data store.</summary>
    public const string ScheduledRefresh = "EducationOrganization.ScheduledRefresh";

    public const short PayloadVersion = 1;
}

/// <summary>
/// Which data stores a refresh covers. Job payloads persist the numeric value, so the values never change; 0 is not
/// defined, so a payload whose scope is 0 is rejected before it reaches the validator.
/// </summary>
public enum EducationOrganizationRefreshScope
{
    All = 1,
    One = 2,
}

/// <summary>
/// Version 1 of the refresh payload. <see cref="DataStoreId"/> is 0 when <see cref="Scope"/> is
/// <see cref="EducationOrganizationRefreshScope.All"/>. The tenant comes from the job, never from the payload.
/// </summary>
public sealed record EducationOrganizationRefreshPayload(
    EducationOrganizationRefreshScope Scope,
    int DataStoreId
);

/// <summary>
/// The cross-field rules of <see cref="EducationOrganizationRefreshPayload"/>: a refresh of every data store names no
/// data store, and a refresh of one names a positive ID.
/// </summary>
public sealed class EducationOrganizationRefreshPayloadValidator
    : IJobPayloadValidator<EducationOrganizationRefreshPayload>
{
    public const string InvalidScope = nameof(InvalidScope);
    public const string DataStoreIdNotAllowed = nameof(DataStoreIdNotAllowed);
    public const string InvalidDataStoreId = nameof(InvalidDataStoreId);

    public IReadOnlyList<string> Validate(EducationOrganizationRefreshPayload payload) =>
        payload.Scope switch
        {
            EducationOrganizationRefreshScope.All => payload.DataStoreId == 0 ? [] : [DataStoreIdNotAllowed],
            EducationOrganizationRefreshScope.One => payload.DataStoreId > 0 ? [] : [InvalidDataStoreId],
            _ => [InvalidScope],
        };
}
