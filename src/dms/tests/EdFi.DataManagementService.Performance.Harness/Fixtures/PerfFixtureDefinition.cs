// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// The deterministic fixture definition shared between this baseline and the final-gate story,
/// so both measure byte-identical data. Row k (1-based) maps to the k-th positive integer that
/// is not congruent to 1 modulo 10: every complete block of ten ids carries exactly one gap,
/// and leading each block with its gap keeps the gap share of the id space at or above 10%
/// even when the final block is partial (a trailing gap would leave it fractionally under).
/// Relative to the row count the gap share is one per nine rows.
///
/// Every identity derivation here must stay expressible in set-based SQL on both PostgreSQL
/// and SQL Server, because the loaders generate rows with generate_series/GENERATE_SERIES and
/// must produce exactly these values.
/// </summary>
public sealed record PerfFixtureDefinition(PerfFixtureKind Kind)
{
    public const string DefinitionVersion = "1.0.0";

    public const string ResourceEndpoint = "/data/ed-fi/students";

    /// <summary>
    /// Fixed prefix of every fixture DocumentUuid; the final twelve hex digits are the row
    /// ordinal. The '4' and '8' nibbles keep the value RFC-4122-shaped.
    /// </summary>
    public const string DocumentUuidPrefix = "8f7a0000-0000-4000-8000-";

    public long RowCount => Kind.RowCount;

    public static long MinDocumentId => DocumentIdFor(1);

    public long MaxDocumentId => DocumentIdFor(RowCount);

    /// <summary>
    /// Missing ids inside [1, MaxDocumentId], counted analytically.
    /// </summary>
    public long GapCount => MaxDocumentId - RowCount;

    /// <summary>
    /// Gap share of the id space [1, MaxDocumentId].
    /// </summary>
    public double GapDensity => (double)GapCount / MaxDocumentId;

    public static long DocumentIdFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        long block = (ordinal - 1) / 9;
        long positionInBlock = (ordinal - 1) % 9;
        return (block * 10) + positionInBlock + 2;
    }

    public static string StudentUniqueIdFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return "perf-" + ordinal.ToString("D9", CultureInfo.InvariantCulture);
    }

    public static Guid DocumentUuidFor(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        return Guid.ParseExact(
            DocumentUuidPrefix + ordinal.ToString("x12", CultureInfo.InvariantCulture),
            "D"
        );
    }
}
