// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that provides the precomputed API schema documents for pipeline processing.
/// The effective schema (merged core + extensions) is built once at startup and cached
/// for the lifetime of the application. This middleware simply attaches the cached
/// artifacts to the RequestInfo.
/// </summary>
internal class ProvideApiSchemaMiddleware(
    IEffectiveApiSchemaProvider effectiveApiSchemaProvider,
    ILogger logger
) : IPipelineStep
{
    private readonly IEffectiveApiSchemaProvider _effectiveApiSchemaProvider = effectiveApiSchemaProvider;
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Attaches the precomputed API schema documents to the requestInfo.
    /// This makes the unified schema available to all subsequent
    /// pipeline steps for request processing and validation.
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ProvideApiSchemaMiddleware- {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.ApiSchemaDocuments = _effectiveApiSchemaProvider.Documents;
        requestInfo.ApiSchemaReloadId = _effectiveApiSchemaProvider.SchemaId;

        await next();
    }
}
