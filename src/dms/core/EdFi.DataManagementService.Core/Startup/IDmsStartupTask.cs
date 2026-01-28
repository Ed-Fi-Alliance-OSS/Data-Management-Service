// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Defines a startup task that runs during DMS application initialization.
/// Tasks are executed in order before the application begins serving requests.
/// </summary>
public interface IDmsStartupTask
{
    /// <summary>
    /// The execution order of this task. Lower values run first.
    /// Recommended ranges:
    /// - 100-199: Schema loading and validation
    /// - 200-299: Schema processing (normalization, hashing)
    /// - 300-399: Backend mapping initialization
    /// </summary>
    int Order { get; }

    /// <summary>
    /// A descriptive name for the task, used in logging.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes the startup task.
    /// Implementations should throw exceptions on failure to abort startup.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
