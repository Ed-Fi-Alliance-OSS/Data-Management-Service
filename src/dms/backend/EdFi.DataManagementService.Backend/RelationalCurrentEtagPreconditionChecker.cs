// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal sealed record RelationalCurrentEtagPreconditionCheckRequest
{
    public RelationalCurrentEtagPreconditionCheckRequest(
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        WritePrecondition.IfMatch precondition,
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

    public WritePrecondition.IfMatch Precondition { get; init; }

    /// <summary>
    /// The readable-profile name of the representation the client is acting on, or <see langword="null"/>
    /// when no profile applies (e.g. a DELETE). Drives the <c>profileCode</c> of the composed (served)
    /// etag; as of the 2026-07-04 ADR amendment <c>profileCode</c> is projected out of the If-Match
    /// comparison, so this affects the returned etag but not whether the precondition matches.
    /// </summary>
    public string? ProfileName { get; init; }
}

internal sealed record RelationalCurrentEtagPreconditionCheckResult(
    RelationalWriteCurrentState CurrentState,
    RelationalWriteTargetContext.ExistingDocument TargetContext,
    string CurrentEtag,
    bool IsMatch
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
    IEtagComposer etagComposer
) : IRelationalCurrentEtagPreconditionChecker, IRelationalDeleteEtagPreconditionChecker
{
    private readonly IRelationalWriteCurrentStateLoader _currentStateLoader =
        currentStateLoader ?? throw new ArgumentNullException(nameof(currentStateLoader));

    private readonly IEtagComposer _etagComposer =
        etagComposer ?? throw new ArgumentNullException(nameof(etagComposer));

    public async Task<RelationalDeleteEtagPreconditionCheckResult?> CheckAsync(
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        WritePrecondition.IfMatch precondition,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken = default
    )
    {
        var result = await CheckAsync(
                new RelationalCurrentEtagPreconditionCheckRequest(
                    mappingSet,
                    readPlan,
                    targetContext,
                    precondition
                ),
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        return result is null
            ? null
            : new RelationalDeleteEtagPreconditionCheckResult(result.TargetContext, result.IsMatch);
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

        // Compose the current served etag (it still carries profileCode from the request's profile, for
        // the returned CurrentEtag). If-Match then compares only the state-significant projection
        // (ContentVersion, schemaEpoch); format, linkFlag, and profileCode are projected out — profileCode
        // as of the 2026-07-04 ADR amendment. A wildcard precondition (If-Match: *) short-circuits to a
        // match here because reaching this point means the target row exists and is locked.
        var currentEtag = _etagComposer.Compose(
            currentState.DocumentMetadata.ContentVersion,
            VariantKeyFactory.Create(
                request.MappingSet.Key.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileVariantCode.Of(request.ProfileName),
                linksEnabled: true
            )
        );
        var refreshedTargetContext = request.TargetContext with
        {
            ObservedContentVersion = currentState.DocumentMetadata.ContentVersion,
        };

        return new RelationalCurrentEtagPreconditionCheckResult(
            currentState,
            refreshedTargetContext,
            currentEtag,
            request.Precondition.IsWildcard
                || string.Equals(
                    EtagMatchProjection.Of(request.Precondition.Value),
                    EtagMatchProjection.Of(currentEtag),
                    StringComparison.Ordinal
                )
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
