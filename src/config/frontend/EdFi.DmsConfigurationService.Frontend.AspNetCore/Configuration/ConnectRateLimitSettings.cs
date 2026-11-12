// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;

/// <summary>
/// Limits for the <c>/connect/*</c> endpoints (DMS-1482), bound from the top-level
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

    /// <summary>
    /// Token, revocation and authenticated introspection requests admitted per source and
    /// <c>client_id</c> pair in each window.
    /// </summary>
    public int ClientPermitLimit { get; set; } = 30;

    /// <summary>Length in seconds of the fixed window that both budgets are counted over.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// When set, enabled limiting requires forwarded headers and this exact trusted proxy address.
    /// Compose sets it to the reserved DMS peer; direct deployments leave it empty.
    /// </summary>
    public string RequiredProxyAddress { get; set; } = string.Empty;
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

/// <summary>Prevents a configured token proxy from silently sharing its source budget with its callers.</summary>
public class ConnectRateLimitProxySettingsValidator(IOptions<ReverseProxySettings> proxyOptions)
    : IValidateOptions<ConnectRateLimitSettings>
{
    public ValidateOptionsResult Validate(string? name, ConnectRateLimitSettings options)
    {
        if (!options.Enabled || string.IsNullOrEmpty(options.RequiredProxyAddress))
        {
            return ValidateOptionsResult.Success;
        }

        if (!IPAddress.TryParse(options.RequiredProxyAddress, out var requiredAddress))
        {
            return ValidateOptionsResult.Fail("ConnectRateLimit:RequiredProxyAddress must be an IP address.");
        }

        ReverseProxySettings proxy = proxyOptions.Value;
        bool explicitlyTrusted = Array.Exists(
            proxy.GetKnownProxies(),
            value => IPAddress.TryParse(value, out var address) && address.Equals(requiredAddress)
        );
        return proxy.UseForwardedHeaders && explicitlyTrusted
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "ConnectRateLimit:RequiredProxyAddress requires AppSettings:ReverseProxy:UseForwardedHeaders=true "
                    + "and that exact address in AppSettings:ReverseProxy:KnownProxies."
            );
    }
}
