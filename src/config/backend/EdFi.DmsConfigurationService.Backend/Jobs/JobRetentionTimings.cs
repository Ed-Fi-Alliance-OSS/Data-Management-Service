// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>The fixed time bound of job retention (spec D-17, §6.1).</summary>
public static class JobRetentionTimings
{
    /// <summary>The deadline of one retention batch: its connection, its <c>DELETE</c>, and its cleanup.</summary>
    public static TimeSpan BatchTimeout { get; } = TimeSpan.FromSeconds(30);
}
