// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// Creates or updates the current tenant's schedule of <paramref name="ScheduleType"/> (spec D-10).
/// </summary>
public sealed record JobScheduleUpsertCommand(
    string ScheduleType,
    string JobType,
    short PayloadVersion,
    string PayloadJson,
    int IntervalMinutes,
    bool RunFirstOccurrenceImmediately
);
