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
/// <see cref="EtagPreconditionEvaluator"/> so the inverted semantics live in one place. The
/// <see cref="CurrentState"/> is loaded only when the precondition is satisfied.
/// </summary>
internal sealed record RelationalCurrentEtagPreconditionCheckResult(
    RelationalWriteCurrentState? CurrentState,
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
        // schemaEpoch. Evaluate that projection directly instead of inventing
        // format/profile/link/content-coding inputs.
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

        var lockedContentVersion = await RelationalWriteTargetLocking
            .TryLockExistingTargetAsync(
                request.MappingSet.Key.Dialect,
                request.TargetContext.DocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (lockedContentVersion is null)
        {
            return null;
        }

        var lockedTargetContext = request.TargetContext with
        {
            ObservedContentVersion = lockedContentVersion.Value,
        };

        var isSatisfied = EtagPreconditionEvaluator.IsSatisfiedByCurrentState(
            request.Precondition,
            lockedContentVersion.Value,
            request.MappingSet.Key.EffectiveSchemaHash
        );

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Etag precondition for document {DocumentId}: contentVersion={ContentVersion}, satisfied={IsSatisfied}",
                request.TargetContext.DocumentId,
                lockedContentVersion.Value,
                isSatisfied
            );
        }

        if (!isSatisfied)
        {
            return new RelationalCurrentEtagPreconditionCheckResult(null, lockedTargetContext, false);
        }

        var currentState = await _currentStateLoader
            .LoadAsync(
                new RelationalWriteCurrentStateLoadRequest(
                    request.ReadPlan,
                    lockedTargetContext,
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

        var refreshedTargetContext = lockedTargetContext with
        {
            ObservedContentVersion = currentState.DocumentMetadata.ContentVersion,
        };

        return new RelationalCurrentEtagPreconditionCheckResult(currentState, refreshedTargetContext, true);
    }
}
