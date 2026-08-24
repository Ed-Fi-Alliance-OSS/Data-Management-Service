// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// Thrown when the harness run configuration is invalid. Carries every validation error so a
/// misconfigured run is fixed in one pass rather than one error at a time.
/// </summary>
public sealed class PerfConfigurationException(IReadOnlyList<string> errors)
    : Exception(
        "Invalid performance harness configuration:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, errors)
    )
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
