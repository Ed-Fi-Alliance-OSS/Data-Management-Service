// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel;

public interface IRelationalModelBuilderStep
{
    void Execute(RelationalModelBuilderContext context);
}

public sealed class RelationalModelBuilderContext
{
    public RelationalResourceModel? ResourceModel { get; set; }

    public List<ExtensionSite> ExtensionSites { get; } = [];

    public RelationalModelBuildResult BuildResult()
    {
        if (ResourceModel is null)
        {
            throw new InvalidOperationException("Resource model must be set before building results.");
        }

        return new RelationalModelBuildResult(ResourceModel, ExtensionSites.ToArray());
    }
}

public sealed record RelationalModelBuildResult(
    RelationalResourceModel ResourceModel,
    IReadOnlyList<ExtensionSite> ExtensionSites
);

public sealed class RelationalModelBuilderPipeline
{
    private readonly IRelationalModelBuilderStep[] _steps;

    public RelationalModelBuilderPipeline(IEnumerable<IRelationalModelBuilderStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        _steps = steps.ToArray();
        if (Array.Exists(_steps, step => step is null))
        {
            throw new ArgumentException("Pipeline steps cannot contain null values.", nameof(steps));
        }
    }

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
