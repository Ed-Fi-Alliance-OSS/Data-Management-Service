// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal enum DeleteTargetKind
{
    Document,
    Descriptor,
}

/// <summary>
/// Shared execution scaffolding for relational DELETE statements. Both the non-descriptor delete
/// path (on a write session, inside a transaction) and the descriptor delete path (on the ambient
/// command executor) funnel their final DELETE command through here so the
/// row→<c>DeleteSuccess</c>/<c>DeleteFailureNotExists</c> mapping and the
/// FK/transient/other exception-to-<see cref="DeleteResult"/> translation live in exactly one place.
/// Callers retain ownership of everything around the delete: transaction management, resource
/// scoping, lookups, and the dialect-specific SQL command construction.
/// </summary>
internal static class RelationalDeleteExecution
{
    public static async Task<DeleteResult> TryExecuteAsync(
        IRelationalCommandExecutor commandExecutor,
        RelationalCommand command,
        IRelationalWriteExceptionClassifier classifier,
        ILogger logger,
        DocumentUuid documentUuid,
        TraceId traceId,
        DeleteTargetKind targetKind,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(commandExecutor);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(logger);

        var scopeLabel = ScopeLabel(targetKind);
        var referencedLabel = $"(referenced {scopeLabel})";

        try
        {
            var deleted = await commandExecutor
                .ExecuteReaderAsync(
                    command,
                    static async (reader, ct) => await reader.ReadAsync(ct).ConfigureAwait(false),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return deleted ? new DeleteResult.DeleteSuccess() : new DeleteResult.DeleteFailureNotExists();
        }
        catch (DbException ex) when (classifier.IsForeignKeyViolation(ex))
        {
            logger.LogDebug(
                ex,
                "FK constraint violation on {ScopeLabel} DELETE for {DocumentUuid} - {TraceId}",
                scopeLabel,
                documentUuid.Value,
                LoggingSanitizer.SanitizeForLogging(traceId.Value)
            );

            return new DeleteResult.DeleteFailureReference([referencedLabel]);
        }
        catch (DbException ex) when (classifier.IsTransientFailure(ex))
        {
            logger.LogDebug(
                ex,
                "Transient conflict on {ScopeLabel} DELETE for {DocumentUuid} - {TraceId}",
                scopeLabel,
                documentUuid.Value,
                LoggingSanitizer.SanitizeForLogging(traceId.Value)
            );

            return new DeleteResult.DeleteFailureWriteConflict();
        }
        catch (DbException ex)
        {
            logger.LogError(
                ex,
                "Database error on {ScopeLabel} DELETE for {DocumentUuid} - {TraceId}",
                scopeLabel,
                documentUuid.Value,
                LoggingSanitizer.SanitizeForLogging(traceId.Value)
            );

            return new DeleteResult.UnknownFailure(
                "An unexpected error occurred while processing the delete request."
            );
        }
    }

    private static string ScopeLabel(DeleteTargetKind kind) =>
        kind switch
        {
            DeleteTargetKind.Document => "document",
            DeleteTargetKind.Descriptor => "descriptor",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown DeleteTargetKind."),
        };
}
