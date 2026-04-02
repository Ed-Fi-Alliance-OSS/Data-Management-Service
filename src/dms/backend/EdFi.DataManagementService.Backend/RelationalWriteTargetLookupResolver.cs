// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalWriteTargetLookupResolver
{
    Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default
    );

    Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalWriteTargetLookupResolver : IRelationalWriteTargetLookupResolver
{
    private const string ReferentialIdParameterName = "@referentialId";
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";

    public async Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var existingDocument = await TryResolveExistingDocumentByReferentialIdAsync(
            mappingSet,
            resource,
            referentialId,
            connection,
            transaction,
            cancellationToken
        );

        return existingDocument is null
            ? new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid)
            : new RelationalWriteTargetLookupResult.ExistingDocument(
                existingDocument.DocumentId,
                existingDocument.DocumentUuid,
                existingDocument.ObservedContentVersion
            );
    }

    public async Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var existingDocument = await TryResolveExistingDocumentByDocumentUuidAsync(
            mappingSet,
            resource,
            documentUuid,
            connection,
            transaction,
            cancellationToken
        );

        return existingDocument is null
            ? new RelationalWriteTargetLookupResult.NotFound()
            : new RelationalWriteTargetLookupResult.ExistingDocument(
                existingDocument.DocumentId,
                existingDocument.DocumentUuid,
                existingDocument.ObservedContentVersion
            );
    }

    private static Task<ResolvedExistingDocument?> TryResolveExistingDocumentByReferentialIdAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DbConnection connection,
        DbTransaction transaction,
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
                    $"Relational POST target lookup does not support SQL dialect '{mappingSet.Key.Dialect}'."
                ),
            },
            $"resource '{RelationalWriteSupport.FormatResource(resource)}' and referential id '{referentialId.Value}'",
            connection,
            transaction,
            cancellationToken
        );
    }

    private static Task<ResolvedExistingDocument?> TryResolveExistingDocumentByDocumentUuidAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        DbConnection connection,
        DbTransaction transaction,
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
                    $"Relational PUT target lookup does not support SQL dialect '{mappingSet.Key.Dialect}'."
                ),
            },
            $"resource '{RelationalWriteSupport.FormatResource(resource)}' and document uuid '{documentUuid.Value}'",
            connection,
            transaction,
            cancellationToken
        );
    }

    private static Task<ResolvedExistingDocument?> ExecuteLookupAsync(
        RelationalCommand command,
        string lookupDescription,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        var sessionCommandExecutor = new SessionRelationalCommandExecutor(connection, transaction);

        return sessionCommandExecutor.ExecuteReaderAsync(
            command,
            async (reader, ct) =>
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var resolvedDocument = new ResolvedExistingDocument(
                    reader.GetRequiredFieldValue<long>("DocumentId"),
                    new DocumentUuid(reader.GetRequiredFieldValue<Guid>("DocumentUuid")),
                    reader.GetRequiredFieldValue<long>("ContentVersion")
                );

                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Relational write target lookup returned multiple rows for {lookupDescription}."
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
                document."DocumentUuid" AS "DocumentUuid",
                document."ContentVersion" AS "ContentVersion"
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
                document."DocumentUuid" AS "DocumentUuid",
                document."ContentVersion" AS "ContentVersion"
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
                document.[DocumentUuid] AS [DocumentUuid],
                document.[ContentVersion] AS [ContentVersion]
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
                document.[DocumentUuid] AS [DocumentUuid],
                document.[ContentVersion] AS [ContentVersion]
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

    private sealed record ResolvedExistingDocument(
        long DocumentId,
        DocumentUuid DocumentUuid,
        long ObservedContentVersion
    );
}
