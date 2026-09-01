// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// A clock the test advances by hand. Expiry is a behavior worth asserting, and asserting it by
/// sleeping would make the suite both slow and flaky.
/// </summary>
internal sealed class ControlledTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}

/// <summary>
/// Shared arrangement for the validation-cache fixtures.
/// </summary>
internal static class ValidationCacheSupport
{
    /// <summary>An arbitrary fixed instant. Nothing depends on its value, only on its stability.</summary>
    public static readonly DateTimeOffset Start = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public const string ConnectionString = "Server=shared;Database=edfi";

    public static CacheSettings SettingsWith(
        int derivativeSeconds = 600,
        bool dataStoreRefreshEnabled = true,
        int dataStoreSeconds = 3600
    ) =>
        new()
        {
            DerivativeValidationCacheExpirationSeconds = derivativeSeconds,
            DataStoreCacheRefreshEnabled = dataStoreRefreshEnabled,
            DataStoreCacheExpirationSeconds = dataStoreSeconds,
        };

    public static ValidationCacheKey PrimaryKey(string connectionString = ConnectionString) =>
        new(ValidationCachePolicyClass.Primary, connectionString);

    public static ValidationCacheKey DerivativeKey(string connectionString = ConnectionString) =>
        new(ValidationCachePolicyClass.Derivative, connectionString);

    public static async Task<Exception?> CatchAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
