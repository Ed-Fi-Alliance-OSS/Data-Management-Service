// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

public interface IRelationalWriteTargetLookupService
{
    Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        CancellationToken cancellationToken = default
    );

    Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    );
}

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
}

internal sealed class RelationalWriteTargetLookupService(IRelationalCommandExecutor commandExecutor)
    : IRelationalWriteTargetLookupService
{
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

    public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        CancellationToken cancellationToken = default
    )
    {
        return RelationalWriteTargetLookupSupport.ResolveForPostAsync(
            _commandExecutor,
            mappingSet,
            resource,
            referentialId,
            candidateDocumentUuid,
            cancellationToken
        );
    }

    public Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        return RelationalWriteTargetLookupSupport.ResolveForPutAsync(
            _commandExecutor,
            mappingSet,
            resource,
            documentUuid,
            cancellationToken
        );
    }
}

internal sealed class RelationalWriteTargetLookupResolver : IRelationalWriteTargetLookupResolver
{
    public Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        return RelationalWriteTargetLookupSupport.ResolveForPostAsync(
            new SessionRelationalCommandExecutor(connection, transaction),
            mappingSet,
            resource,
            referentialId,
            candidateDocumentUuid,
            cancellationToken
        );
    }
}

internal static class RelationalWriteTargetLookupSupport
{
    private const string ReferentialIdParameterName = "@referentialId";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";

    public static async Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(mappingSet);

        var existingDocument = await TryResolveExistingDocumentByReferentialIdAsync(
            commandExecutor,
            mappingSet,
            resource,
            referentialId,
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

    public static async Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(mappingSet);

        var existingDocument = await TryResolveExistingDocumentByDocumentUuidAsync(
            commandExecutor,
            mappingSet,
            resource,
            documentUuid,
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
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        CancellationToken cancellationToken
    )
    {
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);

        return ExecuteLookupAsync(
            commandExecutor,
            mappingSet.Key.Dialect switch
            {
                SqlDialect.Pgsql => BuildPostgresqlLookupByReferentialIdCommand(referentialId, resourceKeyId),
                SqlDialect.Mssql => BuildMssqlLookupByReferentialIdCommand(referentialId, resourceKeyId),
                _ => throw new NotSupportedException(
                    $"Relational POST target lookup does not support SQL dialect '{mappingSet.Key.Dialect}'."
                ),
            },
            $"resource '{RelationalWriteSupport.FormatResource(resource)}' and referential id '{referentialId.Value}'",
            cancellationToken
        );
    }

    private static Task<ResolvedExistingDocument?> TryResolveExistingDocumentByDocumentUuidAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken
    )
    {
        return TryResolveExistingDocumentByDocumentUuidCoreAsync(
            commandExecutor,
            mappingSet,
            resource,
            documentUuid,
            cancellationToken
        );
    }

    private static async Task<ResolvedExistingDocument?> TryResolveExistingDocumentByDocumentUuidCoreAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken
    )
    {
        var resolvedDocument = await RelationalDocumentUuidLookupSupport
            .TryResolveByDocumentUuidAndResourceAsync(
                commandExecutor,
                mappingSet,
                resource,
                documentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        return resolvedDocument is null
            ? null
            : new ResolvedExistingDocument(
                resolvedDocument.DocumentId,
                resolvedDocument.DocumentUuid,
                resolvedDocument.ContentVersion
                    ?? throw new InvalidOperationException(
                        $"Relational PUT target lookup for document uuid '{documentUuid.Value}' returned a row without ContentVersion."
                    )
            );
    }

    private static Task<ResolvedExistingDocument?> ExecuteLookupAsync(
        IRelationalCommandExecutor commandExecutor,
        RelationalCommand command,
        string lookupDescription,
        CancellationToken cancellationToken
    )
    {
        return commandExecutor.ExecuteReaderAsync(
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

    private sealed record ResolvedExistingDocument(
        long DocumentId,
        DocumentUuid DocumentUuid,
        long ObservedContentVersion
    );
}
