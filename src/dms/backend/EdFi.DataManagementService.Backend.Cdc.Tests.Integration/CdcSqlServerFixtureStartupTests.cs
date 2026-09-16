// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcSqlServerFixtureStartup
{
    internal const string LsaFailureLog = """
        ** ERROR: [AppLoader] Failed to load LSA: 0xc0070102
        AppLoader: Exiting with status=0xc0070102
        This program has encountered a fatal error and cannot continue running.
        Reason: 0x00000006
        Message: Termination of \SystemRoot\system32\AppLoader.exe was due to fatal error 0xC0000001
        Last errno: 2
        """;

    private readonly List<string> _events = [];
    private CdcSqlServerContainerState _state = null!;
    private int _starts;
    private bool _keep;
    private bool _failReplacement;
    private string _blockedPhase = string.Empty;
    private string _failedPhase = string.Empty;
    private CdcSqlServerStartupBudgets _budgets = null!;

    [SetUp]
    public void Setup()
    {
        _events.Clear();
        _state = new("exited", 1, false)
        {
            Logs = CdcSqlServerStartupLogClassifier.Parse(new(0, LsaFailureLog, "private-log-detail")),
        };
        _starts = 0;
        _keep = false;
        _failReplacement = false;
        _blockedPhase = string.Empty;
        _failedPhase = string.Empty;
        _budgets = CdcSqlServerStartupBudgets.Default;
    }

    [Test]
    public async Task It_starts_once_without_inspection_when_initial_readiness_succeeds()
    {
        int starts = 0;
        await CdcSqlServerFixtureStartup.RunAsync(
            _ =>
            {
                starts++;
                return Task.CompletedTask;
            },
            _ => throw new AssertionException("Healthy startup must not inspect failure state."),
            (_, _, _, _) => throw new AssertionException("Healthy startup has no failure attachment."),
            _ => throw new AssertionException("Healthy startup must not recreate a container."),
            false,
            CancellationToken.None,
            _budgets
        );
        starts.Should().Be(1);
    }

    [TestCase("inspect")]
    [TestCase("retain")]
    [TestCase("remove")]
    public void It_prevents_replacement_after_cancellation_during_recovery(string phase)
    {
        using var cancellation = new CancellationTokenSource();
        int starts = 0;
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await CdcSqlServerFixtureStartup.RunAsync(
                _ =>
                {
                    starts++;
                    throw new InvalidOperationException("startup failed");
                },
                _ =>
                {
                    if (phase == "inspect")
                    {
                        cancellation.Cancel();
                    }
                    return Task.FromResult(_state);
                },
                (_, _, _, _) =>
                {
                    if (phase == "retain")
                    {
                        cancellation.Cancel();
                    }
                    return Task.CompletedTask;
                },
                _ =>
                {
                    if (phase == "remove")
                    {
                        cancellation.Cancel();
                    }
                    return Task.CompletedTask;
                },
                false,
                cancellation.Token,
                _budgets
            )
        );
        starts.Should().Be(1);
    }

    [Test]
    public async Task It_retains_failure_before_one_recreation_and_requires_replacement_readiness()
    {
        await RunAsync();
        _events.Should().Equal("start-1", "inspect", "retain-1-True", "remove", "start-2", "ready");
    }

    [Test]
    public void It_preserves_a_second_failure_without_a_third_start()
    {
        _failReplacement = true;
        Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        _events
            .Should()
            .Equal("start-1", "inspect", "retain-1-True", "remove", "start-2", "inspect", "retain-2-False");
    }

    [TestCase("running")]
    [TestCase("dead")]
    [TestCase("oom")]
    [TestCase("exit-code")]
    [TestCase("unavailable")]
    [TestCase("truncated")]
    [TestCase("tail-limit")]
    [TestCase("memory")]
    [TestCase("mapping")]
    [TestCase("sql-error")]
    [TestCase("signal")]
    [TestCase("reason")]
    [TestCase("errno")]
    [TestCase("lsa-status")]
    [TestCase("termination-status")]
    [TestCase("missing-marker")]
    [TestCase("contradictory-marker")]
    public void It_does_not_recreate_other_or_incomplete_failures(string scenario)
    {
        _state = scenario switch
        {
            "running" or "dead" => _state with { Status = scenario },
            "oom" => _state with { OomKilled = true },
            "exit-code" => _state with { ExitCode = 137 },
            _ => _state with
            {
                Logs = scenario switch
                {
                    "unavailable" => CdcSqlServerStartupLogEvidence.Empty("Unavailable"),
                    "truncated" => _state.Logs with { Truncated = true },
                    "memory" => _state.Logs with { MemoryMessage = true },
                    "mapping" => _state.Logs with { MappingMessage = true },
                    "sql-error" => _state.Logs with { SqlErrorNumbers = [701] },
                    "signal" => _state.Logs with { Signals = ["SIGKILL"] },
                    "reason" => _state.Logs with { FatalReasonCodes = [6, 2] },
                    "errno" => _state.Logs with { LastErrnos = [12] },
                    _ => CdcSqlServerStartupLogClassifier.Parse(
                        new(
                            0,
                            scenario switch
                            {
                                "tail-limit" => new string('\n', 400) + LsaFailureLog,
                                "lsa-status" => LsaFailureLog.Replace(
                                    "0xc0070102",
                                    "0xc0070005",
                                    StringComparison.Ordinal
                                ),
                                "termination-status" => LsaFailureLog.Replace(
                                    "0xC0000001",
                                    "0xC0000002",
                                    StringComparison.Ordinal
                                ),
                                "missing-marker" => LsaFailureLog.Replace(
                                    "AppLoader: Exiting with status=0xc0070102",
                                    "",
                                    StringComparison.Ordinal
                                ),
                                _ => LsaFailureLog + "\n** ERROR: [AppLoader] Failed to load LSA: 0xc0070005",
                            },
                            ""
                        )
                    ),
                },
            },
        };
        Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        _events.Should().Equal("start-1", "inspect", "retain-1-False");
    }

    [Test]
    public void It_preserves_keep_containers_for_debugging()
    {
        _keep = true;
        Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        _events.Should().NotContain("remove").And.NotContain("start-2");
    }

    [TestCase("inspect")]
    [TestCase("retain-1-True")]
    [TestCase("remove")]
    public void It_aborts_recreation_when_diagnostics_or_removal_fail(string phase)
    {
        _failedPhase = phase;
        Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        _events.Should().NotContain("start-2");
    }

    [TestCase("inspect")]
    [TestCase("retain-1-True")]
    [TestCase("remove")]
    [TestCase("start-2")]
    public void It_bounds_recovery_phases_by_one_overall_deadline(string phase)
    {
        _blockedPhase = phase;
        _budgets = _budgets with { Overall = TimeSpan.FromMilliseconds(100) };
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await RunAsync().WaitAsync(TimeSpan.FromSeconds(5))
        );
        _starts.Should().BeLessThanOrEqualTo(2);
        _events.Should().NotContain("ready");
    }

    [Test]
    public async Task It_can_inspect_an_attempt_timeout_within_the_remaining_overall_budget()
    {
        _blockedPhase = "start-1";
        _budgets = _budgets with { Attempt = TimeSpan.FromMilliseconds(100) };
        await RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
        _events.Should().Equal("start-1", "inspect", "retain-1-True", "remove", "start-2", "ready");
    }

    [Test]
    public void It_does_not_recreate_after_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await RunAsync(cancellation.Token));
        _events.Should().BeEmpty();
    }

    [Test]
    public void It_exposes_only_sanitized_failure_classifications()
    {
        _state.IsLsaInitializationTimeout.Should().BeTrue();
        JsonSerializer.Serialize(_state).Should().NotContain("private").And.NotContain("AppLoader");
    }

    private Task RunAsync() => RunAsync(CancellationToken.None);

    private Task RunAsync(CancellationToken token) =>
        CdcSqlServerFixtureStartup.RunAsync(
            async cancellation =>
            {
                _starts++;
                await PhaseAsync($"start-{_starts}", cancellation);
                if (_starts == 1 || _failReplacement)
                {
                    throw new InvalidOperationException("fixture startup failed");
                }
                _events.Add("ready");
            },
            async cancellation =>
            {
                await PhaseAsync("inspect", cancellation);
                return _state;
            },
            (attempt, _, recreate, cancellation) => PhaseAsync($"retain-{attempt}-{recreate}", cancellation),
            cancellation => PhaseAsync("remove", cancellation),
            _keep,
            token,
            _budgets
        );

    private async Task PhaseAsync(string phase, CancellationToken token)
    {
        _events.Add(phase);
        if (_failedPhase == phase)
        {
            throw new InvalidOperationException("private failure detail");
        }
        if (_blockedPhase == phase)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
