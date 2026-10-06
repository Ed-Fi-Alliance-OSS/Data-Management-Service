// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;

/// <summary>
/// The validated signing-key settings as durations. The only way to obtain one is <see cref="FromIdentityOptions"/>,
/// which applies <see cref="SigningKeyOptionsValidator"/>, so a component built from these settings can never run with
/// values the startup validation would have rejected.
/// </summary>
public sealed class SigningKeySettings
{
    private SigningKeySettings(IdentityOptions options)
    {
        RefreshInterval = TimeSpan.FromSeconds(options.SigningKeyRefreshIntervalSeconds);
        MaxStaleness = TimeSpan.FromSeconds(options.SigningKeyMaxStalenessSeconds);
        UnknownKeyRefreshCooldown = TimeSpan.FromSeconds(options.SigningKeyUnknownKeyRefreshCooldownSeconds);
        LoadTimeout = TimeSpan.FromSeconds(options.SigningKeyLoadTimeoutSeconds);
    }

    /// <summary>Time between scheduled reloads; a snapshot younger than this is fresh.</summary>
    public TimeSpan RefreshInterval { get; }

    /// <summary>Maximum age of a usable snapshot; older snapshots are expired and fail closed (<c>T_max</c>).</summary>
    public TimeSpan MaxStaleness { get; }

    /// <summary>Minimum time between the last completed load and an unknown-kid refresh.</summary>
    public TimeSpan UnknownKeyRefreshCooldown { get; }

    /// <summary>Maximum duration of a single load attempt.</summary>
    public TimeSpan LoadTimeout { get; }

    /// <summary>
    /// Validates <paramref name="options"/> and returns its signing-key settings.
    /// </summary>
    /// <exception cref="OptionsValidationException">A signing-key setting is invalid; every failure is listed.</exception>
    public static SigningKeySettings FromIdentityOptions(IdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ValidateOptionsResult result = new SigningKeyOptionsValidator().Validate(
            Options.DefaultName,
            options
        );
        if (result.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(IdentityOptions),
                result.Failures ?? [result.FailureMessage]
            );
        }

        return new SigningKeySettings(options);
    }
}
