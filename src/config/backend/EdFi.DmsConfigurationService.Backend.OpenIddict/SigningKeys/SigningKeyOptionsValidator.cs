// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// Validates the <c>SigningKey*</c> settings of <see cref="IdentityOptions"/> (spec §4.3.12). Every failed rule is
/// reported, and each message names <c>IdentitySettings:&lt;Property&gt;</c> and its accepted range. The other identity
/// settings are not this validator's concern.
/// </summary>
public sealed class SigningKeyOptionsValidator : IValidateOptions<IdentityOptions>
{
    private const string Section = "IdentitySettings";

    public const int MinimumRefreshIntervalSeconds = 30;
    public const int MaximumRefreshIntervalSeconds = 86_400;

    /// <summary>
    /// The largest accepted staleness bound. <c>MaxStaleness</c> is how long a key retired during a key-store outage can
    /// still be trusted (<c>T_max</c>), so it is bounded rather than open-ended.
    /// </summary>
    public const int MaximumMaxStalenessSeconds = 86_400;

    public const int MinimumCooldownSeconds = 1;
    public const int MaximumCooldownSeconds = 3_600;
    public const int MinimumLoadTimeoutSeconds = 1;
    public const int MaximumLoadTimeoutSeconds = 60;

    public ValidateOptionsResult Validate(string? name, IdentityOptions options)
    {
        List<string> failures = [];

        bool refreshIntervalInRange = Range(
            failures,
            nameof(IdentityOptions.SigningKeyRefreshIntervalSeconds),
            options.SigningKeyRefreshIntervalSeconds,
            MinimumRefreshIntervalSeconds,
            MaximumRefreshIntervalSeconds
        );
        Range(
            failures,
            nameof(IdentityOptions.SigningKeyUnknownKeyRefreshCooldownSeconds),
            options.SigningKeyUnknownKeyRefreshCooldownSeconds,
            MinimumCooldownSeconds,
            MaximumCooldownSeconds
        );
        bool loadTimeoutInRange = Range(
            failures,
            nameof(IdentityOptions.SigningKeyLoadTimeoutSeconds),
            options.SigningKeyLoadTimeoutSeconds,
            MinimumLoadTimeoutSeconds,
            MaximumLoadTimeoutSeconds
        );

        // A snapshot must outlive one whole failed refresh cycle: a healthy instance whose next scheduled reload fails
        // still serves overdue keys until the following one, instead of failing closed between two refreshes. The
        // relational rules are checked only when their terms are in range, so each failure is reported once.
        if (refreshIntervalInRange)
        {
            long minimumMaxStaleness = 2L * options.SigningKeyRefreshIntervalSeconds;
            if (
                options.SigningKeyMaxStalenessSeconds < minimumMaxStaleness
                || options.SigningKeyMaxStalenessSeconds > MaximumMaxStalenessSeconds
            )
            {
                failures.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{Section}:{nameof(IdentityOptions.SigningKeyMaxStalenessSeconds)} must be between 2 x {Section}:{nameof(IdentityOptions.SigningKeyRefreshIntervalSeconds)} ({minimumMaxStaleness}) and {MaximumMaxStalenessSeconds}; it is {options.SigningKeyMaxStalenessSeconds}."
                    )
                );
            }
        }
        else
        {
            Range(
                failures,
                nameof(IdentityOptions.SigningKeyMaxStalenessSeconds),
                options.SigningKeyMaxStalenessSeconds,
                2 * MinimumRefreshIntervalSeconds,
                MaximumMaxStalenessSeconds
            );
        }

        // A load must end before the next scheduled one is due, so scheduled attempts never overlap.
        if (
            refreshIntervalInRange
            && loadTimeoutInRange
            && options.SigningKeyLoadTimeoutSeconds >= options.SigningKeyRefreshIntervalSeconds
        )
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Section}:{nameof(IdentityOptions.SigningKeyLoadTimeoutSeconds)} must be less than {Section}:{nameof(IdentityOptions.SigningKeyRefreshIntervalSeconds)} ({options.SigningKeyRefreshIntervalSeconds}); it is {options.SigningKeyLoadTimeoutSeconds}."
                )
            );
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool Range(List<string> failures, string property, int value, int minimum, int maximum)
    {
        if (value >= minimum && value <= maximum)
        {
            return true;
        }

        failures.Add(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{Section}:{property} must be between {minimum} and {maximum}; it is {value}."
            )
        );
        return false;
    }
}
