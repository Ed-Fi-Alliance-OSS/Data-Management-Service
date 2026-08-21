// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Configuration;

/// <summary>
/// The fixed traditional-paging scenario matrix this harness measures: three offsets by two
/// page sizes. The matrix is deliberately closed; widening it belongs to a later story.
/// </summary>
public static class PerfScenarios
{
    public const string TraditionalOffsetZero = "traditional-offset-zero";
    public const string TraditionalOffsetShallow = "traditional-offset-shallow";
    public const string TraditionalOffsetDeep = "traditional-offset-deep";

    public static readonly IReadOnlyList<string> AllIds =
    [
        TraditionalOffsetZero,
        TraditionalOffsetShallow,
        TraditionalOffsetDeep,
    ];

    public static readonly IReadOnlyList<int> PageSizes = [25, 500];

    public const int MaximumPageSize = 500;

    public static bool IsKnown(string scenarioId) => AllIds.Contains(scenarioId);
}
