// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// Expired-token cleanup settings, bound from <c>IdentitySettings:TokenCleanup</c>.
/// </summary>
public class TokenCleanupOptions
{
    /// <summary>
    /// Whether the background sweep that deletes expired OpenIddict tokens runs.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval, in minutes, between expired-token cleanup sweeps.
    /// </summary>
    public int IntervalMinutes { get; set; } = 30;
}
