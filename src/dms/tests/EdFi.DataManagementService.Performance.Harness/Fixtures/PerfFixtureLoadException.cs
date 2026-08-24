// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// Thrown when the fixture load could not run or the loaded database failed verification. A
/// run measured against an unverified fixture is not evidence, so the loader fails loudly.
/// </summary>
public sealed class PerfFixtureLoadException(IReadOnlyList<string> errors)
    : Exception("Fixture load failed:" + Environment.NewLine + string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
