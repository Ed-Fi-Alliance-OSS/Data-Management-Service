// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware
{
    internal class InjectLastModifiedDateToEdFiDocumentMiddleware(ILogger _logger) : IPipelineStep
    {
        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            _logger.LogDebug(
                "Entering InjectPropertiesToEdFiDocumentMiddleware - {TraceId}",
                context.FrontendRequest.TraceId
            );

            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            string formattedUtcDateTime = utcNow.ToString("yyyy-MM-ddTHH:mm:sszzz");
            context.ParsedBody["_lastModifiedDate"] = formattedUtcDateTime;
            await next();
        }
    }
}
