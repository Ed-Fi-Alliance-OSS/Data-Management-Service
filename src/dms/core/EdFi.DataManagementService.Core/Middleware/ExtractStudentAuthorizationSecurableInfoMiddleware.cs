// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Extracts StudentAuthorizationSecurableInfo from a valid document
/// </summary>
/// <param name="_logger"></param>
internal class ExtractStudentAuthorizationSecurableInfoMiddleware(ILogger _logger) : IPipelineStep
{
    /// <summary>
    ///  Builds StudentAuthorizationSecurableInfo from context.ParsedBody
    /// </summary>
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering ExtractStudentAuthorizationSecurableInfoMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        context.StudentAuthorizationSecurableInfo =
            context.ResourceSchema.ExtractStudentAuthorizationSecurableInfo(context.ParsedBody, _logger);
        await next();
    }
}
