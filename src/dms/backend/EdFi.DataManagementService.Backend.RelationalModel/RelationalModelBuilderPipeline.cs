// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

public interface IRelationalModelBuilderStep
{
    void Execute(RelationalModelBuilderContext context);
}

public sealed class RelationalModelBuilderContext
{
    public JsonNode? ApiSchemaRoot { get; set; }

    public string? ResourceEndpointName { get; set; }

    public string? ProjectName { get; set; }

    public string? ProjectEndpointName { get; set; }

    public string? ProjectVersion { get; set; }

    public string? ResourceName { get; set; }

    public JsonNode? JsonSchemaForInsert { get; set; }

    public IReadOnlyList<JsonPathExpression> IdentityJsonPaths { get; set; } =
        Array.Empty<JsonPathExpression>();

    public IReadOnlyDictionary<string, DescriptorPathInfo> DescriptorPathsByJsonPath { get; set; } =
        new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);

    public IReadOnlyDictionary<
        string,
        DecimalPropertyValidationInfo
    > DecimalPropertyValidationInfosByPath { get; set; } =
        new Dictionary<string, DecimalPropertyValidationInfo>(StringComparer.Ordinal);

    public IReadOnlySet<string> StringMaxLengthOmissionPaths { get; set; } =
        new HashSet<string>(StringComparer.Ordinal);

    public RelationalResourceModel? ResourceModel { get; set; }

    public List<ExtensionSite> ExtensionSites { get; } = [];

    public bool TryGetDescriptorPath(JsonPathExpression path, out DescriptorPathInfo descriptorPathInfo)
    {
        if (DescriptorPathsByJsonPath.Count == 0)
        {
            descriptorPathInfo = default;
            return false;
        }

        return DescriptorPathsByJsonPath.TryGetValue(path.Canonical, out descriptorPathInfo);
    }

    public bool TryGetDecimalPropertyValidationInfo(
        JsonPathExpression path,
        out DecimalPropertyValidationInfo validationInfo
    )
    {
        if (DecimalPropertyValidationInfosByPath.Count == 0)
        {
            validationInfo = default;
            return false;
        }

        return DecimalPropertyValidationInfosByPath.TryGetValue(path.Canonical, out validationInfo);
    }

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
