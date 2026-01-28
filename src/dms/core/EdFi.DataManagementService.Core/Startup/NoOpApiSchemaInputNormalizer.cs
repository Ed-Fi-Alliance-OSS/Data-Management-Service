// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Startup;

/// <summary>
/// No-op implementation of IApiSchemaInputNormalizer.
/// Returns the input unchanged. This is a placeholder for future implementation
/// of deterministic schema normalization as defined in DMS-923.
/// </summary>
internal class NoOpApiSchemaInputNormalizer(ILogger<NoOpApiSchemaInputNormalizer> logger)
    : IApiSchemaInputNormalizer
{
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public ApiSchemaDocumentNodes Normalize(ApiSchemaDocumentNodes nodes)
    {
        _logger.LogDebug(
            "NoOpApiSchemaInputNormalizer invoked - returning nodes unchanged. "
                + "Deterministic normalization will be implemented in DMS-923."
        );
        return nodes;
    }
}
