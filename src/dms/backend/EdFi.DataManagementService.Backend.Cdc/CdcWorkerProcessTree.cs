// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Globalization;

namespace EdFi.DataManagementService.Backend.Cdc;

internal static class CdcWorkerProcessTree
{
    internal static Task TerminateAsync(Process process, CancellationToken cancellationToken) =>
        TerminateAsync(
            process,
            cancellationToken,
            CaptureDescendants,
            root => root.Kill(entireProcessTree: true)
        );

    internal static async Task TerminateAsync(
        Process process,
        CancellationToken cancellationToken,
        Action<int, List<Process>, CancellationToken> captureDescendants,
        Action<Process> terminate
    )
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Process> descendants = [];
        Task rootExit = process.WaitForExitAsync(observation.Token);
        List<Task> exits = [rootExit];
        try
        {
            try
            {
                if (OperatingSystem.IsLinux() && !process.HasExited)
                {
                    captureDescendants(process.Id, descendants, cancellationToken);
                }
                // Register exit observation before terminating the tree. Waiting for the
                // root alone does not establish that its Linux descendants have exited.
                foreach (var descendant in descendants)
                {
                    exits.Add(descendant.WaitForExitAsync(observation.Token));
                }
            }
            finally
            {
                // Still attempt termination if inventory/observation fails. Non-Linux
                // hosts retain the existing .NET tree-kill and root-exit behavior.
                try
                {
                    terminate(process);
                }
                catch (InvalidOperationException)
                { /* Root already exited. */
                }
            }
            await Task.WhenAll(exits);
        }
        catch
        {
            // Inventory or termination failure must not cancel the root observer early.
            // Its wait still uses the caller's bounded cleanup deadline.
            try
            {
                await rootExit;
            }
            catch (Exception)
            { /* Preserve the original cleanup failure if root exit cannot be confirmed. */
            }
            throw;
        }
        finally
        {
            await observation.CancelAsync();
            try
            {
                await Task.WhenAll(exits);
            }
            catch (Exception)
            { /* Preserve the cleanup result; all exit observers are now complete. */
            }
            foreach (var descendant in descendants)
            {
                descendant.Dispose();
            }
        }
    }

    private static void CaptureDescendants(
        int rootId,
        List<Process> descendants,
        CancellationToken cancellationToken
    )
    {
        HashSet<int> seen = [rootId];
        Queue<int> pending = new([rootId]);
        while (pending.TryDequeue(out int parentId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] threads;
            try
            {
                threads = Directory.GetDirectories($"/proc/{parentId}/task");
            }
            catch (DirectoryNotFoundException)
            {
                continue; // Process exited during inventory.
            }
            foreach (string thread in threads)
            {
                string children;
                try
                {
                    children = File.ReadAllText(Path.Combine(thread, "children"));
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }
                foreach (string child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int id = int.Parse(child, CultureInfo.InvariantCulture);
                    if (!seen.Add(id))
                    {
                        continue;
                    }
                    try
                    {
                        descendants.Add(Process.GetProcessById(id));
                        pending.Enqueue(id);
                    }
                    catch (ArgumentException)
                    { /* Child already exited. */
                    }
                }
            }
        }
    }
}
