// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Validates the paging-related <see cref="AppSettings"/> values that later request handling and
/// partition sizing depend on, and the education-organization projection limits.
/// </summary>
/// <remarks>
/// Every failure is reported rather than stopping at the first, so an operator correcting a
/// misconfigured deployment sees all of it in one startup attempt.
/// </remarks>
public sealed class AppSettingsValidator : IValidateOptions<AppSettings>
{
    /// <summary>The smallest partition count a client may request or configure.</summary>
    public const int MinimumDefaultPartitionCount = 1;

    /// <summary>The largest partition count a client may request or configure.</summary>
    public const int MaximumDefaultPartitionCount = 200;

    public ValidateOptionsResult Validate(string? name, AppSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (options.MaximumPageSize <= 0)
        {
            failures.Add($"AppSettings value {nameof(AppSettings.MaximumPageSize)} must be greater than 0");
        }

        if (
            options.DefaultPartitionCount < MinimumDefaultPartitionCount
            || options.DefaultPartitionCount > MaximumDefaultPartitionCount
        )
        {
            failures.Add(
                $"AppSettings value {nameof(AppSettings.DefaultPartitionCount)} must be between {MinimumDefaultPartitionCount} and {MaximumDefaultPartitionCount}"
            );
        }

        ValidateEducationOrganizationProjection(options.EducationOrganizationProjection, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateEducationOrganizationProjection(
        EducationOrganizationProjectionSettings? settings,
        List<string> failures
    )
    {
        const string SectionName = nameof(AppSettings.EducationOrganizationProjection);

        if (settings is null)
        {
            failures.Add($"AppSettings value {SectionName} must be configured");
            return;
        }

        AddRangeFailure(
            failures,
            $"{SectionName}:{nameof(EducationOrganizationProjectionSettings.MaximumPageSize)}",
            settings.MaximumPageSize,
            EducationOrganizationProjectionSettings.MaximumPageSizeMinimum,
            EducationOrganizationProjectionSettings.MaximumPageSizeMaximum
        );
        AddRangeFailure(
            failures,
            $"{SectionName}:{nameof(EducationOrganizationProjectionSettings.MaxProjectionRows)}",
            settings.MaxProjectionRows,
            EducationOrganizationProjectionSettings.MaxProjectionRowsMinimum,
            EducationOrganizationProjectionSettings.MaxProjectionRowsMaximum
        );
        AddRangeFailure(
            failures,
            $"{SectionName}:{nameof(EducationOrganizationProjectionSettings.CursorLifetimeMinutes)}",
            settings.CursorLifetimeMinutes,
            EducationOrganizationProjectionSettings.CursorLifetimeMinutesMinimum,
            EducationOrganizationProjectionSettings.CursorLifetimeMinutesMaximum
        );
        AddRangeFailure(
            failures,
            $"{SectionName}:{nameof(EducationOrganizationProjectionSettings.ReadLockTimeoutSeconds)}",
            settings.ReadLockTimeoutSeconds,
            EducationOrganizationProjectionSettings.ReadLockTimeoutSecondsMinimum,
            EducationOrganizationProjectionSettings.ReadLockTimeoutSecondsMaximum
        );
        AddRangeFailure(
            failures,
            $"{SectionName}:{nameof(EducationOrganizationProjectionSettings.ReadCommandTimeoutSeconds)}",
            settings.ReadCommandTimeoutSeconds,
            EducationOrganizationProjectionSettings.ReadCommandTimeoutSecondsMinimum,
            EducationOrganizationProjectionSettings.ReadCommandTimeoutSecondsMaximum
        );
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
            failures.Add($"AppSettings value {settingName} must be between {minimum} and {maximum}");
        }
    }
}
