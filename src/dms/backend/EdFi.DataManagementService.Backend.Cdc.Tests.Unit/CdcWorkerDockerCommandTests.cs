// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[NonParallelizable]
[Platform("Linux")]
public sealed class Given_CdcWorkerDockerCommand
{
    private CdcWorkerDockerCommand _command = null!;

    [SetUp]
    public void Setup() => _command = new();

    [TestCase("stdout")]
    [TestCase("stderr")]
    public async Task It_terminates_continuous_overflow_before_caller_cancellation(string stream)
    {
        await WithDockerAsync(
            stream,
            async (run, guard, directory) =>
            {
                Func<Task> act = async () => await run;
                await act.Should()
                    .ThrowAsync<IOException>()
                    .WithMessage("Docker inspection output exceeded its bound.");
                guard
                    .IsCancellationRequested.Should()
                    .BeFalse("overflow must terminate the child before the outer guard");
                AssertTerminated(directory);
            }
        );
    }

    [Test]
    public async Task It_returns_small_successful_output()
    {
        await WithDockerAsync(
            "success",
            async (run, guard, directory) =>
            {
                (await run).Should().Be("worker output\n");
                guard.IsCancellationRequested.Should().BeFalse();
                AssertTerminated(directory);
            }
        );
    }

    [Test]
    public async Task It_sanitizes_nonzero_exit_output()
    {
        await WithDockerAsync(
            "failure",
            async (run, guard, directory) =>
            {
                Func<Task> act = async () => await run;
                await act.Should().ThrowAsync<IOException>().WithMessage("Docker worker inspection failed.");
                guard.IsCancellationRequested.Should().BeFalse();
                AssertTerminated(directory);
            }
        );
    }

    [Test]
    public async Task It_preserves_caller_cancellation_and_terminates_the_child()
    {
        await WithDockerAsync(
            "cancel",
            async (run, guard, directory) =>
            {
                while (!File.Exists(Path.Combine(directory, "child.pid")))
                {
                    await Task.Delay(10, guard.Token);
                }
                await guard.CancelAsync();
                Func<Task> act = async () => await run;
                await act.Should().ThrowAsync<OperationCanceledException>();
                AssertTerminated(directory);
            }
        );
    }

    private async Task WithDockerAsync(
        string mode,
        Func<Task<string>, CancellationTokenSource, string, Task> verify
    )
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cdc-docker-{Guid.NewGuid():N}");
        string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        Task<string> run = Task.FromResult("");
        try
        {
            Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "docker");
            await File.WriteAllTextAsync(executable, FakeDocker);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    executable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);
            run = _command.RunAsync([mode, directory], guard.Token);
            await verify(run, guard, directory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            await guard.CancelAsync();
            if (Directory.Exists(directory))
            {
                foreach (string file in Directory.GetFiles(directory, "*.pid"))
                {
                    try
                    {
                        using var process = Process.GetProcessById(
                            int.Parse(await File.ReadAllTextAsync(file), CultureInfo.InvariantCulture)
                        );
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (ArgumentException)
                    { /* Already reaped. */
                    }
                    catch (InvalidOperationException)
                    { /* Already exited. */
                    }
                }
            }
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (IOException)
            { /* Expected command failure, already asserted. */
            }
            catch (OperationCanceledException)
            { /* Expected cancellation, already asserted. */
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertTerminated(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.pid");
        files.Should().NotBeEmpty();
        foreach (string file in files)
        {
            int pid = int.Parse(File.ReadAllText(file), CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                process
                    .HasExited.Should()
                    .BeTrue($"owned process {pid} must terminate before RunAsync returns");
            }
            catch (ArgumentException)
            { /* Already reaped. */
            }
        }
    }

    // The parent waits while its child writes indefinitely, filling the abandoned pipe on overflow.
    // Both PIDs are recorded before writing so process-tree cleanup is observable.
    private const string FakeDocker = """
        #!/bin/bash
        if [[ "$1" == writer ]]; then
            if [[ "$3" == cancel ]]; then
                exec /bin/sleep 300
            fi
            if [[ "$3" == stderr ]]; then
                exec 1>&2
            fi
            printf -v block '%4096s' 'private subprocess output'
            while :; do printf '%s' "$block"; done
        fi
        printf '%s' "$$" > "$2/parent.pid"
        case "$1" in
            success) printf 'worker output\n'; printf 'private stderr\n' >&2; exit 0 ;;
            failure) printf 'private stdout\n'; printf 'private stderr\n' >&2; exit 17 ;;
        esac
        /bin/bash "$0" writer "$2" "$1" &
        printf '%s' "$!" > "$2/child.pid"
        wait
        """;
}
