// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// Startup task that initializes backend mappings after the effective schema is built.
/// This task runs in the backend initialization phase (Order 300+) to ensure the schema
/// is available before backend mapping initialization begins.
/// </summary>
internal class BackendMappingInitializationTask(
    IBackendMappingInitializer backendMappingInitializer,
    ILogger<BackendMappingInitializationTask> logger
) : IDmsStartupTask
{
    private readonly IBackendMappingInitializer _backendMappingInitializer = backendMappingInitializer;
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public string Name => "Backend Mapping Initialization";

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing backend mappings");

        await _backendMappingInitializer.InitializeAsync(cancellationToken);

        _logger.LogInformation("Backend mapping initialization complete");
    }
}
