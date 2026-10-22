// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

/// <summary>
/// Limits for the anonymous <c>/connect/*</c> endpoints (DMS-1482), bound from the top-level
/// <c>ConnectRateLimit</c> section. Each budget is a fixed window of <see cref="WindowSeconds"/> with no
/// queue, enforced per process. See docs/CONFIGURATION.md#connectratelimit.
/// </summary>
public class ConnectRateLimitSettings
{
    public const string SectionName = "ConnectRateLimit";

    /// <summary>When false, no <c>/connect/*</c> request is rate limited.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Requests admitted per source address in each window, across all four <c>/connect/*</c> routes.
    /// A native IPv6 source is counted per /64.
    /// </summary>
    public int SourcePermitLimit { get; set; } = 120;

    /// <summary>Token and revocation requests admitted per <c>client_id</c> in each window, across all sources.</summary>
    public int ClientPermitLimit { get; set; } = 30;

    public int WindowSeconds { get; set; } = 60;
}

public class ConnectRateLimitSettingsValidator : IValidateOptions<ConnectRateLimitSettings>
{
    public ValidateOptionsResult Validate(string? name, ConnectRateLimitSettings options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        AddFailureUnlessPositive(failures, nameof(options.SourcePermitLimit), options.SourcePermitLimit);
        AddFailureUnlessPositive(failures, nameof(options.ClientPermitLimit), options.ClientPermitLimit);
        AddFailureUnlessPositive(failures, nameof(options.WindowSeconds), options.WindowSeconds);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddFailureUnlessPositive(List<string> failures, string setting, int value)
    {
        if (value < 1)
        {
            failures.Add($"{ConnectRateLimitSettings.SectionName}:{setting} must be a positive integer.");
        }
    }
}
