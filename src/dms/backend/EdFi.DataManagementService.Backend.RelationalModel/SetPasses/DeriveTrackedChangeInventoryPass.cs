// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Build.Steps.ExtractInputs;
using EdFi.DataManagementService.Core.External.Model;
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;
using static EdFi.DataManagementService.Backend.RelationalModel.SetPasses.IdentityProjectionResolver;

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Derives the tracked-change table inventory (<c>tracked_changes_*</c>) into
/// <see cref="RelationalModelSetBuilderContext.TrackedChangeInventory"/> and attaches
/// <see cref="TriggerKindParameters.DocumentStamping.ChangeTracking"/> to the owning stamping triggers.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="TrackedChangeTableInfo"/> is derived per <see cref="ResourceStorageKind.RelationalTables"/>
/// resource (including concrete subclasses of abstract resources, each as its own table), plus a single
/// shared descriptor table covering every <see cref="ResourceStorageKind.SharedDescriptorTable"/> resource
/// when any descriptor exists.
/// </para>
/// <para>
/// Value columns combine the resource's identity paths and securable-element paths into <c>Old_</c>/<c>New_</c>
/// pairs. Descriptor reference paths materialize <c>Namespace</c>/<c>CodeValue</c> and reference a table-level
/// descriptor join; Student/Contact/Staff securable paths materialize the person <c>DocumentId</c> and
/// reference a table-level person join. Key-unification canonical columns are de-duplicated, merging origin
/// flags and widening nullability.
/// </para>
/// <para>
/// This pass owns semantic derivation only; dialect emitters render the inventory mechanically (DMS-1177).
/// </para>
/// </remarks>
public sealed class DeriveTrackedChangeInventoryPass : IRelationalModelSetPass
{
    private const string OldPrefix = "Old_";
    private const string NewPrefix = "New_";
    private const string DocumentIdSuffix = "_DocumentId";
    private const string DescriptorIdSuffix = "_DescriptorId";
    private const string NamespaceSuffix = "_Namespace";
    private const string CodeValueSuffix = "_CodeValue";

    private static readonly DbColumnName _idColumn = new("Id");
    private static readonly DbColumnName _changeVersionColumn = new("ChangeVersion");
    private static readonly DbColumnName _createdAtColumn = new("CreatedAt");
    private static readonly DbColumnName _discriminatorColumn = new("Discriminator");

    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resourceLookup = PersonJoinPathResolver.BuildResourceLookup(context.ConcreteResourcesInNameOrder);

        var abstractResources = context
            .AbstractIdentityTablesInNameOrder.Select(table => table.AbstractResourceKey.Resource)
            .ToHashSet();

        // Resolve the descriptor Namespace/CodeValue scalar types from the shared descriptor model once.
        var descriptorValueTypes = ResolveDescriptorValueTypes(context);

        // Per-resource tracked-change tables (regular + concrete abstract).
        foreach (var resourceContext in context.EnumerateConcreteResourceSchemasInNameOrder())
        {
            if (IsResourceExtension(resourceContext))
            {
                continue;
            }

            var resource = new QualifiedResourceName(
                resourceContext.Project.ProjectSchema.ProjectName,
                resourceContext.ResourceName
            );

            if (!TryGetConcreteResource(context, resource, out var concreteModel))
            {
                continue;
            }

            if (concreteModel.StorageKind != ResourceStorageKind.RelationalTables)
            {
                continue;
            }

            var builderContext = context.GetOrCreateResourceBuilderContext(resourceContext);
            var kind = IsConcreteAbstract(resourceContext, abstractResources)
                ? TrackedChangeTableKind.ConcreteAbstract
                : TrackedChangeTableKind.Resource;

            var trackedTable = BuildResourceTrackedChangeTable(
                concreteModel,
                builderContext,
                resource,
                kind,
                resourceLookup,
                descriptorValueTypes
            );

            context.TrackedChangeInventory.Add(trackedTable);
            AttachChangeTracking(context, concreteModel.RelationalModel.Root.Table, trackedTable.Table);
        }

        // Single shared descriptor tracked-change table when any descriptor resource exists.
        var sharedDescriptorTable = BuildSharedDescriptorTrackedChangeTable(context, descriptorValueTypes);
        if (sharedDescriptorTable is not null)
        {
            context.TrackedChangeInventory.Add(sharedDescriptorTable);
            AttachChangeTracking(context, DescriptorTableName, sharedDescriptorTable.Table);
        }
    }

    /// <summary>
    /// Builds the tracked-change table for a single relational-storage resource.
    /// </summary>
    private static TrackedChangeTableInfo BuildResourceTrackedChangeTable(
        ConcreteResourceModel concreteModel,
        RelationalModelBuilderContext builderContext,
        QualifiedResourceName resource,
        TrackedChangeTableKind kind,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        (RelationalScalarType Namespace, RelationalScalarType CodeValue) descriptorValueTypes
    )
    {
        var resourceModel = concreteModel.RelationalModel;
        var root = resourceModel.Root;

        // Merge identity + securable origins per canonical source path.
        var originByPath = BuildOriginByPath(builderContext, concreteModel);

        // Resolve identity paths to their stored columns via the shared resolver, which handles
        // reference-component identity (locally stored identity-part columns) that a plain SourceJsonPath
        // match would miss. A single identity path can map to multiple binding columns under key
        // unification (e.g. Section's $.courseOfferingReference.schoolId binds both the CourseOffering and
        // Session school columns); those are unification variants that ResolveScalarColumnModel unwraps to
        // the same canonical storage column, so taking the first binding yields one correct value column.
        var identityColumnByPath = BuildIdentityElementMappings(resourceModel, builderContext, resource)
            .GroupBy(mapping => mapping.IdentityJsonPath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Column, StringComparer.Ordinal);

        var valueColumns = new List<TrackedChangeColumnInfo>();
        var valueColumnsByOldName = new Dictionary<string, int>(StringComparer.Ordinal);
        var descriptorJoins = new Dictionary<string, TrackedChangeDescriptorJoinInfo>(StringComparer.Ordinal);
        var personJoins = new Dictionary<string, TrackedChangePersonJoinInfo>(StringComparer.Ordinal);

        var descriptorEdgesByPath = resourceModel
            .DescriptorEdgeSources.GroupBy(edge => edge.DescriptorValuePath.Canonical, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var unresolvedPaths = new List<string>();

        foreach (var (path, origin) in originByPath)
        {
            if (descriptorEdgesByPath.TryGetValue(path, out var descriptorEdge))
            {
                AddDescriptorColumns(
                    descriptorEdge,
                    path,
                    origin,
                    root,
                    descriptorValueTypes,
                    valueColumns,
                    valueColumnsByOldName,
                    descriptorJoins
                );
                continue;
            }

            var resolved = ResolveScalarColumnModel(resourceModel, root, path, identityColumnByPath);
            if (resolved is { } scalar)
            {
                AddScalarColumn(scalar, path, origin, valueColumns, valueColumnsByOldName);
            }
            else
            {
                // Every identity and securable (EdOrg / Namespace) path is expected to resolve to a
                // stored scalar column. Failing loudly mirrors DeriveAuthorizationIndexInventoryPass and
                // prevents an authorization-incomplete tracked-change table from being derived silently.
                unresolvedPaths.Add(path);
            }
        }

        if (unresolvedPaths.Count > 0)
        {
            throw new InvalidOperationException(
                $"Tracked-change derivation for resource '{resource.ProjectName}.{resource.ResourceName}' "
                    + "could not resolve identity/securable path(s) to a stored column: "
                    + string.Join(
                        ", ",
                        unresolvedPaths
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(p => p, StringComparer.Ordinal)
                    )
                    + "."
            );
        }

        // Person securable paths (Student / Contact / Staff) -> person DocumentId columns.
        AddPersonColumns(concreteModel, resourceLookup, valueColumns, valueColumnsByOldName, personJoins);

        return new TrackedChangeTableInfo(
            BuildTrackedChangeTableName(resourceModel.PhysicalSchema, root.Table.Name),
            kind,
            root.Table,
            valueColumns,
            BuildSystemColumns(includeDiscriminator: false),
            [_changeVersionColumn],
            descriptorJoins.Values.OrderBy(join => join.DescriptorJoinName, StringComparer.Ordinal).ToArray(),
            personJoins.Values.OrderBy(join => join.PersonJoinName, StringComparer.Ordinal).ToArray()
        );
    }

    /// <summary>
    /// Builds the single shared descriptor tracked-change table, or <c>null</c> when no descriptor exists.
    /// </summary>
    private static TrackedChangeTableInfo? BuildSharedDescriptorTrackedChangeTable(
        RelationalModelSetBuilderContext context,
        (RelationalScalarType Namespace, RelationalScalarType CodeValue) descriptorValueTypes
    )
    {
        var descriptorResource = context.ConcreteResourcesInNameOrder.Find(resource =>
            resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
        );

        if (descriptorResource is null)
        {
            return null;
        }

        // The shared descriptor table tracks its own Namespace/CodeValue identity. Namespace is also a
        // securable element on descriptor resources, so it carries both origins when any descriptor
        // declares it; CodeValue is identity-only.
        var valueColumns = new[]
        {
            BuildValueColumn(
                "Namespace",
                "$.namespace",
                descriptorValueTypes.Namespace,
                isOldNullable: false,
                TrackedChangeColumnRole.Scalar,
                DescriptorNamespaceOrigin(context)
            ),
            BuildValueColumn(
                "CodeValue",
                "$.codeValue",
                descriptorValueTypes.CodeValue,
                isOldNullable: false,
                TrackedChangeColumnRole.Scalar,
                TrackedChangeColumnOrigin.Identity
            ),
        };

        return new TrackedChangeTableInfo(
            BuildTrackedChangeTableName(
                ResolveSharedDescriptorSchema(context, descriptorResource),
                "Descriptor"
            ),
            TrackedChangeTableKind.SharedDescriptor,
            DescriptorTableName,
            valueColumns,
            BuildSystemColumns(includeDiscriminator: true),
            [_changeVersionColumn],
            [],
            []
        );
    }

    /// <summary>
    /// Computes the shared descriptor <c>Namespace</c> column origin: always identity, plus securable element
    /// when any descriptor resource declares <c>$.namespace</c> as a namespace securable element.
    /// </summary>
    private static TrackedChangeColumnOrigin DescriptorNamespaceOrigin(
        RelationalModelSetBuilderContext context
    )
    {
        var origin = TrackedChangeColumnOrigin.Identity;

        foreach (var descriptor in context.ConcreteResourcesInNameOrder)
        {
            if (descriptor.StorageKind != ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            if (descriptor.SecurableElements.Namespace.Contains("$.namespace", StringComparer.Ordinal))
            {
                origin |= TrackedChangeColumnOrigin.SecurableElement;
                break;
            }
        }

        return origin;
    }

    /// <summary>
    /// Resolves the physical schema for the single shared descriptor tracked-change table. Per
    /// <c>change-queries.md</c>, every descriptor — including extension descriptors — is tracked in the
    /// core project's schema (<c>tracked_changes_edfi.Descriptor</c>), so the schema is taken from the core
    /// Ed-Fi descriptor rather than whichever descriptor sorts first in endpoint order (an extension whose
    /// endpoint name sorts ahead of <c>ed-fi</c> would otherwise mis-target <c>tracked_changes_&lt;ext&gt;</c>).
    /// Falls back to the supplied descriptor only in the degenerate case of extension-only descriptors.
    /// </summary>
    private static DbSchemaName ResolveSharedDescriptorSchema(
        RelationalModelSetBuilderContext context,
        ConcreteResourceModel fallbackDescriptor
    )
    {
        var coreDescriptor = context.ConcreteResourcesInNameOrder.Find(resource =>
            resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
            && DataModelConstants.IsCoreProjectName(resource.ResourceKey.Resource.ProjectName)
        );

        return (coreDescriptor ?? fallbackDescriptor).RelationalModel.PhysicalSchema;
    }

    /// <summary>
    /// Collects the identity and securable (EdOrg + Namespace) source paths with merged origin flags.
    /// Person securable paths are handled separately.
    /// </summary>
    private static Dictionary<string, TrackedChangeColumnOrigin> BuildOriginByPath(
        RelationalModelBuilderContext builderContext,
        ConcreteResourceModel concreteModel
    )
    {
        var originByPath = new Dictionary<string, TrackedChangeColumnOrigin>(StringComparer.Ordinal);

        void Merge(string path, TrackedChangeColumnOrigin origin)
        {
            originByPath[path] = originByPath.TryGetValue(path, out var existing)
                ? existing | origin
                : origin;
        }

        foreach (var identityPath in builderContext.IdentityJsonPaths)
        {
            Merge(identityPath.Canonical, TrackedChangeColumnOrigin.Identity);
        }

        var securable = concreteModel.SecurableElements;
        foreach (var edOrg in securable.EducationOrganization)
        {
            Merge(edOrg.JsonPath, TrackedChangeColumnOrigin.SecurableElement);
        }

        foreach (var namespacePath in securable.Namespace)
        {
            Merge(namespacePath, TrackedChangeColumnOrigin.SecurableElement);
        }

        return originByPath;
    }

    /// <summary>
    /// A scalar source column resolved to its stored model, tracking whether key unification was unwrapped
    /// so the canonical storage column can be recorded on the tracked-change column.
    /// </summary>
    private readonly record struct ResolvedScalarColumn(DbColumnModel Column, bool IsUnified);

    /// <summary>
    /// Resolves a canonical source path to its stored (canonical, unified-alias-unwrapped) scalar column.
    /// Prefers the identity resolver's column when the path is an identity path (handles reference-component
    /// identity); otherwise matches a column by <see cref="DbColumnModel.SourceJsonPath"/>.
    /// </summary>
    private static ResolvedScalarColumn? ResolveScalarColumnModel(
        RelationalResourceModel resourceModel,
        DbTableModel root,
        string canonicalPath,
        IReadOnlyDictionary<string, DbColumnName> identityColumnByPath
    )
    {
        if (identityColumnByPath.TryGetValue(canonicalPath, out var identityColumn))
        {
            var rootColumn = root.Columns.FirstOrDefault(column => column.ColumnName.Equals(identityColumn));
            if (rootColumn is not null)
            {
                return ResolveCanonicalColumnModel(rootColumn, root);
            }
        }

        foreach (var table in resourceModel.TablesInDependencyOrder)
        {
            var match = table.Columns.FirstOrDefault(column =>
                column.SourceJsonPath is { } sourcePath && sourcePath.Canonical == canonicalPath
            );

            if (match is not null)
            {
                return ResolveCanonicalColumnModel(match, table);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a column model to its canonical stored column model when it is a unified alias, reporting
    /// whether the alias was unwrapped so the caller can record the canonical storage column.
    /// </summary>
    private static ResolvedScalarColumn ResolveCanonicalColumnModel(
        DbColumnModel columnModel,
        DbTableModel table
    )
    {
        if (columnModel.Storage is ColumnStorage.UnifiedAlias alias)
        {
            var canonical = table.Columns.FirstOrDefault(column =>
                column.ColumnName.Equals(alias.CanonicalColumn)
            );
            if (canonical is not null)
            {
                return new ResolvedScalarColumn(canonical, IsUnified: true);
            }
        }

        return new ResolvedScalarColumn(columnModel, IsUnified: false);
    }

    private static void AddScalarColumn(
        ResolvedScalarColumn resolved,
        string sourcePath,
        TrackedChangeColumnOrigin origin,
        List<TrackedChangeColumnInfo> valueColumns,
        Dictionary<string, int> valueColumnsByOldName
    )
    {
        var columnModel = resolved.Column;
        var canonicalStorageColumn = resolved.IsUnified ? columnModel.ColumnName : (DbColumnName?)null;

        var entry = BuildValueColumn(
            columnModel.ColumnName.Value,
            sourcePath,
            columnModel.ScalarType ?? new RelationalScalarType(ScalarKind.String),
            columnModel.IsNullable,
            TrackedChangeColumnRole.Scalar,
            origin
        ) with
        {
            CanonicalStorageColumn = canonicalStorageColumn,
        };

        MergeOrAdd(entry, valueColumns, valueColumnsByOldName);
    }

    private static void AddDescriptorColumns(
        DescriptorEdgeSource descriptorEdge,
        string sourcePath,
        TrackedChangeColumnOrigin origin,
        DbTableModel root,
        (RelationalScalarType Namespace, RelationalScalarType CodeValue) descriptorValueTypes,
        List<TrackedChangeColumnInfo> valueColumns,
        Dictionary<string, int> valueColumnsByOldName,
        Dictionary<string, TrackedChangeDescriptorJoinInfo> descriptorJoins
    )
    {
        var baseName = TrimSuffix(descriptorEdge.FkColumn.Value, DescriptorIdSuffix);
        var joinName = baseName;

        descriptorJoins.TryAdd(
            joinName,
            new TrackedChangeDescriptorJoinInfo(
                joinName,
                descriptorEdge.FkColumn,
                descriptorEdge.DescriptorResource
            )
        );

        // Descriptor identity/securable values are not null on the source row; tombstones leave New_* null.
        var isOldNullable = FindColumnNullable(root, descriptorEdge.FkColumn) ?? false;

        var namespaceColumn = BuildValueColumn(
            baseName + NamespaceSuffix,
            sourcePath,
            descriptorValueTypes.Namespace,
            isOldNullable,
            TrackedChangeColumnRole.DescriptorNamespace,
            origin
        ) with
        {
            DescriptorJoinName = joinName,
        };

        var codeValueColumn = BuildValueColumn(
            baseName + CodeValueSuffix,
            sourcePath,
            descriptorValueTypes.CodeValue,
            isOldNullable,
            TrackedChangeColumnRole.DescriptorCodeValue,
            origin
        ) with
        {
            DescriptorJoinName = joinName,
        };

        MergeOrAdd(namespaceColumn, valueColumns, valueColumnsByOldName);
        MergeOrAdd(codeValueColumn, valueColumns, valueColumnsByOldName);
    }

    private static void AddPersonColumns(
        ConcreteResourceModel concreteModel,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<TrackedChangeColumnInfo> valueColumns,
        Dictionary<string, int> valueColumnsByOldName,
        Dictionary<string, TrackedChangePersonJoinInfo> personJoins
    )
    {
        var securable = concreteModel.SecurableElements;

        AddPersonKindColumns(
            concreteModel,
            securable.Student,
            "Student",
            SecurableElementKind.Student,
            resourceLookup,
            valueColumns,
            valueColumnsByOldName,
            personJoins
        );
        AddPersonKindColumns(
            concreteModel,
            securable.Contact,
            "Contact",
            SecurableElementKind.Contact,
            resourceLookup,
            valueColumns,
            valueColumnsByOldName,
            personJoins
        );
        AddPersonKindColumns(
            concreteModel,
            securable.Staff,
            "Staff",
            SecurableElementKind.Staff,
            resourceLookup,
            valueColumns,
            valueColumnsByOldName,
            personJoins
        );
    }

    private static void AddPersonKindColumns(
        ConcreteResourceModel concreteModel,
        IReadOnlyList<string> personPaths,
        string personResourceName,
        SecurableElementKind personKind,
        IReadOnlyDictionary<QualifiedResourceName, ConcreteResourceModel> resourceLookup,
        List<TrackedChangeColumnInfo> valueColumns,
        Dictionary<string, int> valueColumnsByOldName,
        Dictionary<string, TrackedChangePersonJoinInfo> personJoins
    )
    {
        if (personPaths.Count == 0)
        {
            return;
        }

        foreach (var personPath in personPaths)
        {
            List<string> skipped = [];
            var chain = PersonJoinPathResolver.ResolveShortestPersonChain(
                concreteModel,
                [personPath],
                personResourceName,
                resourceLookup,
                skipped,
                out var unresolvedRootLevelPaths
            );

            if (chain is null || chain.Count == 0)
            {
                // A null/empty chain is expected only for the zero-hop self person identity path (anchored
                // on the resource's own DocumentId, materializing no person-join column) and for array-nested
                // paths (unsupported, accumulated into `skipped`). Any other unresolved root-level path is an
                // authorization-completeness defect — fail loudly, mirroring DeriveAuthorizationIndexInventoryPass
                // and the identity/securable guard in BuildResourceTrackedChangeTable rather than silently
                // dropping the person securable.
                foreach (var unresolvedPath in unresolvedRootLevelPaths)
                {
                    if (
                        !PersonJoinPathResolver.IsSelfPersonIdentityPath(
                            concreteModel.RelationalModel.Resource,
                            personKind,
                            unresolvedPath
                        )
                    )
                    {
                        throw new InvalidOperationException(
                            $"Tracked-change derivation for resource "
                                + $"'{concreteModel.ResourceKey.Resource.ProjectName}."
                                + $"{concreteModel.ResourceKey.Resource.ResourceName}': {personResourceName} "
                                + $"securable path '{unresolvedPath}' could not be resolved to a person join."
                        );
                    }
                }

                continue;
            }

            var joinBaseName = BuildPersonJoinBaseName(chain);
            var joinName = joinBaseName;
            if (personJoins.TryGetValue(joinName, out var existingJoin))
            {
                // Two person paths can legitimately collapse to the same join name when they resolve to the
                // same chain; a name collision over a *different* chain would otherwise silently keep only
                // the first path's join, so reject it explicitly.
                if (existingJoin.PersonKind != personKind || !existingJoin.JoinPath.SequenceEqual(chain))
                {
                    throw new InvalidOperationException(
                        $"Tracked-change derivation for resource "
                            + $"'{concreteModel.ResourceKey.Resource.ProjectName}."
                            + $"{concreteModel.ResourceKey.Resource.ResourceName}': person join name "
                            + $"'{joinName}' resolves to conflicting join paths."
                    );
                }
            }
            else
            {
                personJoins.Add(joinName, new TrackedChangePersonJoinInfo(joinName, personKind, chain));
            }

            // Nullability follows whether the first hop FK is optional (override-driven nullable securables).
            var isOldNullable = FirstStepNullable(concreteModel, chain) ?? false;

            var personColumn = BuildValueColumn(
                joinBaseName + DocumentIdSuffix,
                personPath,
                new RelationalScalarType(ScalarKind.Int64),
                isOldNullable,
                TrackedChangeColumnRole.PersonDocumentId,
                TrackedChangeColumnOrigin.SecurableElement
            ) with
            {
                PersonJoinName = joinName,
            };

            MergeOrAdd(personColumn, valueColumns, valueColumnsByOldName);
        }
    }

    /// <summary>
    /// Builds the person <c>DocumentId</c> column base name by concatenating the <c>_DocumentId</c>-stripped
    /// FK source-column bases along the join chain (e.g. <c>StudentSectionAssociation_Student</c>).
    /// </summary>
    private static string BuildPersonJoinBaseName(IReadOnlyList<ColumnPathStep> chain)
    {
        return string.Join(
            "_",
            chain.Select(step => TrimSuffix(step.SourceColumnName.Value, DocumentIdSuffix))
        );
    }

    /// <summary>
    /// Merges a value column into the accumulator by <c>Old_</c> name: combines origin flags and widens
    /// nullability so a column reached via multiple paths (e.g. identity + securable) loses no information.
    /// </summary>
    private static void MergeOrAdd(
        TrackedChangeColumnInfo entry,
        List<TrackedChangeColumnInfo> valueColumns,
        Dictionary<string, int> valueColumnsByOldName
    )
    {
        if (valueColumnsByOldName.TryGetValue(entry.OldColumnName.Value, out var existingIndex))
        {
            var existing = valueColumns[existingIndex];
            valueColumns[existingIndex] = existing with
            {
                Origin = existing.Origin | entry.Origin,
                IsOldColumnNullable = existing.IsOldColumnNullable || entry.IsOldColumnNullable,
                IsNewColumnNullable = existing.IsNewColumnNullable || entry.IsNewColumnNullable,
            };
            return;
        }

        valueColumnsByOldName[entry.OldColumnName.Value] = valueColumns.Count;
        valueColumns.Add(entry);
    }

    private static TrackedChangeColumnInfo BuildValueColumn(
        string baseColumnName,
        string sourcePath,
        RelationalScalarType scalarType,
        bool isOldNullable,
        TrackedChangeColumnRole role,
        TrackedChangeColumnOrigin origin
    )
    {
        return new TrackedChangeColumnInfo(
            new DbColumnName(OldPrefix + baseColumnName),
            new DbColumnName(NewPrefix + baseColumnName),
            sourcePath,
            CanonicalStorageColumn: null,
            IsOldColumnNullable: isOldNullable,
            // New_* columns are populated only by key-change rows; tombstones leave them null.
            IsNewColumnNullable: true,
            scalarType,
            role,
            origin
        );
    }

    private static IReadOnlyList<TrackedChangeSystemColumnInfo> BuildSystemColumns(bool includeDiscriminator)
    {
        var columns = new List<TrackedChangeSystemColumnInfo>();

        if (includeDiscriminator)
        {
            columns.Add(
                new TrackedChangeSystemColumnInfo(
                    TrackedChangeSystemColumnRole.Discriminator,
                    _discriminatorColumn,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 128),
                    IsNullable: false,
                    IsPrimaryKey: false
                )
            );
        }

        columns.Add(
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.Id,
                _idColumn,
                // uuid / uniqueidentifier has no ScalarKind; the dialect emitter renders it by role.
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            )
        );
        columns.Add(
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.ChangeVersion,
                _changeVersionColumn,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: true
            )
        );
        columns.Add(
            new TrackedChangeSystemColumnInfo(
                TrackedChangeSystemColumnRole.CreatedAt,
                _createdAtColumn,
                new RelationalScalarType(ScalarKind.DateTime),
                IsNullable: false,
                IsPrimaryKey: false
            )
        );

        return columns;
    }

    /// <summary>
    /// Replaces the owning root-table (or shared descriptor) DocumentStamping trigger entry with one carrying
    /// the change-tracking attachment for the derived tracked-change table.
    /// </summary>
    private static void AttachChangeTracking(
        RelationalModelSetBuilderContext context,
        DbTableName sourceTable,
        DbTableName trackedChangeTable
    )
    {
        for (var index = 0; index < context.TriggerInventory.Count; index++)
        {
            var trigger = context.TriggerInventory[index];

            if (
                trigger.Table.Equals(sourceTable)
                && trigger.Parameters is TriggerKindParameters.DocumentStamping
            )
            {
                context.TriggerInventory[index] = trigger with
                {
                    Parameters = new TriggerKindParameters.DocumentStamping(
                        new TrackedChangeAttachment(trackedChangeTable)
                    ),
                };
                return;
            }
        }
    }

    /// <summary>
    /// Resolves the Namespace/CodeValue scalar types from any shared descriptor resource's root columns,
    /// falling back to the ODS-conventional lengths when none is available.
    /// </summary>
    private static (
        RelationalScalarType Namespace,
        RelationalScalarType CodeValue
    ) ResolveDescriptorValueTypes(RelationalModelSetBuilderContext context)
    {
        var descriptorResource = context.ConcreteResourcesInNameOrder.Find(resource =>
            resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
        );

        var namespaceType = new RelationalScalarType(ScalarKind.String, MaxLength: 255);
        var codeValueType = new RelationalScalarType(ScalarKind.String, MaxLength: 50);

        if (descriptorResource is not null)
        {
            var columns = descriptorResource.RelationalModel.Root.Columns;
            namespaceType = FindColumnScalarType(columns, "Namespace") ?? namespaceType;
            codeValueType = FindColumnScalarType(columns, "CodeValue") ?? codeValueType;
        }

        return (namespaceType, codeValueType);
    }

    private static RelationalScalarType? FindColumnScalarType(
        IReadOnlyList<DbColumnModel> columns,
        string columnName
    )
    {
        return columns
            .FirstOrDefault(column =>
                string.Equals(column.ColumnName.Value, columnName, StringComparison.Ordinal)
            )
            ?.ScalarType;
    }

    private static bool? FindColumnNullable(DbTableModel table, DbColumnName columnName)
    {
        return table.Columns.FirstOrDefault(column => column.ColumnName.Equals(columnName))?.IsNullable;
    }

    private static bool? FirstStepNullable(
        ConcreteResourceModel concreteModel,
        IReadOnlyList<ColumnPathStep> chain
    )
    {
        var firstStep = chain[0];
        var sourceTable = concreteModel.RelationalModel.TablesInDependencyOrder.FirstOrDefault(table =>
            table.Table.Equals(firstStep.SourceTable)
        );

        return sourceTable is null ? null : FindColumnNullable(sourceTable, firstStep.SourceColumnName);
    }

    private static DbTableName BuildTrackedChangeTableName(DbSchemaName projectSchema, string tableName)
    {
        return new DbTableName(new DbSchemaName($"tracked_changes_{projectSchema.Value}"), tableName);
    }

    private static bool IsConcreteAbstract(
        ConcreteResourceSchemaContext resourceContext,
        IReadOnlySet<QualifiedResourceName> abstractResources
    )
    {
        var isSubclass = ApiSchemaNodeRequirements.TryGetOptionalBoolean(
            resourceContext.ResourceSchema,
            "isSubclass",
            defaultValue: false
        );

        if (!isSubclass)
        {
            return false;
        }

        // Subclass resources always carry superclass identifiers; RequireString is safe here.
        var superclassProjectName = RequireString(resourceContext.ResourceSchema, "superclassProjectName");
        var superclassResourceName = RequireString(resourceContext.ResourceSchema, "superclassResourceName");

        return abstractResources.Contains(
            new QualifiedResourceName(superclassProjectName, superclassResourceName)
        );
    }

    private static bool TryGetConcreteResource(
        RelationalModelSetBuilderContext context,
        QualifiedResourceName resource,
        out ConcreteResourceModel concreteModel
    )
    {
        concreteModel = context.ConcreteResourcesInNameOrder.Find(model =>
            model.ResourceKey.Resource.Equals(resource)
        )!;
        return concreteModel is not null;
    }

    private static string TrimSuffix(string value, string suffix)
    {
        return value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;
    }

    private static DbTableName DescriptorTableName { get; } = new(new DbSchemaName("dms"), "Descriptor");
}
