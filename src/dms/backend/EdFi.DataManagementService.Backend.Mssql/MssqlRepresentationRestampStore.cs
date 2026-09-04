// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.DocumentCache;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Backend.Mssql;

#pragma warning disable S2325 // SQL construction helpers retain a uniform instance shape.
#pragma warning disable S6966 // Fields are read only after an awaited ReadAsync advances the provider reader.

/// <summary>
/// SQL Server implementation of the representation-restamp transactional store. Every command is
/// created by the supplied session so an administrative mutex lease remains the sole connection owner.
/// </summary>
public sealed class MssqlRepresentationRestampStore(
    IMappingSetProvider mappingSetProvider,
    IRuntimeMappingSetCompiler runtimeMappingSetCompiler
) : IRepresentationRestampStore
{
    private const string BigIntTableType = "dms.BigIntTable";
    private const string UniqueIdentifierTableType = "dms.UniqueIdentifierTable";

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IMappingSetProvider _mappingSetProvider =
        mappingSetProvider ?? throw new ArgumentNullException(nameof(mappingSetProvider));
    private readonly IRuntimeMappingSetCompiler _runtimeMappingSetCompiler =
        runtimeMappingSetCompiler ?? throw new ArgumentNullException(nameof(runtimeMappingSetCompiler));

    public async Task<long> GetMaxChangeVersionAsync(
        IRelationalWriteSession session,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);

        await using DbCommand command = session.CreateCommand(
            new RelationalCommand("SELECT [dms].[GetMaxChangeVersion]();")
        );
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    public async Task<RepresentationRestampSelection> ResolveSelectionAsync(
        IRelationalWriteSession session,
        DocumentCacheRepresentationRestampScope scope,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(scope);
        ValidatePageSize(pageSize);
        MappingSet mappingSet = await GetMappingSetAsync(cancellationToken).ConfigureAwait(false);

        return scope switch
        {
            DocumentCacheRepresentationRestampResourceScope resourceScope =>
                await ResolveResourceSelectionAsync(session, mappingSet, resourceScope, cancellationToken)
                    .ConfigureAwait(false),
            DocumentCacheRepresentationRestampDocumentUuidsScope uuidScope => await ResolveUuidSelectionAsync(
                    session,
                    mappingSet,
                    uuidScope,
                    cancellationToken
                )
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException("Representation restamp scope type is unsupported."),
        };
    }

    public async Task CreateDraftAsync(
        IRelationalWriteSession session,
        DocumentCacheRepresentationRestampOperation operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);

        const string sql = """
            INSERT INTO [dms].[RepresentationRestampOperation] (
                [OperationId], [ContractVersion], [TenantKey], [DataStoreId],
                [PhysicalSourceFingerprint], [ScopeJson], [Reason], [Mode],
                [PreRestampBoundary], [PreviewDocumentCount], [CommittedDocumentCount],
                [State], [CreatedAt], [UpdatedAt]
            ) VALUES (
                @operationId, @contractVersion, @tenantKey, @dataStoreId,
                @physicalSourceFingerprint, @scopeJson, @reason, @mode,
                @preRestampBoundary, @previewDocumentCount, @committedDocumentCount,
                @state, @createdAt, @updatedAt
            );
            """;

        int affected = await ExecuteNonQueryAsync(
                session,
                new RelationalCommand(
                    sql,
                    [
                        Parameter("@operationId", operation.OperationId, SqlDbType.UniqueIdentifier),
                        Parameter("@contractVersion", operation.ContractVersion, SqlDbType.Int),
                        Parameter("@tenantKey", operation.TargetKey.TenantKey, SqlDbType.NVarChar),
                        Parameter(
                            "@dataStoreId",
                            checked((int)operation.TargetKey.DataStoreId),
                            SqlDbType.Int
                        ),
                        Parameter(
                            "@physicalSourceFingerprint",
                            operation.PhysicalSourceFingerprint.Value,
                            SqlDbType.NVarChar
                        ),
                        NVarCharMaxParameter(
                            "@scopeJson",
                            JsonSerializer.Serialize<DocumentCacheRepresentationRestampScope>(
                                operation.Scope,
                                _jsonOptions
                            )
                        ),
                        Parameter("@reason", operation.Reason, SqlDbType.NVarChar),
                        Parameter("@mode", operation.Mode.ToString(), SqlDbType.NVarChar),
                        Parameter("@preRestampBoundary", operation.PreRestampBoundary, SqlDbType.BigInt),
                        Parameter("@previewDocumentCount", operation.PreviewDocumentCount, SqlDbType.BigInt),
                        Parameter(
                            "@committedDocumentCount",
                            operation.CommittedDocumentCount,
                            SqlDbType.BigInt
                        ),
                        Parameter("@state", operation.State.ToString(), SqlDbType.NVarChar),
                        Parameter("@createdAt", operation.CreatedAt.UtcDateTime, SqlDbType.DateTime2),
                        Parameter("@updatedAt", operation.UpdatedAt.UtcDateTime, SqlDbType.DateTime2),
                    ]
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        RequireExactlyOne(affected, "create the representation restamp manifest");
    }

    public async Task<DocumentCacheRepresentationRestampOperation?> LoadAsync(
        IRelationalWriteSession session,
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        const string sql = """
            SELECT [OperationId], [ContractVersion], [TenantKey], [DataStoreId],
                   [PhysicalSourceFingerprint], [ScopeJson], [Reason], [Mode],
                   [PreRestampBoundary], [PreviewDocumentCount], [CommittedDocumentCount],
                   [State], [CreatedAt], [UpdatedAt]
            FROM [dms].[RepresentationRestampOperation]
            WHERE [OperationId] = @operationId;
            """;

        await using DbCommand command = session.CreateCommand(
            new RelationalCommand(sql, [Parameter("@operationId", operationId, SqlDbType.UniqueIdentifier)])
        );
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DocumentCacheRepresentationRestampScope scope =
            JsonSerializer.Deserialize<DocumentCacheRepresentationRestampScope>(
                reader.GetFieldValue<string>(5),
                _jsonOptions
            )
            ?? throw new InvalidOperationException(
                "Representation restamp manifest contains an invalid scope."
            );
        if (!scope.TryCanonicalize(int.MaxValue, out DocumentCacheRepresentationRestampScope canonicalScope))
        {
            throw new InvalidOperationException(
                "Representation restamp manifest contains a non-canonical scope."
            );
        }

        DocumentCacheRepresentationRestampOperation operation = new(
            reader.GetFieldValue<Guid>(0),
            reader.GetFieldValue<int>(1),
            new DocumentCacheAdministrativeTargetKey(
                reader.GetFieldValue<string>(2),
                reader.GetFieldValue<int>(3)
            ),
            new DocumentCachePhysicalSourceFingerprint(reader.GetFieldValue<string>(4)),
            canonicalScope,
            reader.GetFieldValue<string>(6),
            ParseEnum<DocumentCacheRepresentationRestampMode>(reader.GetFieldValue<string>(7), "mode"),
            reader.GetFieldValue<long>(8),
            reader.GetFieldValue<long>(9),
            reader.GetFieldValue<long>(10),
            ParseEnum<DocumentCacheRepresentationRestampOperationState>(
                reader.GetFieldValue<string>(11),
                "state"
            ),
            ReadUtcDateTimeOffset(reader, 12),
            ReadUtcDateTimeOffset(reader, 13)
        );
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Representation restamp manifest lookup was ambiguous.");
        }

        return operation;
    }

    public async Task<RepresentationRestampPage> SelectNextPageAsync(
        IRelationalWriteSession session,
        DocumentCacheRepresentationRestampOperation operation,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);
        ValidatePageSize(pageSize);
        MappingSet mappingSet = await GetMappingSetAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<SelectedDocument> documents = await ReadEligibleDocumentsAsync(
                session,
                mappingSet,
                operation.Scope,
                operation.PreRestampBoundary,
                pageSize,
                cancellationToken
            )
            .ConfigureAwait(false);
        return new RepresentationRestampPage([
            .. documents.Select(document => ToRepresentationDocument(mappingSet, document)),
        ]);
    }

    public async Task<RepresentationRestampPageCommit> StampPageAsync(
        IRelationalWriteSession session,
        RepresentationRestampPage page,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(page);
        if (page.IsEmpty)
        {
            throw new ArgumentException("Cannot stamp an empty representation restamp page.", nameof(page));
        }

        DataTable documentIdTable = CreateDocumentIdTable(
            page.Documents.Select(document => document.DocumentId)
        );
        // dms.Document carries the document-stamping trigger, and SQL Server refuses a bare
        // OUTPUT (no INTO) on any DML target that has enabled triggers. Route the stamped tuples
        // through a table variable instead, then select them back in the same batch.
        const string stampSql = """
            DECLARE @stamped TABLE (
                [DocumentId] bigint NOT NULL PRIMARY KEY,
                [ContentVersion] bigint NOT NULL,
                [ContentLastModifiedAt] datetime2(7) NOT NULL
            );

            UPDATE d
            SET d.[ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
                d.[ContentLastModifiedAt] = SYSUTCDATETIME()
            OUTPUT inserted.[DocumentId], inserted.[ContentVersion], inserted.[ContentLastModifiedAt]
                INTO @stamped
            FROM [dms].[Document] AS d
            INNER JOIN @documentIds AS ids ON ids.[Id] = d.[DocumentId];

            SELECT [DocumentId], [ContentVersion], [ContentLastModifiedAt] FROM @stamped;
            """;
        ImmutableArray<RepresentationRestampStamp> stamps = await ReadStampsAsync(
                session,
                new RelationalCommand(
                    stampSql,
                    [StructuredParameter("@documentIds", documentIdTable, BigIntTableType)]
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (
            stamps.Length != page.Count
            || stamps.Select(stamp => stamp.DocumentId).Distinct().Count() != page.Count
        )
        {
            throw new InvalidOperationException(
                "Canonical representation restamp update did not stamp the selected page exactly once."
            );
        }

        Dictionary<long, RepresentationRestampStamp> stampsByDocumentId = stamps.ToDictionary(stamp =>
            stamp.DocumentId
        );
        int mirrorStampedCount = 0;
        foreach (
            IGrouping<
                RepresentationRestampMirrorRoute,
                RepresentationRestampDocument
            > group in page.Documents.GroupBy(document => document.MirrorRoute)
        )
        {
            ImmutableArray<RepresentationRestampStamp> groupStamps =
            [
                .. group.Select(document => stampsByDocumentId[document.DocumentId]),
            ];
            int affected = await UpdateMirrorAsync(session, group.Key, groupStamps, cancellationToken)
                .ConfigureAwait(false);
            if (affected != groupStamps.Length)
            {
                throw new InvalidOperationException(
                    $"Representation restamp mirror '{group.Key.MirrorStampTargetSchema}.{group.Key.MirrorStampTargetTable}' did not stamp the selected page exactly once."
                );
            }

            mirrorStampedCount += affected;
        }

        return new RepresentationRestampPageCommit(page, stamps, stamps.Length, mirrorStampedCount);
    }

    public async Task UpdateProgressAsync(
        IRelationalWriteSession session,
        Guid operationId,
        long committedCount,
        DocumentCacheRepresentationRestampOperationState state,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        if (state is not DocumentCacheRepresentationRestampOperationState.Incomplete)
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                "Only incomplete page progress may be written."
            );
        }

        const string sql = """
            DECLARE @updated TABLE ([OperationId] uniqueidentifier NOT NULL);

            UPDATE [dms].[RepresentationRestampOperation]
            SET [CommittedDocumentCount] = @committedDocumentCount,
                [State] = @state,
                [UpdatedAt] = SYSUTCDATETIME()
            OUTPUT inserted.[OperationId] INTO @updated
            WHERE [OperationId] = @operationId
              AND [State] IN ('Draft', 'Incomplete')
              AND [CommittedDocumentCount] < @committedDocumentCount
              AND @committedDocumentCount <= [PreviewDocumentCount];
            SELECT COUNT(*) FROM @updated;
            """;
        int affected = await ExecuteScalarAsync<int>(
                session,
                new RelationalCommand(
                    sql,
                    [
                        Parameter("@operationId", operationId, SqlDbType.UniqueIdentifier),
                        Parameter("@committedDocumentCount", committedCount, SqlDbType.BigInt),
                        Parameter("@state", state.ToString(), SqlDbType.NVarChar),
                    ]
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        RequireExactlyOne(affected, "update representation restamp progress");
    }

    public async Task<long> CountRemainingEligibleAsync(
        IRelationalWriteSession session,
        DocumentCacheRepresentationRestampOperation operation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);
        MappingSet mappingSet = await GetMappingSetAsync(cancellationToken).ConfigureAwait(false);
        return await CountEligibleAsync(
                session,
                mappingSet,
                operation.Scope,
                operation.PreRestampBoundary,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public async Task MarkCompletedAsync(
        IRelationalWriteSession session,
        Guid operationId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        const string sql = """
            DECLARE @updated TABLE ([OperationId] uniqueidentifier NOT NULL);

            UPDATE [dms].[RepresentationRestampOperation]
            SET [State] = 'Completed', [UpdatedAt] = SYSUTCDATETIME()
            OUTPUT inserted.[OperationId] INTO @updated
            WHERE [OperationId] = @operationId
              AND [State] IN ('Draft', 'Incomplete')
              AND [CommittedDocumentCount] = [PreviewDocumentCount];
            SELECT COUNT(*) FROM @updated;
            """;
        int affected = await ExecuteScalarAsync<int>(
                session,
                new RelationalCommand(
                    sql,
                    [Parameter("@operationId", operationId, SqlDbType.UniqueIdentifier)]
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        RequireExactlyOne(affected, "complete the representation restamp manifest");
    }

    private async Task<RepresentationRestampSelection> ResolveResourceSelectionAsync(
        IRelationalWriteSession session,
        MappingSet mappingSet,
        DocumentCacheRepresentationRestampResourceScope scope,
        CancellationToken cancellationToken
    )
    {
        short resourceKeyId = ResolveResourceKeyId(mappingSet, scope);

        _ = ResolveRoute(mappingSet, resourceKeyId);
        long count = await ExecuteScalarAsync<long>(
                session,
                new RelationalCommand(
                    "SELECT COUNT(*) FROM [dms].[Document] WHERE [ResourceKeyId] = @resourceKeyId;",
                    [Parameter("@resourceKeyId", resourceKeyId, SqlDbType.SmallInt)]
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        return new RepresentationRestampSelection(scope, count);
    }

    private async Task<RepresentationRestampSelection> ResolveUuidSelectionAsync(
        IRelationalWriteSession session,
        MappingSet mappingSet,
        DocumentCacheRepresentationRestampDocumentUuidsScope scope,
        CancellationToken cancellationToken
    )
    {
        ImmutableArray<SelectedDocument> documents = await ReadDocumentsByUuidAsync(
                session,
                scope.DocumentUuids,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (documents.Length != scope.DocumentUuids.Length)
        {
            throw new InvalidOperationException(
                "Representation restamp UUID scope contains a document UUID that does not resolve uniquely to a current document."
            );
        }

        foreach (SelectedDocument document in documents)
        {
            _ = ResolveRoute(mappingSet, document.ResourceKeyId);
        }

        return new RepresentationRestampSelection(scope, documents.Length);
    }

    private async Task<ImmutableArray<SelectedDocument>> ReadEligibleDocumentsAsync(
        IRelationalWriteSession session,
        MappingSet mappingSet,
        DocumentCacheRepresentationRestampScope scope,
        long boundary,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        RelationalCommand command = scope switch
        {
            DocumentCacheRepresentationRestampResourceScope resourceScope => new(
                """
                SELECT [DocumentId], [DocumentUuid], [ResourceKeyId]
                FROM [dms].[Document]
                WHERE [ResourceKeyId] = @resourceKeyId
                  AND [ContentVersion] <= @boundary
                ORDER BY [DocumentId]
                OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY;
                """,
                [
                    Parameter(
                        "@resourceKeyId",
                        ResolveResourceKeyId(mappingSet, resourceScope),
                        SqlDbType.SmallInt
                    ),
                    Parameter("@boundary", boundary, SqlDbType.BigInt),
                    Parameter("@pageSize", pageSize, SqlDbType.Int),
                ]
            ),
            DocumentCacheRepresentationRestampDocumentUuidsScope uuidScope => new(
                """
                SELECT [DocumentId], [DocumentUuid], [ResourceKeyId]
                FROM [dms].[Document]
                WHERE [ContentVersion] <= @boundary
                  AND [DocumentUuid] IN (SELECT [Id] FROM @documentUuids)
                ORDER BY [DocumentId]
                OFFSET 0 ROWS FETCH NEXT @pageSize ROWS ONLY;
                """,
                [
                    Parameter("@boundary", boundary, SqlDbType.BigInt),
                    StructuredParameter(
                        "@documentUuids",
                        CreateDocumentUuidTable(uuidScope.DocumentUuids),
                        UniqueIdentifierTableType
                    ),
                    Parameter("@pageSize", pageSize, SqlDbType.Int),
                ]
            ),
            _ => throw new InvalidOperationException("Representation restamp scope type is unsupported."),
        };

        return await ReadSelectedDocumentsAsync(session, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> CountEligibleAsync(
        IRelationalWriteSession session,
        MappingSet mappingSet,
        DocumentCacheRepresentationRestampScope scope,
        long boundary,
        CancellationToken cancellationToken
    )
    {
        RelationalCommand command = scope switch
        {
            DocumentCacheRepresentationRestampResourceScope resourceScope => new(
                """
                SELECT COUNT(*)
                FROM [dms].[Document]
                WHERE [ResourceKeyId] = @resourceKeyId
                  AND [ContentVersion] <= @boundary;
                """,
                [
                    Parameter(
                        "@resourceKeyId",
                        ResolveResourceKeyId(mappingSet, resourceScope),
                        SqlDbType.SmallInt
                    ),
                    Parameter("@boundary", boundary, SqlDbType.BigInt),
                ]
            ),
            DocumentCacheRepresentationRestampDocumentUuidsScope uuidScope => new(
                """
                SELECT COUNT(*)
                FROM [dms].[Document]
                WHERE [ContentVersion] <= @boundary
                  AND [DocumentUuid] IN (SELECT [Id] FROM @documentUuids);
                """,
                [
                    Parameter("@boundary", boundary, SqlDbType.BigInt),
                    StructuredParameter(
                        "@documentUuids",
                        CreateDocumentUuidTable(uuidScope.DocumentUuids),
                        UniqueIdentifierTableType
                    ),
                ]
            ),
            _ => throw new InvalidOperationException("Representation restamp scope type is unsupported."),
        };
        return await ExecuteScalarAsync<long>(session, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImmutableArray<SelectedDocument>> ReadDocumentsByUuidAsync(
        IRelationalWriteSession session,
        ImmutableArray<Guid> documentUuids,
        CancellationToken cancellationToken
    ) =>
        await ReadSelectedDocumentsAsync(
                session,
                new RelationalCommand(
                    """
                    SELECT [DocumentId], [DocumentUuid], [ResourceKeyId]
                    FROM [dms].[Document]
                    WHERE [DocumentUuid] IN (SELECT [Id] FROM @documentUuids)
                    ORDER BY [DocumentId];
                    """,
                    [
                        StructuredParameter(
                            "@documentUuids",
                            CreateDocumentUuidTable(documentUuids),
                            UniqueIdentifierTableType
                        ),
                    ]
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

    private static async Task<ImmutableArray<SelectedDocument>> ReadSelectedDocumentsAsync(
        IRelationalWriteSession session,
        RelationalCommand relationalCommand,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = session.CreateCommand(relationalCommand);
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        ImmutableArray<SelectedDocument>.Builder documents = ImmutableArray.CreateBuilder<SelectedDocument>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(
                new SelectedDocument(
                    reader.GetFieldValue<long>(0),
                    reader.GetFieldValue<Guid>(1),
                    reader.GetFieldValue<short>(2)
                )
            );
        }

        return documents.ToImmutable();
    }

    private static async Task<ImmutableArray<RepresentationRestampStamp>> ReadStampsAsync(
        IRelationalWriteSession session,
        RelationalCommand relationalCommand,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = session.CreateCommand(relationalCommand);
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        ImmutableArray<RepresentationRestampStamp>.Builder stamps =
            ImmutableArray.CreateBuilder<RepresentationRestampStamp>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            stamps.Add(
                new RepresentationRestampStamp(
                    reader.GetFieldValue<long>(0),
                    reader.GetFieldValue<long>(1),
                    ReadUtcDateTimeOffset(reader, 2)
                )
            );
        }

        return stamps.ToImmutable();
    }

    private static async Task<int> UpdateMirrorAsync(
        IRelationalWriteSession session,
        RepresentationRestampMirrorRoute route,
        ImmutableArray<RepresentationRestampStamp> stamps,
        CancellationToken cancellationToken
    )
    {
        List<RelationalParameter> parameters = [];
        List<string> values = [];
        for (var index = 0; index < stamps.Length; index++)
        {
            RepresentationRestampStamp stamp = stamps[index];
            values.Add($"(@documentId{index}, @contentVersion{index}, @contentLastModifiedAt{index})");
            parameters.Add(Parameter($"@documentId{index}", stamp.DocumentId, SqlDbType.BigInt));
            parameters.Add(Parameter($"@contentVersion{index}", stamp.ContentVersion, SqlDbType.BigInt));
            parameters.Add(
                Parameter(
                    $"@contentLastModifiedAt{index}",
                    stamp.ContentLastModifiedAt.UtcDateTime,
                    SqlDbType.DateTime2
                )
            );
        }

        string table =
            SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, route.MirrorStampTargetSchema)
            + "."
            + SqlIdentifierQuoter.QuoteIdentifier(SqlDialect.Mssql, route.MirrorStampTargetTable);
        string sql = $$"""
            UPDATE mirror
            SET mirror.[ContentVersion] = stamped.[ContentVersion],
                mirror.[ContentLastModifiedAt] = stamped.[ContentLastModifiedAt]
            FROM {{table}} AS mirror
            INNER JOIN (VALUES {{string.Join(", ", values)}})
                AS stamped([DocumentId], [ContentVersion], [ContentLastModifiedAt])
                ON mirror.[DocumentId] = stamped.[DocumentId];
            """;
        return await ExecuteNonQueryAsync(session, new RelationalCommand(sql, parameters), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MappingSet> GetMappingSetAsync(CancellationToken cancellationToken)
    {
        if (_runtimeMappingSetCompiler.Dialect is not SqlDialect.Mssql)
        {
            throw new InvalidOperationException(
                "SQL Server representation restamp store requires a SQL Server mapping set compiler."
            );
        }

        MappingSet mappingSet = await _mappingSetProvider
            .GetOrCreateAsync(_runtimeMappingSetCompiler.GetCurrentKey(), cancellationToken)
            .ConfigureAwait(false);
        if (mappingSet.Key.Dialect is not SqlDialect.Mssql)
        {
            throw new InvalidOperationException(
                "SQL Server representation restamp store received a non-SQL Server mapping set."
            );
        }

        return mappingSet;
    }

    private static RepresentationRestampDocument ToRepresentationDocument(
        MappingSet mappingSet,
        SelectedDocument document
    ) => new(document.DocumentId, document.DocumentUuid, ResolveRoute(mappingSet, document.ResourceKeyId));

    private static short ResolveResourceKeyId(
        MappingSet mappingSet,
        DocumentCacheRepresentationRestampResourceScope scope
    )
    {
        QualifiedResourceName resource = new(scope.ProjectName, scope.ResourceName);
        if (!mappingSet.ResourceKeyIdByResource.TryGetValue(resource, out short resourceKeyId))
        {
            throw new InvalidOperationException(
                "Representation restamp resource scope did not resolve to a compiled resource key."
            );
        }

        _ = ResolveRoute(mappingSet, resourceKeyId);
        return resourceKeyId;
    }

    private static RepresentationRestampMirrorRoute ResolveRoute(MappingSet mappingSet, short resourceKeyId)
    {
        if (!mappingSet.ResourceKeyById.TryGetValue(resourceKeyId, out ResourceKeyEntry? resourceKey))
        {
            throw new InvalidOperationException(
                "Representation restamp document resource key is not present in the compiled mapping set."
            );
        }

        ConcreteResourceModel? model = mappingSet.Model.ConcreteResourcesInNameOrder.SingleOrDefault(
            candidate => candidate.ResourceKey.Resource.Equals(resourceKey.Resource)
        );
        if (model is null)
        {
            throw new InvalidOperationException(
                "Representation restamp document resource is missing compiled metadata."
            );
        }
        if (model.ResourceKey.ResourceKeyId != resourceKeyId)
        {
            throw new InvalidOperationException(
                "Representation restamp document resource key does not match compiled resource metadata."
            );
        }

        DbTriggerInfo? trigger = mappingSet.Model.TriggersInCreateOrder.SingleOrDefault(trigger =>
            trigger.Table.Equals(model.RelationalModel.Root.Table)
            && trigger.Parameters is TriggerKindParameters.DocumentStamping
        );
        if (trigger?.MirrorStampTargetTable is not { } mirrorTarget)
        {
            throw new InvalidOperationException(
                "Representation restamp document resource has no unique compiled document-stamping mirror route."
            );
        }

        return new RepresentationRestampMirrorRoute(
            resourceKeyId,
            mirrorTarget.Schema.Value,
            mirrorTarget.Name,
            mirrorTarget.Equals(new DbTableName(new DbSchemaName("dms"), "Descriptor"))
        );
    }

    private static DataTable CreateDocumentIdTable(IEnumerable<long> documentIds)
    {
        DataTable table = new();
        table.Columns.Add("Id", typeof(long));
        foreach (long documentId in documentIds)
        {
            table.Rows.Add(documentId);
        }

        return table;
    }

    private static DataTable CreateDocumentUuidTable(IEnumerable<Guid> documentUuids)
    {
        DataTable table = new();
        table.Columns.Add("Id", typeof(Guid));
        foreach (Guid documentUuid in documentUuids)
        {
            table.Rows.Add(documentUuid);
        }

        return table;
    }

    private static DateTimeOffset ReadUtcDateTimeOffset(DbDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetFieldValue<DateTime>(ordinal), DateTimeKind.Utc));

    private static RelationalParameter Parameter(string name, object value, SqlDbType dbType) =>
        new(name, value, parameter => ((SqlParameter)parameter).SqlDbType = dbType);

    private static RelationalParameter NVarCharMaxParameter(string name, string value) =>
        new(
            name,
            value,
            parameter =>
            {
                var sqlParameter = (SqlParameter)parameter;
                sqlParameter.SqlDbType = SqlDbType.NVarChar;
                sqlParameter.Size = -1;
            }
        );

    private static RelationalParameter StructuredParameter(string name, DataTable table, string typeName) =>
        new(
            name,
            table,
            parameter =>
            {
                var sqlParameter = (SqlParameter)parameter;
                sqlParameter.SqlDbType = SqlDbType.Structured;
                sqlParameter.TypeName = typeName;
            }
        );

    private static async Task<int> ExecuteNonQueryAsync(
        IRelationalWriteSession session,
        RelationalCommand relationalCommand,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = session.CreateCommand(relationalCommand);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        IRelationalWriteSession session,
        RelationalCommand relationalCommand,
        CancellationToken cancellationToken
    )
    {
        await using DbCommand command = session.CreateCommand(relationalCommand);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new InvalidOperationException("Representation restamp query returned no scalar value.");
        }

        return (T)Convert.ChangeType(result, typeof(T));
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName)
        where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out TEnum parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Representation restamp manifest contains an invalid {fieldName}."
            );

    private static void ValidatePageSize(int pageSize)
    {
        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                "Representation restamp page size must be positive."
            );
        }
    }

    private static void RequireExactlyOne(int affected, string operation)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Unable to {operation}; expected one affected row but observed {affected}."
            );
        }
    }

    private sealed record SelectedDocument(long DocumentId, Guid DocumentUuid, short ResourceKeyId);
}

#pragma warning restore S6966
#pragma warning restore S2325
