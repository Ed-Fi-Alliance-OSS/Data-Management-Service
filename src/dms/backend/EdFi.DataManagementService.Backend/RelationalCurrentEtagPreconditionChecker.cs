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
        WritePrecondition precondition,
        string? profileName = null
    )
    {
        MappingSet = mappingSet ?? throw new ArgumentNullException(nameof(mappingSet));
        ReadPlan = readPlan ?? throw new ArgumentNullException(nameof(readPlan));
        TargetContext = targetContext ?? throw new ArgumentNullException(nameof(targetContext));
        Precondition = precondition ?? throw new ArgumentNullException(nameof(precondition));
        ProfileName = profileName;
    }

    public MappingSet MappingSet { get; init; }

    public ResourceReadPlan ReadPlan { get; init; }

    public RelationalWriteTargetContext.ExistingDocument TargetContext { get; init; }

    public WritePrecondition Precondition { get; init; }

    /// <summary>
    /// The readable-profile name of the representation the client is acting on, or <see langword="null"/>
    /// when no profile applies (e.g. a DELETE). Drives the <c>profileCode</c> of the composed (served)
    /// etag; as of the 2026-07-04 ADR amendment <c>profileCode</c> is projected out of the If-Match
    /// comparison, so this affects the returned etag but not whether the precondition matches.
    /// </summary>
    public string? ProfileName { get; init; }
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
    string CurrentEtag,
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
    IServedEtagComposer servedEtagComposer,
    ILogger<RelationalCurrentEtagPreconditionChecker> logger
) : IRelationalCurrentEtagPreconditionChecker, IRelationalDeleteEtagPreconditionChecker
{
    private readonly IRelationalWriteCurrentStateLoader _currentStateLoader =
        currentStateLoader ?? throw new ArgumentNullException(nameof(currentStateLoader));

    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));

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

        // DELETE serves no body and applies no profile lens, so there is nothing to hydrate: the caller
        // has already resolved and locked the target row (capturing its ContentVersion), so the current
        // served etag is composed directly from that locked ContentVersion plus the schema epoch. If-Match
        // then compares only the state-significant projection (ContentVersion, schemaEpoch); format,
        // linkFlag, and profileCode are projected out. A wildcard (If-Match: *) matches unconditionally
        // because reaching this point means the caller holds the lock on an existing row.
        var currentEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                mappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: null,
                LinksEnabled: true,
                lockedTargetContext.ObservedContentVersion
            )
        );

        var isSatisfied = EtagPreconditionEvaluator.IsSatisfied(
            precondition,
            targetExists: true,
            currentEtag
        );

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "DELETE If-Match precondition for document {DocumentId}: wildcard={IsWildcard}, "
                    + "clientTag={ClientTag}, currentTag={CurrentTag}, matched={IsMatch}",
                lockedTargetContext.DocumentId,
                precondition.IsWildcard,
                LoggingSanitizer.SanitizeForLogging(precondition.IsWildcard ? "*" : precondition.Value),
                currentEtag,
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

        // Compose the current served etag for the If-Match comparison. If-Match compares only the
        // state-significant projection (ContentVersion, schemaEpoch); format, linkFlag, and profileCode
        // are projected out — profileCode as of the 2026-07-04 ADR amendment — so the profile/link inputs
        // here do not affect the match, but the full composition keeps the debug log meaningful. A
        // wildcard precondition (If-Match: *) is handled inside the evaluator, which matches
        // unconditionally because reaching this point means the target row exists and is locked.
        var currentEtag = _servedEtagComposer.Compose(
            new ServedEtagContext(
                request.MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                request.ProfileName,
                LinksEnabled: true,
                currentState.DocumentMetadata.ContentVersion
            )
        );
        var refreshedTargetContext = request.TargetContext with
        {
            ObservedContentVersion = currentState.DocumentMetadata.ContentVersion,
        };

        var isSatisfied = EtagPreconditionEvaluator.IsSatisfied(
            request.Precondition,
            targetExists: true,
            currentEtag
        );

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Etag precondition for document {DocumentId}: currentTag={CurrentTag}, satisfied={IsSatisfied}",
                request.TargetContext.DocumentId,
                currentEtag,
                isSatisfied
            );
        }

        return new RelationalCurrentEtagPreconditionCheckResult(
            currentState,
            refreshedTargetContext,
            currentEtag,
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
