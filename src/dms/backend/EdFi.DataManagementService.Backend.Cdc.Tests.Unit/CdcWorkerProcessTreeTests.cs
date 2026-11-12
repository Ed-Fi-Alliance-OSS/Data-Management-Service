// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Platform("Linux")]
public sealed class Given_CdcWorkerProcessTree
{
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
