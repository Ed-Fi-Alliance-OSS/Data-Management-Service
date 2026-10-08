// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>The fixed time bounds of the schedule repository (spec D-8, D-10, §6.1).</summary>
public static class JobScheduleTimings
{
    /// <summary>
    /// <c>ScheduleMaterializationTimeout</c>: the deadline of one materialization transaction, from its connection
    /// through its commit and cleanup.
    /// </summary>
    public static TimeSpan MaterializationTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The deadline of one upsert, disable, or list. It exceeds <see cref="MaterializationTimeout"/>, so a write
    /// that waits on the row lock of a materialization in progress outlasts that transaction.
    /// </summary>
    public static TimeSpan CommandTimeout { get; } = TimeSpan.FromSeconds(30);
}
