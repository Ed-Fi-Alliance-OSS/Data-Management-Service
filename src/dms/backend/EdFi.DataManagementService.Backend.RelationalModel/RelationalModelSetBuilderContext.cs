// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel;

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

        var effectiveResources = RelationalModelSetValidation.BuildEffectiveSchemaResourceIndex(
            effectiveSchemaSet
        );
        RelationalModelSetValidation.ValidateEffectiveSchemaInfo(effectiveSchemaSet, effectiveResources);
        RelationalModelSetValidation.ValidateDocumentPathsMappingTargets(
            effectiveSchemaSet,
            effectiveResources
        );
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
        ValidateDerivedInventories();

        var orderedConcreteResources = ConcreteResourcesInNameOrder
            .OrderBy(resource => resource.ResourceKey.Resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceKey.Resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        var orderedAbstractIdentityTables = AbstractIdentityTablesInNameOrder
            .OrderBy(table => table.AbstractResourceKey.Resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(table => table.AbstractResourceKey.Resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        var orderedAbstractUnionViews = AbstractUnionViewsInNameOrder
            .OrderBy(view => view.AbstractResourceKey.Resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(view => view.AbstractResourceKey.Resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        var orderedIndexes = IndexesInCreateOrder
            .OrderBy(index => index.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(index => index.Table.Name, StringComparer.Ordinal)
            .ThenBy(index => index.Name.Value, StringComparer.Ordinal)
            .ToArray();

        var orderedTriggers = TriggersInCreateOrder
            .OrderBy(trigger => trigger.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Table.Name, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Name.Value, StringComparer.Ordinal)
            .ToArray();

        return new DerivedRelationalModelSet(
            EffectiveSchemaSet.EffectiveSchema,
            Dialect,
            ProjectSchemasInEndpointOrder.ToArray(),
            orderedConcreteResources,
            orderedAbstractIdentityTables,
            orderedAbstractUnionViews,
            orderedIndexes,
            orderedTriggers
        );
    }

    private void ValidateDerivedInventories()
    {
        ValidateNoNullEntries(ConcreteResourcesInNameOrder, nameof(ConcreteResourcesInNameOrder));
        ValidateNoNullEntries(AbstractIdentityTablesInNameOrder, nameof(AbstractIdentityTablesInNameOrder));
        ValidateNoNullEntries(AbstractUnionViewsInNameOrder, nameof(AbstractUnionViewsInNameOrder));
        ValidateNoNullEntries(IndexesInCreateOrder, nameof(IndexesInCreateOrder));
        ValidateNoNullEntries(TriggersInCreateOrder, nameof(TriggersInCreateOrder));
        ValidateConcreteResourceUniqueness();
        ValidateIndexNameUniqueness();
        ValidateTriggerNameUniqueness();
    }

    private void ValidateConcreteResourceUniqueness()
    {
        var duplicateResources = ConcreteResourcesInNameOrder
            .GroupBy(resource => resource.ResourceKey.Resource)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(resource => resource.ProjectName, StringComparer.Ordinal)
            .ThenBy(resource => resource.ResourceName, StringComparer.Ordinal)
            .ToArray();

        if (duplicateResources.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate concrete resources detected for: "
                    + string.Join(", ", duplicateResources.Select(FormatResource))
            );
        }
    }

    private void ValidateIndexNameUniqueness()
    {
        var duplicateIndexKeys = IndexesInCreateOrder
            .GroupBy(index => new TableNamedObjectKey(
                index.Table.Schema.Value,
                index.Table.Name,
                index.Name.Value
            ))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key.Schema, StringComparer.Ordinal)
            .ThenBy(key => key.Table, StringComparer.Ordinal)
            .ThenBy(key => key.Name, StringComparer.Ordinal)
            .Select(key => key.Format())
            .ToArray();

        if (duplicateIndexKeys.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate index names detected for: " + string.Join(", ", duplicateIndexKeys)
            );
        }
    }

    private void ValidateTriggerNameUniqueness()
    {
        var duplicateTriggerKeys = TriggersInCreateOrder
            .GroupBy(trigger => new TableNamedObjectKey(
                trigger.Table.Schema.Value,
                trigger.Table.Name,
                trigger.Name.Value
            ))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key.Schema, StringComparer.Ordinal)
            .ThenBy(key => key.Table, StringComparer.Ordinal)
            .ThenBy(key => key.Name, StringComparer.Ordinal)
            .Select(key => key.Format())
            .ToArray();

        if (duplicateTriggerKeys.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate trigger names detected for: " + string.Join(", ", duplicateTriggerKeys)
            );
        }
    }

    private static void ValidateNoNullEntries<T>(IEnumerable<T> entries, string listName)
        where T : class
    {
        if (entries.Any(entry => entry is null))
        {
            throw new InvalidOperationException($"{listName} must not contain null entries.");
        }
    }

    private readonly record struct TableNamedObjectKey(string Schema, string Table, string Name)
    {
        public string Format()
        {
            return $"{Schema}.{Table}:{Name}";
        }
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
