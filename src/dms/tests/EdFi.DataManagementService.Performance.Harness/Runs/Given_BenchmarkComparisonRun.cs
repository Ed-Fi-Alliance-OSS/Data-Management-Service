// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Results;

namespace EdFi.DataManagementService.Performance.Harness.Runs;

/// <summary>
/// The comparison step: loads the comparison directory named by PERF_BENCHMARK_DIR with the
/// profile this harness build carries, compares it, and writes comparison.json, .csv and .md
/// into the same directory. Needs no database. The test fails only when the report cannot be
/// produced; the verdict (including invalid) and its exit code live in comparison.json.
/// </summary>
[TestFixture]
[Explicit("Report step: compares an existing benchmark directory and writes the comparison report")]
[Category("Performance")]
public class Given_BenchmarkComparisonRun
{
    [Test]
    public async Task It_compares_the_benchmark_directory_and_writes_the_report()
    {
        string? directory = Environment.GetEnvironmentVariable(PerfEnvironmentVariables.BenchmarkDirectory);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory.Trim()))
        {
            Assert.Fail(
                $"{PerfEnvironmentVariables.BenchmarkDirectory} must name an existing comparison directory."
            );
        }

        directory = directory!.Trim();
        PerfBenchmarkComparisonInput input = PerfBenchmarkComparisonLoader.Load(
            directory,
            PerfBenchmarkProfileLoader.LoadFile(PerfBenchmarkProfileLoader.DefaultPath)
        );
        PerfBenchmarkComparisonResult result = PerfBenchmarkComparison.Compare(input);
        PerfBenchmarkComparisonWriter.Write(directory, input.Plan, result);

        await TestContext.Out.WriteLineAsync(
            $"Benchmark comparison ({result.Result}, exit code {result.ExitCode}) written to {directory}"
        );
    }
}
