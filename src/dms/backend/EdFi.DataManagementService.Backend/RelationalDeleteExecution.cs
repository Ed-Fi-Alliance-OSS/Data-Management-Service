// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
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
        IRelationalDeleteConstraintResolver constraintResolver,
        DerivedRelationalModelSet modelSet,
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
        ArgumentNullException.ThrowIfNull(constraintResolver);
        ArgumentNullException.ThrowIfNull(modelSet);
        ArgumentNullException.ThrowIfNull(logger);

        var scopeLabel = ScopeLabel(targetKind);

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
            return MapForeignKeyViolation(
                ex,
                classifier,
                constraintResolver,
                modelSet,
                logger,
                documentUuid,
                traceId,
                scopeLabel
            );
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

    /// <summary>
    /// Translates a FK-violation <see cref="DbException"/> into a
    /// <see cref="DeleteResult.DeleteFailureReference"/>. The caller's catch filter has already
    /// asserted <see cref="IRelationalWriteExceptionClassifier.IsForeignKeyViolation"/>. Two
    /// branches are handled:
    /// <list type="bullet">
    /// <item><see cref="RelationalWriteExceptionClassification.ForeignKeyConstraintViolation"/> — the
    /// constraint name was extractable and is routed through <paramref name="constraintResolver"/>.</item>
    /// <item>anything else (including a classifier that declines to classify) — treated as an
    /// FK violation without an extractable constraint name; the empty-names
    /// <see cref="DeleteResult.DeleteFailureReference"/> is returned and an Information log is
    /// emitted. Observed today with pgsql missing <c>ConstraintName</c> and mssql localized 547
    /// messages; we do not require the public
    /// <see cref="IRelationalWriteExceptionClassifier"/> contract to guarantee a non-null
    /// classification on this branch.</item>
    /// </list>
    /// </summary>
    private static DeleteResult.DeleteFailureReference MapForeignKeyViolation(
        DbException exception,
        IRelationalWriteExceptionClassifier classifier,
        IRelationalDeleteConstraintResolver constraintResolver,
        DerivedRelationalModelSet modelSet,
        ILogger logger,
        DocumentUuid documentUuid,
        TraceId traceId,
        string scopeLabel
    )
    {
        var sanitizedTraceId = LoggingSanitizer.SanitizeForLogging(traceId.Value);

        _ = classifier.TryClassify(exception, out var classification);

        if (classification is RelationalWriteExceptionClassification.ForeignKeyConstraintViolation foreignKey)
        {
            var referencing = constraintResolver.TryResolveReferencingResource(
                modelSet,
                foreignKey.ConstraintName
            );

            if (referencing is { } resource)
            {
                logger.LogDebug(
                    exception,
                    "FK constraint '{ConstraintName}' violation on {ScopeLabel} DELETE for {DocumentUuid} -> referencing resource '{ReferencingResource}' - {TraceId}",
                    foreignKey.ConstraintName,
                    scopeLabel,
                    documentUuid.Value,
                    resource.ResourceName,
                    sanitizedTraceId
                );

                return new DeleteResult.DeleteFailureReference([resource.ResourceName]);
            }

            // Constraint name surfaced by the driver but the compiled relational model has no
            // matching FK - drift between the deployed DDL and the runtime model. Emit a Warning so
            // operators notice, and return an empty names array so the response layer can render a
            // generic conflict message.
            logger.LogWarning(
                exception,
                "FK constraint '{ConstraintName}' violation on {ScopeLabel} DELETE for {DocumentUuid} could not be mapped to a resource in the compiled model - {TraceId}",
                foreignKey.ConstraintName,
                scopeLabel,
                documentUuid.Value,
                sanitizedTraceId
            );

            return new DeleteResult.DeleteFailureReference([]);
        }

        // IsForeignKeyViolation reported true but no constraint name was extractable. Expected with
        // some pgsql / mssql driver + locale combinations; not actionable.
        logger.LogInformation(
            exception,
            "FK constraint violation on {ScopeLabel} DELETE for {DocumentUuid} without an extractable constraint name - {TraceId}",
            scopeLabel,
            documentUuid.Value,
            sanitizedTraceId
        );

        return new DeleteResult.DeleteFailureReference([]);
    }
}
