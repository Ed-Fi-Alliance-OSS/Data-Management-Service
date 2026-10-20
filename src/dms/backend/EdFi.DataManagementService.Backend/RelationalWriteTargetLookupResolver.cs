// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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
    /// <summary>
    /// Resolves the POST target inside an open write session. Takes the session's command executor
    /// rather than a raw connection/transaction pair so the lookup stays on the session's single
    /// command-creation seam and a session decorator observes it.
    /// </summary>
    Task<RelationalWriteTargetLookupResult> ResolveForPostAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ReferentialId referentialId,
        DocumentUuid candidateDocumentUuid,
        IRelationalCommandExecutor commandExecutor,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Resolves the PUT target inside an open write session, on the same session command-creation
    /// seam as <see cref="ResolveForPostAsync"/>.
    /// </summary>
    Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        IRelationalCommandExecutor commandExecutor,
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
        IRelationalCommandExecutor commandExecutor,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);

        return RelationalWriteTargetLookupSupport.ResolveForPostAsync(
            commandExecutor,
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
        IRelationalCommandExecutor commandExecutor,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);

        return RelationalWriteTargetLookupSupport.ResolveForPutAsync(
            commandExecutor,
            mappingSet,
            resource,
            documentUuid,
            cancellationToken
        );
    }
}

internal static class RelationalWriteTargetLookupSupport
{
    private const string ReferentialIdParameterName = "@referentialId";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";
    private const string DocumentUuidParameterName = "@documentUuid";

    /// <summary>
    /// Builds the POST target predicate over the aliased <c>dms.Document</c> row <c>d</c> from the
    /// resource's own natural key: one scalar <c>=</c> subselect reads the root table's
    /// <c>DocumentId</c> through the <c>UX_&lt;R&gt;_NK</c> columns, filtered to the request's resource key.
    /// Descriptor parts compare the stored <c>int DescriptorId</c> through a lowered-URI descriptor
    /// subselect; each identity reference compares its <c>_DocumentId</c> FK through a subselect of the
    /// referenced root or abstract identity table. Every level is a scalar subselect, so a key that admits
    /// more than one row fails the statement instead of silently picking one. A value no stored row can
    /// hold binds nothing and yields the no-match predicate. Returned as a <see cref="RelationalCommand"/>
    /// whose text is the bare predicate, so the composite rewriter can rename its parameters.
    /// </summary>
    public static RelationalCommand BuildNaturalKeyCaptureTargetPredicate(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        DocumentIdentity documentIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(documentIdentity);

        var resource = writePlan.Model.Resource;
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        var builder = new NaturalKeyCapturePredicateBuilder(
            mappingSet,
            writePlan.Model.DocumentReferenceBindings,
            RelationalWriteSupport.GetOwnNaturalKeyProbeOrThrow(mappingSet, resource),
            documentIdentity
        );
        RelationalParameter resourceKeyIdParameter = new(ResourceKeyIdParameterName, resourceKeyId);

        return builder.TryBuildRootSubselect(out var rootSubselect)
            ? new RelationalCommand(
                $"{builder.Column("d", "DocumentId")} = {rootSubselect}"
                    + $" AND {builder.Column("d", "ResourceKeyId")} = {ResourceKeyIdParameterName}",
                [.. builder.Parameters, resourceKeyIdParameter]
            )
            : new RelationalCommand(
                $"{builder.Column("d", "DocumentId")} IS NULL"
                    + $" AND {builder.Column("d", "ResourceKeyId")} = {ResourceKeyIdParameterName}",
                [resourceKeyIdParameter]
            );
    }

    /// <summary>
    /// Builds the target predicate over the aliased <c>dms.Document</c> row <c>d</c> for a composite
    /// captured-target statement addressing a document by its external id, matching
    /// <see cref="ResolveForPutAsync"/>'s document-uuid-and-resource lookup semantics. PUT and DELETE both
    /// address their target this way, so they capture through the same predicate.
    /// </summary>
    public static RelationalCommand BuildDocumentUuidCaptureTargetPredicate(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        var predicateSql = mappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => """d."DocumentUuid" = @documentUuid AND d."ResourceKeyId" = @resourceKeyId""",
            SqlDialect.Mssql => "d.[DocumentUuid] = @documentUuid AND d.[ResourceKeyId] = @resourceKeyId",
            _ => throw new NotSupportedException(
                $"Relational document-uuid capture predicate does not support SQL dialect '{mappingSet.Key.Dialect}'."
            ),
        };

        return new RelationalCommand(
            predicateSql,
            [
                new RelationalParameter(DocumentUuidParameterName, documentUuid.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId),
            ]
        );
    }

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
