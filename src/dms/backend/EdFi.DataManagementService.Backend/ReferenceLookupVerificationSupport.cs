// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

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
    RelationalScalarType ScalarType
);

internal static class ReferenceLookupVerificationSupport
{
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

            builder.Append('$');
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

        Dictionary<QualifiedResourceName, ReferenceLookupRequestEntry> representativeLookupByResource = [];
        List<QualifiedResourceName> orderedRequestedResources = [];

        foreach (var lookup in request.Lookups)
        {
            if (representativeLookupByResource.TryGetValue(lookup.RequestedResource, out var existingLookup))
            {
                EnsureCompatibleIdentityShape(existingLookup, lookup);
                continue;
            }

            representativeLookupByResource[lookup.RequestedResource] = lookup;
            orderedRequestedResources.Add(lookup.RequestedResource);
        }

        List<ReferenceLookupVerificationProjection> projections = [];

        foreach (var requestedResource in orderedRequestedResources)
        {
            var lookup = representativeLookupByResource[requestedResource];

            if (
                TryBuildProjection(request.MappingSet, requestedResource, lookup, out var projection)
                && projection is not null
            )
            {
                projections.Add(projection);
            }
        }

        return projections;
    }

    private static bool TryBuildProjection(
        MappingSet mappingSet,
        QualifiedResourceName requestedResource,
        ReferenceLookupRequestEntry lookup,
        out ReferenceLookupVerificationProjection? projection
    )
    {
        if (!mappingSet.ResourceKeyIdByResource.TryGetValue(requestedResource, out var resourceKeyId))
        {
            throw new InvalidOperationException(
                $"Reference lookup verification metadata lookup failed for target '{FormatResource(requestedResource)}': "
                    + "ResourceKeyIdByResource is missing the requested resource."
            );
        }

        if (TryGetConcreteResourceModel(mappingSet, requestedResource, out var concreteResourceModel))
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
                    lookup.RequestedIdentity,
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
                    lookup.RequestedIdentity,
                    BuildAbstractColumnByPath(abstractUnionView.OutputColumnsInSelectOrder)
                )
            );

            return true;
        }

        throw new InvalidOperationException(
            $"Reference lookup verification metadata lookup failed for target '{FormatResource(requestedResource)}': "
                + "the requested resource was not found in ConcreteResourcesInNameOrder or AbstractUnionViewsInNameOrder."
        );
    }

    private static IReadOnlyList<ReferenceLookupVerificationElement> BuildIdentityElements(
        QualifiedResourceName requestedResource,
        DocumentIdentity requestedIdentity,
        IReadOnlyDictionary<string, VerificationSourceColumn> columnByPath
    )
    {
        if (requestedIdentity.DocumentIdentityElements.Length == 0)
        {
            throw new InvalidOperationException(
                $"Reference lookup verification metadata lookup failed for target '{FormatResource(requestedResource)}': "
                    + "the requested identity is empty."
            );
        }

        return requestedIdentity
            .DocumentIdentityElements.Select(identityElement =>
            {
                if (!columnByPath.TryGetValue(identityElement.IdentityJsonPath.Value, out var sourceColumn))
                {
                    throw new InvalidOperationException(
                        $"Reference lookup verification metadata lookup failed for target '{FormatResource(requestedResource)}': "
                            + $"no authoritative column was found for identity path '{identityElement.IdentityJsonPath.Value}'."
                    );
                }

                return new ReferenceLookupVerificationElement(
                    sourceColumn.Column,
                    identityElement.IdentityJsonPath.Value,
                    sourceColumn.ScalarType
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
                IsPreferred: column.Storage is ColumnStorage.Stored
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
                    IsPreferred: true
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

    private static void EnsureCompatibleIdentityShape(
        ReferenceLookupRequestEntry existingLookup,
        ReferenceLookupRequestEntry candidateLookup
    )
    {
        if (
            existingLookup.RequestedIdentity.DocumentIdentityElements.Length
            != candidateLookup.RequestedIdentity.DocumentIdentityElements.Length
        )
        {
            throw CreateIdentityShapeMismatchException(existingLookup);
        }

        for (var index = 0; index < existingLookup.RequestedIdentity.DocumentIdentityElements.Length; index++)
        {
            if (
                existingLookup.RequestedIdentity.DocumentIdentityElements[index].IdentityJsonPath.Value
                == candidateLookup.RequestedIdentity.DocumentIdentityElements[index].IdentityJsonPath.Value
            )
            {
                continue;
            }

            throw CreateIdentityShapeMismatchException(existingLookup);
        }
    }

    private static Exception CreateIdentityShapeMismatchException(ReferenceLookupRequestEntry existingLookup)
    {
        return new InvalidOperationException(
            $"Reference lookup verification metadata lookup failed for target '{FormatResource(existingLookup.RequestedResource)}': "
                + "multiple lookup entries for the same resource used different identity path orderings."
        );
    }

    private static bool TryGetConcreteResourceModel(
        MappingSet mappingSet,
        QualifiedResourceName requestedResource,
        out ConcreteResourceModel concreteResourceModel
    )
    {
        foreach (var candidate in mappingSet.Model.ConcreteResourcesInNameOrder)
        {
            if (candidate.ResourceKey.Resource == requestedResource)
            {
                concreteResourceModel = candidate;
                return true;
            }
        }

        concreteResourceModel = null!;
        return false;
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

    private static string FormatResource(QualifiedResourceName resource) =>
        $"{resource.ProjectName}.{resource.ResourceName}";

    private sealed record VerificationSourceColumn(
        DbColumnName Column,
        RelationalScalarType ScalarType,
        bool IsPreferred
    );
}
