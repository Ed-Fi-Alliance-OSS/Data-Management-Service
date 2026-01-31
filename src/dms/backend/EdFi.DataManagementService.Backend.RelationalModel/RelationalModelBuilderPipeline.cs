// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// A single step in the relational model derivation pipeline.
/// </summary>
public interface IRelationalModelBuilderStep
{
    /// <summary>
    /// Executes the step, reading inputs from and writing outputs to the supplied context.
    /// </summary>
    /// <param name="context">The mutable builder context for the current pipeline run.</param>
    void Execute(RelationalModelBuilderContext context);
}

/// <summary>
/// Shared mutable context passed through the relational model builder pipeline.
///
/// This context aggregates schema inputs, extracted metadata, and derived outputs. Pipeline steps are expected
/// to validate required inputs at their entry points and fail fast with actionable errors.
/// </summary>
public sealed class RelationalModelBuilderContext
{
    /// <summary>
    /// Controls whether descriptor path inference should be computed from schema inputs or supplied
    /// externally (for example, from a set-level derivation pass).
    /// </summary>
    public DescriptorPathSource DescriptorPathSource { get; set; } = DescriptorPathSource.InferFromSchema;

    /// <summary>
    /// The root <c>ApiSchema.json</c> document node (when building from a full schema payload).
    /// </summary>
    public JsonNode? ApiSchemaRoot { get; set; }

    /// <summary>
    /// The endpoint name for the resource (used for schema selection and naming).
    /// </summary>
    public string? ResourceEndpointName { get; set; }

    /// <summary>
    /// The logical project name (e.g., <c>Ed-Fi</c>).
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// The endpoint name used for physical schema naming (e.g., <c>ed-fi</c>).
    /// </summary>
    public string? ProjectEndpointName { get; set; }

    /// <summary>
    /// The project version (used for effective schema hashing and compatibility checks).
    /// </summary>
    public string? ProjectVersion { get; set; }

    /// <summary>
    /// The logical resource name (e.g., <c>School</c>).
    /// </summary>
    public string? ResourceName { get; set; }

    /// <summary>
    /// Whether the resource is a descriptor resource.
    /// </summary>
    public bool IsDescriptorResource { get; set; }

    /// <summary>
    /// The fully dereferenced JSON schema used for inserts/updates (<c>resourceSchema.jsonSchemaForInsert</c>).
    /// </summary>
    public JsonNode? JsonSchemaForInsert { get; set; }

    /// <summary>
    /// Identity JSONPaths for the resource, used for identity-component tracking and constraints.
    /// </summary>
    public IReadOnlyList<JsonPathExpression> IdentityJsonPaths { get; set; } =
        Array.Empty<JsonPathExpression>();

    /// <summary>
    /// Whether identity updates are allowed for the resource.
    /// </summary>
    public bool AllowIdentityUpdates { get; set; }

    /// <summary>
    /// Document reference mappings derived from <c>documentPathsMapping.referenceJsonPaths</c>.
    /// </summary>
    public IReadOnlyList<DocumentReferenceMapping> DocumentReferenceMappings { get; set; } =
        Array.Empty<DocumentReferenceMapping>();

    /// <summary>
    /// Uniqueness constraints defined for array scopes.
    /// </summary>
    public IReadOnlyList<ArrayUniquenessConstraintInput> ArrayUniquenessConstraints { get; set; } =
        Array.Empty<ArrayUniquenessConstraintInput>();

    /// <summary>
    /// Reference base-name overrides keyed by canonical JSONPath.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReferenceNameOverridesByPath { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Descriptor value paths keyed by canonical JSONPath, used to derive descriptor FK columns instead of
    /// storing raw descriptor strings.
    /// </summary>
    public IReadOnlyDictionary<string, DescriptorPathInfo> DescriptorPathsByJsonPath { get; set; } =
        new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);

    /// <summary>
    /// Decimal validation metadata (precision/scale) keyed by canonical JSONPath, used to map JSON Schema
    /// <c>number</c> properties to a deterministic relational decimal type.
    /// </summary>
#pragma warning disable IDE0055
    public IReadOnlyDictionary<
        string,
        DecimalPropertyValidationInfo
    > DecimalPropertyValidationInfosByPath { get; set; } =
        new Dictionary<string, DecimalPropertyValidationInfo>(StringComparer.Ordinal);
#pragma warning restore IDE0055

    /// <summary>
    /// Canonical JSONPaths where missing string <c>maxLength</c> is acceptable (e.g., duration and
    /// enumeration-like strings).
    /// </summary>
    public IReadOnlySet<string> StringMaxLengthOmissionPaths { get; set; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The derived resource model produced by pipeline steps (tables, columns, constraints, edges).
    /// </summary>
    public RelationalResourceModel? ResourceModel { get; set; }

    /// <summary>
    /// Derived extension mapping sites discovered during traversal.
    /// </summary>
    public List<ExtensionSite> ExtensionSites { get; } = [];

    /// <summary>
    /// Looks up descriptor metadata for a JSONPath value.
    /// </summary>
    public bool TryGetDescriptorPath(JsonPathExpression path, out DescriptorPathInfo descriptorPathInfo)
    {
        if (DescriptorPathsByJsonPath.Count == 0)
        {
            descriptorPathInfo = default;
            return false;
        }

        return DescriptorPathsByJsonPath.TryGetValue(path.Canonical, out descriptorPathInfo);
    }

    /// <summary>
    /// Looks up decimal validation metadata for a JSONPath value.
    /// </summary>
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

    /// <summary>
    /// Builds the final immutable result object for the pipeline run.
    /// </summary>
    public RelationalModelBuildResult BuildResult()
    {
        if (ResourceModel is null)
        {
            throw new InvalidOperationException("Resource model must be set before building results.");
        }

        return new RelationalModelBuildResult(ResourceModel, ExtensionSites.ToArray());
    }
}

/// <summary>
/// Indicates how descriptor paths should be supplied to the per-resource pipeline.
/// </summary>
public enum DescriptorPathSource
{
    /// <summary>
    /// Infer descriptor paths from the resource schema.
    /// </summary>
    InferFromSchema,

    /// <summary>
    /// Use precomputed descriptor paths supplied by the caller.
    /// </summary>
    Precomputed,
}

/// <summary>
/// The final output of a relational model build run, including the derived resource model and any extension
/// site metadata discovered during traversal.
/// </summary>
public sealed record RelationalModelBuildResult(
    RelationalResourceModel ResourceModel,
    IReadOnlyList<ExtensionSite> ExtensionSites
);

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
