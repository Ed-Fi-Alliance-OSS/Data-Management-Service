// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Orchestrates the execution of DMS startup tasks in order.
/// Tasks are sorted by their Order property and executed sequentially.
/// Any task failure aborts the startup process.
/// </summary>
public class DmsStartupOrchestrator(
    IEnumerable<IDmsStartupTask> tasks,
    ILogger<DmsStartupOrchestrator> logger
)
{
    private readonly IEnumerable<IDmsStartupTask> _tasks = tasks;
    private readonly ILogger<DmsStartupOrchestrator> _logger = logger;

    /// <summary>
    /// Runs all registered startup tasks in order.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if any task fails.</exception>
    public async Task RunAllAsync(CancellationToken cancellationToken)
    {
        var orderedTasks = _tasks.OrderBy(t => t.Order).ToList();

        _logger.LogInformation(
            "DMS startup orchestrator beginning execution of {TaskCount} tasks",
            orderedTasks.Count
        );

        foreach (var task in orderedTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "Executing startup task: {TaskName} (Order: {Order})",
                task.Name,
                task.Order
            );

            try
            {
                await task.ExecuteAsync(cancellationToken);

                _logger.LogInformation("Completed startup task: {TaskName}", task.Name);
            }
            catch (OperationCanceledException)
            {
                // Rethrow without logging - cancellation is expected behavior
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Startup task {TaskName} failed. DMS cannot start.", task.Name);
                throw new InvalidOperationException($"Startup task '{task.Name}' failed: {ex.Message}", ex);
            }
        }

        _logger.LogInformation("DMS startup orchestrator completed all tasks successfully");
    }
}
