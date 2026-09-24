// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// The spec §6.3 validation of <see cref="JobOptions"/>, run at startup. Every failed rule is reported, each message
/// names <c>JobSettings:&lt;Property&gt;</c> and its accepted range, and the renewal lateness inequality reports the
/// value of each of its terms.
/// </summary>
public sealed class JobOptionsValidator : IValidateOptions<JobOptions>
{
    private const string Section = JobOptions.SectionName;

    /// <summary>
    /// The smallest renewal interval: it keeps <see cref="JobOptions.RenewalTimeout"/> at least
    /// <see cref="JobLeaseTimings.WriteLockWait"/> + 1 s, so a renewal's lock wait ends in the provider's lock-timeout
    /// error rather than at its deadline (A3).
    /// </summary>
    public static TimeSpan MinimumRenewalInterval { get; } = TimeSpan.FromSeconds(12);

    public ValidateOptionsResult Validate(string? name, JobOptions options)
    {
        List<string> failures = [];

        Range(
            failures,
            nameof(JobOptions.PollInterval),
            options.PollInterval,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(5)
        );
        Range(
            failures,
            nameof(JobOptions.LeaseDuration),
            options.LeaseDuration,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1)
        );
        Range(
            failures,
            nameof(JobOptions.FenceTimeout),
            options.FenceTimeout,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMinutes(1)
        );
        Range(failures, nameof(JobOptions.MaxAttempts), options.MaxAttempts, 1, 20);
        Range(
            failures,
            nameof(JobOptions.RetryBackoffBase),
            options.RetryBackoffBase,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromHours(1)
        );
        Range(failures, nameof(JobOptions.MaxConcurrentJobs), options.MaxConcurrentJobs, 1, 32);
        Range(
            failures,
            nameof(JobOptions.FinishedJobRetention),
            options.FinishedJobRetention,
            TimeSpan.FromHours(1),
            TimeSpan.FromDays(365)
        );
        Range(
            failures,
            nameof(JobOptions.RetentionInterval),
            options.RetentionInterval,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromHours(24)
        );
        Range(failures, nameof(JobOptions.RetentionBatchSize), options.RetentionBatchSize, 1, 2_000);

        if (
            options.RetryBackoffMaximum < options.RetryBackoffBase
            || options.RetryBackoffMaximum > TimeSpan.FromHours(24)
        )
        {
            failures.Add(
                $"{Section}:{nameof(JobOptions.RetryBackoffMaximum)} must be at least {Section}:{nameof(JobOptions.RetryBackoffBase)} "
                    + $"({Format(options.RetryBackoffBase)}) and at most {Format(TimeSpan.FromHours(24))}; it is {Format(options.RetryBackoffMaximum)}."
            );
        }

        TimeSpan maximumRenewalInterval = options.LeaseDuration / 3;
        if (
            options.RenewalInterval < MinimumRenewalInterval
            || options.RenewalInterval > maximumRenewalInterval
        )
        {
            failures.Add(
                $"{Section}:{nameof(JobOptions.RenewalInterval)} must be between {Format(MinimumRenewalInterval)} and "
                    + $"{Section}:{nameof(JobOptions.LeaseDuration)} / 3 ({Format(maximumRenewalInterval)}); it is {Format(options.RenewalInterval)}. "
                    + $"The minimum keeps the renewal timeout (half the interval) above {Format(JobLeaseTimings.WriteLockWait)} + 1 s."
            );
        }

        // Renewal lateness (§6.3): a renewal scheduled late by a fence holding the gate, then waiting for the row lock
        // and taking its whole timeout, must still land before the lease expires, with a positive safety margin. The
        // sum is taken in decimal ticks, so settings far outside their bounds, which already failed above, cannot
        // overflow it and hide those failures.
        TimeSpan safetyMargin = SafetyMargin(options.LeaseDuration);
        decimal worstRenewalTicks =
            (decimal)options.RenewalInterval.Ticks
            + JobLeaseTimings.FenceLockWait.Ticks
            + options.FenceTimeout.Ticks
            + JobLeaseTimings.WriteLockWait.Ticks
            + options.RenewalTimeout.Ticks
            + safetyMargin.Ticks;
        if (worstRenewalTicks > options.LeaseDuration.Ticks)
        {
            failures.Add(
                $"{Section}:{nameof(JobOptions.LeaseDuration)} ({Format(options.LeaseDuration)}) must be at least "
                    + $"RenewalInterval ({Format(options.RenewalInterval)}) + FenceLockWait ({Format(JobLeaseTimings.FenceLockWait)}) "
                    + $"+ FenceTimeout ({Format(options.FenceTimeout)}) + WriteLockWait ({Format(JobLeaseTimings.WriteLockWait)}) "
                    + $"+ RenewalTimeout ({Format(options.RenewalTimeout)}) + SafetyMargin ({Format(safetyMargin)}, the larger of 10 s "
                    + $"and LeaseDuration / 6) = {Format(worstRenewalTicks)}, so that a renewal can never be scheduled to land at "
                    + "expiry. Lengthen JobSettings:LeaseDuration, or shorten JobSettings:RenewalInterval or JobSettings:FenceTimeout."
            );
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The §6.3 safety margin: the larger of 10 s and a sixth of the lease.</summary>
    public static TimeSpan SafetyMargin(TimeSpan leaseDuration) =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(10).Ticks, leaseDuration.Ticks / 6));

    private static void Range(
        List<string> failures,
        string property,
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum
    )
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(
                $"{Section}:{property} must be between {Format(minimum)} and {Format(maximum)}; it is {Format(value)}."
            );
        }
    }

    private static void Range(List<string> failures, string property, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Section}:{property} must be between {minimum} and {maximum}; it is {value}."
                )
            );
        }
    }

    private static string Format(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

    /// <summary>A tick count that may lie outside <see cref="TimeSpan"/>'s range, formatted as a duration.</summary>
    private static string Format(decimal ticks)
    {
        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            return $"more than {Format(TimeSpan.MaxValue)}";
        }

        if (ticks < TimeSpan.MinValue.Ticks)
        {
            return $"less than {Format(TimeSpan.MinValue)}";
        }

        return Format(TimeSpan.FromTicks((long)ticks));
    }
}
