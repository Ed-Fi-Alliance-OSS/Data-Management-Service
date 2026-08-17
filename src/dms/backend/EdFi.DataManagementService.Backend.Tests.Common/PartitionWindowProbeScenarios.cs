// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// One partition-boundary execution case and the boundaries it must produce.
/// </summary>
/// <param name="Description">Names the case in assertion messages.</param>
/// <param name="SchoolIdFilter">The root-column filter to apply, or null for the whole table.</param>
/// <param name="ChangeVersionRange">The change-version window to apply, or null for none.</param>
/// <param name="RequestedPartitionCount">The count the request asks for.</param>
/// <param name="MinimumPartitionSize">The smallest partition, in candidate rows.</param>
/// <param name="ExpectedStarts">
/// The starting <c>DocumentId</c>s the statement must return, written out rather than recomputed from
/// the sizing rule. Deriving them from the same formula the SQL implements would make every assertion
/// agree with the implementation by construction.
/// </param>
internal sealed record PartitionWindowProbeScenario(
    string Description,
    long? SchoolIdFilter,
    ChangeVersionRange? ChangeVersionRange,
    int RequestedPartitionCount,
    long MinimumPartitionSize,
    IReadOnlyList<long> ExpectedStarts
);

/// <summary>
/// The seed, the cases, and the expected boundaries both provider partition-execution probes run.
/// </summary>
/// <remarks>
/// Shared so the two providers are held to one set of expectations. Cross-provider typed-range
/// equivalence is what this file makes assertable: each fixture executes its own dialect's compiled
/// statement and compares against these same values, so a divergence in either dialect's ceiling,
/// modulo, or count expression fails on the provider that diverged.
/// <para>
/// Identifiers are seeded ten apart so a boundary can only be right by selecting the identifier at a
/// row number. Dividing the identifier range arithmetically would land between stored values, and every
/// expected start below is a stored one.
/// </para>
/// </remarks>
internal static class PartitionWindowProbeScenarios
{
    /// <summary>The probe root. Named for this probe so it cannot collide with another fixture's.</summary>
    public static readonly DbTableName RootTable = new(new DbSchemaName("edfi"), "PartitionProbeRoot");

    /// <summary>Rows seeded into the probe root.</summary>
    public const int SeededRowCount = 200;

    /// <summary>The gap between consecutive seeded identifiers.</summary>
    public const long DocumentIdStride = 10L;

    /// <summary>The <c>SchoolId</c> carried by rows 1 through 149.</summary>
    public const long BulkSchoolId = 900L;

    /// <summary>The <c>SchoolId</c> carried by rows 151 through 200.</summary>
    public const long TailSchoolId = 901L;

    /// <summary>The <c>SchoolId</c> carried by row 150 alone, so a one-candidate case exists.</summary>
    public const long SingletonSchoolId = 902L;

    /// <summary>A <c>SchoolId</c> no row carries, so an empty case exists.</summary>
    public const long UnmatchedSchoolId = 999L;

    /// <summary>The identifier at a one-based candidate row number.</summary>
    public static long DocumentIdAt(int rowNumber) => rowNumber * DocumentIdStride;

    /// <summary>Every seeded identifier, ascending.</summary>
    public static IReadOnlyList<long> AllDocumentIds =>
        [.. Enumerable.Range(1, SeededRowCount).Select(DocumentIdAt)];

    /// <summary>
    /// The preprocessing result for a request that carries no resource-property filter.
    /// </summary>
    public static RelationalQueryPreprocessingResult CreateUnfilteredPreprocessingResult() =>
        new(new RelationalQueryPreprocessingOutcome.Continue(), []);

    /// <summary>
    /// Every case both providers run. Counts of 1, 10, and 200 over the same 200 rows, a division that
    /// does not divide evenly, more partitions requested than there are rows, the minimum-size clamp, a
    /// filtered candidate set, a change-version window, both composed, a single candidate, and none.
    /// </summary>
    public static IReadOnlyList<PartitionWindowProbeScenario> All =>
        [
            new PartitionWindowProbeScenario(
                "one partition over every row",
                SchoolIdFilter: null,
                ChangeVersionRange: null,
                RequestedPartitionCount: 1,
                MinimumPartitionSize: 1L,
                ExpectedStarts: [10L]
            ),
            new PartitionWindowProbeScenario(
                "ten partitions over every row",
                SchoolIdFilter: null,
                ChangeVersionRange: null,
                RequestedPartitionCount: 10,
                MinimumPartitionSize: 1L,
                ExpectedStarts: [10L, 210L, 410L, 610L, 810L, 1010L, 1210L, 1410L, 1610L, 1810L]
            ),
            new PartitionWindowProbeScenario(
                "two hundred partitions over two hundred rows",
                SchoolIdFilter: null,
                ChangeVersionRange: null,
                RequestedPartitionCount: SeededRowCount,
                MinimumPartitionSize: 1L,
                ExpectedStarts: AllDocumentIds
            ),
            // 200 / 3 is 66.67. A truncating division would size partitions at 66 and produce a fourth
            // boundary at row 199, so this case is what proves the ceiling is a real non-integer divide.
            new PartitionWindowProbeScenario(
                "three partitions over a count that does not divide evenly",
                SchoolIdFilter: null,
                ChangeVersionRange: null,
                RequestedPartitionCount: 3,
                MinimumPartitionSize: 1L,
                ExpectedStarts: [10L, 680L, 1350L]
            ),
            // More partitions than rows cannot produce more partitions than rows.
            new PartitionWindowProbeScenario(
                "more partitions requested than there are rows",
                SchoolIdFilter: null,
                ChangeVersionRange: null,
                RequestedPartitionCount: 300,
                MinimumPartitionSize: 1L,
                ExpectedStarts: AllDocumentIds
            ),
            // The clamp, not the requested count, decides the size here: 200 partitions of 200 rows would
            // be one row each, and the minimum forces 50.
            new PartitionWindowProbeScenario(
                "minimum partition size larger than the requested count implies",
                SchoolIdFilter: null,
                ChangeVersionRange: null,
                RequestedPartitionCount: SeededRowCount,
                MinimumPartitionSize: 50L,
                ExpectedStarts: [10L, 510L, 1010L, 1510L]
            ),
            new PartitionWindowProbeScenario(
                "five partitions over a filtered candidate set",
                SchoolIdFilter: TailSchoolId,
                ChangeVersionRange: null,
                RequestedPartitionCount: 5,
                MinimumPartitionSize: 1L,
                ExpectedStarts: [1510L, 1610L, 1710L, 1810L, 1910L]
            ),
            new PartitionWindowProbeScenario(
                "four partitions inside a change-version window",
                SchoolIdFilter: null,
                ChangeVersionRange: new ChangeVersionRange(181L, 200L),
                RequestedPartitionCount: 4,
                MinimumPartitionSize: 1L,
                ExpectedStarts: [1810L, 1860L, 1910L, 1960L]
            ),
            new PartitionWindowProbeScenario(
                "a filter and a change-version window composed",
                SchoolIdFilter: TailSchoolId,
                ChangeVersionRange: new ChangeVersionRange(191L, 200L),
                RequestedPartitionCount: 2,
                MinimumPartitionSize: 1L,
                ExpectedStarts: [1910L, 1960L]
            ),
            new PartitionWindowProbeScenario(
                "a single candidate",
                SchoolIdFilter: SingletonSchoolId,
                ChangeVersionRange: null,
                RequestedPartitionCount: 10,
                MinimumPartitionSize: 1L,
                ExpectedStarts: [1500L]
            ),
            new PartitionWindowProbeScenario(
                "no candidates",
                SchoolIdFilter: UnmatchedSchoolId,
                ChangeVersionRange: null,
                RequestedPartitionCount: 4,
                MinimumPartitionSize: 1L,
                ExpectedStarts: []
            ),
        ];

    /// <summary>
    /// The inclusive ranges a scenario's starts must become: every range but the last closes one before
    /// the next start, and the last is unbounded above.
    /// </summary>
    public static IReadOnlyList<CursorRange> ExpectedRanges(PartitionWindowProbeScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        return
        [
            .. scenario.ExpectedStarts.Select(
                (start, index) =>
                    new CursorRange(
                        start,
                        index + 1 < scenario.ExpectedStarts.Count
                            ? scenario.ExpectedStarts[index + 1] - 1
                            : long.MaxValue
                    )
            ),
        ];
    }

    public static IReadOnlyList<string> BuildPostgresqlSchemaStatements() =>
        [
            """CREATE SCHEMA IF NOT EXISTS "edfi";""",
            """
                CREATE TABLE "edfi"."PartitionProbeRoot" (
                    "DocumentId" bigint NOT NULL PRIMARY KEY,
                    "ContentVersion" bigint NOT NULL,
                    "SchoolId" bigint NOT NULL,
                    "Namespace" varchar(255) NULL
                );
                """,
            $"""
                INSERT INTO "edfi"."PartitionProbeRoot"
                    ("DocumentId", "ContentVersion", "SchoolId", "Namespace")
                SELECT
                    n * {DocumentIdStride},
                    n,
                    CASE
                        WHEN n < 150 THEN {BulkSchoolId}
                        WHEN n = 150 THEN {SingletonSchoolId}
                        ELSE {TailSchoolId}
                    END,
                    'uri://ed-fi.org/Probe'
                FROM generate_series(1, {SeededRowCount}) AS n;
                """,
        ];

    public static IReadOnlyList<string> BuildMssqlSchemaStatements() =>
        [
            "IF SCHEMA_ID('edfi') IS NULL EXEC('CREATE SCHEMA [edfi]');",
            """
                CREATE TABLE [edfi].[PartitionProbeRoot] (
                    [DocumentId] bigint NOT NULL PRIMARY KEY,
                    [ContentVersion] bigint NOT NULL,
                    [SchoolId] bigint NOT NULL,
                    [Namespace] nvarchar(255) NULL
                );
                """,
            $"""
                WITH rowNumbers AS (
                    SELECT CAST(1 AS bigint) AS n
                    UNION ALL
                    SELECT n + 1 FROM rowNumbers WHERE n < {SeededRowCount}
                )
                INSERT INTO [edfi].[PartitionProbeRoot]
                    ([DocumentId], [ContentVersion], [SchoolId], [Namespace])
                SELECT
                    n * {DocumentIdStride},
                    n,
                    CASE
                        WHEN n < 150 THEN {BulkSchoolId}
                        WHEN n = 150 THEN {SingletonSchoolId}
                        ELSE {TailSchoolId}
                    END,
                    N'uri://ed-fi.org/Probe'
                FROM rowNumbers
                OPTION (MAXRECURSION {SeededRowCount});
                """,
        ];
}
