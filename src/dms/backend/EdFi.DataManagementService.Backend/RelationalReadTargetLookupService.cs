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
}

public abstract record RelationalReadTargetLookupResult
{
    private RelationalReadTargetLookupResult() { }

    public sealed record ExistingDocument(long DocumentId, DocumentUuid DocumentUuid)
        : RelationalReadTargetLookupResult;

    public sealed record NotFound() : RelationalReadTargetLookupResult;

    public sealed record WrongResource(DocumentUuid DocumentUuid, QualifiedResourceName ActualResource)
        : RelationalReadTargetLookupResult;
}

internal sealed class RelationalReadTargetLookupService(IRelationalCommandExecutor commandExecutor)
    : IRelationalReadTargetLookupService
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

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

        if (resolvedDocument.ResourceKeyId == expectedResourceKeyId)
        {
            return new RelationalReadTargetLookupResult.ExistingDocument(
                resolvedDocument.DocumentId,
                resolvedDocument.DocumentUuid
            );
        }

        if (
            !mappingSet.ResourceKeyById.TryGetValue(resolvedDocument.ResourceKeyId, out var actualResourceKey)
        )
        {
            throw new KeyNotFoundException(
                $"Mapping set '{RelationalWriteSupport.FormatMappingSetKey(mappingSet.Key)}' does not contain a resource key entry for id "
                    + $"'{resolvedDocument.ResourceKeyId}' resolved from document uuid '{documentUuid.Value}'. "
                    + "This indicates an internal compilation/selection bug."
            );
        }

        return new RelationalReadTargetLookupResult.WrongResource(
            resolvedDocument.DocumentUuid,
            actualResourceKey.Resource
        );
    }
}
