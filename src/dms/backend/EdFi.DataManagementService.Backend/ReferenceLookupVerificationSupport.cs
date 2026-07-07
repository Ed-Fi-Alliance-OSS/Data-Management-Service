// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal sealed record ReferenceLookupVerificationProjection(
    QualifiedResourceName RequestedResource,
    short ResourceKeyId,
    DbTableName SourceTable,
    IReadOnlyList<ReferenceLookupVerificationElement> IdentityElements
);

internal sealed record ReferenceLookupVerificationElement(
    DbColumnName Column,
    string IdentityJsonPath,
    RelationalScalarType ScalarType,
    bool IsDescriptorReference
);

internal sealed class ReferenceLookupVerificationShapeKey(
    IReadOnlyList<ReferenceLookupVerificationResourceShape> resourceShapes,
    int hashCode
) : IEquatable<ReferenceLookupVerificationShapeKey>
{
    private readonly IReadOnlyList<ReferenceLookupVerificationResourceShape> _resourceShapes = resourceShapes;
    private readonly int _hashCode = hashCode;

    public IReadOnlyList<ReferenceLookupVerificationResourceShape> ResourceShapes => _resourceShapes;

    public static ReferenceLookupVerificationShapeKey Create(
        IReadOnlyList<ReferenceLookupRequestEntry> lookups
    )
    {
        ArgumentNullException.ThrowIfNull(lookups);

        Dictionary<QualifiedResourceName, ReferenceLookupVerificationResourceShape> shapeByResource = [];
        List<ReferenceLookupVerificationResourceShape> orderedResourceShapes = [];

        foreach (var lookup in lookups)
        {
            if (shapeByResource.TryGetValue(lookup.RequestedResource, out var existingShape))
            {
                if (!existingShape.HasSameIdentityPathOrder(lookup.RequestedIdentity))
                {
                    throw ReferenceLookupVerificationSupport.CreateIdentityShapeMismatchException(
                        lookup.RequestedResource
                    );
                }

                continue;
            }

            var candidateShape = ReferenceLookupVerificationResourceShape.Create(lookup);
            shapeByResource[lookup.RequestedResource] = candidateShape;
            orderedResourceShapes.Add(candidateShape);
        }

        var resourceShapes = orderedResourceShapes.ToArray();
        var hash = new HashCode();

        foreach (var resourceShape in resourceShapes)
        {
            hash.Add(resourceShape);
        }

        return new ReferenceLookupVerificationShapeKey(resourceShapes, hash.ToHashCode());
    }

    public bool Equals(ReferenceLookupVerificationShapeKey? other)
    {
        if (
            other is null
            || _hashCode != other._hashCode
            || _resourceShapes.Count != other._resourceShapes.Count
        )
        {
            return false;
        }

        for (var index = 0; index < _resourceShapes.Count; index++)
        {
            if (!_resourceShapes[index].Equals(other._resourceShapes[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) =>
        obj is ReferenceLookupVerificationShapeKey other && Equals(other);

    public override int GetHashCode() => _hashCode;
}

internal sealed class ReferenceLookupVerificationResourceShape(
    QualifiedResourceName requestedResource,
    IReadOnlyList<string> identityJsonPaths,
    int hashCode
) : IEquatable<ReferenceLookupVerificationResourceShape>
{
    private readonly QualifiedResourceName _requestedResource = requestedResource;
    private readonly IReadOnlyList<string> _identityJsonPaths = identityJsonPaths;
    private readonly int _hashCode = hashCode;

    public QualifiedResourceName RequestedResource => _requestedResource;

    public IReadOnlyList<string> IdentityJsonPaths => _identityJsonPaths;

    public static ReferenceLookupVerificationResourceShape Create(ReferenceLookupRequestEntry lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var identityJsonPaths = lookup
            .RequestedIdentity.DocumentIdentityElements.Select(static identityElement =>
                identityElement.IdentityJsonPath.Value
            )
            .ToArray();

        var hash = new HashCode();
        hash.Add(lookup.RequestedResource);

        foreach (var identityJsonPath in identityJsonPaths)
        {
            hash.Add(identityJsonPath, StringComparer.Ordinal);
        }

        return new ReferenceLookupVerificationResourceShape(
            lookup.RequestedResource,
            identityJsonPaths,
            hash.ToHashCode()
        );
    }

    public bool HasSameIdentityPathOrder(ReferenceLookupVerificationResourceShape other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return HasSameIdentityPathOrder(other._identityJsonPaths);
    }

    public bool HasSameIdentityPathOrder(DocumentIdentity requestedIdentity)
    {
        ArgumentNullException.ThrowIfNull(requestedIdentity);

        if (_identityJsonPaths.Count != requestedIdentity.DocumentIdentityElements.Length)
        {
            return false;
        }

        for (var index = 0; index < _identityJsonPaths.Count; index++)
        {
            if (
                !StringComparer.Ordinal.Equals(
                    _identityJsonPaths[index],
                    requestedIdentity.DocumentIdentityElements[index].IdentityJsonPath.Value
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private bool HasSameIdentityPathOrder(IReadOnlyList<string> identityJsonPaths)
    {
        if (_identityJsonPaths.Count != identityJsonPaths.Count)
        {
            return false;
        }

        for (var index = 0; index < _identityJsonPaths.Count; index++)
        {
            if (!StringComparer.Ordinal.Equals(_identityJsonPaths[index], identityJsonPaths[index]))
            {
                return false;
            }
        }

        return true;
    }

    public bool Equals(ReferenceLookupVerificationResourceShape? other)
    {
        if (
            other is null
            || _hashCode != other._hashCode
            || !_requestedResource.Equals(other._requestedResource)
            || !HasSameIdentityPathOrder(other)
        )
        {
            return false;
        }

        return true;
    }

    public override bool Equals(object? obj) =>
        obj is ReferenceLookupVerificationResourceShape other && Equals(other);

    public override int GetHashCode() => _hashCode;
}

internal static class ReferenceLookupVerificationSupport
{
    private static readonly ConditionalWeakTable<
        MappingSet,
        ConcurrentDictionary<
            ReferenceLookupVerificationResourceShape,
            ReferenceLookupVerificationProjectionCacheEntry
        >
    > ProjectionByResourceShapeByMappingSet = new();

    public static string BuildExpectedVerificationIdentityKey(
        DocumentIdentity requestedIdentity,
        bool normalizeDescriptorValues = false
    )
    {
        ArgumentNullException.ThrowIfNull(requestedIdentity);

        if (requestedIdentity.DocumentIdentityElements.Length == 0)
        {
            throw new InvalidOperationException(
                "Reference lookup verification requires at least one identity element."
            );
        }

        StringBuilder builder = new();

        for (var index = 0; index < requestedIdentity.DocumentIdentityElements.Length; index++)
        {
            var identityElement = requestedIdentity.DocumentIdentityElements[index];

            if (index > 0)
            {
                builder.Append('#');
            }

            builder.Append(identityElement.IdentityJsonPath.Value);
            builder.Append('=');
            builder.Append(
                normalizeDescriptorValues
                    ? identityElement.IdentityValue.ToLowerInvariant()
                    : identityElement.IdentityValue
            );
        }

        return builder.ToString();
    }

    public static IReadOnlyList<ReferenceLookupVerificationProjection> BuildProjections(
        ReferenceLookupRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MappingSet);

        return BuildProjections(
            request.MappingSet,
            ReferenceLookupVerificationShapeKey.Create(request.Lookups)
        );
    }

    public static ReferenceLookupVerificationShapeKey CreateShapeKey(ReferenceLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ReferenceLookupVerificationShapeKey.Create(request.Lookups);
    }

    public static IReadOnlyList<ReferenceLookupVerificationProjection> BuildProjections(
        MappingSet mappingSet,
        ReferenceLookupVerificationShapeKey shapeKey
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(shapeKey);

        var projectionByResourceShape = ProjectionByResourceShapeByMappingSet.GetValue(
            mappingSet,
            static _ => new ConcurrentDictionary<
                ReferenceLookupVerificationResourceShape,
                ReferenceLookupVerificationProjectionCacheEntry
            >()
        );

        List<ReferenceLookupVerificationProjection> projections = [];

        foreach (var resourceShape in shapeKey.ResourceShapes)
        {
            var projectionEntry = projectionByResourceShape.GetOrAdd(
                resourceShape,
                static (staticResourceShape, staticMappingSet) =>
                    BuildProjectionCacheEntry(staticMappingSet, staticResourceShape),
                mappingSet
            );

            if (projectionEntry.Projection is not null)
            {
                projections.Add(projectionEntry.Projection);
            }
        }

        return projections.ToArray();
    }

    private static ReferenceLookupVerificationProjectionCacheEntry BuildProjectionCacheEntry(
        MappingSet mappingSet,
        ReferenceLookupVerificationResourceShape resourceShape
    )
    {
        return TryBuildProjection(mappingSet, resourceShape, out var projection) && projection is not null
            ? new ReferenceLookupVerificationProjectionCacheEntry(projection)
            : ReferenceLookupVerificationProjectionCacheEntry.Empty;
    }

    private static bool TryBuildProjection(
        MappingSet mappingSet,
        ReferenceLookupVerificationResourceShape resourceShape,
        out ReferenceLookupVerificationProjection? projection
    )
    {
        var requestedResource = resourceShape.RequestedResource;

        if (!mappingSet.ResourceKeyIdByResource.TryGetValue(requestedResource, out var resourceKeyId))
        {
            throw new InvalidOperationException(
                $"Reference lookup verification metadata lookup failed for target '{MappingSetResourceLookupSupport.FormatResource(requestedResource)}': "
                    + "ResourceKeyIdByResource is missing the requested resource."
            );
        }

        if (
            MappingSetResourceLookupSupport.TryGetConcreteResourceModel(
                mappingSet,
                requestedResource,
                out var concreteResourceModel
            )
        )
        {
            if (concreteResourceModel.StorageKind is ResourceStorageKind.SharedDescriptorTable)
            {
                projection = null;
                return false;
            }

            projection = new ReferenceLookupVerificationProjection(
                RequestedResource: requestedResource,
                ResourceKeyId: resourceKeyId,
                SourceTable: concreteResourceModel.RelationalModel.Root.Table,
                IdentityElements: BuildIdentityElements(
                    requestedResource,
                    resourceShape.IdentityJsonPaths,
                    BuildConcreteColumnByPath(concreteResourceModel.RelationalModel.Root.Columns)
                )
            );

            return true;
        }

        if (TryGetAbstractUnionView(mappingSet, requestedResource, out var abstractUnionView))
        {
            projection = new ReferenceLookupVerificationProjection(
                RequestedResource: requestedResource,
                ResourceKeyId: resourceKeyId,
                SourceTable: abstractUnionView.ViewName,
                IdentityElements: BuildIdentityElements(
                    requestedResource,
                    resourceShape.IdentityJsonPaths,
                    BuildAbstractColumnByPath(abstractUnionView.OutputColumnsInSelectOrder)
                )
            );

            return true;
        }

        throw new InvalidOperationException(
            $"Reference lookup verification metadata lookup failed for target '{MappingSetResourceLookupSupport.FormatResource(requestedResource)}': "
                + "the requested resource was not found in ConcreteResourcesInNameOrder or AbstractUnionViewsInNameOrder."
        );
    }

    private static IReadOnlyList<ReferenceLookupVerificationElement> BuildIdentityElements(
        QualifiedResourceName requestedResource,
        IReadOnlyList<string> requestedIdentityPaths,
        IReadOnlyDictionary<string, VerificationSourceColumn> columnByPath
    )
    {
        if (requestedIdentityPaths.Count == 0)
        {
            throw new InvalidOperationException(
                $"Reference lookup verification metadata lookup failed for target '{MappingSetResourceLookupSupport.FormatResource(requestedResource)}': "
                    + "the requested identity is empty."
            );
        }

        return requestedIdentityPaths
            .Select(identityJsonPath =>
            {
                if (!columnByPath.TryGetValue(identityJsonPath, out var sourceColumn))
                {
                    throw new InvalidOperationException(
                        $"Reference lookup verification metadata lookup failed for target '{MappingSetResourceLookupSupport.FormatResource(requestedResource)}': "
                            + $"no authoritative column was found for identity path '{identityJsonPath}'."
                    );
                }

                return new ReferenceLookupVerificationElement(
                    sourceColumn.Column,
                    identityJsonPath,
                    sourceColumn.ScalarType,
                    sourceColumn.IsDescriptorReference
                );
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, VerificationSourceColumn> BuildConcreteColumnByPath(
        IReadOnlyList<DbColumnModel> columns
    )
    {
        Dictionary<string, VerificationSourceColumn> columnByPath = [];

        foreach (var column in columns)
        {
            if (column.SourceJsonPath is not { } sourceJsonPath || column.ScalarType is null)
            {
                continue;
            }

            var candidate = new VerificationSourceColumn(
                column.ColumnName,
                column.ScalarType,
                IsPreferred: column.Storage is ColumnStorage.Stored,
                IsDescriptorReference: column.Kind is ColumnKind.DescriptorFk
            );

            AddPreferredColumn(columnByPath, sourceJsonPath.Canonical, candidate);
        }

        return columnByPath;
    }

    private static IReadOnlyDictionary<string, VerificationSourceColumn> BuildAbstractColumnByPath(
        IReadOnlyList<AbstractUnionViewOutputColumn> outputColumns
    )
    {
        Dictionary<string, VerificationSourceColumn> columnByPath = [];

        foreach (var outputColumn in outputColumns)
        {
            if (outputColumn.SourceJsonPath is not { } sourceJsonPath)
            {
                continue;
            }

            AddPreferredColumn(
                columnByPath,
                sourceJsonPath.Canonical,
                new VerificationSourceColumn(
                    outputColumn.ColumnName,
                    outputColumn.ScalarType,
                    IsPreferred: true,
                    IsDescriptorReference: outputColumn.IsDescriptorReference
                )
            );
        }

        return columnByPath;
    }

    private static void AddPreferredColumn(
        IDictionary<string, VerificationSourceColumn> columnByPath,
        string identityJsonPath,
        VerificationSourceColumn candidate
    )
    {
        if (!columnByPath.TryGetValue(identityJsonPath, out var existingColumn))
        {
            columnByPath[identityJsonPath] = candidate;
            return;
        }

        if (!existingColumn.IsPreferred && candidate.IsPreferred)
        {
            columnByPath[identityJsonPath] = candidate;
        }
    }

    internal static Exception CreateIdentityShapeMismatchException(QualifiedResourceName requestedResource)
    {
        return new InvalidOperationException(
            $"Reference lookup verification metadata lookup failed for target '{MappingSetResourceLookupSupport.FormatResource(requestedResource)}': "
                + "multiple lookup entries for the same resource used different identity path orderings."
        );
    }

    private static bool TryGetAbstractUnionView(
        MappingSet mappingSet,
        QualifiedResourceName requestedResource,
        out AbstractUnionViewInfo abstractUnionView
    )
    {
        foreach (var candidate in mappingSet.Model.AbstractUnionViewsInNameOrder)
        {
            if (candidate.AbstractResourceKey.Resource == requestedResource)
            {
                abstractUnionView = candidate;
                return true;
            }
        }

        abstractUnionView = null!;
        return false;
    }

    private sealed record VerificationSourceColumn(
        DbColumnName Column,
        RelationalScalarType ScalarType,
        bool IsPreferred,
        bool IsDescriptorReference
    );

    private sealed record ReferenceLookupVerificationProjectionCacheEntry(
        ReferenceLookupVerificationProjection? Projection
    )
    {
        public static readonly ReferenceLookupVerificationProjectionCacheEntry Empty = new(
            (ReferenceLookupVerificationProjection?)null
        );
    }
}
