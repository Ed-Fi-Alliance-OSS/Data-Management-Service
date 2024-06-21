// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Extensions;
using Microsoft.Extensions.Logging;
using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Core.Middleware
{
    /// <summary>
    /// For boolean and numeric properties that were submitted as strings, i.e. surrounded with double quotes,
    /// this middleware tries to coerce those back to their proper ValueKind. 
    /// </summary>
    /// <param name="_logger"></param>
    internal class CoerceStringTypeMiddleware(ILogger _logger) : IPipelineStep
    {
        public async Task Execute(PipelineContext context, Func<Task> next)
        {
            _logger.LogDebug("Entering CoerceStringTypeMiddleware - {TraceId}", context.FrontendRequest.TraceId);

            foreach (var path in context.ResourceSchema.BooleanJsonPaths.Select(path => path.Value))
            {
                var jsonNodes = context.ParsedBody.SelectNodesFromArrayPath(path, _logger);
                foreach (var jsonNode in jsonNodes)
                {
                    jsonNode.TryCoerceBooleanToBoolean();
                }
            }

            foreach (var path in context.ResourceSchema.NumericJsonPaths.Select(path => path.Value))
            {
                var jsonNodes = context.ParsedBody.SelectNodesFromArrayPath(path, _logger);
                foreach (var jsonNode in jsonNodes)
                {
                    jsonNode.TryCoerceStringToNumber();
                }
            }

            await next();
        }
    }
}
