// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

public partial class Given_Cdc_packaged_command
{
    [TestCase("stop", "cdc-disclosure-containment-result")]
    [TestCase("retire", "cdc-retire")]
    public async Task It_Cdc_runbook_emits_the_operation_scoped_example_in_one_stdout_value(
        string operation,
        string snippet
    )
    {
        var fixture =
            operation == "stop"
                ? Given_Cdc_runbook_result_output.StopResult
                : Given_Cdc_runbook_result_output.RetireResult;
        string path = Path.Combine(_temporary, "result.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixture, CdcCommandHost.JsonOptions));
        var args = CdcRunbookSnippets.Arguments(
            CdcRunbookSnippets.Read(snippet),
            CdcRunbookSnippets.CommandInputs(Sentinel, "/state")
        );
        var result = await RunAsync(_driver, ["example:" + path, .. args]);
        AssertResult(result, 0);
        result.Item3.Should().BeEmpty();
        CdcRunbookExamples.AssertExcerpt(
            "cdc-output-" + operation,
            result.Item2,
            operation == "stop"
                ? Given_Cdc_runbook_result_output.StopPaths
                : Given_Cdc_runbook_result_output.RetirePaths
        );
    }

    [TestCase("failure", 1, "cdc-output-failure")]
    [TestCase("invalid", 2, "cdc-output-invalid")]
    [TestCase("cancel", 130, "cdc-output-cancelled")]
    public async Task It_Cdc_runbook_matches_packaged_failure_diagnostics_and_exit_codes(
        string mode,
        int exitCode,
        string example
    )
    {
        var args = CdcRunbookSnippets.Arguments(
            CdcRunbookSnippets.Read("cdc-watch"),
            CdcRunbookSnippets.CommandInputs(Sentinel, "/state")
        );
        var result =
            mode == "invalid"
                ? await RunAsync(
                    Path.Combine(_directory, "api-schema-tools.dll"),
                    [.. args, "--invalid-option", Sentinel]
                )
                : await RunAsync(_driver, [mode, .. args]);
        AssertResult(result, exitCode);
        using var json = JsonDocument.Parse(result.Item2);
        foreach (var diagnostic in json.RootElement.GetProperty("diagnostics").EnumerateArray())
        {
            result
                .Item3.Should()
                .Contain(
                    $"{diagnostic.GetProperty("component").GetString()}: {diagnostic.GetProperty("message").GetString()}"
                );
        }
        CdcRunbookExamples.AssertExcerpt(
            example,
            result.Item2,
            "operation",
            "succeeded",
            "exitCode",
            "diagnostics/0/component",
            "diagnostics/0/failure",
            "data"
        );
    }

    [Test]
    public async Task It_Cdc_runbook_keeps_watch_pass_json_on_stderr_and_one_final_result_on_stdout()
    {
        // Controlled controller boundary: stream routing only, not a live readiness observation.
        var fixture = new CdcCommandResult(
            "watch",
            false,
            1,
            [],
            new CdcControllerStatusResult(
                new(1, DateTimeOffset.UnixEpoch, CdcReadiness.Unknown, CdcBlockingCategory.None, []),
                []
            )
        );
        string path = Path.Combine(_temporary, "watch-result.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixture, CdcCommandHost.JsonOptions));
        var args = CdcRunbookSnippets.Arguments(
            CdcRunbookSnippets.Read("cdc-watch"),
            CdcRunbookSnippets.CommandInputs(Sentinel, "/state")
        );
        var result = await RunAsync(_driver, ["example:" + path, .. args]);
        AssertResult(result, 1);
        var passes = result.Item3.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        passes.Should().HaveCount(3);
        foreach (string pass in passes)
        {
            using var json = JsonDocument.Parse(pass);
            json.RootElement.GetProperty("aggregate")
                .GetProperty("readiness")
                .GetString()
                .Should()
                .Be("Unknown");
        }
        CdcRunbookExamples.AssertExcerpt(
            "cdc-output-watch",
            result.Item2,
            "operation",
            "succeeded",
            "exitCode",
            "data/aggregate/readiness"
        );
    }
}
