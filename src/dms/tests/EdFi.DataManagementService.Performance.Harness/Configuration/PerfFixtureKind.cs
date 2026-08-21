// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// A fixture the harness can measure against: the epic's single 500,000-row primary fixture,
/// or its scaled-down variant for loader validation and end-to-end harness smokes.
/// </summary>
public sealed record PerfFixtureKind(string Id, long RowCount)
{
    public static readonly PerfFixtureKind Primary500k = new("primary-500k", 500_000);

    public static readonly PerfFixtureKind Smoke10k = new("smoke-10k", 10_000);

    public static readonly IReadOnlyList<PerfFixtureKind> All = [Primary500k, Smoke10k];

    public static PerfFixtureKind? FindById(string fixtureId) =>
        All.FirstOrDefault(fixture => fixture.Id == fixtureId.Trim());
}
