// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// The deprecated 8.x upper bound on how long DMS reuses its Configuration Service bearer. The issuer's
/// lifetime governs reuse; an explicit value only lowers it. The setting is removed in the next major
/// release.
/// </summary>
public sealed class ConfigurationServiceTokenCacheOptions
{
    public const string Key = "CacheSettings:TokenCacheExpirationSeconds";

    /// <summary>The explicit cap in seconds, or null when the setting is absent.</summary>
    public int? ExpirationCapSeconds { get; set; }

    /// <summary>
    /// Logs the deprecation warning when the cap is set, and nothing otherwise. Each host calls this
    /// once while it starts, before its first Configuration Service call. The value is not logged:
    /// the warning is about the setting, and an operator can read their own value.
    /// </summary>
    public static void LogDeprecationIfSet(ConfigurationServiceTokenCacheOptions options, ILogger logger)
    {
        if (options.ExpirationCapSeconds is not null)
        {
            logger.LogWarning(
                "{SettingKey} is deprecated. DMS now reuses its Configuration Service token for the lifetime the issuer returns, and an explicit value only lowers that. Remove the setting; it is removed in the next major release.",
                Key
            );
        }
    }
}

/// <summary>
/// Reads <see cref="ConfigurationServiceTokenCacheOptions.Key" /> from its raw configuration value. An
/// absent key means no cap. A present value must be a positive whole number of seconds; anything else
/// fails options validation with a message that names the key and never the supplied value.
/// </summary>
internal sealed class ConfigurationServiceTokenCacheOptionsSetup(IConfiguration configuration)
    : IConfigureOptions<ConfigurationServiceTokenCacheOptions>,
        IValidateOptions<ConfigurationServiceTokenCacheOptions>
{
    public const string InvalidCapError =
        $"{ConfigurationServiceTokenCacheOptions.Key} must be a positive whole number of seconds when it is set.";

    public void Configure(ConfigurationServiceTokenCacheOptions options) =>
        options.ExpirationCapSeconds = TryParse(configuration[ConfigurationServiceTokenCacheOptions.Key]);

    // Configure leaves the cap null for a present value it cannot parse, so a present key with no cap
    // is exactly the invalid case.
    public ValidateOptionsResult Validate(string? name, ConfigurationServiceTokenCacheOptions options) =>
        configuration[ConfigurationServiceTokenCacheOptions.Key] is null
        || options.ExpirationCapSeconds is not null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(InvalidCapError);

    private static int? TryParse(string? rawValue) =>
        int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
        && seconds > 0
            ? seconds
            : null;
}
