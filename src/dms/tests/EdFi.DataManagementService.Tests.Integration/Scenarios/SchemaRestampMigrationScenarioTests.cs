// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

[TestFixture]
[Category("ApiIntegration")]
[Category("PostgresqlIntegration")]
[Category("MssqlIntegration")]
public class Given_a_cancelled_schema_restamp_tool_process
{
    private Process _process = null!;

    [SetUp]
    public async Task Setup()
    {
        // The child signals readiness, then blocks on its open stdin pipe. Cancellation
        // follows the signal rather than relying on the relative timing of timers.
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        startInfo.ArgumentList.Add(
            OperatingSystem.IsWindows() ? "echo ready & set /p wait=" : "printf 'ready\\n'; read wait"
        );
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start child.");
        try
        {
            using var cancellation = new CancellationTokenSource();
            Task execution = SchemaRestampMigrationScenario.WaitForToolExitAsync(
                _process,
                cancellation.Token
            );
            string ready =
                await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
                ?? string.Empty;
            ready.TrimEnd().Should().Be("ready");
            _process.HasExited.Should().BeFalse();
            await cancellation.CancelAsync();
            Func<Task> action = () => execution.WaitAsync(TimeSpan.FromSeconds(30));
            await action.Should().ThrowAsync<OperationCanceledException>();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    [Test]
    public void It_terminates_the_blocked_child_before_reporting_cancellation() =>
        _process.HasExited.Should().BeTrue();

    [TearDown]
    public void Cleanup()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10000);
        }
        _process.Dispose();
    }
}
