// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using EdFi.DataManagementService.Core.External.Diagnostics;

namespace EdFi.DataManagementService.Core.Pipeline;

/// <summary>
/// Implements a simple Data Management Service pipeline provider, which
/// is modeled on the Middleware pattern. The class does not maintain
/// state beyond the initialized task list.
///
/// Create pipeline steps in execution order with constructor.
/// Run the pipeline steps with Run().
///
/// When a DMS-1236 request timing context is ambient, each step's inclusive wall-clock
/// time is recorded under "{pipelineName}.{NN}.{StepTypeName}" (self time is derived at
/// aggregation by subtracting the next step's inclusive time).
/// </summary>
internal class PipelineProvider
{
    private readonly List<IPipelineStep> _steps;
    private readonly string _pipelineName;
    private readonly string[] _stepPhaseNames;

    public PipelineProvider(List<IPipelineStep> steps, string pipelineName = "Pipeline")
    {
        _steps = steps;
        _pipelineName = pipelineName;
        _stepPhaseNames = new string[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            _stepPhaseNames[i] = $"{pipelineName}.{i:D2}.{steps[i].GetType().Name}";
        }
    }

    /// <summary>
    /// Runs the step at the given index, if there is one. (If not, we are at the end.)
    /// Passes the requestInfo to the step, along with a "next" function that will
    /// run the next step in the list.
    /// </summary>
    private async Task RunInternal(int stepIndex, RequestInfo requestInfo)
    {
        if (_steps.Count > stepIndex)
        {
            RequestTiming? timing = RequestTimingContext.Current;
            if (timing is null)
            {
                await _steps[stepIndex].Execute(requestInfo, () => RunInternal(stepIndex + 1, requestInfo));
                return;
            }

            long start = Stopwatch.GetTimestamp();
            try
            {
                await _steps[stepIndex].Execute(requestInfo, () => RunInternal(stepIndex + 1, requestInfo));
            }
            finally
            {
                timing.Record(_stepPhaseNames[stepIndex], start);
            }
        }
    }

    /// <summary>
    /// Start the pipeline with the first step
    /// </summary>
    public async Task Run(RequestInfo requestInfo)
    {
        RequestTimingContext.Current?.SetPipeline(_pipelineName);
        await RunInternal(0, requestInfo);
    }
}
