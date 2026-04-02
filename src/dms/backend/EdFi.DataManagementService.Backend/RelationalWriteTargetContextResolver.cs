// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalWriteTargetContextResolver
{
    Task<RelationalWriteTargetContext> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        CancellationToken cancellationToken = default
    );

    Task<RelationalWriteTargetContext> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWriteTargetContextResolver(IRelationalCommandExecutor commandExecutor)
    : IRelationalWriteTargetContextResolver
{
    private const string ReferentialIdParameterName = "@referentialId";
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";

    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public async Task<RelationalWriteTargetContext> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var existingDocument = await TryResolveExistingDocumentByReferentialIdAsync(
            mappingSet,
            resource,
            referentialId,
            cancellationToken
        );

        return existingDocument is null
            ? new RelationalWriteTargetContext.CreateNew(candidateDocumentUuid)
            : new RelationalWriteTargetContext.ExistingDocument(
                existingDocument.DocumentId,
                existingDocument.DocumentUuid
            );
    }

    public async Task<RelationalWriteTargetContext> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var existingDocument = await TryResolveExistingDocumentByDocumentUuidAsync(
            mappingSet,
            resource,
            documentUuid,
            cancellationToken
        );

        return existingDocument is null
            ? new RelationalWriteTargetContext.CreateNew(documentUuid)
            : new RelationalWriteTargetContext.ExistingDocument(
                existingDocument.DocumentId,
                existingDocument.DocumentUuid
            );
    }

    private Task<ResolvedExistingDocument?> TryResolveExistingDocumentByReferentialIdAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        CancellationToken cancellationToken
    )
    {
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);

        return ExecuteLookupAsync(
            mappingSet.Key.Dialect switch
            {
                SqlDialect.Pgsql => BuildPostgresqlLookupByReferentialIdCommand(referentialId, resourceKeyId),
                SqlDialect.Mssql => BuildMssqlLookupByReferentialIdCommand(referentialId, resourceKeyId),
                _ => throw new NotSupportedException(
                    $"Relational POST target-context resolution does not support SQL dialect '{mappingSet.Key.Dialect}'."
                ),
            },
            $"resource '{RelationalWriteSupport.FormatResource(resource)}' and referential id '{referentialId.Value}'",
            cancellationToken
        );
    }

    private Task<ResolvedExistingDocument?> TryResolveExistingDocumentByDocumentUuidAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken
    )
    {
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);

        return ExecuteLookupAsync(
            mappingSet.Key.Dialect switch
            {
                SqlDialect.Pgsql => BuildPostgresqlLookupByDocumentUuidCommand(documentUuid, resourceKeyId),
                SqlDialect.Mssql => BuildMssqlLookupByDocumentUuidCommand(documentUuid, resourceKeyId),
                _ => throw new NotSupportedException(
                    $"Relational PUT target-context resolution does not support SQL dialect '{mappingSet.Key.Dialect}'."
                ),
            },
            $"resource '{RelationalWriteSupport.FormatResource(resource)}' and document uuid '{documentUuid.Value}'",
            cancellationToken
        );
    }

    private Task<ResolvedExistingDocument?> ExecuteLookupAsync(
        RelationalCommand command,
        string lookupDescription,
        CancellationToken cancellationToken
    )
    {
        return _commandExecutor.ExecuteReaderAsync(
            command,
            async (reader, ct) =>
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var resolvedDocument = new ResolvedExistingDocument(
                    reader.GetRequiredFieldValue<long>("DocumentId"),
                    new DocumentUuid(reader.GetRequiredFieldValue<Guid>("DocumentUuid"))
                );

                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Relational write target-context resolution returned multiple rows for {lookupDescription}."
                    );
                }

                return resolvedDocument;
            },
            cancellationToken
        );
    }

    private static RelationalCommand BuildPostgresqlLookupByReferentialIdCommand(
        ReferentialId referentialId,
        short resourceKeyId
    )
    {
        return new RelationalCommand(
            """
            SELECT
                document."DocumentId" AS "DocumentId",
                document."DocumentUuid" AS "DocumentUuid"
            FROM dms."ReferentialIdentity" referentialIdentity
            INNER JOIN dms."Document" document
                ON document."DocumentId" = referentialIdentity."DocumentId"
            WHERE referentialIdentity."ReferentialId" = @referentialId
                AND document."ResourceKeyId" = @resourceKeyId
            """,
            [
                new RelationalParameter(ReferentialIdParameterName, referentialId.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId),
            ]
        );
    }

    private static RelationalCommand BuildPostgresqlLookupByDocumentUuidCommand(
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return new RelationalCommand(
            """
            SELECT
                document."DocumentId" AS "DocumentId",
                document."DocumentUuid" AS "DocumentUuid"
            FROM dms."Document" document
            WHERE document."DocumentUuid" = @documentUuid
                AND document."ResourceKeyId" = @resourceKeyId
            """,
            [
                new RelationalParameter(DocumentUuidParameterName, documentUuid.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId),
            ]
        );
    }

    private static RelationalCommand BuildMssqlLookupByReferentialIdCommand(
        ReferentialId referentialId,
        short resourceKeyId
    )
    {
        return new RelationalCommand(
            """
            SELECT
                document.[DocumentId] AS [DocumentId],
                document.[DocumentUuid] AS [DocumentUuid]
            FROM [dms].[ReferentialIdentity] referentialIdentity
            INNER JOIN [dms].[Document] document
                ON document.[DocumentId] = referentialIdentity.[DocumentId]
            WHERE referentialIdentity.[ReferentialId] = @referentialId
                AND document.[ResourceKeyId] = @resourceKeyId
            """,
            [
                new RelationalParameter(ReferentialIdParameterName, referentialId.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId),
            ]
        );
    }

    private static RelationalCommand BuildMssqlLookupByDocumentUuidCommand(
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return new RelationalCommand(
            """
            SELECT
                document.[DocumentId] AS [DocumentId],
                document.[DocumentUuid] AS [DocumentUuid]
            FROM [dms].[Document] document
            WHERE document.[DocumentUuid] = @documentUuid
                AND document.[ResourceKeyId] = @resourceKeyId
            """,
            [
                new RelationalParameter(DocumentUuidParameterName, documentUuid.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId),
            ]
        );
    }

    private sealed record ResolvedExistingDocument(long DocumentId, DocumentUuid DocumentUuid);
}
