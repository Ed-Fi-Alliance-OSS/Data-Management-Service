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
    private readonly IReadOnlyDictionary<
        QualifiedResourceName,
        IReadOnlyDictionary<string, DescriptorPathInfo>
    > _descriptorPathsByResource;
    private readonly IReadOnlyDictionary<
        QualifiedResourceName,
        IReadOnlyDictionary<string, DescriptorPathInfo>
    > _extensionDescriptorPathsByResource;
    private readonly IReadOnlyDictionary<QualifiedResourceName, ResourceKeyEntry> _resourceKeysByResource;
    private readonly Dictionary<
        QualifiedResourceName,
        IReadOnlyList<ExtensionSite>
    > _extensionSitesByResource = new();
    private readonly Dictionary<string, ProjectSchemaInfo> _extensionProjectsByKey = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly IReadOnlyList<ExtensionSite> EmptyExtensionSites = Array.Empty<ExtensionSite>();
    private static readonly IReadOnlyDictionary<string, DescriptorPathInfo> EmptyDescriptorPaths =
        new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);

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

        var effectiveResources = BuildEffectiveSchemaResourceIndex(effectiveSchemaSet);
        ValidateEffectiveSchemaInfo(effectiveSchemaSet, effectiveResources);
        ValidateDocumentPathsMappingTargets(effectiveSchemaSet, effectiveResources);
        _resourceKeysByResource = effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.ToDictionary(
            entry => entry.Resource
        );

        var projectsInEndpointOrder = NormalizeProjectsInEndpointOrder(effectiveSchemaSet);
        var projectSchemaBundle = BuildProjectSchemaContexts(projectsInEndpointOrder);

        _projectsInEndpointOrder = projectSchemaBundle.ProjectSchemas;
        ProjectSchemasInEndpointOrder = projectSchemaBundle.ProjectSchemaInfos;
        var descriptorPathMaps = BuildDescriptorPathMaps(_projectsInEndpointOrder);
        _descriptorPathsByResource = descriptorPathMaps.BaseDescriptorPathsByResource;
        _extensionDescriptorPathsByResource = descriptorPathMaps.ExtensionDescriptorPathsByResource;
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
    /// Returns descriptor paths excluding extension-scoped paths for the requested resource.
    /// </summary>
    public IReadOnlyDictionary<string, DescriptorPathInfo> GetDescriptorPathsForResource(
        QualifiedResourceName resource
    )
    {
        return _descriptorPathsByResource.TryGetValue(resource, out var paths) ? paths : EmptyDescriptorPaths;
    }

    /// <summary>
    /// Returns descriptor paths that occur under <c>_ext</c> for the requested resource.
    /// </summary>
    public IReadOnlyDictionary<string, DescriptorPathInfo> GetExtensionDescriptorPathsForResource(
        QualifiedResourceName resource
    )
    {
        return _extensionDescriptorPathsByResource.TryGetValue(resource, out var paths)
            ? paths
            : EmptyDescriptorPaths;
    }

    /// <summary>
    /// Returns all descriptor paths for the requested resource, including extension-scoped paths.
    /// </summary>
    public IReadOnlyDictionary<string, DescriptorPathInfo> GetAllDescriptorPathsForResource(
        QualifiedResourceName resource
    )
    {
        var basePaths = GetDescriptorPathsForResource(resource);
        var extensionPaths = GetExtensionDescriptorPathsForResource(resource);

        if (extensionPaths.Count == 0)
        {
            return basePaths;
        }

        if (basePaths.Count == 0)
        {
            return extensionPaths;
        }

        Dictionary<string, DescriptorPathInfo> combined = new(StringComparer.Ordinal);

        foreach (var entry in basePaths)
        {
            combined[entry.Key] = entry.Value;
        }

        foreach (var entry in extensionPaths)
        {
            combined[entry.Key] = entry.Value;
        }

        return combined;
    }

    /// <summary>
    /// Resolves the effective schema resource key entry for the supplied resource.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    /// <returns>The matching resource key entry.</returns>
    public ResourceKeyEntry GetResourceKeyEntry(QualifiedResourceName resource)
    {
        if (_resourceKeysByResource.TryGetValue(resource, out var entry))
        {
            return entry;
        }

        throw new InvalidOperationException(
            $"Resource key not found for resource '{FormatResource(resource)}'."
        );
    }

    /// <summary>
    /// Registers extension site metadata for the supplied resource.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    /// <param name="extensionSites">The extension sites discovered for the resource.</param>
    public void RegisterExtensionSitesForResource(
        QualifiedResourceName resource,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        ArgumentNullException.ThrowIfNull(extensionSites);

        if (_extensionSitesByResource.ContainsKey(resource))
        {
            throw new InvalidOperationException(
                $"Extension sites already registered for resource '{FormatResource(resource)}'."
            );
        }

        ValidateExtensionProjectKeys(resource, extensionSites);

        _extensionSitesByResource[resource] =
            extensionSites.Count == 0 ? EmptyExtensionSites : extensionSites;
    }

    /// <summary>
    /// Resolves an extension project key to a configured project schema.
    /// </summary>
    /// <param name="projectKey">The extension project key found under <c>_ext</c>.</param>
    /// <param name="extensionSite">The extension site that declared the key.</param>
    /// <param name="resource">The resource owning the extension site.</param>
    /// <returns>The resolved project schema info.</returns>
    public ProjectSchemaInfo ResolveExtensionProjectKey(
        string projectKey,
        ExtensionSite extensionSite,
        QualifiedResourceName resource
    )
    {
        return ResolveExtensionProjectKeyInternal(projectKey, extensionSite, resource);
    }

    /// <summary>
    /// Returns extension sites for the supplied resource.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    /// <returns>The extension sites, or an empty list if none were registered.</returns>
    public IReadOnlyList<ExtensionSite> GetExtensionSitesForResource(QualifiedResourceName resource)
    {
        return _extensionSitesByResource.TryGetValue(resource, out var sites) ? sites : EmptyExtensionSites;
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

    private static (
        IReadOnlyDictionary<
            QualifiedResourceName,
            IReadOnlyDictionary<string, DescriptorPathInfo>
        > BaseDescriptorPathsByResource,
        IReadOnlyDictionary<
            QualifiedResourceName,
            IReadOnlyDictionary<string, DescriptorPathInfo>
        > ExtensionDescriptorPathsByResource
    ) BuildDescriptorPathMaps(IReadOnlyList<ProjectSchemaContext> projectsInEndpointOrder)
    {
        if (projectsInEndpointOrder.Count == 0)
        {
            return (
                new Dictionary<QualifiedResourceName, IReadOnlyDictionary<string, DescriptorPathInfo>>(),
                new Dictionary<QualifiedResourceName, IReadOnlyDictionary<string, DescriptorPathInfo>>()
            );
        }

        var projectSchemas = projectsInEndpointOrder
            .Select(project => new DescriptorPathInference.ProjectDescriptorSchema(
                project.ProjectSchema.ProjectName,
                project.EffectiveProject.ProjectSchema
            ))
            .ToArray();

        var descriptorPathsByResource = DescriptorPathInference.BuildDescriptorPathsByResource(
            projectSchemas
        );

        Dictionary<
            QualifiedResourceName,
            IReadOnlyDictionary<string, DescriptorPathInfo>
        > baseDescriptorPathsByResource = new();
        Dictionary<
            QualifiedResourceName,
            IReadOnlyDictionary<string, DescriptorPathInfo>
        > extensionDescriptorPathsByResource = new();

        foreach (var resourceEntry in descriptorPathsByResource)
        {
            Dictionary<string, DescriptorPathInfo> basePaths = new(StringComparer.Ordinal);
            Dictionary<string, DescriptorPathInfo> extensionPaths = new(StringComparer.Ordinal);

            foreach (var pathEntry in resourceEntry.Value)
            {
                if (IsExtensionScoped(pathEntry.Value.DescriptorValuePath))
                {
                    extensionPaths.Add(pathEntry.Key, pathEntry.Value);
                    continue;
                }

                basePaths.Add(pathEntry.Key, pathEntry.Value);
            }

            baseDescriptorPathsByResource[resourceEntry.Key] = basePaths;
            extensionDescriptorPathsByResource[resourceEntry.Key] = extensionPaths;
        }

        return (baseDescriptorPathsByResource, extensionDescriptorPathsByResource);
    }

    private static void ValidateEffectiveSchemaInfo(
        EffectiveSchemaSet effectiveSchemaSet,
        IReadOnlySet<QualifiedResourceName> effectiveResources
    )
    {
        if (effectiveSchemaSet.EffectiveSchema is null)
        {
            throw new InvalidOperationException("EffectiveSchemaSet.EffectiveSchema must be provided.");
        }

        ValidateSchemaComponentsInEndpointOrder(effectiveSchemaSet);

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

    private static void ValidateSchemaComponentsInEndpointOrder(EffectiveSchemaSet effectiveSchemaSet)
    {
        if (effectiveSchemaSet.EffectiveSchema.SchemaComponentsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.SchemaComponentsInEndpointOrder must be provided."
            );
        }

        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

        if (
            effectiveSchemaSet.EffectiveSchema.SchemaComponentsInEndpointOrder.Any(component =>
                component is null
            )
        )
        {
            throw new InvalidOperationException(
                "EffectiveSchemaInfo.SchemaComponentsInEndpointOrder must not contain null entries."
            );
        }

        var schemaComponentEndpointNames = effectiveSchemaSet
            .EffectiveSchema.SchemaComponentsInEndpointOrder.Select(component =>
                RequireNonEmpty(
                    component.ProjectEndpointName,
                    "SchemaComponentsInEndpointOrder.ProjectEndpointName"
                )
            )
            .ToArray();

        var projectEndpointNames = effectiveSchemaSet
            .ProjectsInEndpointOrder.Select(project =>
                RequireNonEmpty(project.ProjectEndpointName, "ProjectEndpointName")
            )
            .ToArray();

        var schemaComponentSet = new HashSet<string>(schemaComponentEndpointNames, StringComparer.Ordinal);
        var projectSet = new HashSet<string>(projectEndpointNames, StringComparer.Ordinal);

        var missing = projectSet
            .Except(schemaComponentSet)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var extra = schemaComponentSet
            .Except(projectSet)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0 && extra.Length == 0)
        {
            return;
        }

        List<string> messageParts = new();

        if (missing.Length > 0)
        {
            messageParts.Add(
                "SchemaComponentsInEndpointOrder is missing projects: " + string.Join(", ", missing)
            );
        }

        if (extra.Length > 0)
        {
            messageParts.Add(
                "SchemaComponentsInEndpointOrder contains unknown projects: " + string.Join(", ", extra)
            );
        }

        throw new InvalidOperationException(string.Join(" ", messageParts));
    }

    private static HashSet<QualifiedResourceName> BuildEffectiveSchemaResourceIndex(
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

    private static void ValidateDocumentPathsMappingTargets(
        EffectiveSchemaSet effectiveSchemaSet,
        IReadOnlySet<QualifiedResourceName> effectiveResources
    )
    {
        if (effectiveSchemaSet.ProjectsInEndpointOrder is null)
        {
            throw new InvalidOperationException(
                "EffectiveSchemaSet.ProjectsInEndpointOrder must be provided."
            );
        }

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

            ValidateDocumentPathsMappingTargetsForResourceSchemas(
                projectName,
                resourceSchemas,
                effectiveResources,
                "projectSchema.resourceSchemas"
            );

            if (project.ProjectSchema["abstractResources"] is JsonObject abstractResources)
            {
                ValidateDocumentPathsMappingTargetsForResourceSchemas(
                    projectName,
                    abstractResources,
                    effectiveResources,
                    "projectSchema.abstractResources"
                );
            }
        }
    }

    private static void ValidateDocumentPathsMappingTargetsForResourceSchemas(
        string projectName,
        JsonObject resourceSchemas,
        IReadOnlySet<QualifiedResourceName> effectiveResources,
        string resourceSchemasPath
    )
    {
        foreach (var resourceSchemaEntry in OrderResourceSchemas(resourceSchemas, resourceSchemasPath))
        {
            ValidateDocumentPathsMappingTargetsForResource(
                projectName,
                resourceSchemaEntry.ResourceName,
                resourceSchemaEntry.ResourceSchema,
                effectiveResources
            );
        }
    }

    private static void ValidateDocumentPathsMappingTargetsForResource(
        string projectName,
        string resourceName,
        JsonObject resourceSchema,
        IReadOnlySet<QualifiedResourceName> effectiveResources
    )
    {
        if (resourceSchema["documentPathsMapping"] is not JsonObject documentPathsMapping)
        {
            return;
        }

        foreach (var mapping in documentPathsMapping.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (mapping.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be non-null, invalid ApiSchema."
                );
            }

            if (mapping.Value is not JsonObject mappingObject)
            {
                throw new InvalidOperationException(
                    "Expected documentPathsMapping entries to be objects, invalid ApiSchema."
                );
            }

            var isReference =
                mappingObject["isReference"]?.GetValue<bool>()
                ?? throw new InvalidOperationException(
                    "Expected isReference to be on documentPathsMapping entry, invalid ApiSchema."
                );

            if (!isReference)
            {
                continue;
            }

            var targetProjectName = RequireString(mappingObject, "projectName");
            var targetResourceName = RequireString(mappingObject, "resourceName");
            var targetResource = new QualifiedResourceName(targetProjectName, targetResourceName);

            if (effectiveResources.Contains(targetResource))
            {
                continue;
            }

            var mappingLabel = FormatDocumentPathsMappingLabel(mapping.Key, mappingObject);

            throw new InvalidOperationException(
                $"documentPathsMapping {mappingLabel} on resource '{projectName}:{resourceName}' "
                    + $"references unknown resource '{targetProjectName}:{targetResourceName}'."
            );
        }
    }

    private static string FormatDocumentPathsMappingLabel(string mappingKey, JsonObject mappingObject)
    {
        if (string.IsNullOrWhiteSpace(mappingKey))
        {
            return "entry '<empty>'";
        }

        var path = TryGetOptionalString(mappingObject, "path");

        if (path is null)
        {
            return $"entry '{mappingKey}'";
        }

        return $"entry '{mappingKey}' (path '{path}')";
    }

    private static IReadOnlyList<ResourceSchemaEntry> OrderResourceSchemas(
        JsonObject resourceSchemas,
        string resourceSchemasPath
    )
    {
        List<ResourceSchemaEntry> entries = new(resourceSchemas.Count);

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

            entries.Add(new ResourceSchemaEntry(resourceSchemaEntry.Key, resourceName, resourceSchema));
        }

        return entries
            .OrderBy(entry => entry.ResourceName, StringComparer.Ordinal)
            .ThenBy(entry => entry.ResourceKey, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record ResourceSchemaEntry(
        string ResourceKey,
        string ResourceName,
        JsonObject ResourceSchema
    );

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

    private void ValidateExtensionProjectKeys(
        QualifiedResourceName resource,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        foreach (var extensionSite in extensionSites)
        {
            if (extensionSite is null)
            {
                throw new InvalidOperationException(
                    $"Extension sites for resource '{FormatResource(resource)}' must not contain null entries."
                );
            }

            foreach (var projectKey in extensionSite.ProjectKeys)
            {
                ResolveExtensionProjectKeyInternal(projectKey, extensionSite, resource);
            }
        }
    }

    private ProjectSchemaInfo ResolveExtensionProjectKeyInternal(
        string projectKey,
        ExtensionSite? extensionSite,
        QualifiedResourceName? resource
    )
    {
        if (string.IsNullOrWhiteSpace(projectKey))
        {
            throw new InvalidOperationException(
                $"Extension project key is empty{FormatExtensionSiteContext(extensionSite, resource)}."
            );
        }

        if (_extensionProjectsByKey.TryGetValue(projectKey, out var cachedProject))
        {
            return cachedProject;
        }

        var endpointMatches = ProjectSchemasInEndpointOrder
            .Where(project =>
                string.Equals(project.ProjectEndpointName, projectKey, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        if (endpointMatches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Extension project key '{projectKey}'{FormatExtensionSiteContext(extensionSite, resource)} "
                    + "matches multiple configured projects by endpoint name: "
                    + $"{FormatProjectCandidates(endpointMatches)}."
            );
        }

        if (endpointMatches.Length == 1)
        {
            return CacheExtensionProjectKey(projectKey, endpointMatches[0]);
        }

        var projectNameMatches = ProjectSchemasInEndpointOrder
            .Where(project =>
                string.Equals(project.ProjectName, projectKey, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();

        if (projectNameMatches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Extension project key '{projectKey}'{FormatExtensionSiteContext(extensionSite, resource)} "
                    + "matches multiple configured projects by project name: "
                    + $"{FormatProjectCandidates(projectNameMatches)}."
            );
        }

        if (projectNameMatches.Length == 1)
        {
            return CacheExtensionProjectKey(projectKey, projectNameMatches[0]);
        }

        throw new InvalidOperationException(
            $"Extension project key '{projectKey}'{FormatExtensionSiteContext(extensionSite, resource)} "
                + "does not match any configured project."
        );
    }

    private ProjectSchemaInfo CacheExtensionProjectKey(string projectKey, ProjectSchemaInfo project)
    {
        _extensionProjectsByKey[projectKey] = project;
        return project;
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

    private static string RequireString(JsonObject node, string propertyName)
    {
        var value = node[propertyName] switch
        {
            JsonValue jsonValue => jsonValue.GetValue<string>(),
            null => throw new InvalidOperationException(
                $"Expected {propertyName} to be present, invalid ApiSchema."
            ),
            _ => throw new InvalidOperationException(
                $"Expected {propertyName} to be a string, invalid ApiSchema."
            ),
        };

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be non-empty, invalid ApiSchema."
            );
        }

        return value;
    }

    private static string? TryGetOptionalString(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var value))
        {
            return null;
        }

        if (value is null)
        {
            return null;
        }

        if (value is not JsonValue jsonValue)
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be a string, invalid ApiSchema."
            );
        }

        var text = jsonValue.GetValue<string>();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Expected {propertyName} to be non-empty, invalid ApiSchema."
            );
        }

        return text;
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

    private static string FormatExtensionSiteContext(
        ExtensionSite? extensionSite,
        QualifiedResourceName? resource
    )
    {
        List<string> contextParts = new();

        if (resource is { } resourceValue)
        {
            contextParts.Add($"resource '{FormatResource(resourceValue)}'");
        }

        if (extensionSite is not null)
        {
            contextParts.Add($"owning scope '{extensionSite.OwningScope.Canonical}'");
            contextParts.Add($"extension path '{extensionSite.ExtensionPath.Canonical}'");
        }

        if (contextParts.Count == 0)
        {
            return string.Empty;
        }

        return " (" + string.Join(", ", contextParts) + ")";
    }

    private static string FormatProjectCandidates(IReadOnlyList<ProjectSchemaInfo> candidates)
    {
        return string.Join(
            "; ",
            candidates.Select(project => $"{project.ProjectEndpointName} ({project.ProjectName})")
        );
    }

    private static bool IsExtensionScoped(JsonPathExpression path)
    {
        return path.Segments.Any(segment => segment is JsonPathSegment.Property { Name: "_ext" });
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

        if (dialectRules.Dialect != dialect)
        {
            throw new InvalidOperationException(
                $"Dialect mismatch: requested {dialect} but dialect rules target {dialectRules.Dialect}."
            );
        }

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
