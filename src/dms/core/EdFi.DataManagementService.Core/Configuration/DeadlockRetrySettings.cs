// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

internal class DeadlockRetrySettings
{
    /// <summary>
    /// Maximum number of retry attempts after the initial attempt.
    /// Default: 3 (up to 4 total attempts). Set to 0 to disable retries.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay in milliseconds for exponential backoff.
    /// </summary>
    public int BaseDelayMilliseconds { get; set; } = 150;

    /// <summary>
    /// Whether to add jitter to the backoff delay to prevent thundering-herd.
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Total timeout in milliseconds for the entire operation including all retries.
    /// Acts as a safety ceiling to prevent unbounded request latency.
    /// </summary>
    public int TotalTimeoutMilliseconds { get; set; } = 5000;
}
