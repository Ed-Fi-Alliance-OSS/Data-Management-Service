// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// Interface for a simple Data Management Service pipeline step.
///
/// Pipeline steps are given a next() function to call when they are ready to
/// pass execution to the next pipeline step in the chain. If a pipeline step
/// does not call next(), it becomes the last step in the pipeline to receive execution.
///
/// You can think of everything done in a pipeline step before the next() call as being
/// pre-processing and everything after as post-processing.
///
/// Pipeline steps MUST NOT maintain object state, as object state would persist across
/// requests.
/// </summary>
internal interface IPipelineStep
{
    Task Execute(PipelineContext context, Func<Task> next);
}
