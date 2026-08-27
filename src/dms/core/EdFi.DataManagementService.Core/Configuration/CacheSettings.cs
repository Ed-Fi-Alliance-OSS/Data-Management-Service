// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Cache configuration settings for DMS HybridCache behavior.
/// All expiration values are in seconds. Bound from appsettings.json "CacheSettings" section.
/// </summary>
public class CacheSettings
{
    public int ClaimSetsCacheExpirationSeconds { get; set; } = 600; // 10 minutes
    public int ApplicationContextCacheExpirationSeconds { get; set; } = 600; // 10 minutes
    public int TokenCacheExpirationSeconds { get; set; } = 1500; // 25 minutes
    public int ProfileCacheExpirationSeconds { get; set; } = 1800; // 30 minutes

    /// <summary>
    /// Enables the TTL-based refresh of cached data store configuration from CMS.
    /// </summary>
    public bool DataStoreCacheRefreshEnabled { get; set; } = true;

    /// <summary>
    /// The number of seconds between automatic refreshes of the data store cache.
    /// Set to 0 or a negative value to keep the cached configuration until the next explicit reload.
    /// </summary>
    public int DataStoreCacheExpirationSeconds { get; set; } = 600; // 10 minutes

    /// <summary>
    /// How long a validation verdict for a derivative database stays cached, in seconds.
    /// Default 600, accepted range 1 to 3600; a value outside that range is clamped and logged.
    /// </summary>
    /// <remarks>
    /// <b>A non-positive value means use the default, not "never expire."</b> That deliberately
    /// inverts <see cref="DataStoreCacheExpirationSeconds"/>, where 0 or a negative value means keep
    /// the cached value until an explicit reload. A derivative is a database an operator can rebuild
    /// or repoint without telling DMS, so a verdict about one that never expires would outlive the
    /// database it describes; there is no way to ask for that here.
    /// </remarks>
    public int DerivativeValidationCacheExpirationSeconds { get; set; } = 600; // 10 minutes
}

public sealed class CacheSettingsValidator : IValidateOptions<CacheSettings>
{
    public const string ApplicationContextExpirationValidationError =
        "ApplicationContextCacheExpirationSeconds must be positive.";

    public ValidateOptionsResult Validate(string? name, CacheSettings options) =>
        options.ApplicationContextCacheExpirationSeconds > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(ApplicationContextExpirationValidationError);
}
