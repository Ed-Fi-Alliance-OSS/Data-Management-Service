// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Parses the SET STATISTICS IO, TIME message text SQL Server raises through InfoMessage.
/// Logical and physical reads are summed across the per-table lines, excluding the separately
/// labeled lob counters; CPU and elapsed time come from the last "SQL Server Execution Times"
/// block, because the first CPU/elapsed pair belongs to parse-and-compile time.
/// </summary>
public static partial class MssqlStatisticsParser
{
    public static PerfDatabaseMetrics Parse(string statisticsText)
    {
        MatchCollection tableLines = TableIoRegex().Matches(statisticsText);
        if (tableLines.Count == 0)
        {
            throw new PerfObservationException("STATISTICS IO output carries no per-table read counters.");
        }

        long logicalReads = 0;
        long physicalReads = 0;
        foreach (Match tableLine in tableLines)
        {
            logicalReads += long.Parse(tableLine.Groups[1].Value, CultureInfo.InvariantCulture);
            physicalReads += long.Parse(tableLine.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        MatchCollection executionTimes = ExecutionTimesRegex().Matches(statisticsText);
        if (executionTimes.Count == 0)
        {
            throw new PerfObservationException("STATISTICS TIME output carries no execution-times block.");
        }

        Match lastExecution = executionTimes[^1];
        double cpuMs = double.Parse(lastExecution.Groups[1].Value, CultureInfo.InvariantCulture);
        double elapsedMs = double.Parse(lastExecution.Groups[2].Value, CultureInfo.InvariantCulture);

        return new PerfDatabaseMetrics(
            BuffersHit: null,
            BuffersRead: null,
            DbExecutionMs: null,
            LogicalReads: logicalReads,
            PhysicalReads: physicalReads,
            DbCpuMs: cpuMs,
            DbElapsedMs: elapsedMs
        );
    }

    [GeneratedRegex(
        @"Table '[^']*'\.[^\r\n]*?(?<!lob )logical reads (\d+),[^\r\n]*?(?<!lob )physical reads (\d+)"
    )]
    private static partial Regex TableIoRegex();

    [GeneratedRegex(@"SQL Server Execution Times:\s*CPU time = (\d+) ms,\s*elapsed time = (\d+) ms")]
    private static partial Regex ExecutionTimesRegex();
}
