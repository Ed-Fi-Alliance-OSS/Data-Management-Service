// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcScenarioAccounting
{
    private string _directory = "";
    private string _path = "";
    private string _invocation = "";
    private RecordingScenarios _scenarios = null!;
    private CdcScenarioRunner _runner = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cdc-accounting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "scenarios.json");
        _invocation = Guid.NewGuid().ToString("D");
        _runner = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        _scenarios = new(_path, _invocation);
    }

    [TearDown]
    public void Teardown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task It_initializes_before_attachment_and_persists_each_ordered_phase_with_terminal_loss_last()
    {
        await RunAsync();
        _scenarios
            .Events.Should()
            .Equal(
                "Attachment",
                "Student",
                "Descriptor",
                "Overlap",
                "DeleteBeforeProjection",
                "Rebuild",
                "Restart",
                "Unavailable",
                "Loss",
                "Disposal"
            );
        var report = Read();
        report["Version"]!.GetValue<int>().Should().Be(1);
        report["InvocationId"]!.GetValue<string>().Should().Be(_invocation);
        report["Identity"]!["Provider"]!.GetValue<string>().Should().Be("Mssql");
        report["Identity"]!["BindingId"]!.GetValue<string>().Should().Be(new string('a', 64));
        report["Identity"]!["Generation"]!.GetValue<long>().Should().Be(3);
        Outcome(report, "Attachment").Should().Be("Passed");
        Outcome(report, "Disposal").Should().Be("Passed");
        Outcomes(report).Should().Equal(Enumerable.Repeat("Passed", 8));
        Directory.GetFiles(_directory).Should().Equal(_path);
    }

    [Test]
    public async Task It_accounts_for_attachment_failure_and_partial_resource_disposal_without_identity_or_scenario_success()
    {
        _scenarios.OnAttach = _ => throw new InvalidOperationException("private credentials");
        await ExpectFailure("CDC_API_Attachment_Error");
        var report = Read();
        _scenarios.Events.Should().Equal("Attachment", "Disposal");
        Outcome(report, "Attachment").Should().Be("Failed");
        Outcome(report, "Disposal").Should().Be("Passed");
        report["Identity"]!["BindingId"]!.GetValue<string>().Should().BeEmpty();
        report["Identity"]!["Generation"]!.GetValue<long>().Should().Be(0);
        Outcomes(report).Should().Equal(Enumerable.Repeat("NotRun", 8));
        (await File.ReadAllTextAsync(_path)).Should().NotContain("private credentials");
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(8)]
    public async Task It_aborts_at_the_failing_phase_and_keeps_subsequent_phases_not_run(int failingPhase)
    {
        _scenarios.OnPhase = (phase, _) =>
            phase == failingPhase
                ? Task.FromException(new InvalidOperationException("private document body"))
                : Task.CompletedTask;
        await ExpectFailure($"CDC_API_CDC-E2E-{failingPhase:00}_Error");
        Outcomes(Read())
            .Should()
            .Equal(
                Enumerable
                    .Repeat("Passed", failingPhase - 1)
                    .Concat(["Failed"])
                    .Concat(Enumerable.Repeat("NotRun", 8 - failingPhase))
            );
        _scenarios.Events.Should().HaveCount(failingPhase + 2);
        (await File.ReadAllTextAsync(_path)).Should().NotContain("private document body");
    }

    [Test]
    public async Task It_fails_unimplemented_work_through_normal_phase_accounting()
    {
        _scenarios.OnPhase = (_, _) => throw new NotImplementedException();
        await ExpectFailure("CDC_API_CDC-E2E-01_Unimplemented");
        Read()["Scenarios"]![0]!["Failure"]!.GetValue<string>().Should().Be("Unimplemented");
        Outcomes(Read()).Should().Equal(new[] { "Failed" }.Concat(Enumerable.Repeat("NotRun", 7)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_uses_independent_finalization_after_phase_or_attachment_cancellation(
        bool duringAttachment
    )
    {
        using var cancellation = new CancellationTokenSource();
        async Task Cancel(CancellationToken token)
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
        }
        if (duringAttachment)
        {
            _scenarios.OnAttach = Cancel;
        }
        else
        {
            _scenarios.OnPhase = (_, token) => Cancel(token);
        }
        await ExpectFailure(
            duringAttachment ? "CDC_API_Attachment_Cancelled" : "CDC_API_CDC-E2E-01_Cancelled",
            cancellation.Token
        );
        Outcome(Read(), "Disposal").Should().Be("Passed");
        _scenarios.Events[^1].Should().Be("Disposal");
        Read()["InvocationId"]!.GetValue<string>().Should().Be(_invocation);
    }

    [Test]
    public async Task It_initializes_a_report_even_when_already_cancelled_and_never_attaches()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await ExpectFailure("CDC_API_Attachment_Cancelled", cancellation.Token);
        _scenarios.Events.Should().Equal("Disposal");
        Outcomes(Read()).Should().Equal(Enumerable.Repeat("NotRun", 8));
        Outcome(Read(), "Disposal").Should().Be("Passed");
    }

    [Test]
    public async Task It_records_timeout_and_awaits_cancelled_work_before_disposal()
    {
        _runner = new(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1));
        bool unwound = false;
        _scenarios.OnPhase = async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                unwound = true;
            }
        };
        _scenarios.OnDispose = () =>
        {
            unwound.Should().BeTrue();
            return Task.CompletedTask;
        };
        await ExpectFailure("CDC_API_CDC-E2E-01_TimedOut");
        Outcome(Read(), "Disposal").Should().Be("Passed");
        Outcomes(Read()).Should().Equal(new[] { "Failed" }.Concat(Enumerable.Repeat("NotRun", 7)));
    }

    [Test]
    public async Task It_records_noncooperative_work_as_failed_disposal_with_a_bounded_finalization()
    {
        _runner = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scenarios.OnPhase = async (_, _) =>
        {
            await cancellation.CancelAsync();
            await release.Task;
        };
        try
        {
            await ExpectFailure("CDC_API_CDC-E2E-01_Cancelled", cancellation.Token);
            Outcome(Read(), "Disposal").Should().Be("Failed");
            Read()["Disposal"]!["Failure"]!.GetValue<string>().Should().Be("TimedOut");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_fails_qualification_when_disposal_fails_or_times_out_even_after_all_phases_pass(
        bool timeout
    )
    {
        _runner = new(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scenarios.OnDispose = () =>
            timeout ? release.Task : Task.FromException(new InvalidOperationException("private cleanup"));
        try
        {
            await ExpectFailure(timeout ? "CDC_API_DISPOSAL_TimedOut" : "CDC_API_DISPOSAL_Error");
            Outcomes(Read()).Should().Equal(Enumerable.Repeat("Passed", 8));
            Outcome(Read(), "Disposal").Should().Be("Failed");
            (await File.ReadAllTextAsync(_path)).Should().NotContain("private cleanup");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Test]
    public async Task It_preserves_the_original_phase_failure_when_disposal_also_fails()
    {
        _scenarios.OnPhase = (_, _) => throw new InvalidOperationException("original private error");
        _scenarios.OnDispose = () => throw new InvalidOperationException("cleanup private error");
        await ExpectFailure("CDC_API_CDC-E2E-01_Error");
        Outcome(Read(), "Disposal").Should().Be("Failed");
    }

    [TestCase("", "valid", "CDC_API_REPORT_ABSOLUTE_PATH_REQUIRED")]
    [TestCase("relative.json", "valid", "CDC_API_REPORT_ABSOLUTE_PATH_REQUIRED")]
    [TestCase("valid", "", "CDC_API_REPORT_INVOCATION_REQUIRED")]
    [TestCase("valid", "secret-invalid-id", "CDC_API_REPORT_INVOCATION_REQUIRED")]
    [TestCase("valid", "00000000-0000-0000-0000-000000000000", "CDC_API_REPORT_INVOCATION_REQUIRED")]
    public async Task It_requires_runner_inputs_before_attachment(string path, string invocation, string code)
    {
        Func<Task> action = () =>
            _runner.RunAsync(
                path == "valid" ? _path : path,
                invocation == "valid" ? _invocation : invocation,
                _scenarios,
                CancellationToken.None
            );
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(code);
        _scenarios.Events.Should().BeEmpty();
        File.Exists(_path).Should().BeFalse();
    }

    [Test]
    public async Task It_rejects_reused_report_paths_without_overwriting_or_attaching()
    {
        await File.WriteAllTextAsync(_path, "previous invocation");
        await ExpectFailure("CDC_API_REPORT_INITIALIZATION_FAILED");
        (await File.ReadAllTextAsync(_path)).Should().Be("previous invocation");
        _scenarios.Events.Should().BeEmpty();
        Directory.GetFiles(_directory).Should().Equal(_path);
    }

    [Test]
    public async Task It_rejects_unwritable_report_paths_before_attachment()
    {
        _path = Path.Combine(_directory, "missing-parent", "report.json");
        await ExpectFailure("CDC_API_REPORT_INITIALIZATION_FAILED");
        _scenarios.Events.Should().BeEmpty();
    }

    [Test]
    public async Task It_attempts_disposal_and_preserves_phase_failure_when_final_report_writes_fail()
    {
        _scenarios.OnPhase = (_, _) =>
        {
            File.Delete(_path);
            Directory.CreateDirectory(_path);
            throw new InvalidOperationException("original private error");
        };
        await ExpectFailure("CDC_API_CDC-E2E-01_Error");
        _scenarios.Events.Should().Equal("Attachment", "Student", "Disposal");
    }

    [Test]
    public async Task It_fails_if_final_persistence_fails_after_successful_scenarios_and_disposal()
    {
        _scenarios.OnDispose = () =>
        {
            File.Delete(_path);
            Directory.CreateDirectory(_path);
            return Task.CompletedTask;
        };
        await ExpectFailure("CDC_API_REPORT_PERSISTENCE_FAILED");
        _scenarios.Events.Should().HaveCount(10);
    }

    [Test]
    public async Task It_attempts_cleanup_when_a_cancellation_callback_throws()
    {
        _scenarios.OnPhase = (phase, token) =>
        {
            if (phase == 1)
            {
                token.Register(() => throw new InvalidOperationException("private callback"));
            }
            return Task.CompletedTask;
        };
        await ExpectFailure("CDC_API_DISPOSAL_Error");
        Outcome(Read(), "Disposal").Should().Be("Failed");
        _scenarios.Events[^1].Should().Be("Disposal");
    }

    private Task RunAsync(CancellationToken token = default) =>
        _runner.RunAsync(_path, _invocation, _scenarios, token);

    private JsonNode Read() => JsonNode.Parse(File.ReadAllText(_path))!;

    private static string Outcome(JsonNode report, string stage) =>
        report[stage]!["Outcome"]!.GetValue<string>();

    private static IEnumerable<string> Outcomes(JsonNode report) =>
        report["Scenarios"]!.AsArray().Select(s => s!["Outcome"]!.GetValue<string>());

    private async Task ExpectFailure(string code, CancellationToken token = default)
    {
        Func<Task> action = () => RunAsync(token);
        var failure = await action.Should().ThrowAsync<InvalidOperationException>().WithMessage(code);
        failure.Which.InnerException.Should().BeNull();
    }

    private sealed class RecordingScenarios(string path, string invocation) : ICdcApiScenarios
    {
        public List<string> Events { get; } = [];
        public Func<CancellationToken, Task> OnAttach { get; set; } = _ => Task.CompletedTask;
        public Func<int, CancellationToken, Task> OnPhase { get; set; } = (_, _) => Task.CompletedTask;
        public Func<Task> OnDispose { get; set; } = () => Task.CompletedTask;

        public async Task<CdcScenarioIdentity> AttachAsync(CancellationToken token)
        {
            Events.Add("Attachment");
            var report = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!;
            report["InvocationId"]!.GetValue<string>().Should().Be(invocation);
            Outcomes(report).Should().Equal(Enumerable.Repeat("NotRun", 8));
            Outcome(report, "Attachment").Should().Be("Running");
            Outcome(report, "Disposal").Should().Be("NotRun");
            await OnAttach(token);
            return new("Mssql", new string('a', 64), 3);
        }

        private Task Phase(int phase, string name, CancellationToken token)
        {
            Events.Add(name);
            var report = JsonNode.Parse(File.ReadAllText(path))!;
            report["InvocationId"]!.GetValue<string>().Should().Be(invocation);
            report["Scenarios"]!
                .AsArray()
                .Select(s => s!["Id"]!.GetValue<string>())
                .Should()
                .Equal(Enumerable.Range(1, 8).Select(i => $"CDC-E2E-{i:00}"));
            Outcomes(report)
                .Should()
                .Equal(
                    Enumerable
                        .Repeat("Passed", phase - 1)
                        .Concat(["Running"])
                        .Concat(Enumerable.Repeat("NotRun", 8 - phase))
                );
            return OnPhase(phase, token);
        }

        public Task StudentCrudAsync(CancellationToken token) => Phase(1, "Student", token);

        public Task DescriptorCrudAsync(CancellationToken token) => Phase(2, "Descriptor", token);

        public Task OverlapAsync(CancellationToken token) => Phase(3, "Overlap", token);

        public Task DeleteBeforeProjectionAsync(CancellationToken token) =>
            Phase(4, "DeleteBeforeProjection", token);

        public Task OnlineRebuildAsync(CancellationToken token) => Phase(5, "Rebuild", token);

        public Task ExecutorRestartAsync(CancellationToken token) => Phase(6, "Restart", token);

        public Task UnavailableEvidenceAsync(CancellationToken token) => Phase(7, "Unavailable", token);

        public Task TerminalLossAsync(CancellationToken token) => Phase(8, "Loss", token);

        public async ValueTask DisposeAsync()
        {
            Events.Add("Disposal");
            if (File.Exists(path))
            {
                Outcome(JsonNode.Parse(await File.ReadAllTextAsync(path))!, "Disposal")
                    .Should()
                    .Be("Running");
            }
            await OnDispose();
        }
    }
}
