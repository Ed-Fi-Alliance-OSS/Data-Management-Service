// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// A descriptor fixture the final gate can measure against: the epic's 25,000-row descriptor
/// set split across accessible and inaccessible namespaces, or its scaled-down variant for
/// loader validation and end-to-end harness smokes. Separate from <see cref="PerfFixtureKind" />
/// because a descriptor fixture has no student shape, no sparse-id scheme, and no deep-offset
/// semantics.
/// </summary>
public sealed record PerfDescriptorFixtureKind(string Id, long RowCount)
{
    public static readonly PerfDescriptorFixtureKind Descriptors25k = new("descriptors-25k", 25_000);

    public static readonly PerfDescriptorFixtureKind DescriptorsSmoke2k = new("descriptors-smoke-2k", 2_000);

    public static readonly IReadOnlyList<PerfDescriptorFixtureKind> All =
    [
        Descriptors25k,
        DescriptorsSmoke2k,
    ];

    public static PerfDescriptorFixtureKind? FindById(string fixtureId) =>
        All.FirstOrDefault(fixture => fixture.Id == fixtureId.Trim());
}
