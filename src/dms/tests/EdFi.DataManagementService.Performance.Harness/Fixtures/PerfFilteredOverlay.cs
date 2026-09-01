// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Performance.Harness.Configuration;

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// The filtered-variant overlay: a deterministic in-place update giving every tenth student
/// (the filtered candidate selection) a distinct birth date of equal ISO text length, so a
/// birthDate equality filter selects exactly ten percent of the candidate set while row
/// counts, DocumentIds, gaps, and value byte lengths stay untouched. The overlay is
/// irreversible within a run and must therefore be the last mutation of the shared primary
/// load; no earlier-phase cell may measure after it. The update fires the production stamp
/// trigger — ContentVersion advances on the varied rows, which DocumentId-anchored cursor
/// measurement never reads — and writes no tracked-change rows, because the identity column
/// is untouched.
/// </summary>
public static class PerfFilteredOverlay
{
    public const string OverlayBirthDateIso = "2010-06-15";

    public static long OverlaidStudentCount(PerfFixtureDefinition definition) =>
        PerfVariantCandidates.CandidateCount(PerfFinalGateVariant.Filtered, definition.RowCount);

    /// <summary>
    /// The student row ordinal the k-th overlaid row occupies.
    /// </summary>
    public static long OverlaidStudentOrdinal(long candidateIndex) =>
        PerfVariantCandidates.RowOrdinalOfCandidate(PerfFinalGateVariant.Filtered, candidateIndex);

    /// <summary>
    /// Sum of every overlaid student's DocumentId, the cross-provider checksum the overlay
    /// verification holds the varied rows to.
    /// </summary>
    public static long OverlaidDocumentIdSum(PerfFixtureDefinition definition)
    {
        long sum = 0;
        long count = OverlaidStudentCount(definition);
        for (long candidateIndex = 1; candidateIndex <= count; candidateIndex++)
        {
            sum += PerfFixtureDefinition.DocumentIdFor(OverlaidStudentOrdinal(candidateIndex));
        }

        return sum;
    }

    public static async Task ApplyAndVerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfFixtureDefinition definition,
        long chunkSize = PerfFixtureLoader.DefaultChunkSize
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        bool postgresql = provider == PerfProvider.Postgresql;

        string updateSql = postgresql
            ? PgsqlPerfFilteredOverlaySql.UpdateSql
            : MssqlPerfFilteredOverlaySql.UpdateSql;
        foreach (
            (long from, long to) in PerfFixtureLoader.Chunks(OverlaidStudentCount(definition), chunkSize)
        )
        {
            await PerfSeederDatabase.ExecuteRangeAsync(connection, updateSql, from, to, []);
        }

        IReadOnlyList<string> statisticsSqls = postgresql
            ? PgsqlPerfFilteredOverlaySql.StatisticsRefreshSqls
            : MssqlPerfFilteredOverlaySql.StatisticsRefreshSqls;
        foreach (string statisticsSql in statisticsSqls)
        {
            await PerfSeederDatabase.ExecuteNonQueryAsync(connection, statisticsSql);
        }

        await VerifyAsync(connection, provider, definition);
    }

    public static async Task VerifyAsync(
        DbConnection connection,
        PerfProvider provider,
        PerfFixtureDefinition definition
    ) =>
        await PerfSeederDatabase.VerifyAsync(
            connection,
            provider == PerfProvider.Postgresql
                ? PgsqlPerfFilteredOverlaySql.VerificationQueries(definition)
                : MssqlPerfFilteredOverlaySql.VerificationQueries(definition)
        );
}
