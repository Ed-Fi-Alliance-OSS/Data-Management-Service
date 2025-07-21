// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;
using static EdFi.DataManagementService.Core.Backend.DocumentComparer;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class InjectVersionMetadataToEdFiDocumentMiddleware(ILogger _logger) : IPipelineStep
    {
        public async Task Execute(RequestInfo requestInfo, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering InjectPropertiesToEdFiDocumentMiddleware - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.ParsedBody["_etag"] = GenerateContentHash(requestInfo.ParsedBody);

            requestInfo.ParsedBody["_lastModifiedDate"] = DateTimeOffset.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ssZ"
            );

            await next();
        }
    }
}
