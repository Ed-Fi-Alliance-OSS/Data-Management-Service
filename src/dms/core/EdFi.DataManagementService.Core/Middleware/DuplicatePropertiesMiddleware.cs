// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that detects duplicate property names in JSON request bodies.
/// Scans original JSON tokens so duplicate detection does not depend on JsonNode materialization.
/// </summary>
/// <param name="logger">The logger.</param>
/// <param name="errorContentType">
/// The media type of the 400 response for a duplicate property. The identity JSON-body pipeline
/// passes application/problem+json, which its published OpenAPI document declares; every other
/// pipeline keeps the application/json default.
/// </param>
internal class DuplicatePropertiesMiddleware(ILogger logger, string errorContentType = "application/json")
    : IPipelineStep
{
    private const int MaxLoggedDuplicatePathLength = 1024;

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicatePropertiesMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        string duplicatePath = requestInfo.FrontendRequest.DuplicatePropertyPath ?? string.Empty;
        bool hasDuplicate = requestInfo.FrontendRequest.DuplicatePropertyPath is not null;

        if (!hasDuplicate && !string.IsNullOrEmpty(requestInfo.FrontendRequest.Body))
        {
            hasDuplicate = DuplicatePropertyPathScanner.TryFindFirstDuplicatePropertyPath(
                Encoding.UTF8.GetBytes(requestInfo.FrontendRequest.Body),
                out duplicatePath
            );
        }

        if (hasDuplicate)
        {
            var validationErrors = new Dictionary<string, string[]>
            {
                { duplicatePath, ["An item with the same key has already been added."] },
            };

            logger.LogDebug(
                "Duplicate key found at {DuplicatePath} - {TraceId}",
                LoggingSanitizer.SanitizeFreeTextForLogging(duplicatePath, MaxLoggedDuplicatePathLength),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = new FrontendResponse(
                StatusCode: 400,
                Body: ForDataValidation(
                    "Data validation failed. See 'validationErrors' for details.",
                    traceId: requestInfo.FrontendRequest.TraceId,
                    validationErrors,
                    []
                ),
                Headers: [],
                ContentType: errorContentType
            );
            return;
        }

        await next();
    }
}
