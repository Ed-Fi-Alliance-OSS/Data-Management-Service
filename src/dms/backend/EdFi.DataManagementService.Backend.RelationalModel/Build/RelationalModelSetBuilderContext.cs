// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel.SetPasses;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Build;

/// <summary>
/// Shared mutable context passed through set-level relational model derivation passes.
/// </summary>
public sealed class RelationalModelSetBuilderContext
{
    private readonly IReadOnlyList<ProjectSchemaContext> _projectsInEndpointOrder;
    private readonly List<ProjectSchemaInfo> _projectSchemasInEndpointOrder;
    private readonly IReadOnlyList<ProjectSchemaInfo> _projectSchemasInEndpointOrderView;
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
    private readonly IRelationalModelBuilderStep _extractInputsStep;
    private readonly IReadOnlyDictionary<QualifiedResourceName, ResourceKeyEntry> _resourceKeysByResource;
    private readonly Dictionary<
        QualifiedResourceName,
        IReadOnlyList<ExtensionSite>
    > _extensionSitesByResource = new();
    private readonly Dictionary<string, ProjectSchemaInfo> _extensionProjectsByKey = new(
        ExtensionProjectKeyComparer
    );
    private readonly Dictionary<
        DbTableName,
        List<StrictUnifiedAliasTableMetadataEntry>
    > _strictUnifiedAliasTableMetadataByTable = [];
    private static readonly IReadOnlyList<ExtensionSite> _emptyExtensionSites = Array.Empty<ExtensionSite>();
    private static readonly IReadOnlyDictionary<string, DescriptorPathInfo> _emptyDescriptorPaths =
        new Dictionary<string, DescriptorPathInfo>(StringComparer.Ordinal);

    /// <summary>
    /// Creates a new builder context for the supplied effective schema set and dialect.
    /// </summary>
    /// <param name="effectiveSchemaSet">The normalized effective schema set payload.</param>
    /// <param name="dialect">The target SQL dialect.</param>
    /// <param name="dialectRules">The shared dialect rules used during derivation.</param>
    /// <param name="extractInputsStep">Optional extractor to populate per-resource inputs.</param>
    public RelationalModelSetBuilderContext(
        EffectiveSchemaSet effectiveSchemaSet,
        SqlDialect dialect,
        ISqlDialectRules dialectRules,
        IRelationalModelBuilderStep? extractInputsStep = null
    )
    {
        ArgumentNullException.ThrowIfNull(effectiveSchemaSet);
        ArgumentNullException.ThrowIfNull(dialectRules);

        EffectiveSchemaSet = effectiveSchemaSet;
        Dialect = dialect;
        DialectRules = dialectRules;
        _extractInputsStep = extractInputsStep ?? new ExtractInputsStep();

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
        RelationalModelSetValidation.ValidateSubclassJsonSchemaForInsertPresence(effectiveSchemaSet);
        RelationalModelSetValidation.ValidateSubclassSuperclassIdentityJsonPathMappings(
            effectiveSchemaSet,
            effectiveResources.IsAbstractByResource
        );
        _resourceKeysByResource = effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.ToDictionary(
            entry => entry.Resource
        );

        var projectSchemaBundle = new ProjectSchemaNormalizer().Normalize(effectiveSchemaSet);

        _projectsInEndpointOrder = projectSchemaBundle.ProjectSchemas;
        _projectSchemasInEndpointOrder = projectSchemaBundle.ProjectSchemaInfos.ToList();
        _projectSchemasInEndpointOrderView = _projectSchemasInEndpointOrder.AsReadOnly();
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
    /// Collision detector used for name overrides prior to dialect shortening.
    /// </summary>
    internal OverrideCollisionDetector OverrideCollisionDetector { get; } = new();

    /// <summary>
    /// Project schemas ordered by endpoint name, with physical schema normalization applied.
    /// </summary>
    public IReadOnlyList<ProjectSchemaInfo> ProjectSchemasInEndpointOrder =>
        _projectSchemasInEndpointOrderView;

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
    /// Clears cached strict unified-alias metadata.
    /// </summary>
    internal void ClearStrictUnifiedAliasTableMetadataCache()
    {
        _strictUnifiedAliasTableMetadataByTable.Clear();
    }

    /// <summary>
    /// Attempts to resolve strict unified-alias metadata for the requested table.
    /// </summary>
    internal bool TryGetStrictUnifiedAliasTableMetadata(
        DbTableModel table,
        [NotNullWhen(true)] out UnifiedAliasStorageResolver.TableMetadata? metadata
    )
    {
        ArgumentNullException.ThrowIfNull(table);

        if (!_strictUnifiedAliasTableMetadataByTable.TryGetValue(table.Table, out var entries))
        {
            metadata = null;
            return false;
        }

        foreach (var entry in entries)
        {
            if (
                entry.JsonScope.Equals(table.JsonScope)
                && entry.Columns.Count == table.Columns.Count
                && entry.Columns.SequenceEqual(table.Columns)
            )
            {
                metadata = entry.Metadata;
                return true;
            }
        }

        metadata = null;
        return false;
    }

    /// <summary>
    /// Caches strict unified-alias metadata for a table.
    /// </summary>
    internal void SetStrictUnifiedAliasTableMetadata(
        DbTableModel table,
        UnifiedAliasStorageResolver.TableMetadata metadata
    )
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(metadata);

        if (!_strictUnifiedAliasTableMetadataByTable.TryGetValue(table.Table, out var entries))
        {
            entries = [];
            _strictUnifiedAliasTableMetadataByTable[table.Table] = entries;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            if (
                entry.JsonScope.Equals(table.JsonScope)
                && entry.Columns.Count == table.Columns.Count
                && entry.Columns.SequenceEqual(table.Columns)
            )
            {
                entries[index] = new StrictUnifiedAliasTableMetadataEntry(
                    table.JsonScope,
                    table.Columns,
                    metadata
                );
                return;
            }
        }

        entries.Add(new StrictUnifiedAliasTableMetadataEntry(table.JsonScope, table.Columns, metadata));
    }

    private sealed record StrictUnifiedAliasTableMetadataEntry(
        JsonPathExpression JsonScope,
        IReadOnlyList<DbColumnModel> Columns,
        UnifiedAliasStorageResolver.TableMetadata Metadata
    );

    /// <summary>
    /// Enumerates projects in canonical endpoint order.
    /// </summary>
    public IEnumerable<ProjectSchemaContext> EnumerateProjectsInEndpointOrder()
    {
        return _projectsInEndpointOrder;
    }

    /// <summary>
    /// Updates project schema info entries in endpoint order.
    /// </summary>
    /// <param name="update">The update callback for each project schema info.</param>
    internal void UpdateProjectSchemasInEndpointOrder(Func<ProjectSchemaInfo, ProjectSchemaInfo> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        for (var index = 0; index < _projectSchemasInEndpointOrder.Count; index++)
        {
            var current = _projectSchemasInEndpointOrder[index];
            var updated = update(current);

            if (updated.Equals(current))
            {
                continue;
            }

            _projectSchemasInEndpointOrder[index] = updated;
            UpdateExtensionProjectCache(current, updated);
        }
    }

    /// <summary>
    /// Updates a single project schema entry by endpoint name.
    /// </summary>
    /// <param name="projectEndpointName">The project endpoint name to update.</param>
    /// <param name="update">The update callback.</param>
    internal void UpdateProjectSchema(
        string projectEndpointName,
        Func<ProjectSchemaInfo, ProjectSchemaInfo> update
    )
    {
        if (!TryUpdateProjectSchema(projectEndpointName, update))
        {
            throw new InvalidOperationException($"Project schema '{projectEndpointName}' was not found.");
        }
    }

    /// <summary>
    /// Attempts to update a single project schema entry by endpoint name.
    /// </summary>
    /// <param name="projectEndpointName">The project endpoint name to update.</param>
    /// <param name="update">The update callback.</param>
    /// <returns>True when the project schema was found and updated.</returns>
    internal bool TryUpdateProjectSchema(
        string projectEndpointName,
        Func<ProjectSchemaInfo, ProjectSchemaInfo> update
    )
    {
        if (string.IsNullOrWhiteSpace(projectEndpointName))
        {
            throw new ArgumentException(
                "Project endpoint name must be non-empty.",
                nameof(projectEndpointName)
            );
        }

        ArgumentNullException.ThrowIfNull(update);

        for (var index = 0; index < _projectSchemasInEndpointOrder.Count; index++)
        {
            var schema = _projectSchemasInEndpointOrder[index];

            if (!string.Equals(schema.ProjectEndpointName, projectEndpointName, StringComparison.Ordinal))
            {
                continue;
            }

            var updated = update(schema);

            if (!updated.Equals(schema))
            {
                _projectSchemasInEndpointOrder[index] = updated;
                UpdateExtensionProjectCache(schema, updated);
            }

            return true;
        }

        return false;
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
            OverrideCollisionDetector = OverrideCollisionDetector,
        };

        _extractInputsStep.Execute(builderContext);

        _builderContextsByResource[resource] = builderContext;

        return builderContext;
    }

    /// <summary>
    /// Registers a prebuilt builder context for the supplied resource.
    /// </summary>
    /// <param name="resource">The resource identifier.</param>
    /// <param name="builderContext">The builder context to cache.</param>
    public void RegisterResourceBuilderContext(
        QualifiedResourceName resource,
        RelationalModelBuilderContext builderContext
    )
    {
        ArgumentNullException.ThrowIfNull(builderContext);

        if (_builderContextsByResource.ContainsKey(resource))
        {
            throw new InvalidOperationException(
                $"Builder context already registered for resource '{FormatResource(resource)}'."
            );
        }

        _builderContextsByResource[resource] = builderContext;
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
        ValidateNameOverridesConsumed();

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
            .OrderBy(trigger => trigger.TriggerTable.Schema.Value, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.TriggerTable.Name, StringComparer.Ordinal)
            .ThenBy(trigger => trigger.Name.Value, StringComparer.Ordinal)
            .ToArray();

        OverrideCollisionDetector.ThrowIfCollisions();

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
    /// Validates that all declared name overrides were applied during derivation.
    /// </summary>
    private void ValidateNameOverridesConsumed()
    {
        foreach (var entry in _builderContextsByResource)
        {
            var resource = entry.Key;
            var builderContext = entry.Value;
            var unused = builderContext.GetUnusedNameOverrides();

            if (unused.Count == 0)
            {
                continue;
            }

            var resourceLabel = FormatResource(resource);
            var details = string.Join(
                ", ",
                unused.Select(overrideEntry =>
                    $"'{overrideEntry.RawKey}' (canonical '{overrideEntry.CanonicalPath}')"
                )
            );

            throw new InvalidOperationException(
                $"relational.nameOverrides entries did not match any derived columns or collection scopes "
                    + $"on resource '{resourceLabel}': {details}."
            );
        }
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
    /// Ensures that derived index names are unique within the dialect's required scope.
    /// </summary>
    private void ValidateIndexNameUniqueness()
    {
        var duplicateIndexKeys = IndexInventory
            .GroupBy(index => BuildIndexUniquenessKey(index))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key.Schema, StringComparer.Ordinal)
            .ThenBy(key => key.TableOrEmpty, StringComparer.Ordinal)
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
    /// Ensures that derived trigger names are unique within the dialect's required scope.
    /// </summary>
    private void ValidateTriggerNameUniqueness()
    {
        var duplicateTriggerKeys = TriggerInventory
            .GroupBy(trigger => BuildTriggerUniquenessKey(trigger))
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key.Schema, StringComparer.Ordinal)
            .ThenBy(key => key.TableOrEmpty, StringComparer.Ordinal)
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
    private readonly record struct NamedObjectKey(string Schema, string? Table, string Name)
    {
        /// <summary>
        /// Gets the table portion for ordering (empty string when schema-scoped).
        /// </summary>
        public string TableOrEmpty => Table ?? string.Empty;

        /// <summary>
        /// Formats the key for diagnostics.
        /// </summary>
        /// <returns>A formatted key string.</returns>
        public string Format()
        {
            return string.IsNullOrWhiteSpace(Table) ? $"{Schema}:{Name}" : $"{Schema}.{Table}:{Name}";
        }

        /// <summary>
        /// Builds a schema-scoped uniqueness key.
        /// </summary>
        public static NamedObjectKey ForSchema(string schema, string name)
        {
            return new NamedObjectKey(schema, null, name);
        }

        /// <summary>
        /// Builds a table-scoped uniqueness key.
        /// </summary>
        public static NamedObjectKey ForTable(string schema, string table, string name)
        {
            return new NamedObjectKey(schema, table, name);
        }
    }

    /// <summary>
    /// Builds the uniqueness key used to detect index-name collisions based on dialect scoping rules.
    /// </summary>
    private NamedObjectKey BuildIndexUniquenessKey(DbIndexInfo index)
    {
        var schema = index.Table.Schema.Value;
        var name = index.Name.Value;

        return Dialect switch
        {
            SqlDialect.Pgsql => NamedObjectKey.ForSchema(schema, name),
            SqlDialect.Mssql => NamedObjectKey.ForTable(schema, index.Table.Name, name),
            _ => NamedObjectKey.ForSchema(schema, name),
        };
    }

    /// <summary>
    /// Builds the uniqueness key used to detect trigger-name collisions based on dialect scoping rules.
    /// </summary>
    private NamedObjectKey BuildTriggerUniquenessKey(DbTriggerInfo trigger)
    {
        var schema = trigger.TriggerTable.Schema.Value;
        var name = trigger.Name.Value;

        return Dialect switch
        {
            SqlDialect.Pgsql => NamedObjectKey.ForTable(schema, trigger.TriggerTable.Name, name),
            SqlDialect.Mssql => NamedObjectKey.ForSchema(schema, name),
            _ => NamedObjectKey.ForSchema(schema, name),
        };
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
            .Where(project => ExtensionProjectKeyComparer.Equals(project.ProjectEndpointName, projectKey))
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
            .Where(project => ExtensionProjectKeyComparer.Equals(project.ProjectName, projectKey))
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

    /// <summary>
    /// Updates cached extension-project key lookups when a project schema entry is replaced in the builder context.
    /// </summary>
    private void UpdateExtensionProjectCache(ProjectSchemaInfo current, ProjectSchemaInfo updated)
    {
        if (ReferenceEquals(current, updated) || _extensionProjectsByKey.Count == 0)
        {
            return;
        }

        List<string>? updateKeys = null;

        foreach (var entry in _extensionProjectsByKey)
        {
            if (!ReferenceEquals(entry.Value, current))
            {
                continue;
            }

            updateKeys ??= new List<string>();
            updateKeys.Add(entry.Key);
        }

        if (updateKeys is null)
        {
            return;
        }

        foreach (var key in updateKeys)
        {
            _extensionProjectsByKey[key] = updated;
        }
    }
}
