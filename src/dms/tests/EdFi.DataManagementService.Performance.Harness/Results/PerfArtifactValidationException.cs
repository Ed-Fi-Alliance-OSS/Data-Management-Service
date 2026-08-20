// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Thrown when a run produced incomplete or inconsistent artifacts. The harness fails loudly
/// on artifact defects because a baseline that did not do the expected work is not evidence.
/// </summary>
public sealed class PerfArtifactValidationException(IReadOnlyList<string> errors)
    : Exception(
        "Invalid performance harness artifacts:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, errors)
    )
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
