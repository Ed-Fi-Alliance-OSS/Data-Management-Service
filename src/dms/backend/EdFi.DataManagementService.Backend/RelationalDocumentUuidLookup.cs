// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend;

internal static class RelationalDocumentUuidLookupSupport
{
    private const string DocumentUuidParameterName = "@documentUuid";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";

    public static Task<ResolvedDocumentByUuid?> TryResolveByDocumentUuidAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(mappingSet);

        return ExecuteLookupAsync(
            commandExecutor,
            mappingSet.Key.Dialect switch
            {
                SqlDialect.Pgsql => BuildPostgresqlLookupByDocumentUuidCommand(documentUuid),
                SqlDialect.Mssql => BuildMssqlLookupByDocumentUuidCommand(documentUuid),
                _ => throw new NotSupportedException(
                    $"Relational document UUID lookup does not support SQL dialect '{mappingSet.Key.Dialect}'."
                ),
            },
            $"document uuid '{documentUuid.Value}'",
            cancellationToken
        );
    }

    /// <summary>
    /// Delete-path entry point for resolving a document by (resource, DocumentUuid). The regular
    /// non-descriptor DELETE needs only the internal <c>DocumentId</c> to scope the actual DELETE
    /// statement, so this helper narrows the shared UUID lookup result to a
    /// <see cref="ResolvedDeleteTarget"/> (DocumentId only) and keeps "resolve delete target" as
    /// an explicit concept separate from the PUT-oriented
    /// <see cref="RelationalWriteTargetLookupService.ResolveForPutAsync"/> (which additionally
    /// requires a non-null <c>ContentVersion</c>).
    /// </summary>
    public static async Task<ResolvedDeleteTarget?> TryResolveDeleteTargetAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        var resolved = await TryResolveByDocumentUuidAndResourceAsync(
                commandExecutor,
                mappingSet,
                resource,
                documentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        return resolved is null ? null : new ResolvedDeleteTarget(resolved.DocumentId);
    }

    public static Task<ResolvedDocumentByUuid?> TryResolveByDocumentUuidAndResourceAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(mappingSet);

        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);

        return ExecuteLookupAsync(
            commandExecutor,
            mappingSet.Key.Dialect switch
            {
                SqlDialect.Pgsql => BuildPostgresqlLookupByDocumentUuidCommand(documentUuid, resourceKeyId),
                SqlDialect.Mssql => BuildMssqlLookupByDocumentUuidCommand(documentUuid, resourceKeyId),
                _ => throw new NotSupportedException(
                    $"Relational document UUID lookup does not support SQL dialect '{mappingSet.Key.Dialect}'."
                ),
            },
            $"resource '{RelationalWriteSupport.FormatResource(resource)}' and document uuid '{documentUuid.Value}'",
            cancellationToken
        );
    }

    /// <summary>
    /// GET-by-id entry point. The route already names the resource, so the target probe seeks the
    /// resource root table's <c>UX_&lt;Root&gt;_DocumentUuid</c> unique index instead of
    /// <c>dms.Document</c>: resource scoping is structural (a uuid belonging to another resource is
    /// simply absent from this root table) and the probed <c>DocumentUuid</c>/<c>ContentVersion</c>
    /// come from the same root row that GET hydration reads.
    /// </summary>
    public static Task<ResolvedRootTarget?> TryResolveGetTargetByRootTableAsync(
        IRelationalCommandExecutor commandExecutor,
        DbTableName rootTable,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);

        var command = commandExecutor.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlGetTargetByRootTableCommand(rootTable, documentUuid),
            SqlDialect.Mssql => BuildMssqlGetTargetByRootTableCommand(rootTable, documentUuid),
            _ => throw new NotSupportedException(
                $"Relational GET target root-table probe does not support SQL dialect '{commandExecutor.Dialect}'."
            ),
        };

        return commandExecutor.ExecuteReaderAsync(
            command,
            async (reader, ct) =>
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var resolvedTarget = new ResolvedRootTarget(
                    reader.GetRequiredFieldValue<long>("DocumentId"),
                    reader.GetRequiredFieldValue<Guid>("DocumentUuid"),
                    reader.GetRequiredFieldValue<long>("ContentVersion")
                );

                // UX_<Root>_DocumentUuid makes a second row impossible; keep the read defensive so a
                // missing or corrupt index fails loudly instead of serving an arbitrary row.
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Relational GET target root-table probe returned multiple rows for root table "
                            + $"'{rootTable}' and document uuid '{documentUuid.Value}'."
                    );
                }

                return resolvedTarget;
            },
            cancellationToken
        );
    }

    private static Task<ResolvedDocumentByUuid?> ExecuteLookupAsync(
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

                var resolvedDocument = new ResolvedDocumentByUuid(
                    reader.GetRequiredFieldValue<long>("DocumentId"),
                    new DocumentUuid(reader.GetRequiredFieldValue<Guid>("DocumentUuid")),
                    reader.GetRequiredFieldValue<short>("ResourceKeyId"),
                    reader.IsDBNull(reader.GetOrdinal("ContentVersion"))
                        ? null
                        : reader.GetRequiredFieldValue<long>("ContentVersion")
                );

                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Relational document UUID lookup returned multiple rows for {lookupDescription}."
                    );
                }

                return resolvedDocument;
            },
            cancellationToken
        );
    }

    private static RelationalCommand BuildPostgresqlLookupByDocumentUuidCommand(
        DocumentUuid documentUuid,
        short? resourceKeyId = null
    )
    {
        var commandText =
            """
                SELECT
                    document."DocumentId" AS "DocumentId",
                    document."DocumentUuid" AS "DocumentUuid",
                    document."ResourceKeyId" AS "ResourceKeyId",
                    document."ContentVersion" AS "ContentVersion"
                FROM dms."Document" document
                WHERE document."DocumentUuid" = @documentUuid
                """
            + (
                resourceKeyId is null ? string.Empty : "\n    AND document.\"ResourceKeyId\" = @resourceKeyId"
            );

        IReadOnlyList<RelationalParameter> parameters = resourceKeyId is null
            ? [new RelationalParameter(DocumentUuidParameterName, documentUuid.Value)]
            :
            [
                new RelationalParameter(DocumentUuidParameterName, documentUuid.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId.Value),
            ];

        return new RelationalCommand(commandText, parameters);
    }

    private static RelationalCommand BuildMssqlLookupByDocumentUuidCommand(
        DocumentUuid documentUuid,
        short? resourceKeyId = null
    )
    {
        var commandText =
            """
                SELECT
                    document.[DocumentId] AS [DocumentId],
                    document.[DocumentUuid] AS [DocumentUuid],
                    document.[ResourceKeyId] AS [ResourceKeyId],
                    document.[ContentVersion] AS [ContentVersion]
                FROM [dms].[Document] document
                WHERE document.[DocumentUuid] = @documentUuid
                """
            + (resourceKeyId is null ? string.Empty : "\n    AND document.[ResourceKeyId] = @resourceKeyId");

        IReadOnlyList<RelationalParameter> parameters = resourceKeyId is null
            ? [new RelationalParameter(DocumentUuidParameterName, documentUuid.Value)]
            :
            [
                new RelationalParameter(DocumentUuidParameterName, documentUuid.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId.Value),
            ];

        return new RelationalCommand(commandText, parameters);
    }

    private static RelationalCommand BuildPostgresqlGetTargetByRootTableCommand(
        DbTableName rootTable,
        DocumentUuid documentUuid
    )
    {
        var commandText = $"""
            SELECT
                root."DocumentId" AS "DocumentId",
                root."DocumentUuid" AS "DocumentUuid",
                root."ContentVersion" AS "ContentVersion"
            FROM {SqlIdentifierQuoter.QuoteTableName(SqlDialect.Pgsql, rootTable)} root
            WHERE root."DocumentUuid" = @documentUuid
            """;

        return new RelationalCommand(
            commandText,
            [new RelationalParameter(DocumentUuidParameterName, documentUuid.Value)]
        );
    }

    private static RelationalCommand BuildMssqlGetTargetByRootTableCommand(
        DbTableName rootTable,
        DocumentUuid documentUuid
    )
    {
        var commandText = $"""
            SELECT
                root.[DocumentId] AS [DocumentId],
                root.[DocumentUuid] AS [DocumentUuid],
                root.[ContentVersion] AS [ContentVersion]
            FROM {SqlIdentifierQuoter.QuoteTableName(SqlDialect.Mssql, rootTable)} root
            WHERE root.[DocumentUuid] = @documentUuid
            """;

        return new RelationalCommand(
            commandText,
            [new RelationalParameter(DocumentUuidParameterName, documentUuid.Value)]
        );
    }

    internal sealed record ResolvedDocumentByUuid(
        long DocumentId,
        DocumentUuid DocumentUuid,
        short ResourceKeyId,
        long? ContentVersion
    );

    internal sealed record ResolvedDeleteTarget(long DocumentId);

    /// <summary>
    /// GET-by-id target resolved from the resource root table's document metadata mirror columns.
    /// <c>ContentVersion</c> is non-null there (the stamping triggers maintain it), so unlike
    /// <see cref="ResolvedDocumentByUuid"/> this target needs no nullable content version.
    /// </summary>
    internal sealed record ResolvedRootTarget(long DocumentId, Guid DocumentUuid, long ContentVersion);
}
