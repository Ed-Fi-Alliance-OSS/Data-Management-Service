// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend;

/// <summary>
/// How the host resolves <c>${secret:&lt;name&gt;}</c> references in stored connection strings,
/// bound from <c>SecretsSettings</c>.
/// </summary>
public class SecretsOptions
{
    /// <summary>
    /// How long a resolved value is reused before the resolver is asked again. The expiration is
    /// absolute from the moment the value was fetched, so it bounds how long a rotation takes to
    /// reach this host. Zero disables caching.
    /// </summary>
    public int CacheExpirationSeconds { get; set; } = 300;

    /// <summary>
    /// How long one resolver call may take, from invoking it to receiving its value, before the read
    /// that needed it fails.
    /// </summary>
    public int ResolveTimeoutSeconds { get; set; } = 10;
}

public class SecretsOptionsValidator : IValidateOptions<SecretsOptions>
{
    /// <summary>
    /// The longest delay a .NET timer accepts, so the timeout can always be armed.
    /// </summary>
    internal static readonly int MaximumResolveTimeoutSeconds = (int)
        TimeSpan.FromMilliseconds(uint.MaxValue - 1).TotalSeconds;

    public ValidateOptionsResult Validate(string? name, SecretsOptions options)
    {
        List<string> failures = [];

        if (options.CacheExpirationSeconds < 0)
        {
            failures.Add(
                "SecretsSettings:CacheExpirationSeconds must be zero, which disables caching, or greater."
            );
        }

        if (
            options.ResolveTimeoutSeconds <= 0
            || options.ResolveTimeoutSeconds > MaximumResolveTimeoutSeconds
        )
        {
            failures.Add(
                $"SecretsSettings:ResolveTimeoutSeconds must be between 1 and {MaximumResolveTimeoutSeconds}."
            );
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
