// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalReadTargetLookupService
{
    Task<RelationalReadTargetLookupResult> ResolveForGetByIdAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    );

    Task<RelationalReadTargetLookupResult> ResolveForCacheCandidateAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    );
}

public abstract record RelationalReadTargetLookupResult
{
    private RelationalReadTargetLookupResult() { }

    public sealed record ExistingDocument(long DocumentId, DocumentUuid DocumentUuid, long ContentVersion)
        : RelationalReadTargetLookupResult;

    public sealed record ExistingDocumentWithTimestamp(
        long DocumentId,
        DocumentUuid DocumentUuid,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt
    ) : RelationalReadTargetLookupResult
    {
        private DateTimeOffset _contentLastModifiedAt = ValidateContentLastModifiedAt(ContentLastModifiedAt);

        public DateTimeOffset ContentLastModifiedAt
        {
            get => _contentLastModifiedAt;
            init => _contentLastModifiedAt = ValidateContentLastModifiedAt(value);
        }

        private static DateTimeOffset ValidateContentLastModifiedAt(DateTimeOffset contentLastModifiedAt)
        {
            if (contentLastModifiedAt == default)
            {
                throw new ArgumentException(
                    "ContentLastModifiedAt must be a real persisted timestamp.",
                    nameof(ContentLastModifiedAt)
                );
            }

            return contentLastModifiedAt;
        }
    }

    public sealed record NotFound() : RelationalReadTargetLookupResult;

    public sealed record WrongResource(DocumentUuid DocumentUuid, QualifiedResourceName ActualResource)
        : RelationalReadTargetLookupResult;
}

internal sealed class RelationalReadTargetLookupService(IRelationalCommandExecutor commandExecutor)
    : IRelationalReadTargetLookupService
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    /// <summary>
    /// Resolves identity and version without reading the root timestamp. Hydration supplies response metadata.
    /// </summary>
    public async Task<RelationalReadTargetLookupResult> ResolveForGetByIdAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var expectedResourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        var resolvedDocument = await RelationalDocumentUuidLookupSupport
            .TryResolveByDocumentUuidAsync(_commandExecutor, mappingSet, documentUuid, cancellationToken)
            .ConfigureAwait(false);

        if (resolvedDocument is null)
        {
            return new RelationalReadTargetLookupResult.NotFound();
        }

        if (resolvedDocument.ResourceKeyId != expectedResourceKeyId)
        {
            return CreateWrongResourceResult(
                mappingSet,
                resolvedDocument.ResourceKeyId,
                resolvedDocument.DocumentUuid
            );
        }

        if (resolvedDocument.ContentVersion is null)
        {
            throw new InvalidOperationException(
                $"Relational GET target lookup for document uuid '{documentUuid.Value}' returned a row without ContentVersion."
            );
        }

        return new RelationalReadTargetLookupResult.ExistingDocument(
            resolvedDocument.DocumentId,
            resolvedDocument.DocumentUuid,
            resolvedDocument.ContentVersion.Value
        );
    }

    /// <summary>
    /// Resolves the root timestamp as well as identity and version for cache candidate selection.
    /// </summary>
    public async Task<RelationalReadTargetLookupResult> ResolveForCacheCandidateAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var expectedResourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);

        var resolvedDocument = await RelationalDocumentUuidLookupSupport
            .TryResolveReadByDocumentUuidAsync(
                _commandExecutor,
                mappingSet,
                resource,
                documentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (resolvedDocument is null)
        {
            return new RelationalReadTargetLookupResult.NotFound();
        }

        if (resolvedDocument.ResourceKeyId == expectedResourceKeyId)
        {
            if (resolvedDocument.ContentVersion is null)
            {
                throw new InvalidOperationException(
                    $"Relational GET target lookup for document uuid '{documentUuid.Value}' returned a row without ContentVersion."
                );
            }

            if (resolvedDocument.ContentLastModifiedAt is null)
            {
                // A canonical delete can commit between the joined document and owner reads.
                // Confirm disappearance before treating the missing owner as an invariant violation.
                var currentDocument = await RelationalDocumentUuidLookupSupport
                    .TryResolveByDocumentUuidAsync(
                        _commandExecutor,
                        mappingSet,
                        documentUuid,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (currentDocument is null)
                {
                    return new RelationalReadTargetLookupResult.NotFound();
                }

                throw new InvalidOperationException(
                    $"Relational GET target lookup for document uuid '{documentUuid.Value}' returned a row without ContentLastModifiedAt."
                );
            }

            return new RelationalReadTargetLookupResult.ExistingDocumentWithTimestamp(
                resolvedDocument.DocumentId,
                resolvedDocument.DocumentUuid,
                resolvedDocument.ContentVersion.Value,
                resolvedDocument.ContentLastModifiedAt.Value
            );
        }

        return CreateWrongResourceResult(
            mappingSet,
            resolvedDocument.ResourceKeyId,
            resolvedDocument.DocumentUuid
        );
    }

    private static RelationalReadTargetLookupResult.WrongResource CreateWrongResourceResult(
        MappingSet mappingSet,
        short resourceKeyId,
        DocumentUuid documentUuid
    )
    {
        if (!mappingSet.ResourceKeyById.TryGetValue(resourceKeyId, out var actualResourceKey))
        {
            throw new KeyNotFoundException(
                $"Mapping set '{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}' does not contain a resource key entry for id "
                    + $"'{resourceKeyId}' resolved from document uuid '{documentUuid.Value}'. "
                    + "This indicates an internal compilation/selection bug."
            );
        }

        return new RelationalReadTargetLookupResult.WrongResource(documentUuid, actualResourceKey.Resource);
    }
}
