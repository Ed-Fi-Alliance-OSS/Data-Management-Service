// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;

/// <summary>
/// The refresh settings (DMS-1441 spec §6.10). They live in the <c>AppSettings</c> section but are bound as their own
/// type, with their own validator, so the host's <c>AppSettings</c> type and its validator are unchanged.
/// </summary>
public sealed class EducationOrganizationRefreshOptions
{
    public const string SectionName = "AppSettings";

    public const int DefaultRefreshIntervalInMins = 60;

    public const int MinimumRefreshIntervalInMins = 1;

    /// <summary>The largest interval a job schedule accepts: 366 days.</summary>
    public const int MaximumRefreshIntervalInMins = JobScheduleService.MaxIntervalMinutes;

    /// <summary>The interval of each tenant's scheduled refresh of every data store, in minutes.</summary>
    public int EdOrgsRefreshIntervalInMins { get; set; } = DefaultRefreshIntervalInMins;
}

/// <summary>
/// Validates <see cref="EducationOrganizationRefreshOptions"/> at startup, whatever the job settings are, with a message
/// that names <c>AppSettings:EdOrgsRefreshIntervalInMins</c>.
/// </summary>
public sealed class EducationOrganizationRefreshOptionsValidator
    : IValidateOptions<EducationOrganizationRefreshOptions>
{
    public ValidateOptionsResult Validate(string? name, EducationOrganizationRefreshOptions options)
    {
        int interval = options.EdOrgsRefreshIntervalInMins;
        if (
            interval < EducationOrganizationRefreshOptions.MinimumRefreshIntervalInMins
            || interval > EducationOrganizationRefreshOptions.MaximumRefreshIntervalInMins
        )
        {
            return ValidateOptionsResult.Fail(
                $"{EducationOrganizationRefreshOptions.SectionName}:"
                    + $"{nameof(EducationOrganizationRefreshOptions.EdOrgsRefreshIntervalInMins)} must be between "
                    + $"{EducationOrganizationRefreshOptions.MinimumRefreshIntervalInMins} and "
                    + $"{EducationOrganizationRefreshOptions.MaximumRefreshIntervalInMins} minutes; it is {interval}."
            );
        }

        return ValidateOptionsResult.Success;
    }
}
