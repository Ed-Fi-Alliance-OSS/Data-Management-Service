// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

internal class CircuitBreakerSettings
{
    public double FailureRatio { get; set; }
    public double SamplingDurationSeconds { get; set; }
    public int MinimumThroughput { get; set; }
    public double BreakDurationSeconds { get; set; }
}
