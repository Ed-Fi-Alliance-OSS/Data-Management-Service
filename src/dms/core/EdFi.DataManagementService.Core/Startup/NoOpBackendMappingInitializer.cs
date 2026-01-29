// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// No-op implementation of IBackendMappingInitializer.
/// Does nothing. This is a placeholder for future implementation
/// of backend mapping initialization as defined in DMS-977.
/// </summary>
internal class NoOpBackendMappingInitializer(ILogger<NoOpBackendMappingInitializer> logger)
    : IBackendMappingInitializer
{
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        // TODO: DMS-977 - Replace this no-op with actual implementation:
        //   - Load .mpack files for the configured dialect/version
        //   - Perform runtime compilation of mapping sets if needed
        //   - Validate database fingerprints (EffectiveSchemaHash)
        //   - Cache ResourceKeyId maps

        _logger.LogDebug(
            "NoOpBackendMappingInitializer invoked - no action taken. "
                + "Backend mapping initialization will be implemented in DMS-977."
        );
        return Task.CompletedTask;
    }
}
