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
    private readonly Dictionary<
        QualifiedResourceName,
        IReadOnlyDictionary<string, DescriptorPathInfo>
    > _allDescriptorPathsByResource = new();
    private readonly Dictionary<string, JsonObject> _apiSchemaRootsByProjectEndpoint = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<
        QualifiedResourceName,
        RelationalModelBuilderContext
    > _builderContextsByResource = new();
    private readonly IReadOnlyDictionary<QualifiedResourceName, ResourceKeyEntry> _resourceKeysByResource;
    private readonly Dictionary<
        QualifiedResourceName,
        IReadOnlyList<ExtensionSite>
    > _extensionSitesByResource = new();
    private readonly Dictionary<string, ProjectSchemaInfo> _extensionProjectsByKey = new(
        StringComparer.Ordinal
    );
    private static readonly IReadOnlyList<ExtensionSite> _emptyExtensionSites = Array.Empty<ExtensionSite>();
    private static readonly IReadOnlyDictionary<string, DescriptorPathInfo> _emptyDescriptorPaths =
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
            effectiveResources.Resources
        );
        RelationalModelSetValidation.ValidateReferenceIdentityJsonPaths(
            effectiveSchemaSet,
            effectiveResources.Resources
        );
        _resourceKeysByResource = effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.ToDictionary(
            entry => entry.Resource
        );

        var projectSchemaBundle = new ProjectSchemaNormalizer().Normalize(effectiveSchemaSet);

        _projectsInEndpointOrder = projectSchemaBundle.ProjectSchemas;
        ProjectSchemasInEndpointOrder = projectSchemaBundle.ProjectSchemaInfos;
        var descriptorPathMaps = new DescriptorPathMapBuilder().Build(_projectsInEndpointOrder);
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
    /// Derived index inventory. BuildResult canonicalizes ordering by schema, table, and name; passes must not
    /// rely on insertion order.
    /// </summary>
    public List<DbIndexInfo> IndexInventory { get; } = [];

    /// <summary>
    /// Derived trigger inventory. BuildResult canonicalizes ordering by schema, table, and name; passes must not
    /// rely on insertion order.
    /// </summary>
    public List<DbTriggerInfo> TriggerInventory { get; } = [];

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
        return _descriptorPathsByResource.TryGetValue(resource, out var paths)
            ? paths
            : _emptyDescriptorPaths;
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
            : _emptyDescriptorPaths;
    }

    /// <summary>
    /// Returns all descriptor paths for the requested resource, including extension-scoped paths.
    /// </summary>
    public IReadOnlyDictionary<string, DescriptorPathInfo> GetAllDescriptorPathsForResource(
        QualifiedResourceName resource
    )
    {
        if (_allDescriptorPathsByResource.TryGetValue(resource, out var cached))
        {
            return cached;
        }

        var basePaths = GetDescriptorPathsForResource(resource);
        var extensionPaths = GetExtensionDescriptorPathsForResource(resource);

        if (extensionPaths.Count == 0)
        {
            _allDescriptorPathsByResource[resource] = basePaths;
            return basePaths;
        }

        if (basePaths.Count == 0)
        {
            _allDescriptorPathsByResource[resource] = extensionPaths;
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

        _allDescriptorPathsByResource[resource] = combined;
        return combined;
    }

    /// <summary>
    /// Builds or retrieves a cached builder context for the supplied resource.
    /// </summary>
    /// <param name="resourceContext">The resource schema context.</param>
    /// <returns>The initialized builder context.</returns>
    public RelationalModelBuilderContext GetOrCreateResourceBuilderContext(
        ConcreteResourceSchemaContext resourceContext
    )
    {
        ArgumentNullException.ThrowIfNull(resourceContext);

        var projectSchema = resourceContext.Project.ProjectSchema;
        var resource = new QualifiedResourceName(projectSchema.ProjectName, resourceContext.ResourceName);

        if (_builderContextsByResource.TryGetValue(resource, out var cached))
        {
            return cached;
        }

        var apiSchemaRoot = GetApiSchemaRoot(
            _apiSchemaRootsByProjectEndpoint,
            projectSchema.ProjectEndpointName,
            resourceContext.Project.EffectiveProject.ProjectSchema,
            cloneProjectSchema: true
        );
        var descriptorPaths = GetAllDescriptorPathsForResource(resource);

        var builderContext = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = resourceContext.ResourceEndpointName,
            DescriptorPathSource = DescriptorPathSource.Precomputed,
            DescriptorPathsByJsonPath = descriptorPaths,
        };

        new ExtractInputsStep().Execute(builderContext);

        _builderContextsByResource[resource] = builderContext;

        return builderContext;
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
            extensionSites.Count == 0 ? _emptyExtensionSites : extensionSites;
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
        return _extensionSitesByResource.TryGetValue(resource, out var sites) ? sites : _emptyExtensionSites;
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

        var canonicalIndexes = IndexInventory
            .OrderBy(index => index.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(index => index.Table.Name, StringComparer.Ordinal)
            .ThenBy(index => index.Name.Value, StringComparer.Ordinal)
            .ToArray();

        var canonicalTriggers = TriggerInventory
            .OrderBy(trigger => trigger.Table.Schema.Value, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Table.Name, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Name.Value, StringComparer.Ordinal)
            .ToArray();

        ValidateIdentifierShorteningCollisions(
            orderedConcreteResources,
            orderedAbstractIdentityTables,
            orderedAbstractUnionViews,
            canonicalIndexes,
            canonicalTriggers
        );

        return new DerivedRelationalModelSet(
            EffectiveSchemaSet.EffectiveSchema,
            Dialect,
            ProjectSchemasInEndpointOrder.ToArray(),
            orderedConcreteResources,
            orderedAbstractIdentityTables,
            orderedAbstractUnionViews,
            canonicalIndexes,
            canonicalTriggers
        );
    }

    /// <summary>
    /// Validates derived inventories prior to constructing the immutable model set payload.
    /// </summary>
    private void ValidateDerivedInventories()
    {
        ValidateNoNullEntries(ConcreteResourcesInNameOrder, nameof(ConcreteResourcesInNameOrder));
        ValidateNoNullEntries(AbstractIdentityTablesInNameOrder, nameof(AbstractIdentityTablesInNameOrder));
        ValidateNoNullEntries(AbstractUnionViewsInNameOrder, nameof(AbstractUnionViewsInNameOrder));
        ValidateNoNullEntries(IndexInventory, nameof(IndexInventory));
        ValidateNoNullEntries(TriggerInventory, nameof(TriggerInventory));
        ValidateConcreteResourceUniqueness();
        ValidateIndexNameUniqueness();
        ValidateTriggerNameUniqueness();
    }

    /// <summary>
    /// Detects dialect-shortening collisions across derived identifiers (tables, columns, constraints, indexes, triggers).
    /// </summary>
    /// <param name="orderedConcreteResources">Concrete resources in canonical order.</param>
    /// <param name="orderedAbstractIdentityTables">Abstract identity tables in canonical order.</param>
    /// <param name="orderedAbstractUnionViews">Abstract union views in canonical order.</param>
    /// <param name="canonicalIndexes">Index inventory in canonical order.</param>
    /// <param name="canonicalTriggers">Trigger inventory in canonical order.</param>
    private void ValidateIdentifierShorteningCollisions(
        IReadOnlyList<ConcreteResourceModel> orderedConcreteResources,
        IReadOnlyList<AbstractIdentityTableInfo> orderedAbstractIdentityTables,
        IReadOnlyList<AbstractUnionViewInfo> orderedAbstractUnionViews,
        IReadOnlyList<DbIndexInfo> canonicalIndexes,
        IReadOnlyList<DbTriggerInfo> canonicalTriggers
    )
    {
        var detector = new IdentifierCollisionDetector(DialectRules);

        foreach (var resource in orderedConcreteResources)
        {
            var resourceLabel = FormatResource(resource.ResourceKey.Resource);

            foreach (var table in resource.RelationalModel.TablesInReadDependencyOrder)
            {
                detector.RegisterTable(
                    table.Table,
                    $"table {FormatTable(table.Table)} (resource {resourceLabel})"
                );

                foreach (var column in table.Columns)
                {
                    detector.RegisterColumn(
                        table.Table,
                        column.ColumnName,
                        $"column {FormatColumn(table.Table, column.ColumnName)} "
                            + $"(resource {resourceLabel})"
                    );
                }

                foreach (var constraint in table.Constraints)
                {
                    var constraintName = GetConstraintName(constraint);

                    detector.RegisterConstraint(
                        table.Table,
                        constraintName,
                        $"constraint {constraintName} on {FormatTable(table.Table)} "
                            + $"(resource {resourceLabel})"
                    );
                }
            }
        }

        foreach (var table in orderedAbstractIdentityTables)
        {
            var resourceLabel = FormatResource(table.AbstractResourceKey.Resource);
            var tableModel = table.TableModel;

            detector.RegisterTable(
                tableModel.Table,
                $"table {FormatTable(tableModel.Table)} (abstract identity for {resourceLabel})"
            );

            foreach (var column in tableModel.Columns)
            {
                detector.RegisterColumn(
                    tableModel.Table,
                    column.ColumnName,
                    $"column {FormatColumn(tableModel.Table, column.ColumnName)} "
                        + $"(abstract identity for {resourceLabel})"
                );
            }

            foreach (var constraint in tableModel.Constraints)
            {
                var constraintName = GetConstraintName(constraint);

                detector.RegisterConstraint(
                    tableModel.Table,
                    constraintName,
                    $"constraint {constraintName} on {FormatTable(tableModel.Table)} "
                        + $"(abstract identity for {resourceLabel})"
                );
            }
        }

        foreach (var view in orderedAbstractUnionViews)
        {
            var resourceLabel = FormatResource(view.AbstractResourceKey.Resource);

            detector.RegisterTable(
                view.ViewName,
                $"view {FormatTable(view.ViewName)} (abstract union for {resourceLabel})"
            );

            foreach (var column in view.ColumnsInIdentityOrder)
            {
                detector.RegisterColumn(
                    view.ViewName,
                    column.ColumnName,
                    $"column {FormatColumn(view.ViewName, column.ColumnName)} "
                        + $"(abstract union for {resourceLabel})"
                );
            }
        }

        foreach (var index in canonicalIndexes)
        {
            detector.RegisterIndex(
                index.Table,
                index.Name,
                $"index {index.Name.Value} on {FormatTable(index.Table)}"
            );
        }

        foreach (var trigger in canonicalTriggers)
        {
            detector.RegisterTrigger(
                trigger.Table,
                trigger.Name,
                $"trigger {trigger.Name.Value} on {FormatTable(trigger.Table)}"
            );
        }

        detector.ThrowIfCollisions();
    }

    /// <summary>
    /// Ensures that each qualified resource contributes at most one concrete resource model.
    /// </summary>
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

    /// <summary>
    /// Ensures that derived index names are unique within a table and schema.
    /// </summary>
    private void ValidateIndexNameUniqueness()
    {
        var duplicateIndexKeys = IndexInventory
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

    /// <summary>
    /// Ensures that derived trigger names are unique within a table and schema.
    /// </summary>
    private void ValidateTriggerNameUniqueness()
    {
        var duplicateTriggerKeys = TriggerInventory
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

    /// <summary>
    /// Validates that a derived inventory list does not contain null entries.
    /// </summary>
    /// <typeparam name="T">The inventory entry type.</typeparam>
    /// <param name="entries">The entries to check.</param>
    /// <param name="listName">The inventory label used for diagnostics.</param>
    private static void ValidateNoNullEntries<T>(IEnumerable<T> entries, string listName)
        where T : class
    {
        if (entries.Any(entry => entry is null))
        {
            throw new InvalidOperationException($"{listName} must not contain null entries.");
        }
    }

    /// <summary>
    /// Represents a fully-qualified named object key used when checking name uniqueness.
    /// </summary>
    private readonly record struct TableNamedObjectKey(string Schema, string Table, string Name)
    {
        /// <summary>
        /// Formats the key for diagnostics.
        /// </summary>
        /// <returns>A formatted key string.</returns>
        public string Format()
        {
            return $"{Schema}.{Table}:{Name}";
        }
    }

    /// <summary>
    /// Extracts the physical name from a <see cref="TableConstraint"/> instance.
    /// </summary>
    /// <param name="constraint">The constraint to inspect.</param>
    /// <returns>The physical constraint name.</returns>
    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            TableConstraint.AllOrNoneNullability allOrNone => allOrNone.Name,
            _ => throw new InvalidOperationException(
                $"Unsupported constraint type '{constraint.GetType().Name}'."
            ),
        };
    }

    /// <summary>
    /// Formats a table name for diagnostics.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <returns>A formatted table label.</returns>
    private static string FormatTable(DbTableName table)
    {
        return $"{table.Schema.Value}.{table.Name}";
    }

    /// <summary>
    /// Formats a fully-qualified column name for diagnostics.
    /// </summary>
    /// <param name="table">The owning table.</param>
    /// <param name="column">The column name.</param>
    /// <returns>A formatted column label.</returns>
    private static string FormatColumn(DbTableName table, DbColumnName column)
    {
        return $"{FormatTable(table)}.{column.Value}";
    }

    /// <summary>
    /// Enumerates and orders all concrete resource schemas across all projects by (project name, resource name).
    /// </summary>
    /// <param name="projectsInEndpointOrder">Projects in endpoint order.</param>
    /// <returns>Concrete resource schema entries in canonical name order.</returns>
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

    /// <summary>
    /// Validates that every extension project key referenced by the resource's extension sites resolves to a configured extension project.
    /// </summary>
    /// <param name="resource">The owning resource.</param>
    /// <param name="extensionSites">The extension sites to validate.</param>
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

    /// <summary>
    /// Resolves an <c>_ext</c> project key to a configured project schema using endpoint-name and project-name matching rules.
    /// </summary>
    /// <param name="projectKey">The extension project key.</param>
    /// <param name="extensionSite">The declaring extension site (for diagnostics).</param>
    /// <param name="resource">The owning resource (for diagnostics).</param>
    /// <returns>The resolved project schema info.</returns>
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
            EnsureExtensionProject(projectKey, cachedProject, extensionSite, resource);
            return cachedProject;
        }

        var endpointMatches = ProjectSchemasInEndpointOrder
            .Where(project =>
                string.Equals(project.ProjectEndpointName, projectKey, StringComparison.Ordinal)
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
            return CacheExtensionProjectKey(projectKey, endpointMatches[0], extensionSite, resource);
        }

        var projectNameMatches = ProjectSchemasInEndpointOrder
            .Where(project => string.Equals(project.ProjectName, projectKey, StringComparison.Ordinal))
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
            return CacheExtensionProjectKey(projectKey, projectNameMatches[0], extensionSite, resource);
        }

        throw new InvalidOperationException(
            $"Extension project key '{projectKey}'{FormatExtensionSiteContext(extensionSite, resource)} "
                + "does not match any configured project."
        );
    }

    /// <summary>
    /// Caches the resolved extension project key mapping after validating it targets an extension project.
    /// </summary>
    /// <param name="projectKey">The extension project key.</param>
    /// <param name="project">The resolved project schema info.</param>
    /// <param name="extensionSite">The declaring extension site (for diagnostics).</param>
    /// <param name="resource">The owning resource (for diagnostics).</param>
    /// <returns>The resolved project schema info.</returns>
    private ProjectSchemaInfo CacheExtensionProjectKey(
        string projectKey,
        ProjectSchemaInfo project,
        ExtensionSite? extensionSite,
        QualifiedResourceName? resource
    )
    {
        EnsureExtensionProject(projectKey, project, extensionSite, resource);
        _extensionProjectsByKey[projectKey] = project;
        return project;
    }

    /// <summary>
    /// Validates that a resolved project is an extension project for use with <c>_ext</c> mapping.
    /// </summary>
    /// <param name="projectKey">The extension project key.</param>
    /// <param name="project">The resolved project schema info.</param>
    /// <param name="extensionSite">The declaring extension site (for diagnostics).</param>
    /// <param name="resource">The owning resource (for diagnostics).</param>
    private static void EnsureExtensionProject(
        string projectKey,
        ProjectSchemaInfo project,
        ExtensionSite? extensionSite,
        QualifiedResourceName? resource
    )
    {
        if (project.IsExtensionProject)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Extension project key '{projectKey}'{FormatExtensionSiteContext(extensionSite, resource)} "
                + $"resolves to non-extension project '{project.ProjectEndpointName}' ({project.ProjectName})."
        );
    }

    /// <summary>
    /// Formats resource and extension-site context information for error messages.
    /// </summary>
    /// <param name="extensionSite">The extension site (optional).</param>
    /// <param name="resource">The owning resource (optional).</param>
    /// <returns>A formatted context string starting with a leading space, or an empty string.</returns>
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

    /// <summary>
    /// Formats a list of candidate projects for diagnostics.
    /// </summary>
    /// <param name="candidates">The candidate projects.</param>
    /// <returns>A formatted candidate list.</returns>
    private static string FormatProjectCandidates(IReadOnlyList<ProjectSchemaInfo> candidates)
    {
        return string.Join(
            "; ",
            candidates.Select(project => $"{project.ProjectEndpointName} ({project.ProjectName})")
        );
    }
}
