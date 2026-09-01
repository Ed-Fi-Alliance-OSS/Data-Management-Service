// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Results;

/// <summary>
/// Version stamp carried by every final-gate artifact. Separate from the baseline's
/// <see cref="PerfArtifactSchema" />: the DMS-1391 baseline stays at its frozen 1.3.0 shape,
/// which the later comparison work reads for baseline inputs only, while the final-gate
/// artifacts carry this schema. Any change to the final-gate shapes or CSV column set
/// requires bumping this.
/// </summary>
public static class PerfFinalGateArtifactSchema
{
    public const string Version = "2.0.0";
}

/// <summary>
/// The two final-gate run kinds: the shared primary-load run (traditional rerun, unfiltered,
/// authorized, filtered phases over one database) and the separate descriptor fixture run.
/// </summary>
public static class PerfFinalGateRunKinds
{
    public const string Primary = "final-primary";

    public const string Descriptors = "final-descriptors";
}

/// <summary>
/// Where a row's replay parameter values came from: the hydration keyset's captured bound
/// values (regular-resource traditional and cursor reads), or the recorded relational-channel
/// command's bound values (descriptor reads and partition boundary selections).
/// </summary>
public static class PerfFinalGateReplaySources
{
    public const string HydrationKeyset = "hydration-keyset";

    public const string RelationalCommand = "relational-command";
}
