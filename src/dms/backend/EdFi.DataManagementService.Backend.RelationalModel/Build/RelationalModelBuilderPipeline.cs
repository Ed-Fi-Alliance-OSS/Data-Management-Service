// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// Executes a configured sequence of <see cref="IRelationalModelBuilderStep"/> instances to derive a complete
/// relational model for a resource.
/// </summary>
public sealed class RelationalModelBuilderPipeline
{
    private readonly IRelationalModelBuilderStep[] _steps;

    /// <summary>
    /// Creates a pipeline from a fixed set of non-null steps.
    /// </summary>
    /// <param name="steps">The steps to execute in order.</param>
    public RelationalModelBuilderPipeline(IEnumerable<IRelationalModelBuilderStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        _steps = steps.ToArray();
        if (Array.Exists(_steps, step => step is null))
        {
            throw new ArgumentException("Pipeline steps cannot contain null values.", nameof(steps));
        }
    }

    /// <summary>
    /// Runs each configured step in order and returns the final build result.
    /// </summary>
    /// <param name="context">The mutable pipeline context.</param>
    public RelationalModelBuildResult Run(RelationalModelBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var step in _steps)
        {
            step.Execute(context);
        }

        return context.BuildResult();
    }
}
