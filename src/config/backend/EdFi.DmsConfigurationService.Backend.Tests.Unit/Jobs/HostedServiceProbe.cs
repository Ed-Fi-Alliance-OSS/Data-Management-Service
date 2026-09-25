// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

/// <summary>Waiting helpers for the job hosted services, which run on a fake clock.</summary>
internal static class HostedServiceProbe
{
    public static async Task Until(Func<bool> condition, string because)
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    /// <summary>
    /// Advances the clock one <paramref name="step"/> at a time until <paramref name="condition"/> holds. A service may
    /// not have registered its next delay when its last run is observed, so a single advance could be lost; each
    /// advance fires at most the one pending delay, so runs are never skipped or doubled.
    /// </summary>
    public static async Task AdvanceUntil(
        FakeTimeProvider time,
        TimeSpan step,
        Func<bool> condition,
        string because
    )
    {
        DateTime giveUp = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException($"Timed out advancing until {because}.");
            }
            time.Advance(step);
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    /// <summary>Stops <paramref name="service"/>, failing rather than hanging when it does not end.</summary>
    public static async Task StopAsync(BackgroundService service)
    {
        using CancellationTokenSource giveUp = new(TimeSpan.FromSeconds(10));
        await service.StopAsync(giveUp.Token);
        if (!service.ExecuteTask!.IsCompleted)
        {
            throw new TimeoutException("The service did not stop within 10 seconds.");
        }
    }
}
