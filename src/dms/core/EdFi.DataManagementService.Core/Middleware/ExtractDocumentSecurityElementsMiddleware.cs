// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Extracts document security information from a valid JSON document
/// </summary>
internal class ExtractDocumentSecurityElementsMiddleware(ILogger _logger) : IPipelineStep
{
    /// <summary>
    /// Builds a DocumentSecurityElements from a document body
    /// </summary>
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ExtractDocumentSecurityElementsMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        Trace.Assert(requestInfo.ParsedBody != null, "Body was null, pipeline config invalid", "");

        // Guardrail: security elements are extracted from the RAW submitted body
        // (ParsedBody), not the profile-shaped write surface. Relational profile shaping
        // stores its result on BackendProfileWriteContext and does not replace ParsedBody,
        // so this middleware still sees the schema-validated request body used by the
        // existing authorization-pathway setup. Required identity/security references
        // remain validated upstream by ValidateDocumentMiddleware. The relational backend
        // profile merge/persistence path uses BackendProfileWriteContext.Request.WritableRequestBody
        // for shaped writes, so profile-hidden submitted data is not written merely because
        // it was available to raw request validation/extraction.
        requestInfo.DocumentSecurityElements = requestInfo.ResourceSchema.ExtractSecurityElements(
            requestInfo.ParsedBody,
            _logger
        );

        await next();
    }
}
