// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Backend.RelationalModel;

/// <summary>
/// A single ordered pass that participates in deriving a set-level relational model.
/// </summary>
public interface IRelationalModelSetPass
{
    /// <summary>
    /// The explicit order for this pass; lower values execute first.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Executes the pass, reading inputs from and writing outputs to the supplied context.
    /// </summary>
    /// <param name="context">The shared builder context for the current derivation.</param>
    void Execute(RelationalModelSetBuilderContext context);
}

/// <summary>
/// Bundles project-level metadata and the normalized project schema payload.
/// </summary>
/// <param name="EffectiveProject">The normalized project schema payload.</param>
/// <param name="ProjectSchema">The project schema metadata with physical schema info.</param>
public sealed record ProjectSchemaContext(
    EffectiveProjectSchema EffectiveProject,
    ProjectSchemaInfo ProjectSchema
);

/// <summary>
/// Represents a concrete resource schema entry within a project schema.
/// </summary>
/// <param name="Project">The owning project schema context.</param>
/// <param name="ResourceEndpointName">The API endpoint name for the resource.</param>
/// <param name="ResourceName">The logical resource name.</param>
/// <param name="ResourceSchema">The resource schema payload.</param>
public sealed record ConcreteResourceSchemaContext(
    ProjectSchemaContext Project,
    string ResourceEndpointName,
    string ResourceName,
    JsonObject ResourceSchema
);

/// <summary>
/// Shared mutable context passed through set-level relational model derivation passes.
/// </summary>
public sealed class RelationalModelSetBuilderContext
{
    private readonly IReadOnlyList<ProjectSchemaContext> _projectsInEndpointOrder;
    private readonly IReadOnlyList<ConcreteResourceSchemaContext> _concreteResourceSchemasInNameOrder;

    /// <summary>
    /// Creates a new builder context for the supplied effective schema set and dialect.
    /// </summary>
    /// <param name="effectiveSchemaSet">The normalized effective schema set payload.</param>
    /// <param name="dialect">The target SQL dialect.</param>
    /// <param name="dialectRules">The shared dialect rules used during derivation.</param>
    public RelationalModelSetBuilderContext(
        EffectiveSchemaSet effectiveSchemaSet,
        SqlDialect dialect,
        ISqlDialectRules dialectRules
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);
        ArgumentNullException.ThrowIfNull(dialectRules);

        EffectiveSchemaSet = effectiveSchemaSet;
        Dialect = dialect;
        DialectRules = dialectRules;

        ValidateEffectiveSchemaInfo(effectiveSchemaSet);

        var projectsInEndpointOrder = NormalizeProjectsInEndpointOrder(effectiveSchemaSet);
        var projectSchemaBundle = BuildProjectSchemaContexts(projectsInEndpointOrder);

        _projectsInEndpointOrder = projectSchemaBundle.ProjectSchemas;
        ProjectSchemasInEndpointOrder = projectSchemaBundle.ProjectSchemaInfos;
        _concreteResourceSchemasInNameOrder = BuildConcreteResourceSchemasInNameOrder(
            _projectsInEndpointOrder
        );
    }

    /// <summary>
    /// The effective schema set payload used for derivation.
    /// </summary>
    public EffectiveSchemaSet EffectiveSchemaSet { get; }

    /// <summary>
    /// The target SQL dialect for derivation outputs.
    /// </summary>
    public SqlDialect Dialect { get; }

    /// <summary>
    /// Shared dialect rules for identifier handling and scalar defaults.
    /// </summary>
    public ISqlDialectRules DialectRules { get; }

    /// <summary>
    /// Project schemas ordered by endpoint name, with physical schema normalization applied.
    /// </summary>
    public IReadOnlyList<ProjectSchemaInfo> ProjectSchemasInEndpointOrder { get; }

    /// <summary>
    /// Derived concrete resources ordered by (project, resource) name.
    /// </summary>
    public List<ConcreteResourceModel> ConcreteResourcesInNameOrder { get; } = [];

    /// <summary>
    /// Derived abstract identity tables ordered by resource name.
    /// </summary>
    public List<AbstractIdentityTableInfo> AbstractIdentityTablesInNameOrder { get; } = [];

    /// <summary>
    /// Derived abstract union views ordered by resource name.
    /// </summary>
    public List<AbstractUnionViewInfo> AbstractUnionViewsInNameOrder { get; } = [];

    /// <summary>
    /// Derived index inventory in creation order.
    /// </summary>
    public List<DbIndexInfo> IndexesInCreateOrder { get; } = [];

    /// <summary>
    /// Derived trigger inventory in creation order.
    /// </summary>
    public List<DbTriggerInfo> TriggersInCreateOrder { get; } = [];

    /// <summary>
    /// Enumerates projects in canonical endpoint order.
    /// </summary>
    public IEnumerable<ProjectSchemaContext> EnumerateProjectsInEndpointOrder()
    {
        return _projectsInEndpointOrder;
    }

    /// <summary>
    /// Enumerates concrete resource schemas in canonical name order.
    /// </summary>
    public IEnumerable<ConcreteResourceSchemaContext> EnumerateConcreteResourceSchemasInNameOrder()
    {
        return _concreteResourceSchemasInNameOrder;
    }

    /// <summary>
    /// Builds the final immutable derived model set.
    /// </summary>
    public DerivedRelationalModelSet BuildResult()
    {
        var orderedConcreteResources = ConcreteResourcesInNameOrder
            .OrderBy(resource => resource.ResourceKey.Resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceKey.Resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        return new DerivedRelationalModelSet(
            EffectiveSchemaSet.EffectiveSchema,
            Dialect,
            ProjectSchemasInEndpointOrder.ToArray(),
            orderedConcreteResources,
            AbstractIdentityTablesInNameOrder.ToArray(),
            AbstractUnionViewsInNameOrder.ToArray(),
            IndexesInCreateOrder.ToArray(),
            TriggersInCreateOrder.ToArray()
        );
    }

    private static IReadOnlyList<EffectiveProjectSchema> NormalizeProjectsInEndpointOrder(
        EffectiveSchemaSet effectiveSchemaSet
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        if (effectiveSchemaSet.ProjectsInEndpointOrder.Count == 0)
        {
            return Array.Empty<EffectiveProjectSchema>();
        }

        if (effectiveSchemaSet.ProjectsInEndpointOrder.Any(project => project is null))
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
            );
        }

        var orderedProjects = effectiveSchemaSet
            .ProjectsInEndpointOrder.OrderBy(project => project.ProjectEndpointName, StringComparer.Ordinal)
            .ToArray();

        return orderedProjects;
    }

    private static (
        IReadOnlyList<ProjectSchemaContext> ProjectSchemas,
        IReadOnlyList<ProjectSchemaInfo> ProjectSchemaInfos
    ) BuildProjectSchemaContexts(IReadOnlyList<EffectiveProjectSchema> projectsInEndpointOrder)
    {
        List<ProjectSchemaContext> projectSchemas = new(projectsInEndpointOrder.Count);
        List<ProjectSchemaInfo> projectSchemaInfos = new(projectsInEndpointOrder.Count);
        Dictionary<string, ProjectSchemaInfo> physicalSchemasByName = new(StringComparer.Ordinal);

        foreach (var project in projectsInEndpointOrder)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectEndpointName = RequireNonEmpty(project.ProjectEndpointName, "ProjectEndpointName");
            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var projectVersion = RequireNonEmpty(project.ProjectVersion, "ProjectVersion");
            var physicalSchema = RelationalNameConventions.NormalizeSchemaName(projectEndpointName);

            if (physicalSchemasByName.TryGetValue(physicalSchema.Value, out var existing))
            {
                throw new InvalidOperationException(
                    $"Project endpoint '{projectEndpointName}' normalizes to physical schema '{physicalSchema.Value}', "
                        + $"which is already used by project endpoint '{existing.ProjectEndpointName}'."
                );
            }

            var schemaInfo = new ProjectSchemaInfo(
                projectEndpointName,
                projectName,
                projectVersion,
                project.IsExtensionProject,
                physicalSchema
            );

            physicalSchemasByName.Add(physicalSchema.Value, schemaInfo);
            projectSchemaInfos.Add(schemaInfo);
            projectSchemas.Add(new ProjectSchemaContext(project, schemaInfo));
        }

        return (projectSchemas.ToArray(), projectSchemaInfos.ToArray());
    }

    private static IReadOnlyList<ConcreteResourceSchemaContext> BuildConcreteResourceSchemasInNameOrder(
        IReadOnlyList<ProjectSchemaContext> projectsInEndpointOrder
    )
    {
        List<ConcreteResourceSchemaContext> resources = new();

        foreach (var project in projectsInEndpointOrder)
        {
            var projectSchema = project.EffectiveProject.ProjectSchema;
            var resourceSchemas = RequireObject(
                projectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );

            foreach (var resourceSchemaEntry in resourceSchemas)
            {
                if (resourceSchemaEntry.Value is null)
                {
                    throw new InvalidOperationException(
                        "Expected projectSchema.resourceSchemas entries to be non-null, invalid ApiSchema."
                    );
                }

                if (resourceSchemaEntry.Value is not JsonObject resourceSchema)
                {
                    throw new InvalidOperationException(
                        "Expected projectSchema.resourceSchemas entries to be objects, invalid ApiSchema."
                    );
                }

                if (string.IsNullOrWhiteSpace(resourceSchemaEntry.Key))
                {
                    throw new InvalidOperationException(
                        "Expected resource schema entry key to be non-empty, invalid ApiSchema."
                    );
                }

                var resourceName = GetResourceName(resourceSchemaEntry.Key, resourceSchema);

                resources.Add(
                    new ConcreteResourceSchemaContext(
                        project,
                        resourceSchemaEntry.Key,
                        resourceName,
                        resourceSchema
                    )
                );
            }
        }

        var orderedResources = resources
            .OrderBy(resource => resource.Project.ProjectSchema.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        return orderedResources;
    }

    private static void ValidateEffectiveSchemaInfo(EffectiveSchemaSet effectiveSchemaSet)
    {
        if (effectiveSchemaSet.EffectiveSchema is null)
        {
            throw new InvalidOperationException("EffectiveSchemaSet.EffectiveSchema must be provided.");
        }

        if (effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.ResourceKeysInIdOrder must be provided."
            );
        }

        if (
            effectiveSchemaSet.EffectiveSchema.ResourceKeyCount
            != effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Count
        )
        {
            throw new InvalidOperationException(
                $"EffectiveSchemaInfo.ResourceKeyCount ({effectiveSchemaSet.EffectiveSchema.ResourceKeyCount}) "
                    + "does not match ResourceKeysInIdOrder count "
                    + $"({effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Count})."
            );
        }

        if (effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Any(entry => entry is null))
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.ResourceKeysInIdOrder must not contain null entries."
            );
        }

        var duplicateIds = effectiveSchemaSet
            .EffectiveSchema.ResourceKeysInIdOrder.GroupBy(entry => entry.ResourceKeyId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(id => id)
            .ToArray();

        var duplicateResources = effectiveSchemaSet
            .EffectiveSchema.ResourceKeysInIdOrder.GroupBy(entry => entry.Resource)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        if (duplicateIds.Length > 0 || duplicateResources.Length > 0)
        {
            List<string> messageParts = new();

            if (duplicateIds.Length > 0)
            {
                messageParts.Add(
                    "Duplicate ResourceKeyId values detected: " + string.Join(", ", duplicateIds)
                );
            }

            if (duplicateResources.Length > 0)
            {
                messageParts.Add(
                    "Duplicate resource keys detected for: "
                        + string.Join(", ", duplicateResources.Select(FormatResource))
                );
            }

            throw new InvalidOperationException(string.Join(" ", messageParts));
        }

        var effectiveResources = CollectEffectiveSchemaResources(effectiveSchemaSet);
        var resourceKeysByResource = effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.ToDictionary(
            entry => entry.Resource
        );

        var missingResources = effectiveResources
            .Where(resource => !resourceKeysByResource.ContainsKey(resource))
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        var extraResources = resourceKeysByResource
            .Where(entry => !effectiveResources.Contains(entry.Key))
            .Select(entry => entry.Key)
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        if (missingResources.Length > 0 || extraResources.Length > 0)
        {
            List<string> messageParts = new();

            if (missingResources.Length > 0)
            {
                messageParts.Add(
                    "Missing resource keys for: " + string.Join(", ", missingResources.Select(FormatResource))
                );
            }

            if (extraResources.Length > 0)
            {
                messageParts.Add(
                    "Resource keys reference unknown resources: "
                        + string.Join(", ", extraResources.Select(FormatResource))
                );
            }

            throw new InvalidOperationException(string.Join(" ", messageParts));
        }
    }

    private static HashSet<QualifiedResourceName> CollectEffectiveSchemaResources(
        EffectiveSchemaSet effectiveSchemaSet
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        HashSet<QualifiedResourceName> resources = new();

        foreach (var project in effectiveSchemaSet.ProjectsInEndpointOrder)
        {
            if (project is null)
            {
                throw new InvalidOperationException(
                    "EffectiveSchemaSet.ProjectsInEndpointOrder must not contain null entries."
                );
            }

            if (project.ProjectSchema is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema to be present in EffectiveProjectSchema."
                );
            }

            var projectName = RequireNonEmpty(project.ProjectName, "ProjectName");
            var resourceSchemas = RequireObject(
                project.ProjectSchema["resourceSchemas"],
                "projectSchema.resourceSchemas"
            );

            AddResourceEntries(resources, resourceSchemas, projectName, "projectSchema.resourceSchemas");

            if (project.ProjectSchema["abstractResources"] is JsonObject abstractResources)
            {
                AddResourceEntries(
                    resources,
                    abstractResources,
                    projectName,
                    "projectSchema.abstractResources"
                );
            }
        }

        return resources;
    }

    private static void AddResourceEntries(
        HashSet<QualifiedResourceName> resources,
        JsonObject resourceSchemas,
        string projectName,
        string resourceSchemasPath
    )
    {
        foreach (var resourceSchemaEntry in resourceSchemas)
        {
            if (resourceSchemaEntry.Value is null)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be non-null, invalid ApiSchema."
                );
            }

            if (resourceSchemaEntry.Value is not JsonObject resourceSchema)
            {
                throw new InvalidOperationException(
                    $"Expected {resourceSchemasPath} entries to be objects, invalid ApiSchema."
                );
            }

            if (string.IsNullOrWhiteSpace(resourceSchemaEntry.Key))
            {
                throw new InvalidOperationException(
                    "Expected resource schema entry key to be non-empty, invalid ApiSchema."
                );
            }

            var resourceName = GetResourceName(resourceSchemaEntry.Key, resourceSchema);

            resources.Add(new QualifiedResourceName(projectName, resourceName));
        }
    }

    private static string FormatResource(QualifiedResourceName resource)
    {
        return $"{resource.ProjectName}:{resource.ResourceName}";
    }

    private static JsonObject RequireObject(JsonNode? node, string propertyName)
    {
        return node switch
        {
            JsonObject jsonObject => jsonObject,
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be an object, invalid ApiSchema."
            ),
        };
    }

    private static string GetResourceName(string resourceKey, JsonObject resourceSchema)
    {
        if (resourceSchema.TryGetPropertyValue("resourceName", out var resourceNameNode))
        {
            return resourceNameNode switch
            {
                JsonValue jsonValue => RequireNonEmpty(jsonValue.GetValue<string>(), "resourceName"),
                null => throw new InvalidOperationException(
                    "Expected resourceName to be present, invalid ApiSchema."
                ),
                _ => throw new InvalidOperationException(
                    "Expected resourceName to be a string, invalid ApiSchema."
                ),
            };
        }

        return RequireNonEmpty(resourceKey, "resourceName");
    }

    private static string RequireNonEmpty(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Expected {propertyName} to be non-empty.");
        }

        return value;
    }
}

/// <summary>
/// Orchestrates ordered set-level passes to derive a complete relational model set.
/// </summary>
public sealed class DerivedRelationalModelSetBuilder
{
    private readonly IReadOnlyList<IRelationalModelSetPass> _passes;

    /// <summary>
    /// Creates a new builder with a deterministic pass ordering.
    /// </summary>
    /// <param name="passes">The passes to execute in order.</param>
    public DerivedRelationalModelSetBuilder(IReadOnlyList<IRelationalModelSetPass> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);

        if (passes.Count == 0)
        {
            _passes = Array.Empty<IRelationalModelSetPass>();
            return;
        }

        if (passes.Any(pass => pass is null))
        {
            throw new ArgumentException("Pass list cannot contain null entries.", nameof(passes));
        }

        _passes = OrderPasses(passes);
    }

    /// <summary>
    /// Runs the configured passes and returns a derived relational model set.
    /// </summary>
    /// <param name="effectiveSchemaSet">The normalized effective schema set.</param>
    /// <param name="dialect">The target SQL dialect.</param>
    /// <param name="dialectRules">Shared dialect rules for derivation.</param>
    public DerivedRelationalModelSet Build(
        EffectiveSchemaSet effectiveSchemaSet,
        SqlDialect dialect,
        ISqlDialectRules dialectRules
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);
        ArgumentNullException.ThrowIfNull(dialectRules);

        var context = new RelationalModelSetBuilderContext(effectiveSchemaSet, dialect, dialectRules);

        foreach (var pass in _passes)
        {
            pass.Execute(context);
        }

        return context.BuildResult();
    }

    private static IReadOnlyList<IRelationalModelSetPass> OrderPasses(
        IReadOnlyList<IRelationalModelSetPass> passes
    )
    {
        var passEntries = passes
            .Select(pass => new PassEntry(pass, pass.Order, pass.GetType().FullName ?? pass.GetType().Name))
            .ToArray();

        if (passEntries.Any(pass => string.IsNullOrWhiteSpace(pass.TypeName)))
        {
            throw new InvalidOperationException("Pass type name must be non-empty.");
        }

        var duplicateKeys = passEntries
            .GroupBy(entry => (entry.Order, entry.TypeName), entry => entry.Pass)
            .Where(group => group.Count() > 1)
            .Select(group => $"Order {group.Key.Order}, {group.Key.TypeName}")
            .ToArray();

        if (duplicateKeys.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate pass ordering keys detected: " + string.Join("; ", duplicateKeys)
            );
        }

        var orderedPasses = passEntries
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.TypeName, StringComparer.Ordinal)
            .Select(entry => entry.Pass)
            .ToArray();

        return orderedPasses;
    }

    private sealed record PassEntry(IRelationalModelSetPass Pass, int Order, string TypeName);
}
