// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Descriptor write handler that persists descriptor resources into
/// <c>dms.Document</c>, <c>dms.Descriptor</c>, and <c>dms.ReferentialIdentity</c>.
/// </summary>
internal sealed class DescriptorWriteHandler(
    IRelationalWriteTargetLookupService targetLookupService,
    IRelationalCommandExecutor commandExecutor,
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IRelationalDeleteConstraintResolver deleteConstraintResolver,
    IRelationalWriteSessionFactory writeSessionFactory,
    IReadableProfileProjector readableProfileProjector,
    ILogger<DescriptorWriteHandler> logger
) : IDescriptorWriteHandler
{
    private readonly IRelationalWriteTargetLookupService _targetLookupService =
        targetLookupService ?? throw new ArgumentNullException(nameof(targetLookupService));
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));
    private readonly IRelationalDeleteConstraintResolver _deleteConstraintResolver =
        deleteConstraintResolver ?? throw new ArgumentNullException(nameof(deleteConstraintResolver));
    private readonly IRelationalWriteSessionFactory _writeSessionFactory =
        writeSessionFactory ?? throw new ArgumentNullException(nameof(writeSessionFactory));
    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));
    private readonly ILogger<DescriptorWriteHandler> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<UpsertResult> HandlePostAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ReferentialId is null)
        {
            throw new InvalidOperationException(
                "Descriptor POST requires a ReferentialId for target context resolution."
            );
        }

        var body = DescriptorWriteBodyExtractor.Extract(request.RequestBody, request.Resource);
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(
            request.MappingSet,
            request.Resource
        );

        _logger.LogDebug(
            "Resolving descriptor POST target context for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        var targetLookupResult = await _targetLookupService
            .ResolveForPostAsync(
                request.MappingSet,
                request.Resource,
                request.ReferentialId.Value,
                request.DocumentUuid,
                cancellationToken
            )
            .ConfigureAwait(false);

        var targetContext =
            RelationalWriteSupport.TryTranslateTargetContext(targetLookupResult)
            ?? throw new InvalidOperationException(
                $"Unexpected target lookup result type '{targetLookupResult.GetType().Name}' for descriptor POST."
            );

        try
        {
            if (targetContext is RelationalWriteTargetContext.CreateNew(var newDocumentUuid))
            {
                return await InsertDescriptorAsync(
                        request,
                        body,
                        newDocumentUuid,
                        resourceKeyId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            if (
                targetContext
                is not RelationalWriteTargetContext.ExistingDocument
                (var existingDocId, var existingDocUuid, _)
            )
            {
                throw new InvalidOperationException(
                    $"Unexpected target context type '{targetContext.GetType().Name}' for descriptor POST."
                );
            }

            if (request.IfMatchEtag is not null)
            {
                // Use a locked read+write session to prevent TOCTOU race: the SELECT acquires a
                // row lock (FOR UPDATE / UPDLOCK ROWLOCK) held for the lifetime of the transaction,
                // so no concurrent writer can modify the row between the pre-check and the UPDATE.
                // If-Match: * is not supported; any non-null value (including "*") is treated as
                // a literal ETag for precondition enforcement.
                return await ExecutePostAsUpdateWithLockedSessionAsync(
                        request,
                        body,
                        existingDocId,
                        existingDocUuid,
                        resourceKeyId,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            return await UpdateDescriptorForUpsertAsync(
                    request,
                    body,
                    existingDocId,
                    existingDocUuid,
                    resourceKeyId,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            return MapPostDbException(request, ex);
        }
    }

    public async Task<UpdateResult> HandlePutAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var body = DescriptorWriteBodyExtractor.Extract(request.RequestBody, request.Resource);

        _logger.LogDebug(
            "Resolving descriptor PUT target context for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        var targetLookupResult = await _targetLookupService
            .ResolveForPutAsync(request.MappingSet, request.Resource, request.DocumentUuid, cancellationToken)
            .ConfigureAwait(false);

        if (targetLookupResult is RelationalWriteTargetLookupResult.NotFound)
        {
            return request.IfMatchEtag is not null
                ? new UpdateResult.UpdateFailureETagMisMatch()
                : new UpdateResult.UpdateFailureNotExists();
        }

        var targetContext =
            RelationalWriteSupport.TryTranslateTargetContext(targetLookupResult)
            ?? throw new InvalidOperationException(
                $"Unexpected target lookup result type '{targetLookupResult.GetType().Name}' for descriptor PUT."
            );

        if (
            targetContext
            is not RelationalWriteTargetContext.ExistingDocument
            (var documentId, var documentUuid, _)
        )
        {
            throw new InvalidOperationException(
                $"Unexpected target context type '{targetContext.GetType().Name}' for descriptor PUT."
            );
        }

        // When IfMatchEtag is provided, use a locked read+write session to prevent TOCTOU race:
        // the SELECT acquires a row lock (FOR UPDATE / UPDLOCK ROWLOCK) held for the lifetime of
        // the transaction, so no concurrent writer can modify the row between the pre-check and
        // the subsequent UPDATE. If-Match: * is not supported; any non-null value (including "*")
        // is treated as a literal ETag for precondition enforcement.
        if (request.IfMatchEtag is not null)
        {
            try
            {
                return await ExecutePutWithLockedSessionAsync(
                        request,
                        body,
                        documentId,
                        documentUuid,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (DbException ex)
            {
                return MapPutDbException(request, ex, lockedSession: true);
            }
        }

        // Non-locked path: no IfMatchEtag guard; proceed without a transaction.
        var persisted = await ReadPersistedDescriptorAsync(
                request.MappingSet.Key.Dialect,
                documentId,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (persisted is null)
        {
            return new UpdateResult.UnknownFailure(
                $"Descriptor row not found for DocumentId {documentId} on resource "
                    + $"'{RelationalWriteSupport.FormatResource(request.Resource)}'."
            );
        }

        if (!string.Equals(body.Uri, persisted.Uri, StringComparison.Ordinal))
        {
            return new UpdateResult.UpdateFailureImmutableIdentity(
                $"Identity of resource '{RelationalWriteSupport.FormatResource(request.Resource)}' "
                    + "cannot be changed. Descriptor identity fields (Namespace, CodeValue) are immutable on PUT."
            );
        }

        if (IsDescriptorUnchanged(body, persisted))
        {
            _logger.LogDebug(
                "Descriptor PUT is a no-op for {Resource} (DocumentId={DocumentId}) - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                documentId,
                request.TraceId.Value
            );

            return new UpdateResult.UpdateSuccess(
                documentUuid,
                ComputeEtagFromPersistedState(persisted, request.BackendProfileWriteContext)
            );
        }

        _logger.LogDebug(
            "Updating descriptor {Resource} (DocumentId={DocumentId}) via PUT - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            documentId,
            request.TraceId.Value
        );

        try
        {
            var command = request.MappingSet.Key.Dialect switch
            {
                SqlDialect.Pgsql => BuildPostgresqlUpdateCommand(body, documentId),
                SqlDialect.Mssql => BuildMssqlUpdateCommand(body, documentId),
                _ => throw new NotSupportedException(
                    $"Descriptor write does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
                ),
            };

            await ExecuteWriteCommandAsync(command, _commandExecutor, cancellationToken)
                .ConfigureAwait(false);

            return new UpdateResult.UpdateSuccess(
                documentUuid,
                ComputeEtagFromDescriptorBody(
                    body,
                    request.BackendProfileWriteContext?.IfMatchReadableProjectionContext
                )
            );
        }
        catch (DbException ex)
        {
            return MapPutDbException(request, ex, lockedSession: false);
        }
    }

    public async Task<DeleteResult> HandleDeleteAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        TraceId traceId,
        string? ifMatchEtag = null,
        ReadableProfileProjectionContext? ifMatchReadableProjectionContext = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Deleting descriptor document {DocumentUuid} for {Resource} - {TraceId}",
            documentUuid.Value,
            RelationalWriteSupport.FormatResource(resource),
            traceId.Value
        );

        // Scope the DELETE by ResourceKeyId so a UUID belonging to a different descriptor
        // (or a non-descriptor document) cannot be deleted through this resource endpoint.
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);

        if (ifMatchEtag is not null)
        {
            try
            {
                return await ExecuteDeleteWithIfMatchAsync(
                        mappingSet,
                        documentUuid,
                        traceId,
                        resourceKeyId,
                        ifMatchEtag,
                        ifMatchReadableProjectionContext,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (DbException ex)
            {
                return MapDeleteDbException(documentUuid, traceId, ex);
            }
        }

        var command = BuildDescriptorDeleteCommand(_commandExecutor.Dialect, documentUuid, resourceKeyId);

        return await RelationalDeleteExecution
            .TryExecuteAsync(
                _commandExecutor,
                command,
                _writeExceptionClassifier,
                _deleteConstraintResolver,
                mappingSet.Model,
                _logger,
                documentUuid,
                traceId,
                DeleteTargetKind.Descriptor,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<DeleteResult> ExecuteDeleteWithIfMatchAsync(
        MappingSet mappingSet,
        DocumentUuid documentUuid,
        TraceId traceId,
        short resourceKeyId,
        string ifMatchEtag,
        ReadableProfileProjectionContext? ifMatchReadableProjectionContext,
        CancellationToken cancellationToken
    )
    {
        await using var session = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        var sessionExecutor = new SessionRelationalCommandExecutor(session.Connection, session.Transaction);

        try
        {
            var persisted = await ReadPersistedDescriptorForDeleteAsync(
                    _commandExecutor.Dialect,
                    sessionExecutor,
                    documentUuid,
                    resourceKeyId,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (persisted is null)
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new DeleteResult.DeleteFailureETagMisMatch();
            }

            if (IsDescriptorEtagMismatch(ifMatchEtag, persisted, ifMatchReadableProjectionContext))
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new DeleteResult.DeleteFailureETagMisMatch();
            }

            var command = BuildDescriptorDeleteCommand(_commandExecutor.Dialect, documentUuid, resourceKeyId);
            var outcome = await RelationalDeleteExecution
                .TryExecuteAsync(
                    sessionExecutor,
                    command,
                    _writeExceptionClassifier,
                    _deleteConstraintResolver,
                    mappingSet.Model,
                    _logger,
                    documentUuid,
                    traceId,
                    DeleteTargetKind.Descriptor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (outcome is DeleteResult.DeleteSuccess)
            {
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
                return outcome;
            }

            await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return outcome;
        }
        catch (DbException ex)
        {
            await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MapDeleteDbException(documentUuid, traceId, ex);
        }
    }

    private static async Task<PersistedDescriptorState?> ReadPersistedDescriptorForDeleteAsync(
        SqlDialect dialect,
        IRelationalCommandExecutor executor,
        DocumentUuid documentUuid,
        short resourceKeyId,
        CancellationToken cancellationToken
    )
    {
        var command = dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlLockedDeleteReadCommand(documentUuid, resourceKeyId),
            SqlDialect.Mssql => BuildMssqlLockedDeleteReadCommand(documentUuid, resourceKeyId),
            _ => throw new NotSupportedException(
                $"Descriptor delete does not support SQL dialect '{dialect}'."
            ),
        };

        return await executor
            .ExecuteReaderAsync(
                command,
                static async (reader, ct) =>
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    var persisted = new PersistedDescriptorState(
                        Namespace: reader.GetRequiredFieldValue<string>("Namespace"),
                        CodeValue: reader.GetRequiredFieldValue<string>("CodeValue"),
                        Uri: reader.GetRequiredFieldValue<string>("Uri"),
                        ShortDescription: reader.GetNullableFieldValue<string>("ShortDescription"),
                        Description: reader.GetNullableFieldValue<string>("Description"),
                        EffectiveBeginDate: reader.GetNullableDateFieldValue("EffectiveBeginDate"),
                        EffectiveEndDate: reader.GetNullableDateFieldValue("EffectiveEndDate")
                    );

                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException(
                            "Descriptor delete locked read returned multiple rows."
                        );
                    }

                    return persisted;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static RelationalCommand BuildDescriptorDeleteCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                DELETE FROM dms."Document"
                WHERE "DocumentUuid" = @documentUuid
                  AND "ResourceKeyId" = @resourceKeyId
                RETURNING "DocumentId";
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                DELETE FROM [dms].[Document]
                OUTPUT DELETED.[DocumentId]
                WHERE [DocumentUuid] = @documentUuid
                  AND [ResourceKeyId] = @resourceKeyId;
                """,
                [
                    new RelationalParameter("@documentUuid", documentUuid.Value),
                    new RelationalParameter("@resourceKeyId", resourceKeyId),
                ]
            ),
            _ => throw new NotSupportedException(
                $"Descriptor delete does not support SQL dialect '{dialect}'."
            ),
        };
    }

    private async Task<UpsertResult> InsertDescriptorAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        CancellationToken cancellationToken
    )
    {
        _logger.LogDebug(
            "Inserting new descriptor {Resource} with DocumentUuid {DocumentUuid} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            documentUuid.Value,
            request.TraceId.Value
        );

        var command = request.MappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlInsertCommand(
                body,
                documentUuid,
                resourceKeyId,
                request.ReferentialId!.Value
            ),
            SqlDialect.Mssql => BuildMssqlInsertCommand(
                body,
                documentUuid,
                resourceKeyId,
                request.ReferentialId!.Value
            ),
            _ => throw new NotSupportedException(
                $"Descriptor write does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
            ),
        };

        await ExecuteWriteCommandAsync(command, _commandExecutor, cancellationToken).ConfigureAwait(false);

        return new UpsertResult.InsertSuccess(
            documentUuid,
            ComputeEtagFromDescriptorBody(
                body,
                request.BackendProfileWriteContext?.IfMatchReadableProjectionContext
            )
        );
    }

    private async Task<UpsertResult> UpdateDescriptorForUpsertAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        DocumentUuid existingDocumentUuid,
        short resourceKeyId,
        CancellationToken cancellationToken
    )
    {
        _logger.LogDebug(
            "Updating existing descriptor {Resource} (DocumentId={DocumentId}) via POST upsert - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            documentId,
            request.TraceId.Value
        );

        var command = request.MappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlUpsertUpdateCommand(
                body,
                documentId,
                resourceKeyId,
                request.ReferentialId!.Value
            ),
            SqlDialect.Mssql => BuildMssqlUpsertUpdateCommand(
                body,
                documentId,
                resourceKeyId,
                request.ReferentialId!.Value
            ),
            _ => throw new NotSupportedException(
                $"Descriptor write does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
            ),
        };

        await ExecuteWriteCommandAsync(command, _commandExecutor, cancellationToken).ConfigureAwait(false);

        var persisted = await ReadPersistedDescriptorAsync(
                request.MappingSet.Key.Dialect,
                documentId,
                cancellationToken
            )
            .ConfigureAwait(false);

        var etag = persisted is not null
            ? ComputeEtagFromPersistedState(persisted, request.BackendProfileWriteContext)
            : ComputeEtagFromDescriptorBody(
                body,
                request.BackendProfileWriteContext?.IfMatchReadableProjectionContext
            );

        return new UpsertResult.UpdateSuccess(existingDocumentUuid, etag);
    }

    private static async Task ExecuteWriteCommandAsync(
        RelationalCommand command,
        IRelationalCommandExecutor executor,
        CancellationToken cancellationToken
    )
    {
        _ = await executor
            .ExecuteReaderAsync(
                command,
                static (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(0);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    // ── PostgreSQL SQL builders ──────────────────────────────────────────

    private static RelationalCommand BuildPostgresqlInsertCommand(
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        ReferentialId referentialId
    )
    {
        const string Sql = """
            WITH new_doc AS (
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                VALUES (@documentUuid, @resourceKeyId)
                RETURNING "DocumentId"
            )
            , new_descriptor AS (
                INSERT INTO dms."Descriptor" (
                    "DocumentId", "Namespace", "CodeValue", "ShortDescription",
                    "Description", "EffectiveBeginDate", "EffectiveEndDate",
                    "Discriminator", "Uri"
                )
                SELECT
                    "DocumentId", @namespace, @codeValue, @shortDescription,
                    @description, @effectiveBeginDate::date, @effectiveEndDate::date,
                    @discriminator, @uri
                FROM new_doc
            )
            INSERT INTO dms."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            SELECT @referentialId, "DocumentId", @resourceKeyId
            FROM new_doc;
            """;

        return new RelationalCommand(
            Sql,
            BuildInsertParameters(body, documentUuid, resourceKeyId, referentialId)
        );
    }

    private static RelationalCommand BuildMssqlInsertCommand(
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        ReferentialId referentialId
    )
    {
        const string Sql = """
            DECLARE @newDocumentId BIGINT;

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            VALUES (@documentUuid, @resourceKeyId);

            SET @newDocumentId = SCOPE_IDENTITY();

            INSERT INTO [dms].[Descriptor] (
                [DocumentId], [Namespace], [CodeValue], [ShortDescription],
                [Description], [EffectiveBeginDate], [EffectiveEndDate],
                [Discriminator], [Uri]
            )
            VALUES (
                @newDocumentId, @namespace, @codeValue, @shortDescription,
                @description, @effectiveBeginDate, @effectiveEndDate,
                @discriminator, @uri
            );

            INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
            VALUES (@referentialId, @newDocumentId, @resourceKeyId);

            """;

        return new RelationalCommand(
            Sql,
            BuildInsertParameters(body, documentUuid, resourceKeyId, referentialId)
        );
    }

    // ── Update SQL builders (POST as upsert-as-update) ───────────────────

    private static RelationalCommand BuildPostgresqlUpdateCommand(
        ExtractedDescriptorBody body,
        long documentId
    )
    {
        const string Sql = """
            UPDATE dms."Descriptor"
            SET "Namespace" = @namespace,
                "CodeValue" = @codeValue,
                "ShortDescription" = @shortDescription,
                "Description" = @description,
                "EffectiveBeginDate" = @effectiveBeginDate::date,
                "EffectiveEndDate" = @effectiveEndDate::date,
                "Uri" = @uri
            WHERE "DocumentId" = @documentId;

            UPDATE dms."Document"
            SET "ContentVersion" = nextval('dms."ChangeVersionSequence"'),
                "ContentLastModifiedAt" = now()
            WHERE "DocumentId" = @documentId;

            """;

        return new RelationalCommand(Sql, BuildUpdateParameters(body, documentId));
    }

    private static RelationalCommand BuildMssqlUpdateCommand(ExtractedDescriptorBody body, long documentId)
    {
        const string Sql = """
            UPDATE [dms].[Descriptor]
            SET [Namespace] = @namespace,
                [CodeValue] = @codeValue,
                [ShortDescription] = @shortDescription,
                [Description] = @description,
                [EffectiveBeginDate] = @effectiveBeginDate,
                [EffectiveEndDate] = @effectiveEndDate,
                [Uri] = @uri
            WHERE [DocumentId] = @documentId;

            UPDATE [dms].[Document]
            SET [ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
                [ContentLastModifiedAt] = GETUTCDATE()
            WHERE [DocumentId] = @documentId;

            """;

        return new RelationalCommand(Sql, BuildUpdateParameters(body, documentId));
    }

    // ── Upsert-update SQL builders (POST as upsert — includes ReferentialIdentity) ──

    private static RelationalCommand BuildPostgresqlUpsertUpdateCommand(
        ExtractedDescriptorBody body,
        long documentId,
        short resourceKeyId,
        ReferentialId referentialId
    )
    {
        const string Sql = """
            UPDATE dms."Descriptor"
            SET "Namespace" = @namespace,
                "CodeValue" = @codeValue,
                "ShortDescription" = @shortDescription,
                "Description" = @description,
                "EffectiveBeginDate" = @effectiveBeginDate::date,
                "EffectiveEndDate" = @effectiveEndDate::date,
                "Uri" = @uri
            WHERE "DocumentId" = @documentId;

            UPDATE dms."Document"
            SET "ContentVersion" = nextval('dms."ChangeVersionSequence"'),
                "ContentLastModifiedAt" = now()
            WHERE "DocumentId" = @documentId;

            INSERT INTO dms."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO UPDATE
            SET "DocumentId" = EXCLUDED."DocumentId",
                "ResourceKeyId" = EXCLUDED."ResourceKeyId";

            """;

        return new RelationalCommand(
            Sql,
            BuildUpsertUpdateParameters(body, documentId, resourceKeyId, referentialId)
        );
    }

    private static RelationalCommand BuildMssqlUpsertUpdateCommand(
        ExtractedDescriptorBody body,
        long documentId,
        short resourceKeyId,
        ReferentialId referentialId
    )
    {
        const string Sql = """
            UPDATE [dms].[Descriptor]
            SET [Namespace] = @namespace,
                [CodeValue] = @codeValue,
                [ShortDescription] = @shortDescription,
                [Description] = @description,
                [EffectiveBeginDate] = @effectiveBeginDate,
                [EffectiveEndDate] = @effectiveEndDate,
                [Uri] = @uri
            WHERE [DocumentId] = @documentId;

            UPDATE [dms].[Document]
            SET [ContentVersion] = NEXT VALUE FOR [dms].[ChangeVersionSequence],
                [ContentLastModifiedAt] = GETUTCDATE()
            WHERE [DocumentId] = @documentId;

            MERGE [dms].[ReferentialIdentity] AS target
            USING (VALUES (@referentialId, @documentId, @resourceKeyId))
                AS source ([ReferentialId], [DocumentId], [ResourceKeyId])
            ON target.[ReferentialId] = source.[ReferentialId]
            WHEN MATCHED THEN
                UPDATE SET [DocumentId] = source.[DocumentId],
                           [ResourceKeyId] = source.[ResourceKeyId]
            WHEN NOT MATCHED THEN
                INSERT ([ReferentialId], [DocumentId], [ResourceKeyId])
                VALUES (source.[ReferentialId], source.[DocumentId], source.[ResourceKeyId]);

            """;

        return new RelationalCommand(
            Sql,
            BuildUpsertUpdateParameters(body, documentId, resourceKeyId, referentialId)
        );
    }

    private string ComputeEtagFromPersistedState(
        PersistedDescriptorState persisted,
        BackendProfileWriteContext? backendProfileWriteContext
    ) =>
        ComputeEtagFromPersistedState(
            persisted,
            backendProfileWriteContext?.IfMatchReadableProjectionContext
        );

    private string ComputeEtagFromDescriptorBody(
        ExtractedDescriptorBody body,
        ReadableProfileProjectionContext? projectionContext
    )
    {
        var document = new JsonObject { ["namespace"] = body.Namespace, ["codeValue"] = body.CodeValue };

        if (body.ShortDescription is not null)
        {
            document["shortDescription"] = body.ShortDescription;
        }

        if (body.Description is not null)
        {
            document["description"] = body.Description;
        }

        if (body.EffectiveBeginDate is not null)
        {
            document["effectiveBeginDate"] = body.EffectiveBeginDate.Value.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        if (body.EffectiveEndDate is not null)
        {
            document["effectiveEndDate"] = body.EffectiveEndDate.Value.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        if (projectionContext is not null)
        {
            document = (JsonObject)
                _readableProfileProjector.Project(
                    document,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                );
            RelationalApiMetadataFormatter.RefreshEtag(document);
            return RelationalApiMetadataFormatter.FormatEtag(document);
        }

        return RelationalApiMetadataFormatter.FormatEtag(body);
    }

    private string ComputeEtagFromPersistedState(
        PersistedDescriptorState persisted,
        ReadableProfileProjectionContext? projectionContext
    )
    {
        var document = new JsonObject
        {
            ["namespace"] = persisted.Namespace,
            ["codeValue"] = persisted.CodeValue,
        };

        if (persisted.ShortDescription is not null)
        {
            document["shortDescription"] = persisted.ShortDescription;
        }

        if (persisted.Description is not null)
        {
            document["description"] = persisted.Description;
        }

        if (persisted.EffectiveBeginDate is DateOnly effectiveBegin)
        {
            document["effectiveBeginDate"] = effectiveBegin.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        if (persisted.EffectiveEndDate is DateOnly effectiveEnd)
        {
            document["effectiveEndDate"] = effectiveEnd.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        if (projectionContext is not null)
        {
            document = (JsonObject)
                _readableProfileProjector.Project(
                    document,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                );
            RelationalApiMetadataFormatter.RefreshEtag(document);
            // FormatEtag reads canonical content; RefreshEtag attached _etag but we want the canonical ETag value
            return RelationalApiMetadataFormatter.FormatEtag(document);
        }

        return RelationalApiMetadataFormatter.FormatEtag(
            new ExtractedDescriptorBody(
                persisted.Namespace,
                persisted.CodeValue,
                persisted.ShortDescription,
                persisted.Description,
                persisted.EffectiveBeginDate,
                persisted.EffectiveEndDate,
                string.Empty,
                string.Empty
            )
        );
    }

    private bool IsDescriptorEtagMismatch(
        string ifMatchEtag,
        PersistedDescriptorState persisted,
        ReadableProfileProjectionContext? ifMatchReadableProjectionContext
    )
    {
        var currentEtag = ComputeEtagFromPersistedState(persisted, ifMatchReadableProjectionContext);
        return !string.Equals(currentEtag, ifMatchEtag, StringComparison.Ordinal);
    }

    // ── Persisted descriptor read ──────────────────────────────────────────

    private async Task<PersistedDescriptorState?> ReadPersistedDescriptorAsync(
        SqlDialect dialect,
        long documentId,
        CancellationToken cancellationToken
    )
    {
        var command = dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlReadCommand(documentId),
            SqlDialect.Mssql => BuildMssqlReadCommand(documentId),
            _ => throw new NotSupportedException(
                $"Descriptor read does not support SQL dialect '{dialect}'."
            ),
        };

        return await ReadDescriptorStateFromExecutorAsync(command, _commandExecutor, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared reader helper: executes <paramref name="command"/> on <paramref name="executor"/> and
    /// materialises a single <see cref="PersistedDescriptorState"/> row, or <see langword="null"/>
    /// when no row is found. Used by both non-locked and locked (session-scoped) read paths.
    /// </summary>
    private static async Task<PersistedDescriptorState?> ReadDescriptorStateFromExecutorAsync(
        RelationalCommand command,
        IRelationalCommandExecutor executor,
        CancellationToken cancellationToken
    )
    {
        return await executor
            .ExecuteReaderAsync(
                command,
                static async (reader, ct) =>
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new PersistedDescriptorState(
                        Namespace: reader.GetRequiredFieldValue<string>("Namespace"),
                        CodeValue: reader.GetRequiredFieldValue<string>("CodeValue"),
                        Uri: reader.GetRequiredFieldValue<string>("Uri"),
                        ShortDescription: reader.GetNullableFieldValue<string>("ShortDescription"),
                        Description: reader.GetNullableFieldValue<string>("Description"),
                        EffectiveBeginDate: reader.GetNullableDateFieldValue("EffectiveBeginDate"),
                        EffectiveEndDate: reader.GetNullableDateFieldValue("EffectiveEndDate")
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    // ── Locked session execution helpers (If-Match guard paths) ─────────────

    /// <summary>
    /// Executes the descriptor PUT write within a locked session transaction.
    /// Acquires a row lock on the pre-check SELECT so no concurrent writer can modify the row
    /// between the ETag comparison and the subsequent UPDATE.
    /// </summary>
    private async Task<UpdateResult> ExecutePutWithLockedSessionAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        DocumentUuid documentUuid,
        CancellationToken cancellationToken
    )
    {
        await using var session = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        var sessionExecutor = new SessionRelationalCommandExecutor(session.Connection, session.Transaction);

        var lockedReadCommand = request.MappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlLockedReadCommand(documentId),
            SqlDialect.Mssql => BuildMssqlLockedReadCommand(documentId),
            _ => throw new NotSupportedException(
                $"Descriptor locked read does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
            ),
        };

        try
        {
            var persisted = await ReadDescriptorStateFromExecutorAsync(
                    lockedReadCommand,
                    sessionExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (persisted is null)
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new UpdateResult.UpdateFailureETagMisMatch();
            }

            if (
                IsDescriptorEtagMismatch(
                    request.IfMatchEtag!,
                    persisted,
                    request.BackendProfileWriteContext?.IfMatchReadableProjectionContext
                )
            )
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new UpdateResult.UpdateFailureETagMisMatch();
            }

            if (!string.Equals(body.Uri, persisted.Uri, StringComparison.Ordinal))
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new UpdateResult.UpdateFailureImmutableIdentity(
                    $"Identity of resource '{RelationalWriteSupport.FormatResource(request.Resource)}' "
                        + "cannot be changed. Descriptor identity fields (Namespace, CodeValue) are immutable on PUT."
                );
            }

            if (IsDescriptorUnchanged(body, persisted))
            {
                _logger.LogDebug(
                    "Descriptor PUT with If-Match is a no-op for {Resource} (DocumentId={DocumentId}) - {TraceId}",
                    RelationalWriteSupport.FormatResource(request.Resource),
                    documentId,
                    request.TraceId.Value
                );

                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new UpdateResult.UpdateSuccess(
                    documentUuid,
                    ComputeEtagFromPersistedState(persisted, request.BackendProfileWriteContext)
                );
            }

            _logger.LogDebug(
                "Updating descriptor {Resource} (DocumentId={DocumentId}) via PUT with If-Match - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                documentId,
                request.TraceId.Value
            );

            var writeCommand = request.MappingSet.Key.Dialect switch
            {
                SqlDialect.Pgsql => BuildPostgresqlUpdateCommand(body, documentId),
                SqlDialect.Mssql => BuildMssqlUpdateCommand(body, documentId),
                _ => throw new NotSupportedException(
                    $"Descriptor write does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
                ),
            };

            await ExecuteWriteCommandAsync(writeCommand, sessionExecutor, cancellationToken)
                .ConfigureAwait(false);

            var persistedAfterWrite = await ReadDescriptorStateFromExecutorAsync(
                    BuildReadCommand(request.MappingSet.Key.Dialect, documentId),
                    sessionExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new UpdateResult.UpdateSuccess(
                documentUuid,
                persistedAfterWrite is not null
                    ? ComputeEtagFromPersistedState(persistedAfterWrite, request.BackendProfileWriteContext)
                    : ComputeEtagFromDescriptorBody(
                        body,
                        request.BackendProfileWriteContext?.IfMatchReadableProjectionContext
                    )
            );
        }
        catch (DbException ex)
        {
            await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MapPutDbException(request, ex, lockedSession: true);
        }
    }

    /// <summary>
    /// Executes the descriptor POST-as-update write within a locked session transaction.
    /// Acquires a row lock on the pre-check SELECT so no concurrent writer can modify the row
    /// between the ETag comparison and the subsequent UPDATE.
    /// </summary>
    private async Task<UpsertResult> ExecutePostAsUpdateWithLockedSessionAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        DocumentUuid existingDocUuid,
        short resourceKeyId,
        CancellationToken cancellationToken
    )
    {
        await using var session = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        var sessionExecutor = new SessionRelationalCommandExecutor(session.Connection, session.Transaction);

        var lockedReadCommand = request.MappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlLockedReadCommand(documentId),
            SqlDialect.Mssql => BuildMssqlLockedReadCommand(documentId),
            _ => throw new NotSupportedException(
                $"Descriptor locked read does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
            ),
        };

        try
        {
            var persisted = await ReadDescriptorStateFromExecutorAsync(
                    lockedReadCommand,
                    sessionExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (persisted is null)
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new UpsertResult.UpsertFailureETagMisMatch();
            }

            if (
                IsDescriptorEtagMismatch(
                    request.IfMatchEtag!,
                    persisted,
                    request.BackendProfileWriteContext?.IfMatchReadableProjectionContext
                )
            )
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new UpsertResult.UpsertFailureETagMisMatch();
            }

            if (IsDescriptorUnchanged(body, persisted))
            {
                _logger.LogDebug(
                    "Descriptor POST-as-update with If-Match is a no-op for {Resource} (DocumentId={DocumentId}) - {TraceId}",
                    RelationalWriteSupport.FormatResource(request.Resource),
                    documentId,
                    request.TraceId.Value
                );

                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new UpsertResult.UpdateSuccess(
                    existingDocUuid,
                    ComputeEtagFromPersistedState(persisted, request.BackendProfileWriteContext)
                );
            }

            var writeCommand = request.MappingSet.Key.Dialect switch
            {
                SqlDialect.Pgsql => BuildPostgresqlUpsertUpdateCommand(
                    body,
                    documentId,
                    resourceKeyId,
                    request.ReferentialId!.Value
                ),
                SqlDialect.Mssql => BuildMssqlUpsertUpdateCommand(
                    body,
                    documentId,
                    resourceKeyId,
                    request.ReferentialId!.Value
                ),
                _ => throw new NotSupportedException(
                    $"Descriptor write does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
                ),
            };

            await ExecuteWriteCommandAsync(writeCommand, sessionExecutor, cancellationToken)
                .ConfigureAwait(false);

            var persistedAfterWrite = await ReadDescriptorStateFromExecutorAsync(
                    BuildReadCommand(request.MappingSet.Key.Dialect, documentId),
                    sessionExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new UpsertResult.UpdateSuccess(
                existingDocUuid,
                persistedAfterWrite is not null
                    ? ComputeEtagFromPersistedState(persistedAfterWrite, request.BackendProfileWriteContext)
                    : ComputeEtagFromDescriptorBody(
                        body,
                        request.BackendProfileWriteContext?.IfMatchReadableProjectionContext
                    )
            );
        }
        catch (DbException ex)
        {
            await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return MapPostDbException(request, ex);
        }
    }

    private static RelationalCommand BuildReadCommand(SqlDialect dialect, long documentId) =>
        dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlReadCommand(documentId),
            SqlDialect.Mssql => BuildMssqlReadCommand(documentId),
            _ => throw new NotSupportedException(
                $"Descriptor read does not support SQL dialect '{dialect}'."
            ),
        };

    private static RelationalCommand BuildPostgresqlReadCommand(long documentId)
    {
        const string Sql = """
            SELECT "Namespace", "CodeValue", "Uri", "ShortDescription", "Description", "EffectiveBeginDate", "EffectiveEndDate"
            FROM dms."Descriptor"
            WHERE "DocumentId" = @documentId;
            """;

        return new RelationalCommand(Sql, [new RelationalParameter("@documentId", documentId)]);
    }

    private static RelationalCommand BuildMssqlReadCommand(long documentId)
    {
        const string Sql = """
            SELECT [Namespace], [CodeValue], [Uri], [ShortDescription], [Description], [EffectiveBeginDate], [EffectiveEndDate]
            FROM [dms].[Descriptor]
            WHERE [DocumentId] = @documentId;
            """;

        return new RelationalCommand(Sql, [new RelationalParameter("@documentId", documentId)]);
    }

    // ── Locked read command builders (FOR UPDATE / UPDLOCK ROWLOCK) ───────

    private static RelationalCommand BuildPostgresqlLockedReadCommand(long documentId)
    {
        // FOR UPDATE acquires a row-level lock held for the lifetime of the enclosing transaction,
        // preventing concurrent writers from modifying the row between the pre-check SELECT and the
        // subsequent UPDATE (eliminating the TOCTOU race for If-Match guarded writes).
        const string Sql = """
            SELECT "Namespace", "CodeValue", "Uri", "ShortDescription", "Description", "EffectiveBeginDate", "EffectiveEndDate"
            FROM dms."Descriptor"
            WHERE "DocumentId" = @documentId
            FOR UPDATE;
            """;

        return new RelationalCommand(Sql, [new RelationalParameter("@documentId", documentId)]);
    }

    private static RelationalCommand BuildPostgresqlLockedDeleteReadCommand(
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        const string Sql = """
            SELECT descriptor."Namespace",
                   descriptor."CodeValue",
                   descriptor."Uri",
                   descriptor."ShortDescription",
                   descriptor."Description",
                   descriptor."EffectiveBeginDate",
                   descriptor."EffectiveEndDate"
            FROM dms."Document" d
            JOIN dms."Descriptor" descriptor ON descriptor."DocumentId" = d."DocumentId"
            WHERE d."DocumentUuid" = @documentUuid
              AND d."ResourceKeyId" = @resourceKeyId
            FOR UPDATE;
            """;

        return new RelationalCommand(
            Sql,
            [
                new RelationalParameter("@documentUuid", documentUuid.Value),
                new RelationalParameter("@resourceKeyId", resourceKeyId),
            ]
        );
    }

    private static RelationalCommand BuildMssqlLockedReadCommand(long documentId)
    {
        // UPDLOCK + ROWLOCK acquire an update row lock held for the lifetime of the enclosing
        // transaction, preventing concurrent writers from modifying the row between the pre-check
        // SELECT and the subsequent UPDATE (eliminating the TOCTOU race for If-Match guarded writes).
        const string Sql = """
            SELECT [Namespace], [CodeValue], [Uri], [ShortDescription], [Description], [EffectiveBeginDate], [EffectiveEndDate]
            FROM [dms].[Descriptor] WITH (UPDLOCK, ROWLOCK)
            WHERE [DocumentId] = @documentId;
            """;

        return new RelationalCommand(Sql, [new RelationalParameter("@documentId", documentId)]);
    }

    private static RelationalCommand BuildMssqlLockedDeleteReadCommand(
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        const string Sql = """
            SELECT descriptor.[Namespace],
                   descriptor.[CodeValue],
                   descriptor.[Uri],
                   descriptor.[ShortDescription],
                   descriptor.[Description],
                   descriptor.[EffectiveBeginDate],
                   descriptor.[EffectiveEndDate]
            FROM [dms].[Document] d WITH (UPDLOCK, ROWLOCK)
            JOIN [dms].[Descriptor] descriptor ON descriptor.[DocumentId] = d.[DocumentId]
            WHERE d.[DocumentUuid] = @documentUuid
              AND d.[ResourceKeyId] = @resourceKeyId;
            """;

        return new RelationalCommand(
            Sql,
            [
                new RelationalParameter("@documentUuid", documentUuid.Value),
                new RelationalParameter("@resourceKeyId", resourceKeyId),
            ]
        );
    }

    // ── No-op detection ─────────────────────────────────────────────────

    private static bool IsDescriptorUnchanged(
        ExtractedDescriptorBody body,
        PersistedDescriptorState persisted
    )
    {
        return string.Equals(body.ShortDescription, persisted.ShortDescription, StringComparison.Ordinal)
            && string.Equals(body.Description, persisted.Description, StringComparison.Ordinal)
            && body.EffectiveBeginDate == persisted.EffectiveBeginDate
            && body.EffectiveEndDate == persisted.EffectiveEndDate;
    }

    private sealed record PersistedDescriptorState(
        string Namespace,
        string CodeValue,
        string Uri,
        string? ShortDescription,
        string? Description,
        DateOnly? EffectiveBeginDate,
        DateOnly? EffectiveEndDate
    );

    // ── Parameter builders ───────────────────────────────────────────────

    private static List<RelationalParameter> BuildInsertParameters(
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        ReferentialId referentialId
    )
    {
        var parameters = BuildInsertFieldParameters(body);
        parameters.Add(new RelationalParameter("@documentUuid", documentUuid.Value));
        parameters.Add(new RelationalParameter("@resourceKeyId", resourceKeyId));
        parameters.Add(new RelationalParameter("@referentialId", referentialId.Value));
        return parameters;
    }

    private static List<RelationalParameter> BuildUpdateParameters(
        ExtractedDescriptorBody body,
        long documentId
    )
    {
        var parameters = BuildCommonFieldParameters(body);
        parameters.Add(new RelationalParameter("@documentId", documentId));
        return parameters;
    }

    private static List<RelationalParameter> BuildUpsertUpdateParameters(
        ExtractedDescriptorBody body,
        long documentId,
        short resourceKeyId,
        ReferentialId referentialId
    )
    {
        var parameters = BuildCommonFieldParameters(body);
        parameters.Add(new RelationalParameter("@documentId", documentId));
        parameters.Add(new RelationalParameter("@resourceKeyId", resourceKeyId));
        parameters.Add(new RelationalParameter("@referentialId", referentialId.Value));
        return parameters;
    }

    private static List<RelationalParameter> BuildCommonFieldParameters(ExtractedDescriptorBody body)
    {
        return
        [
            new RelationalParameter("@namespace", body.Namespace),
            new RelationalParameter("@codeValue", body.CodeValue),
            new RelationalParameter("@shortDescription", body.ShortDescription),
            new RelationalParameter("@description", body.Description),
            new RelationalParameter(
                "@effectiveBeginDate",
                (object?)body.EffectiveBeginDate?.ToString("yyyy-MM-dd")
            ),
            new RelationalParameter(
                "@effectiveEndDate",
                (object?)body.EffectiveEndDate?.ToString("yyyy-MM-dd")
            ),
            new RelationalParameter("@uri", body.Uri),
        ];
    }

    private static List<RelationalParameter> BuildInsertFieldParameters(ExtractedDescriptorBody body)
    {
        var parameters = BuildCommonFieldParameters(body);
        parameters.Add(new RelationalParameter("@discriminator", body.Discriminator));
        return parameters;
    }

    // ── SQL error classification ────────────────────────────────────────

    private UpsertResult MapPostDbException(DescriptorWriteRequest request, DbException ex)
    {
        if (_writeExceptionClassifier.IsUniqueConstraintViolation(ex))
        {
            _logger.LogDebug(
                ex,
                "Unique constraint violation on descriptor POST for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                request.TraceId.Value
            );

            return new UpsertResult.UpsertFailureWriteConflict();
        }

        if (_writeExceptionClassifier.IsTransientFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Transient database error on descriptor POST for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                request.TraceId.Value
            );

            return new UpsertResult.UpsertFailureWriteConflict();
        }

        _logger.LogError(
            ex,
            "Database error on descriptor POST for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        return new UpsertResult.UnknownFailure(
            "An unexpected error occurred while processing the descriptor request."
        );
    }

    private UpdateResult MapPutDbException(DescriptorWriteRequest request, DbException ex, bool lockedSession)
    {
        if (_writeExceptionClassifier.IsTransientFailure(ex))
        {
            _logger.LogWarning(
                ex,
                lockedSession
                    ? "Transient database error on descriptor PUT (locked session) for {Resource} - {TraceId}"
                    : "Transient database error on descriptor PUT for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                request.TraceId.Value
            );

            return new UpdateResult.UpdateFailureWriteConflict();
        }

        _logger.LogError(
            ex,
            lockedSession
                ? "Database error on descriptor PUT (locked session) for {Resource} - {TraceId}"
                : "Database error on descriptor PUT for {Resource} - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            request.TraceId.Value
        );

        return new UpdateResult.UnknownFailure(
            "An unexpected error occurred while processing the descriptor request."
        );
    }

    private DeleteResult MapDeleteDbException(DocumentUuid documentUuid, TraceId traceId, DbException ex)
    {
        if (_writeExceptionClassifier.IsTransientFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Transient database error on descriptor DELETE (locked session) for {DocumentUuid} - {TraceId}",
                documentUuid.Value,
                LoggingSanitizer.SanitizeForLogging(traceId.Value)
            );

            return new DeleteResult.DeleteFailureWriteConflict();
        }

        _logger.LogError(
            ex,
            "Database error on descriptor DELETE (locked session) for {DocumentUuid} - {TraceId}",
            documentUuid.Value,
            LoggingSanitizer.SanitizeForLogging(traceId.Value)
        );

        return new DeleteResult.UnknownFailure(
            "An unexpected error occurred while processing the descriptor delete request."
        );
    }
}
