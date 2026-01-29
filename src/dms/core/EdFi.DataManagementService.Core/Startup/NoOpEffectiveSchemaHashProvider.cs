// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// No-op implementation of IEffectiveSchemaHashProvider.
/// Returns an empty string. This is a placeholder for future implementation
/// of deterministic schema hashing as defined in DMS-925.
/// </summary>
internal class NoOpEffectiveSchemaHashProvider(ILogger<NoOpEffectiveSchemaHashProvider> _logger)
    : IEffectiveSchemaHashProvider
{
    /// <inheritdoc />
    public string ComputeHash(ApiSchemaDocumentNodes nodes)
    {
        _logger.LogDebug(
            "NoOpEffectiveSchemaHashProvider invoked - returning empty hash. "
                + "Deterministic hash computation will be implemented in DMS-925."
        );
        return string.Empty;
    }
}
