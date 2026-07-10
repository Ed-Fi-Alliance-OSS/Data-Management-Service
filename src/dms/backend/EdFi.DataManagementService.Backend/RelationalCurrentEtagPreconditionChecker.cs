// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

internal sealed record RelationalCurrentEtagPreconditionCheckRequest
{
    public RelationalCurrentEtagPreconditionCheckRequest(
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        WritePrecondition precondition
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        ReadPlan = readPlan ?? throw new ArgumentNullException(nameof(readPlan));
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        Precondition = precondition ?? throw new ArgumentNullException(nameof(precondition));
    }

    public MappingSet MappingSet { get; init; }

    public ResourceReadPlan ReadPlan { get; init; }

    public RelationalWriteTargetContext.ExistingDocument TargetContext { get; init; }

    public WritePrecondition Precondition { get; init; }
}

/// <summary>
/// Result of the current-etag precondition check. <see cref="IsSatisfied"/> is whether the write may
/// PROCEED under the precondition: for If-Match this means the tag matched; for If-None-Match the
/// polarity is inverted (satisfied = the tag did NOT match). Computed by
/// <see cref="EtagPreconditionEvaluator"/> so the inverted semantics live in one place.
/// </summary>
internal sealed record RelationalCurrentEtagPreconditionCheckResult(
    RelationalWriteCurrentState CurrentState,
    RelationalWriteTargetContext.ExistingDocument TargetContext,
    bool IsSatisfied
);

internal interface IRelationalCurrentEtagPreconditionChecker
{
    Task<RelationalCurrentEtagPreconditionCheckResult?> CheckAsync(
        RelationalCurrentEtagPreconditionCheckRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    );
}

internal sealed class RelationalCurrentEtagPreconditionChecker(
    IRelationalWriteCurrentStateLoader currentStateLoader,
    ILogger<RelationalCurrentEtagPreconditionChecker> logger
) : IRelationalCurrentEtagPreconditionChecker, IRelationalDeleteEtagPreconditionChecker
{
    private readonly IRelationalWriteCurrentStateLoader _currentStateLoader =
        currentStateLoader ?? throw new ArgumentNullException(nameof(currentStateLoader));

    private readonly ILogger<RelationalCurrentEtagPreconditionChecker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public RelationalDeleteEtagPreconditionCheckResult Evaluate(
        MappingSet mappingSet,
        RelationalWriteTargetContext.ExistingDocument lockedTargetContext,
        WritePrecondition.IfMatch precondition
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(lockedTargetContext);
        ArgumentNullException.ThrowIfNull(precondition);

        // DELETE serves no representation, and write preconditions compare only ContentVersion and
        // schemaEpoch. Evaluate that projection directly instead of inventing format/profile/link inputs.
        var isSatisfied = EtagPreconditionEvaluator.IsSatisfiedByCurrentState(
            precondition,
            lockedTargetContext.ObservedContentVersion,
            mappingSet.Key.EffectiveSchemaHash
        );

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "DELETE If-Match precondition for document {DocumentId}: wildcard={IsWildcard}, "
                    + "clientTag={ClientTag}, contentVersion={ContentVersion}, matched={IsMatch}",
                lockedTargetContext.DocumentId,
                precondition.IsWildcard,
                LoggingSanitizer.SanitizeForLogging(precondition.IsWildcard ? "*" : precondition.Value),
                lockedTargetContext.ObservedContentVersion,
                isSatisfied
            );
        }

        return new RelationalDeleteEtagPreconditionCheckResult(lockedTargetContext, isSatisfied);
    }

    public async Task<RelationalCurrentEtagPreconditionCheckResult?> CheckAsync(
        RelationalCurrentEtagPreconditionCheckRequest request,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeSession);

        var documentLocked = await TryLockDocumentAsync(
                request.MappingSet.Key.Dialect,
                request.TargetContext.DocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!documentLocked)
        {
            return null;
        }

        var currentState = await _currentStateLoader
            .LoadAsync(
                new RelationalWriteCurrentStateLoadRequest(
                    request.ReadPlan,
                    request.TargetContext,
                    // External-response ETag comparison always needs descriptor URI hydration when
                    // the read plan serves descriptor-valued members, regardless of profile use.
                    includeDescriptorProjection: true
                ),
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (currentState is null)
        {
            return null;
        }

        var refreshedTargetContext = request.TargetContext with
        {
            ObservedContentVersion = currentState.DocumentMetadata.ContentVersion,
        };

        var isSatisfied = EtagPreconditionEvaluator.IsSatisfiedByCurrentState(
            request.Precondition,
            currentState.DocumentMetadata.ContentVersion,
            request.MappingSet.Key.EffectiveSchemaHash
        );

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Etag precondition for document {DocumentId}: contentVersion={ContentVersion}, satisfied={IsSatisfied}",
                request.TargetContext.DocumentId,
                currentState.DocumentMetadata.ContentVersion,
                isSatisfied
            );
        }

        return new RelationalCurrentEtagPreconditionCheckResult(
            currentState,
            refreshedTargetContext,
            isSatisfied
        );
    }

    private static async Task<bool> TryLockDocumentAsync(
        SqlDialect dialect,
        long documentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        await using var command = writeSession.CreateCommand(
            RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(dialect, documentId)
        );

        var scalarResult = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalarResult is not null and not DBNull;
    }
}
