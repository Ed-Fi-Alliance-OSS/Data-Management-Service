// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Pipeline;

namespace EdFi.DataManagementService.Api.Core.Handler;

/// <summary>
/// Handles an upsert request that has made it through the middleware pipeline steps.
/// </summary>
public class UpsertHandler(ILogger _logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        _logger.LogInformation("UpsertHandler");

        await Task.FromResult("Here to suppress 'missing await' complaints until this is not a stub");

        context.FrontendResponse = new(StatusCode: 204, Body: null);
    }
}
