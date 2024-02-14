// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// Implements a simple Data Management Service pipeline provider, which
/// is modeled on the Middleware pattern.
///
/// Add pipeline steps in execution order with StartWith() and AndThen().
/// Run the pipleline steps with Run().
/// </summary>
public class PipelineProvider
{
    private readonly List<IPipelineStep> _steps = [];

    public PipelineProvider AndThen(IPipelineStep step)
    {
        _steps.Add(step);
        return this;
    }

    public PipelineProvider StartWith(IPipelineStep step)
    {
        return AndThen(step);
    }

    public async Task Run(PipelineContext context)
    {
        await RunInternal(0, context);
    }

    private async Task RunInternal(int stepIndex, PipelineContext context)
    {
        if (_steps.Count > stepIndex)
        {
            await _steps[stepIndex].Execute(context, () => RunInternal(stepIndex + 1, context));
        }
    }
}

