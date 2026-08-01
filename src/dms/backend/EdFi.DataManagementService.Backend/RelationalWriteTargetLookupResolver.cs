// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;
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

    /// <summary>
    /// PUT target lookup through <c>dms.Document</c>. Only the descriptor write path still uses this;
    /// the regular resource PUT resolves its target from the root table
    /// (<see cref="ResolveForPutByRootTableAsync"/>).
    /// </summary>
    Task<RelationalWriteTargetLookupResult> ResolveForPutAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// PUT target lookup by a single-row seek of the resource root table's
    /// <c>UX_&lt;Root&gt;_DocumentUuid</c> unique index. The route names the resource, so resource
    /// scoping is structural — a uuid persisted for a different resource is simply absent from this
    /// root table — and the returned <c>DocumentUuid</c>/<c>ContentVersion</c> come from the same root
    /// row the write session then locks and loads current state from.
    /// </summary>
    Task<RelationalWriteTargetLookupResult> ResolveForPutByRootTableAsync(
        DbTableName rootTable,
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

    /// <summary>
    /// POST upsert detection by the resource's own natural key: a single-row seek of
    /// <c>UX_&lt;R&gt;_NK</c> on the root table, returning the same
    /// <c>(DocumentId, DocumentUuid, ContentVersion)</c> triple the referential-id probe returned.
    /// </summary>
    /// <remarks>
    /// Reference-sourced natural-key parts bind the <c>DocumentId</c> of the already-resolved reference,
    /// so this probe can only run <b>after</b> reference resolution inside the same write session. The
    /// returned <c>DocumentUuid</c>/<c>ContentVersion</c> come from the root row's trigger-maintained
    /// mirrors, which are in-transaction consistent with <c>dms.Document</c>.
    /// </remarks>
    Task<RelationalWriteTargetLookupResult> TryResolveByNaturalKeyAsync(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        DocumentIdentity documentIdentity,
        DocumentUuid candidateDocumentUuid,
        ResolvedReferenceSet resolvedReferences,
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

    public Task<RelationalWriteTargetLookupResult> ResolveForPutByRootTableAsync(
        DbTableName rootTable,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        return RelationalWriteTargetLookupSupport.ResolveForPutByRootTableAsync(
            _commandExecutor,
            rootTable,
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

    public Task<RelationalWriteTargetLookupResult> TryResolveByNaturalKeyAsync(
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        DocumentIdentity documentIdentity,
        DocumentUuid candidateDocumentUuid,
        ResolvedReferenceSet resolvedReferences,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        return RelationalWriteTargetLookupSupport.TryResolveByNaturalKeyAsync(
            new SessionRelationalCommandExecutor(connection, transaction),
            mappingSet,
            writePlan,
            documentIdentity,
            candidateDocumentUuid,
            resolvedReferences,
            cancellationToken
        );
    }
}

internal static class RelationalWriteTargetLookupSupport
{
    private const string ReferentialIdParameterName = "@referentialId";
    private const string ResourceKeyIdParameterName = "@resourceKeyId";
    private const string NaturalKeyParameterNamePrefix = "@nk";

    /// <summary>
    /// Seeks the resource's own <c>UX_&lt;R&gt;_NK</c> index for a persisted document whose natural key
    /// equals the request's, returning <c>CreateNew</c> when there is none.
    /// </summary>
    /// <remarks>
    /// Column binding follows the compiled <see cref="OwnNaturalKeyProbe"/> exactly, which is the constraint's
    /// own column list: a reference-sourced part binds the resolved reference's <c>DocumentId</c>, a
    /// descriptor-valued part binds the resolved descriptor's <c>DocumentId</c>, and every other part binds a
    /// CLR value typed from the request identity through <see cref="RelationalScalarLiteralParser"/>.
    /// An identity part the request could not resolve to a stored id cannot match any persisted row, so the
    /// probe short-circuits to <c>CreateNew</c> without issuing SQL.
    /// </remarks>
    public static async Task<RelationalWriteTargetLookupResult> TryResolveByNaturalKeyAsync(
        IRelationalCommandExecutor commandExecutor,
        MappingSet mappingSet,
        ResourceWritePlan writePlan,
        DocumentIdentity documentIdentity,
        DocumentUuid candidateDocumentUuid,
        ResolvedReferenceSet resolvedReferences,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(writePlan);
        ArgumentNullException.ThrowIfNull(documentIdentity);
        ArgumentNullException.ThrowIfNull(resolvedReferences);

        var resource = writePlan.Model.Resource;
        var rootTable = writePlan.Model.Root.Table;
        var naturalKeyProbe = RelationalWriteSupport.GetOwnNaturalKeyProbeOrThrow(
            mappingSet,
            resource,
            rootTable
        );

        if (naturalKeyProbe.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Relational POST natural-key target lookup for resource '{RelationalWriteSupport.FormatResource(resource)}' "
                    + "resolved no natural-key columns from compiled natural-key probe metadata."
            );
        }

        if (
            !TryBindNaturalKeyParameters(
                naturalKeyProbe,
                writePlan.Model,
                documentIdentity,
                resolvedReferences,
                resource,
                out var parameters
            )
        )
        {
            return new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid);
        }

        var existingDocument = await ExecuteLookupAsync(
                commandExecutor,
                BuildNaturalKeyProbeCommand(
                    mappingSet.Key.Dialect,
                    rootTable,
                    naturalKeyProbe,
                    parameters,
                    resource
                ),
                $"resource '{RelationalWriteSupport.FormatResource(resource)}' and its request natural key",
                cancellationToken
            )
            .ConfigureAwait(false);

        return existingDocument is null
            ? new RelationalWriteTargetLookupResult.CreateNew(candidateDocumentUuid)
            : new RelationalWriteTargetLookupResult.ExistingDocument(
                existingDocument.DocumentId,
                existingDocument.DocumentUuid,
                existingDocument.ObservedContentVersion
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

    public static async Task<RelationalWriteTargetLookupResult> ResolveForPutByRootTableAsync(
        IRelationalCommandExecutor commandExecutor,
        DbTableName rootTable,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);

        var resolvedTarget = await RelationalDocumentUuidLookupSupport
            .TryResolveWriteTargetByRootTableAsync(
                commandExecutor,
                rootTable,
                documentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        return resolvedTarget is null
            ? new RelationalWriteTargetLookupResult.NotFound()
            : new RelationalWriteTargetLookupResult.ExistingDocument(
                resolvedTarget.DocumentId,
                new DocumentUuid(resolvedTarget.DocumentUuid),
                resolvedTarget.ContentVersion
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

    /// <summary>
    /// Binds one parameter per compiled natural-key column, in constraint order. Returns <c>false</c> when a
    /// part of the key has no resolvable stored value for this request, which proves no persisted row can
    /// carry the request's natural key.
    /// </summary>
    private static bool TryBindNaturalKeyParameters(
        OwnNaturalKeyProbe naturalKeyProbe,
        RelationalResourceModel resourceModel,
        DocumentIdentity documentIdentity,
        ResolvedReferenceSet resolvedReferences,
        QualifiedResourceName resource,
        out IReadOnlyList<RelationalParameter> parameters
    )
    {
        var identityValueByPath = BuildIdentityValueByPath(documentIdentity, resource);
        var referenceObjectPathByFkColumn = BuildRootReferenceObjectPathByFkColumn(resourceModel, resource);
        var documentIdByReferenceObjectPath = BuildRootScopedDocumentIdByPath(
            resolvedReferences.SuccessfulDocumentReferencesByPath,
            static resolved => resolved.DocumentId
        );
        var descriptorIdByValuePath = BuildRootScopedDocumentIdByPath(
            resolvedReferences.SuccessfulDescriptorReferencesByPath,
            static resolved => resolved.DocumentId
        );

        var boundParameters = new RelationalParameter[naturalKeyProbe.Columns.Count];

        for (var columnIndex = 0; columnIndex < naturalKeyProbe.Columns.Count; columnIndex++)
        {
            var column = naturalKeyProbe.Columns[columnIndex];
            var parameterName = FormatNaturalKeyParameterName(columnIndex);

            if (column.ReferenceIdentityJsonPath is not null)
            {
                // Re-join to the compiled reference binding that OWNS this FK column. The reference object
                // path is never recovered by stripping segments off the identity path: a reference object
                // can itself be nested, so segment stripping is not a safe inverse of the collapse.
                if (
                    !referenceObjectPathByFkColumn.TryGetValue(
                        column.ColumnName.Value,
                        out var referenceObjectPath
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Relational POST natural-key target lookup for resource '{RelationalWriteSupport.FormatResource(resource)}' "
                            + $"could not match natural-key column '{column.ColumnName.Value}' to a compiled root document-reference binding."
                    );
                }

                if (
                    !documentIdByReferenceObjectPath.TryGetValue(
                        referenceObjectPath,
                        out var referencedDocumentId
                    )
                )
                {
                    parameters = [];
                    return false;
                }

                boundParameters[columnIndex] = new RelationalParameter(parameterName, referencedDocumentId);
                continue;
            }

            if (column.ScalarSourceJsonPath is not { } scalarSourceJsonPath)
            {
                throw new InvalidOperationException(
                    $"Relational POST natural-key target lookup for resource '{RelationalWriteSupport.FormatResource(resource)}' "
                        + $"found natural-key column '{column.ColumnName.Value}' with neither a scalar nor a reference identity source path."
                );
            }

            if (column.DescriptorResource is not null)
            {
                // The stored column is the descriptor FK, so the URI has to arrive as the descriptor's
                // resolved DocumentId rather than as text.
                if (
                    !descriptorIdByValuePath.TryGetValue(
                        scalarSourceJsonPath.Canonical,
                        out var descriptorDocumentId
                    )
                )
                {
                    parameters = [];
                    return false;
                }

                boundParameters[columnIndex] = new RelationalParameter(parameterName, descriptorDocumentId);
                continue;
            }

            if (!identityValueByPath.TryGetValue(scalarSourceJsonPath.Canonical, out var scalarLiteral))
            {
                throw new InvalidOperationException(
                    $"Relational POST natural-key target lookup for resource '{RelationalWriteSupport.FormatResource(resource)}' "
                        + $"expected the request identity to supply natural-key path '{scalarSourceJsonPath.Canonical}'."
                );
            }

            if (!RelationalScalarLiteralParser.TryParse(scalarLiteral, column.ScalarType, out var value))
            {
                throw new InvalidOperationException(
                    $"Relational POST natural-key target lookup for resource '{RelationalWriteSupport.FormatResource(resource)}' "
                        + $"could not type identity value for path '{scalarSourceJsonPath.Canonical}' as "
                        + $"{column.ScalarType.Kind} for column '{column.ColumnName.Value}'."
                );
            }

            boundParameters[columnIndex] = new RelationalParameter(parameterName, value);
        }

        parameters = boundParameters;
        return true;
    }

    private static Dictionary<string, string> BuildIdentityValueByPath(
        DocumentIdentity documentIdentity,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, string> identityValueByPath = new(StringComparer.Ordinal);

        foreach (var identityElement in documentIdentity.DocumentIdentityElements)
        {
            if (
                !identityValueByPath.TryAdd(
                    identityElement.IdentityJsonPath.Value,
                    identityElement.IdentityValue
                )
            )
            {
                throw new InvalidOperationException(
                    $"Relational POST natural-key target lookup for resource '{RelationalWriteSupport.FormatResource(resource)}' "
                        + $"received a request identity with duplicate path '{identityElement.IdentityJsonPath.Value}'."
                );
            }
        }

        return identityValueByPath;
    }

    private static Dictionary<string, string> BuildRootReferenceObjectPathByFkColumn(
        RelationalResourceModel resourceModel,
        QualifiedResourceName resource
    )
    {
        Dictionary<string, string> referenceObjectPathByFkColumn = new(StringComparer.Ordinal);

        foreach (var binding in resourceModel.DocumentReferenceBindings)
        {
            if (!binding.Table.Equals(resourceModel.Root.Table))
            {
                continue;
            }

            if (
                referenceObjectPathByFkColumn.TryGetValue(binding.FkColumn.Value, out var existingPath)
                && !string.Equals(
                    existingPath,
                    binding.ReferenceObjectPath.Canonical,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    $"Relational POST natural-key target lookup for resource '{RelationalWriteSupport.FormatResource(resource)}' "
                        + $"found root foreign-key column '{binding.FkColumn.Value}' bound to multiple reference objects."
                );
            }

            referenceObjectPathByFkColumn[binding.FkColumn.Value] = binding.ReferenceObjectPath.Canonical;
        }

        return referenceObjectPathByFkColumn;
    }

    /// <summary>
    /// Projects the resolver's concrete-path-keyed successes down to the root scope. Natural-key parts never
    /// sit under an array (the probe compiler rejects array segments), so only occurrences with an empty
    /// ordinal path can supply one.
    /// </summary>
    private static Dictionary<string, long> BuildRootScopedDocumentIdByPath<TResolved>(
        IReadOnlyDictionary<JsonPath, TResolved> successfulReferencesByPath,
        Func<TResolved, long> selectDocumentId
    )
    {
        Dictionary<string, long> documentIdByPath = new(StringComparer.Ordinal);

        foreach (var entry in successfulReferencesByPath)
        {
            var parsedPath = RelationalJsonPathSupport.ParseConcretePath(entry.Key);

            if (parsedPath.OrdinalPath.Length != 0)
            {
                continue;
            }

            documentIdByPath[parsedPath.WildcardPath] = selectDocumentId(entry.Value);
        }

        return documentIdByPath;
    }

    private static string FormatNaturalKeyParameterName(int columnIndex) =>
        $"{NaturalKeyParameterNamePrefix}{columnIndex}";

    /// <summary>
    /// Builds the single-row <c>UX_&lt;R&gt;_NK</c> seek. Both dialects share one statement shape because the
    /// only difference is identifier quoting; the returned columns are the root row's document-metadata
    /// mirrors, so no <c>dms.Document</c> or <c>dms.ReferentialIdentity</c> access is involved.
    /// </summary>
    private static RelationalCommand BuildNaturalKeyProbeCommand(
        SqlDialect dialect,
        DbTableName rootTable,
        OwnNaturalKeyProbe naturalKeyProbe,
        IReadOnlyList<RelationalParameter> parameters,
        QualifiedResourceName resource
    )
    {
        if (dialect is not (SqlDialect.Pgsql or SqlDialect.Mssql))
        {
            throw new NotSupportedException(
                $"Relational POST natural-key target lookup does not support SQL dialect '{dialect}'."
            );
        }

        var documentIdColumn = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            RelationalNameConventions.DocumentIdColumnName
        );
        var documentUuidColumn = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            RelationalNameConventions.DocumentUuidColumnName
        );
        var contentVersionColumn = SqlIdentifierQuoter.QuoteIdentifier(
            dialect,
            RelationalNameConventions.ContentVersionColumnName
        );
        var predicates = string.Join(
            $"{Environment.NewLine}    AND ",
            naturalKeyProbe.Columns.Select(
                (column, columnIndex) =>
                    $"root.{SqlIdentifierQuoter.QuoteIdentifier(dialect, column.ColumnName)} = {FormatNaturalKeyParameterName(columnIndex)}"
            )
        );

        if (parameters.Count != naturalKeyProbe.Columns.Count)
        {
            throw new InvalidOperationException(
                $"Relational POST natural-key target lookup for resource '{RelationalWriteSupport.FormatResource(resource)}' "
                    + $"bound {parameters.Count} parameter(s) for {naturalKeyProbe.Columns.Count} natural-key column(s)."
            );
        }

        var commandText = $"""
            SELECT
                root.{documentIdColumn} AS {documentIdColumn},
                root.{documentUuidColumn} AS {documentUuidColumn},
                root.{contentVersionColumn} AS {contentVersionColumn}
            FROM {SqlIdentifierQuoter.QuoteTableName(dialect, rootTable)} root
            WHERE {predicates}
            """;

        return new RelationalCommand(commandText, parameters);
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
