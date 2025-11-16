// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Placeholder batch handler. It will be expanded with full batch orchestration logic in subsequent steps.
/// </summary>
internal class BatchHandler(
    ILogger<BatchHandler> logger,
    VersionedLazy<PipelineProvider> upsertValidationPipeline,
    VersionedLazy<PipelineProvider> updateValidationPipeline,
    VersionedLazy<PipelineProvider> deleteValidationPipeline
) : IPipelineStep
{
    private readonly ILogger<BatchHandler> _logger = logger;
    private readonly VersionedLazy<PipelineProvider> _upsertValidationPipeline = upsertValidationPipeline;
    private readonly VersionedLazy<PipelineProvider> _updateValidationPipeline = updateValidationPipeline;
    private readonly VersionedLazy<PipelineProvider> _deleteValidationPipeline = deleteValidationPipeline;

    public Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogInformation(
            "BatchHandler invoked for TraceId {TraceId}. Detailed handling will be implemented in subsequent steps.",
            requestInfo.FrontendRequest.TraceId.Value
        );
        return Task.CompletedTask;
    }
}
