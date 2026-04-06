// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Descriptor write handler that persists descriptor resources into
/// <c>dms.Document</c>, <c>dms.Descriptor</c>, and <c>dms.ReferentialIdentity</c>.
/// </summary>
internal sealed class DescriptorWriteHandler(
    IRelationalWriteTargetLookupService targetLookupService,
    IRelationalCommandExecutor commandExecutor,
    ILogger<DescriptorWriteHandler> logger
) : IDescriptorWriteHandler
{
    private readonly IRelationalWriteTargetLookupService _targetLookupService =
        targetLookupService ?? throw new ArgumentNullException(nameof(targetLookupService));
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
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
            return targetContext switch
            {
                RelationalWriteTargetContext.CreateNew(var documentUuid) => await InsertDescriptorAsync(
                        request,
                        body,
                        documentUuid,
                        resourceKeyId,
                        cancellationToken
                    )
                    .ConfigureAwait(false),

                RelationalWriteTargetContext.ExistingDocument(var documentId, var documentUuid, _) =>
                    await UpdateDescriptorForUpsertAsync(
                            request,
                            body,
                            documentId,
                            documentUuid,
                            resourceKeyId,
                            cancellationToken
                        )
                        .ConfigureAwait(false),

                _ => throw new InvalidOperationException(
                    $"Unexpected target context type '{targetContext.GetType().Name}' for descriptor POST."
                ),
            };
        }
        catch (DbException ex) when (IsUniqueConstraintViolation(ex))
        {
            _logger.LogDebug(
                ex,
                "Unique constraint violation on descriptor POST for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                request.TraceId.Value
            );

            return new UpsertResult.UpsertFailureWriteConflict();
        }
        catch (DbException ex)
        {
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
            return new UpdateResult.UpdateFailureNotExists();
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

            return new UpdateResult.UpdateSuccess(documentUuid);
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

            await _commandExecutor
                .ExecuteReaderAsync(command, static (_, _) => Task.FromResult(true), cancellationToken)
                .ConfigureAwait(false);

            return new UpdateResult.UpdateSuccess(documentUuid);
        }
        catch (DbException ex)
        {
            _logger.LogError(
                ex,
                "Database error on descriptor PUT for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                request.TraceId.Value
            );

            return new UpdateResult.UnknownFailure(
                "An unexpected error occurred while processing the descriptor request."
            );
        }
    }

    public async Task<DeleteResult> HandleDeleteAsync(
        DocumentUuid documentUuid,
        TraceId traceId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Deleting descriptor document {DocumentUuid} - {TraceId}",
            documentUuid.Value,
            traceId.Value
        );

        var dialect = _commandExecutor.Dialect;

        var command = dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                DELETE FROM dms."Document"
                WHERE "DocumentUuid" = @documentUuid
                RETURNING "DocumentId";
                """,
                [new RelationalParameter("@documentUuid", documentUuid.Value)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                DELETE FROM [dms].[Document]
                OUTPUT DELETED.[DocumentId]
                WHERE [DocumentUuid] = @documentUuid;
                """,
                [new RelationalParameter("@documentUuid", documentUuid.Value)]
            ),
            _ => throw new NotSupportedException(
                $"Descriptor delete does not support SQL dialect '{dialect}'."
            ),
        };

        try
        {
            var deleted = await _commandExecutor
                .ExecuteReaderAsync(
                    command,
                    static async (reader, ct) => await reader.ReadAsync(ct).ConfigureAwait(false),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return deleted ? new DeleteResult.DeleteSuccess() : new DeleteResult.DeleteFailureNotExists();
        }
        catch (DbException ex) when (IsForeignKeyViolation(ex))
        {
            _logger.LogDebug(
                ex,
                "FK constraint violation on descriptor DELETE for {DocumentUuid} - {TraceId}",
                documentUuid.Value,
                traceId.Value
            );

            return new DeleteResult.DeleteFailureReference(["(referenced descriptor)"]);
        }
        catch (DbException ex)
        {
            _logger.LogError(
                ex,
                "Database error on descriptor DELETE for {DocumentUuid} - {TraceId}",
                documentUuid.Value,
                traceId.Value
            );

            return new DeleteResult.UnknownFailure(
                "An unexpected error occurred while processing the descriptor request."
            );
        }
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

        await _commandExecutor
            .ExecuteReaderAsync(command, static (_, _) => Task.FromResult(true), cancellationToken)
            .ConfigureAwait(false);

        return new UpsertResult.InsertSuccess(documentUuid);
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

        await _commandExecutor
            .ExecuteReaderAsync(command, static (_, _) => Task.FromResult(true), cancellationToken)
            .ConfigureAwait(false);

        return new UpsertResult.UpdateSuccess(existingDocumentUuid);
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

        return await _commandExecutor
            .ExecuteReaderAsync(
                command,
                static async (reader, ct) =>
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new PersistedDescriptorState(
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

    private static RelationalCommand BuildPostgresqlReadCommand(long documentId)
    {
        const string Sql = """
            SELECT "Uri", "ShortDescription", "Description", "EffectiveBeginDate", "EffectiveEndDate"
            FROM dms."Descriptor"
            WHERE "DocumentId" = @documentId;
            """;

        return new RelationalCommand(Sql, [new RelationalParameter("@documentId", documentId)]);
    }

    private static RelationalCommand BuildMssqlReadCommand(long documentId)
    {
        const string Sql = """
            SELECT [Uri], [ShortDescription], [Description], [EffectiveBeginDate], [EffectiveEndDate]
            FROM [dms].[Descriptor]
            WHERE [DocumentId] = @documentId;
            """;

        return new RelationalCommand(Sql, [new RelationalParameter("@documentId", documentId)]);
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

    /// <summary>
    /// Detects unique constraint violations across Postgres (23505) and SQL Server (2627/2601).
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbException ex)
    {
        // Postgres: SqlState "23505" (unique_violation)
        // SQL Server: Number 2627 (unique key) or 2601 (unique index)
        return ex.SqlState == "23505"
            || (
                ex is { HResult: var hr }
                && hr is unchecked((int)0x80131904)
                && (
                    ex.Message.Contains("2627", StringComparison.Ordinal)
                    || ex.Message.Contains("2601", StringComparison.Ordinal)
                )
            );
    }

    /// <summary>
    /// Detects foreign key constraint violations across Postgres (23503) and SQL Server (547).
    /// </summary>
    private static bool IsForeignKeyViolation(DbException ex)
    {
        // Postgres: SqlState "23503" (foreign_key_violation)
        // SQL Server: Number 547 (FK constraint)
        return ex.SqlState == "23503"
            || (
                ex is { HResult: var hr }
                && hr is unchecked((int)0x80131904)
                && ex.Message.Contains("547", StringComparison.Ordinal)
            );
    }
}
