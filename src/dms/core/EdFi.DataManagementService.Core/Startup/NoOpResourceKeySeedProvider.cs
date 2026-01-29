// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// No-op implementation of IResourceKeySeedProvider.
/// Returns empty lists. This is a placeholder for future implementation
/// of resource key seed derivation as defined in DMS-926.
/// </summary>
internal class NoOpResourceKeySeedProvider(ILogger<NoOpResourceKeySeedProvider> _logger)
    : IResourceKeySeedProvider
{
    /// <inheritdoc />
    public IReadOnlyList<ResourceKeySeed> GetSeeds(ApiSchemaDocumentNodes nodes)
    {
        _logger.LogDebug(
            "NoOpResourceKeySeedProvider.GetSeeds invoked - returning empty list. "
                + "Resource key seed derivation will be implemented in DMS-926."
        );
        return [];
    }

    /// <inheritdoc />
    public string ComputeSeedHash(IReadOnlyList<ResourceKeySeed> seeds)
    {
        _logger.LogDebug(
            "NoOpResourceKeySeedProvider.ComputeSeedHash invoked - returning empty hash. "
                + "Seed hash computation will be implemented in DMS-926."
        );
        return string.Empty;
    }
}
