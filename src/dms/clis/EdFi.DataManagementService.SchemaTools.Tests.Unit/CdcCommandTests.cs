// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_Cdc_command_contract
{
    private ICdcCommandRunner _runner = null!;
    private StringWriter _output = null!;
    private StringWriter _error = null!;
    private const string Sentinel = "secret-password-database-sentinel";

    [SetUp]
    public void Setup()
    {
        _runner = A.Fake<ICdcCommandRunner>();
        _output = new();
        _error = new();
        A.CallTo(() => _runner.RunAsync(A<CdcCommandInvocation>._, A<TextWriter>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcCommandInvocation invocation, TextWriter _, CancellationToken _) =>
                    Task.FromResult(
                        new CdcCommandResult(
                            CdcCommandHost.Name(invocation.Operation),
                            true,
                            0,
                            [],
                            new { receipt = "safe" }
                        )
                    )
            );
    }

    [TearDown]
    public void Cleanup()
    {
        _output.Dispose();
        _error.Dispose();
    }

    [TestCase(CdcCommandOperation.StartWorker)]
    [TestCase(CdcCommandOperation.Enable)]
    [TestCase(CdcCommandOperation.Validate)]
    [TestCase(CdcCommandOperation.Status)]
    [TestCase(CdcCommandOperation.Watch)]
    [TestCase(CdcCommandOperation.Start)]
    [TestCase(CdcCommandOperation.Restart)]
    [TestCase(CdcCommandOperation.Resume)]
    [TestCase(CdcCommandOperation.Stop)]
    [TestCase(CdcCommandOperation.IncreaseRecordSize)]
    [TestCase(CdcCommandOperation.Retire)]
    public async Task It_dispatches_the_explicit_operation_and_emits_one_result(CdcCommandOperation operation)
    {
        var args = Arguments(operation);
        int code = await CdcCommandHost.InvokeAsync(args, _runner, _output, _error);
        code.Should().Be(0);
        using var result = JsonDocument.Parse(_output.ToString());
        result.RootElement.GetProperty("succeeded").GetBoolean().Should().BeTrue();
        _output.ToString().Should().NotContain(Sentinel);
        A.CallTo(() =>
                _runner.RunAsync(
                    A<CdcCommandInvocation>.That.Matches(i =>
                        i.Operation == operation && i.SettingsPath == Sentinel && i.StatePath == "/state"
                    ),
                    _error,
                    A<CancellationToken>._
                )
            )
            .MustHaveHappenedOnceExactly();
    }

    [TestCase("adopt")]
    [TestCase("replace-source")]
    [TestCase("rotate-source")]
    [TestCase("import")]
    public async Task It_exposes_no_provenance_or_source_replacement_override(string operation)
    {
        await Reject(["cdc", operation, "--settings", Sentinel, "--state-path", "/state", "--json"]);
    }

    [TestCase("--adopt")]
    [TestCase("--source-fingerprint")]
    [TestCase("--binding")]
    [TestCase("--replace-source")]
    [TestCase("--force")]
    public async Task It_rejects_unsupported_override_arguments_without_echoing_values(string option)
    {
        await Reject([.. Arguments(CdcCommandOperation.Validate), option, Sentinel]);
    }

    [TestCase("--generation", "not-a-number")]
    [TestCase("--generation", "0")]
    [TestCase("--generation", "-1")]
    public async Task It_requires_an_explicit_positive_retirement_generation(string option, string value)
    {
        await Reject([
            "cdc",
            "retire",
            "--settings",
            Sentinel,
            "--state-path",
            "/state",
            "--destructive-cleanup",
            option,
            value,
        ]);
    }

    [Test]
    public async Task It_requires_destructive_intent_before_entering_retirement() =>
        await Reject([
            "cdc",
            "retire",
            "--settings",
            Sentinel,
            "--state-path",
            "/state",
            "--generation",
            "7",
        ]);

    [Test]
    public async Task It_requires_renewed_consumer_confirmation_before_entering_rollout() =>
        await Reject([
            "cdc",
            "increase-record-size",
            "--settings",
            Sentinel,
            "--state-path",
            "/state",
            "--acknowledgement",
            Sentinel,
        ]);

    [TestCase("0")]
    [TestCase("10001")]
    [TestCase(Sentinel)]
    public async Task It_bounds_watch_before_dispatch(string passes) =>
        await Reject([.. Arguments(CdcCommandOperation.Watch), "--maximum-passes", passes]);

    [Test]
    public async Task It_returns_sanitized_json_for_missing_required_options() =>
        await Reject(["cdc", "validate", "--settings", Sentinel]);

    [Test]
    public async Task It_preserves_actionable_controller_diagnostics_and_exit_code()
    {
        A.CallTo(() => _runner.RunAsync(A<CdcCommandInvocation>._, A<TextWriter>._, A<CancellationToken>._))
            .Returns(
                CdcCommandHost.Failure(
                    "validate",
                    1,
                    CdcDeploymentComponent.ProviderSetup,
                    CdcDeploymentFailure.AuthenticationFailed
                )
            );
        (await CdcCommandHost.InvokeAsync(Arguments(CdcCommandOperation.Validate), _runner, _output, _error))
            .Should()
            .Be(1);
        using var result = JsonDocument.Parse(_output.ToString());
        result
            .RootElement.GetProperty("diagnostics")[0]
            .GetProperty("failure")
            .GetString()
            .Should()
            .Be("AuthenticationFailed");
        _error.ToString().Should().Contain("ProviderSetup");
    }

    [Test]
    public async Task It_discards_unhandled_exception_messages_and_inner_secrets()
    {
        A.CallTo(() => _runner.RunAsync(A<CdcCommandInvocation>._, A<TextWriter>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException(Sentinel, new Exception(Sentinel)));
        (await CdcCommandHost.InvokeAsync(Arguments(CdcCommandOperation.Stop), _runner, _output, _error))
            .Should()
            .Be(1);
        using var result = JsonDocument.Parse(_output.ToString());
        (_output.ToString() + _error).Should().NotContain(Sentinel);
    }

    [Test]
    public async Task It_passes_cancellation_and_emits_exactly_one_cancelled_result()
    {
        using var source = new CancellationTokenSource();
        A.CallTo(() => _runner.RunAsync(A<CdcCommandInvocation>._, A<TextWriter>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcCommandInvocation _, TextWriter _, CancellationToken token) =>
                {
                    await source.CancelAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new CdcCommandResult("watch", true, 0, []);
                }
            );
        (
            await CdcCommandHost.InvokeAsync(
                Arguments(CdcCommandOperation.Watch),
                _runner,
                _output,
                _error,
                source.Token
            )
        )
            .Should()
            .Be(130);
        using var result = JsonDocument.Parse(_output.ToString());
        result.RootElement.GetProperty("exitCode").GetInt32().Should().Be(130);
        (_output.ToString() + _error).Should().NotContain(Sentinel);
    }

    [Test]
    public async Task It_keeps_watch_progress_off_stdout()
    {
        A.CallTo(() => _runner.RunAsync(A<CdcCommandInvocation>._, A<TextWriter>._, A<CancellationToken>._))
            .ReturnsLazily(
                async (CdcCommandInvocation _, TextWriter progress, CancellationToken _) =>
                {
                    await progress.WriteLineAsync("{\"pass\":1}");
                    await progress.WriteLineAsync("{\"pass\":2}");
                    return new CdcCommandResult("watch", true, 0, []);
                }
            );
        (await CdcCommandHost.InvokeAsync(Arguments(CdcCommandOperation.Watch), _runner, _output, _error))
            .Should()
            .Be(0);
        using var result = JsonDocument.Parse(_output.ToString());
        _error.ToString().Should().Contain("pass");
        _output.ToString().Should().NotContain("pass");
    }

    private async Task Reject(string[] args)
    {
        (await CdcCommandHost.InvokeAsync(args, _runner, _output, _error)).Should().Be(2);
        using var result = JsonDocument.Parse(_output.ToString());
        result.RootElement.GetProperty("succeeded").GetBoolean().Should().BeFalse();
        (_output.ToString() + _error).Should().NotContain(Sentinel);
        A.CallTo(() => _runner.RunAsync(A<CdcCommandInvocation>._, A<TextWriter>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    private static string[] Arguments(CdcCommandOperation operation) =>
        [
            "cdc",
            CdcCommandHost.Name(operation),
            "--settings",
            Sentinel,
            "--state-path",
            "/state",
            "--json",
            .. operation switch
            {
                CdcCommandOperation.Retire => new[] { "--generation", "7", "--destructive-cleanup" },
                CdcCommandOperation.IncreaseRecordSize =>
                [
                    "--acknowledgement",
                    Sentinel,
                    "--confirm-consumer-capacity",
                ],
                _ => [],
            },
        ];
}
