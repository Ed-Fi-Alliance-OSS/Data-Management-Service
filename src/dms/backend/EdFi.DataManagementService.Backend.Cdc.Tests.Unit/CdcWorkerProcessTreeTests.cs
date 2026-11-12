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
[Platform("Linux")]
public sealed class Given_CdcWorkerProcessTree
{
    [Test]
    public async Task It_completes_cleanup_when_a_killed_descendant_remains_an_unreaped_zombie()
    {
        using var holder = Process.Start(
            new ProcessStartInfo("/bin/bash")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                ArgumentList =
                {
                    "-c",
                    """
                    sleep 300 &
                    child=$!
                    trap 'kill "$child" 2>/dev/null; wait "$child" 2>/dev/null' EXIT
                    printf '%s\n' "$child"
                    kill -STOP $$
                    read -r
                    """,
                },
            }
        )!;
        using var root = Process.Start(
            new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false }
        )!;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task termination = Task.CompletedTask;
        try
        {
            int childId = int.Parse(
                (await holder.StandardOutput.ReadLineAsync(cleanup.Token))!,
                CultureInfo.InvariantCulture
            );
            // A stopped parent cannot reap its child. Observe the kernel state before killing it.
            while (
                !(await File.ReadAllTextAsync($"/proc/{holder.Id}/status", cleanup.Token)).Contains(
                    "State:\tT"
                )
            )
            {
                await Task.Delay(10, cleanup.Token);
            }
            using var child = Process.GetProcessById(childId);
            termination = CdcWorkerProcessTree.TerminateAsync(
                root,
                cleanup.Token,
                (_, descendants, _) => descendants.Add(Process.GetProcessById(childId)),
                process =>
                {
                    process.Kill();
                    child.Kill();
                }
            );

            await termination;

            root.HasExited.Should().BeTrue();
            (await File.ReadAllTextAsync($"/proc/{childId}/status", cleanup.Token))
                .Should()
                .Contain("State:\tZ");
            cleanup.IsCancellationRequested.Should().BeFalse();
        }
        finally
        {
            await cleanup.CancelAsync();
            root.Kill();
            await root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            // Release the holder only after the assertion, then let its EXIT trap reap the child.
            holder.StandardInput.Close();
            using var resume = Process.Start(
                new ProcessStartInfo("/bin/kill")
                {
                    UseShellExecute = false,
                    ArgumentList = { "-CONT", holder.Id.ToString(CultureInfo.InvariantCulture) },
                }
            )!;
            await resume.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await termination;
            }
            catch (OperationCanceledException)
            { /* Observe the cleanup deadline if the regression fails. */
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_waits_for_root_exit_or_cleanup_cancellation_after_inventory_failure(
        bool cancelCleanup
    )
    {
        using var process = Process.Start(
            new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false }
        )!;
        using var cleanup = new CancellationTokenSource();
        var inventoryFailure = new IOException("inventory unavailable");
        bool terminationAttempted = false;
        Task termination = CdcWorkerProcessTree.TerminateAsync(
            process,
            cleanup.Token,
            (_, _, _) => throw inventoryFailure,
            _ => terminationAttempted = true
        );
        try
        {
            // Hold the actual exit until explicitly released, independently of OS kill timing.
            terminationAttempted.Should().BeTrue();
            termination.IsCompleted.Should().BeFalse("inventory failure must not bypass the root exit wait");
            if (cancelCleanup)
            {
                await cleanup.CancelAsync();
            }
            else
            {
                process.Kill();
            }

            Func<Task> act = async () => await termination.WaitAsync(TimeSpan.FromSeconds(5));
            var thrown = await act.Should().ThrowAsync<IOException>();
            thrown.Which.Should().BeSameAs(inventoryFailure);
            process.HasExited.Should().Be(!cancelCleanup);
        }
        finally
        {
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await cleanup.CancelAsync();
            try
            {
                await termination.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (IOException)
            { /* Expected inventory failure, already asserted. */
            }
        }
    }
}
