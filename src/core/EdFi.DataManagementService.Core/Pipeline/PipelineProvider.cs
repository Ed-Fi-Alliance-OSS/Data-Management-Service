// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// Implements a simple Data Management Service pipeline provider, which
/// is modeled on the Middleware pattern. The class does not maintain
/// state beyond the initialized task list.
///
/// Create pipeline steps in execution order with constructor.
/// Run the pipeline steps with Run().
/// </summary>
internal class PipelineProvider(List<IPipelineStep> _steps)
{
    /// <summary>
    /// Runs the step at the given index, if there is one. (If not, we are at the end.)
    /// Passes the context to the step, along with a "next" function that will
    /// run the next step in the list.
    /// </summary>
    private async Task RunInternal(int stepIndex, PipelineContext context)
    {
        if (_steps.Count > stepIndex)
        {
            await _steps[stepIndex].Execute(context, () => RunInternal(stepIndex + 1, context));
        }
    }

    /// <summary>
    /// Start the pipeline with the first step
    /// </summary>
    public async Task Run(PipelineContext context)
    {
        await RunInternal(0, context);
    }
}
