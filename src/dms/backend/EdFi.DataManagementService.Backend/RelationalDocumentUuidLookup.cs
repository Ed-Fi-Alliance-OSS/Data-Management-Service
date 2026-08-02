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

    /// <summary>
    /// Descriptor PUT/DELETE entry point. Descriptors share one physical table, so the route's resource
    /// cannot be scoped structurally the way a resource root table scopes its own uuid index; the probe
    /// seeks <c>UX_Descriptor_DocumentUuid</c> and carries the descriptor row's <c>ResourceKeyId</c>
    /// mirror as a residual predicate — the exact predicate pair the descriptor GET already proved.
    /// </summary>
    /// <remarks>
    /// That mirror is trigger-sourced and nullable, so a descriptor row written with triggers suppressed
    /// carries <c>NULL</c> there and never matches: the lookup fails closed to not-exists rather than
    /// serving a row this endpoint cannot prove it owns.
    /// </remarks>
    public static Task<ResolvedRootTarget?> TryResolveDescriptorTargetByDocumentUuidAsync(
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

        var command = mappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlDescriptorLookupByDocumentUuidCommand(
                documentUuid,
                resourceKeyId
            ),
            SqlDialect.Mssql => BuildMssqlDescriptorLookupByDocumentUuidCommand(documentUuid, resourceKeyId),
            _ => throw new NotSupportedException(
                $"Relational descriptor UUID lookup does not support SQL dialect '{mappingSet.Key.Dialect}'."
            ),
        };

        return ExecuteRootRowTargetLookupAsync(
            commandExecutor,
            command,
            () =>
                "Relational descriptor target probe returned multiple rows for resource "
                + $"'{RelationalWriteSupport.FormatResource(resource)}' and document uuid '{documentUuid.Value}'.",
            cancellationToken
        );
    }

    /// <summary>
    /// Descriptor delete-path entry point. Narrows the descriptor uuid probe to the internal
    /// <c>DocumentId</c>, which is all the DELETE statement needs to scope itself.
    /// </summary>
    public static async Task<ResolvedDeleteTarget?> TryResolveDescriptorDeleteTargetAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        var resolved = await TryResolveDescriptorTargetByDocumentUuidAsync(
                commandExecutor,
                mappingSet,
                resource,
                documentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        return resolved is null ? null : new ResolvedDeleteTarget(resolved.DocumentId);
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
    ) => TryResolveTargetByRootTableAsync(commandExecutor, "GET", rootTable, documentUuid, cancellationToken);

    /// <summary>
    /// PUT entry point, and the shared uuid probe for the regular DELETE
    /// (<see cref="TryResolveDeleteTargetByRootTableAsync"/>). Identical reasoning to the GET probe:
    /// the route names the resource, so seeking <c>UX_&lt;Root&gt;_DocumentUuid</c> scopes the lookup
    /// structurally and returns the same root row the write path then locks, loads current state from,
    /// and re-reads the stamp on.
    /// </summary>
    public static Task<ResolvedRootTarget?> TryResolveWriteTargetByRootTableAsync(
        IRelationalCommandExecutor commandExecutor,
        DbTableName rootTable,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    ) =>
        TryResolveTargetByRootTableAsync(
            commandExecutor,
            "write",
            rootTable,
            documentUuid,
            cancellationToken
        );

    /// <summary>
    /// Delete-path entry point for the regular (non-descriptor) DELETE. Narrows the root-table uuid
    /// probe to the internal <c>DocumentId</c>, which is all the DELETE statement needs to scope
    /// itself; the target's <c>ContentVersion</c> is captured by the subsequent row lock, not here.
    /// </summary>
    public static async Task<ResolvedDeleteTarget?> TryResolveDeleteTargetByRootTableAsync(
        IRelationalCommandExecutor commandExecutor,
        DbTableName rootTable,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        var resolved = await TryResolveWriteTargetByRootTableAsync(
                commandExecutor,
                rootTable,
                documentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        return resolved is null ? null : new ResolvedDeleteTarget(resolved.DocumentId);
    }

    private static Task<ResolvedRootTarget?> TryResolveTargetByRootTableAsync(
        IRelationalCommandExecutor commandExecutor,
        string probeKind,
        DbTableName rootTable,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);

        var command = commandExecutor.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlTargetByRootTableCommand(rootTable, documentUuid),
            SqlDialect.Mssql => BuildMssqlTargetByRootTableCommand(rootTable, documentUuid),
            _ => throw new NotSupportedException(
                $"Relational {probeKind} target root-table probe does not support SQL dialect '{commandExecutor.Dialect}'."
            ),
        };

        return ExecuteRootRowTargetLookupAsync(
            commandExecutor,
            command,
            () =>
                $"Relational {probeKind} target root-table probe returned multiple rows for root table "
                + $"'{rootTable}' and document uuid '{documentUuid.Value}'.",
            cancellationToken
        );
    }

    /// <summary>
    /// Reads the <c>(DocumentId, DocumentUuid, ContentVersion)</c> triple a single-row uuid seek returns
    /// from a table that carries the document-metadata mirrors. The message factory is deferred so the
    /// diagnostic string is only composed on the impossible-second-row path.
    /// </summary>
    private static Task<ResolvedRootTarget?> ExecuteRootRowTargetLookupAsync(
        IRelationalCommandExecutor commandExecutor,
        RelationalCommand command,
        Func<string> multipleRowsMessage,
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

                var resolvedTarget = new ResolvedRootTarget(
                    reader.GetRequiredFieldValue<long>("DocumentId"),
                    reader.GetRequiredFieldValue<Guid>("DocumentUuid"),
                    reader.GetRequiredFieldValue<long>("ContentVersion")
                );

                // The seeked uuid index is unique, so a second row is impossible; keep the read defensive
                // so a missing or corrupt index fails loudly instead of serving an arbitrary row.
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(multipleRowsMessage());
                }

                return resolvedTarget;
            },
            cancellationToken
        );
    }

    private static RelationalCommand BuildPostgresqlDescriptorLookupByDocumentUuidCommand(
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return new RelationalCommand(
            """
            SELECT
                descriptor."DocumentId" AS "DocumentId",
                descriptor."DocumentUuid" AS "DocumentUuid",
                descriptor."ContentVersion" AS "ContentVersion"
            FROM dms."Descriptor" descriptor
            WHERE descriptor."DocumentUuid" = @documentUuid
                AND descriptor."ResourceKeyId" = @resourceKeyId
            """,
            [
                new RelationalParameter(DocumentUuidParameterName, documentUuid.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId),
            ]
        );
    }

    private static RelationalCommand BuildMssqlDescriptorLookupByDocumentUuidCommand(
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return new RelationalCommand(
            """
            SELECT
                descriptor.[DocumentId] AS [DocumentId],
                descriptor.[DocumentUuid] AS [DocumentUuid],
                descriptor.[ContentVersion] AS [ContentVersion]
            FROM [dms].[Descriptor] descriptor
            WHERE descriptor.[DocumentUuid] = @documentUuid
                AND descriptor.[ResourceKeyId] = @resourceKeyId
            """,
            [
                new RelationalParameter(DocumentUuidParameterName, documentUuid.Value),
                new RelationalParameter(ResourceKeyIdParameterName, resourceKeyId),
            ]
        );
    }

    private static RelationalCommand BuildPostgresqlTargetByRootTableCommand(
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

    private static RelationalCommand BuildMssqlTargetByRootTableCommand(
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

    internal sealed record ResolvedDeleteTarget(long DocumentId);

    /// <summary>
    /// Target resolved from a table that carries the document-metadata mirror columns — the resource root
    /// table for GET-by-id, PUT, and the regular DELETE, or <c>dms.Descriptor</c> for the descriptor
    /// routes. <c>ContentVersion</c> is non-null there because the stamping triggers maintain it.
    /// </summary>
    internal sealed record ResolvedRootTarget(long DocumentId, Guid DocumentUuid, long ContentVersion);
}
