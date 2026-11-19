// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Validates the education-organization projection limits at startup, whether or not the endpoint is
/// enabled, so a deployment that later turns it on cannot start with an unusable value.
/// </summary>
/// <remarks>
/// Every failure is reported rather than stopping at the first, and each names the full configuration
/// key, so an operator correcting a misconfigured deployment sees all of it in one startup attempt.
/// </remarks>
public sealed class EducationOrganizationProjectionSettingsValidator
    : IValidateOptions<EducationOrganizationProjectionSettings>
{
    public ValidateOptionsResult Validate(string? name, EducationOrganizationProjectionSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        AddRangeFailure(
            failures,
            nameof(EducationOrganizationProjectionSettings.MaximumPageSize),
            options.MaximumPageSize,
            EducationOrganizationProjectionSettings.MaximumPageSizeMinimum,
            EducationOrganizationProjectionSettings.MaximumPageSizeMaximum
        );
        AddRangeFailure(
            failures,
            nameof(EducationOrganizationProjectionSettings.MaxProjectionRows),
            options.MaxProjectionRows,
            EducationOrganizationProjectionSettings.MaxProjectionRowsMinimum,
            EducationOrganizationProjectionSettings.MaxProjectionRowsMaximum
        );
        AddRangeFailure(
            failures,
            nameof(EducationOrganizationProjectionSettings.CursorLifetimeMinutes),
            options.CursorLifetimeMinutes,
            EducationOrganizationProjectionSettings.CursorLifetimeMinutesMinimum,
            EducationOrganizationProjectionSettings.CursorLifetimeMinutesMaximum
        );
        AddRangeFailure(
            failures,
            nameof(EducationOrganizationProjectionSettings.ReadLockTimeoutSeconds),
            options.ReadLockTimeoutSeconds,
            EducationOrganizationProjectionSettings.ReadLockTimeoutSecondsMinimum,
            EducationOrganizationProjectionSettings.ReadLockTimeoutSecondsMaximum
        );
        AddRangeFailure(
            failures,
            nameof(EducationOrganizationProjectionSettings.ReadCommandTimeoutSeconds),
            options.ReadCommandTimeoutSeconds,
            EducationOrganizationProjectionSettings.ReadCommandTimeoutSecondsMinimum,
            EducationOrganizationProjectionSettings.ReadCommandTimeoutSecondsMaximum
        );

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddRangeFailure(
        List<string> failures,
        string settingName,
        int value,
        int minimum,
        int maximum
    )
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(
                $"Setting {EducationOrganizationProjectionSettings.SectionName}:{settingName} must be between {minimum} and {maximum}"
            );
        }
    }
}
