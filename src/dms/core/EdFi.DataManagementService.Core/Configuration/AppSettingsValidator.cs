// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Validates the paging-related <see cref="AppSettings"/> values that later request handling and
/// partition sizing depend on.
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

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
