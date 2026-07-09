// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Descriptor write handler that persists descriptor resources into
/// <c>dms.Document</c>, <c>dms.Descriptor</c>, and <c>dms.ReferentialIdentity</c>.
/// </summary>
internal sealed class DescriptorWriteHandler(
    IRelationalWriteTargetLookupService targetLookupService,
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IRelationalDeleteConstraintResolver deleteConstraintResolver,
    IRelationalWriteSessionFactory writeSessionFactory,
    ILogger<DescriptorWriteHandler> logger,
    IServedEtagComposer servedEtagComposer,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null
) : IDescriptorWriteHandler
{
    private readonly IRelationalWriteTargetLookupService _targetLookupService =
        targetLookupService ?? throw new ArgumentNullException(nameof(targetLookupService));
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));
    private readonly IRelationalDeleteConstraintResolver _deleteConstraintResolver =
        deleteConstraintResolver ?? throw new ArgumentNullException(nameof(deleteConstraintResolver));
    private readonly IRelationalWriteSessionFactory _writeSessionFactory =
        writeSessionFactory ?? throw new ArgumentNullException(nameof(writeSessionFactory));
    private readonly ILogger<DescriptorWriteHandler> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));
    private readonly IRelationshipAuthorizationProviderFailureExtractor _relationshipAuthorizationProviderFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;

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

        // Namespace planner terminals (no usable root column, no prefixes, MSSQL prefix cap) and
        // unsupported strategies resolve before any session opens, so a denial issues no DB roundtrip.
        // The stored and proposed namespace checks run inside the descriptor write session against the
        // resolved target (see the per-path execution helpers below).
        var authorizationPreflight = ResolveDescriptorWriteAuthorization(
            request,
            NamespaceAuthorizationOperation.Update,
            "descriptor POST",
            "POST"
        );

        switch (authorizationPreflight)
        {
            case DescriptorWriteAuthorizationPreflightOutcome.NotImplemented notImplemented:
                return new UpsertResult.UpsertFailureNotImplemented(
                    notImplemented.FailureMessage,
                    UpsertFailureNotImplementedReason.StrategyNotEnabled
                );
            case DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                return new UpsertResult.UpsertFailureSecurityConfiguration(
                    configError.Errors,
                    configError.Diagnostics
                );
            case DescriptorWriteAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                return new UpsertResult.UpsertFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure);
        }

        var proceed = (DescriptorWriteAuthorizationPreflightOutcome.Proceed)authorizationPreflight;
        var storedNamespaceAuthorization = proceed.StoredNamespaceAuthorization;
        var proposedNamespaceAuthorization = proceed.ProposedNamespaceAuthorization;

        var body = DescriptorWriteBodyExtractor.Extract(request.RequestBody, request.Resource);
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(
            request.MappingSet,
            request.Resource
        );

        IRelationalWriteSession? writeSession = null;

        try
        {
            if (!RelationalWriteExecutionStateResolver.HasEtagPrecondition(request.WritePrecondition))
            {
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

                return targetContext switch
                {
                    RelationalWriteTargetContext.CreateNew(var documentUuid) =>
                        await ExecuteDescriptorInsertWithProposedNamespaceCheckAsync(
                                request,
                                body,
                                documentUuid,
                                resourceKeyId,
                                proposedNamespaceAuthorization,
                                cancellationToken
                            )
                            .ConfigureAwait(false),

                    RelationalWriteTargetContext.ExistingDocument(var documentId, var documentUuid, _) =>
                        await ApplyDescriptorPostUpsertWithLockedCurrentStateAsync(
                                request,
                                body,
                                documentId,
                                documentUuid,
                                resourceKeyId,
                                storedNamespaceAuthorization,
                                proposedNamespaceAuthorization,
                                cancellationToken
                            )
                            .ConfigureAwait(false),

                    _ => throw new InvalidOperationException(
                        $"Unexpected target context type '{targetContext.GetType().Name}' for descriptor POST."
                    ),
                };
            }

            writeSession = await _writeSessionFactory.CreateAsync(cancellationToken).ConfigureAwait(false);

            var preconditionResult = await ResolveLockedDescriptorForPreconditionAsync(
                    request.MappingSet,
                    request.Resource,
                    request.DocumentUuid,
                    request.ReferentialId,
                    DescriptorPreconditionTargetKind.Post,
                    request.WritePrecondition,
                    writeSession,
                    cancellationToken,
                    request.ProfileName,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization,
                    body.Namespace
                )
                .ConfigureAwait(false);

            switch (preconditionResult)
            {
                case DescriptorLockedPreconditionResult.CreateNew(var createDocumentUuid):
                    // If-Match on an insert has no current representation to match, so it fails (412).
                    // If-None-Match on an insert is the create-only success case: no current
                    // representation exists, so the insert proceeds in the same locked session (the
                    // proposed namespace check already ran inside the resolve).
                    if (request.WritePrecondition is WritePrecondition.IfNoneMatch)
                    {
                        var insertResult = await InsertDescriptorAsync(
                                request,
                                body,
                                createDocumentUuid,
                                resourceKeyId,
                                writeSession.CreateCommandExecutor(),
                                cancellationToken
                            )
                            .ConfigureAwait(false);

                        await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return insertResult;
                    }

                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UpsertFailureETagMisMatch(
                        ETagPreconditionFailureReason.TargetDoesNotExist
                    );

                case DescriptorLockedPreconditionResult.MissingDocument:
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UpsertFailureWriteConflict();

                case DescriptorLockedPreconditionResult.MissingDescriptor(var missingDescriptorDocumentId):
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UnknownFailure(
                        BuildMissingDescriptorMessage(request.Resource, missingDescriptorDocumentId)
                    );

                case DescriptorLockedPreconditionResult.NamespaceNotAuthorized(var namespaceFailure):
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UpsertFailureNamespaceNotAuthorized(namespaceFailure);

                case DescriptorLockedPreconditionResult.NamespaceAuthorizationInvalid(
                    var namespaceFailureMessage,
                    var diagnostics
                ):
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UpsertFailureSecurityConfiguration(
                        [namespaceFailureMessage],
                        diagnostics
                    );

                case DescriptorLockedPreconditionResult.Mismatch:
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UpsertFailureETagMisMatch();

                case DescriptorLockedPreconditionResult.Loaded(
                    var sessionTargetContext,
                    var persisted,
                    var currentEtag
                ):
                    return await ApplyLockedDescriptorPostUpsertAsync(
                            request,
                            body,
                            sessionTargetContext.DocumentId,
                            sessionTargetContext.DocumentUuid,
                            resourceKeyId,
                            persisted,
                            currentEtag,
                            writeSession,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                default:
                    throw new InvalidOperationException(
                        $"Unexpected locked descriptor precondition result type '{preconditionResult.GetType().Name}'."
                    );
            }
        }
        catch (DbException ex) when (_writeExceptionClassifier.IsUniqueConstraintViolation(ex))
        {
            if (writeSession is not null)
            {
                await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogDebug(
                ex,
                "Unique constraint violation on descriptor POST for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                request.TraceId.Value
            );

            return new UpsertResult.UpsertFailureWriteConflict();
        }
        catch (DbException ex) when (_writeExceptionClassifier.IsTransientFailure(ex))
        {
            if (writeSession is not null)
            {
                await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogDebug(
                ex,
                "Transient conflict on descriptor POST for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
            );

            return new UpsertResult.UpsertFailureWriteConflict();
        }
        catch (DbException ex)
        {
            if (writeSession is not null)
            {
                await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            if (writeSession is not null)
            {
                await writeSession.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<UpdateResult> HandlePutAsync(
        DescriptorWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Namespace planner terminals (no usable root column, no prefixes, MSSQL prefix cap) and
        // unsupported strategies resolve before any session opens, so a denial issues no DB roundtrip.
        // The stored and proposed namespace checks run inside the descriptor write session against the
        // resolved target (see the per-path execution helpers below).
        var authorizationPreflight = ResolveDescriptorWriteAuthorization(
            request,
            NamespaceAuthorizationOperation.Update,
            "descriptor PUT",
            "PUT"
        );

        switch (authorizationPreflight)
        {
            case DescriptorWriteAuthorizationPreflightOutcome.NotImplemented notImplemented:
                return new UpdateResult.UpdateFailureNotImplemented(notImplemented.FailureMessage);
            case DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                return new UpdateResult.UpdateFailureSecurityConfiguration(
                    configError.Errors,
                    configError.Diagnostics
                );
            case DescriptorWriteAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                return new UpdateResult.UpdateFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure);
        }

        var proceed = (DescriptorWriteAuthorizationPreflightOutcome.Proceed)authorizationPreflight;
        var storedNamespaceAuthorization = proceed.StoredNamespaceAuthorization;
        var proposedNamespaceAuthorization = proceed.ProposedNamespaceAuthorization;

        var body = DescriptorWriteBodyExtractor.Extract(request.RequestBody, request.Resource);

        IRelationalWriteSession? writeSession = null;

        try
        {
            if (RelationalWriteExecutionStateResolver.HasEtagPrecondition(request.WritePrecondition))
            {
                writeSession = await _writeSessionFactory
                    .CreateAsync(cancellationToken)
                    .ConfigureAwait(false);

                var preconditionResult = await ResolveLockedDescriptorForPreconditionAsync(
                        request.MappingSet,
                        request.Resource,
                        request.DocumentUuid,
                        referentialId: null,
                        DescriptorPreconditionTargetKind.Put,
                        request.WritePrecondition,
                        writeSession,
                        cancellationToken,
                        request.ProfileName,
                        storedNamespaceAuthorization,
                        proposedNamespaceAuthorization,
                        body.Namespace
                    )
                    .ConfigureAwait(false);

                switch (preconditionResult)
                {
                    case DescriptorLockedPreconditionResult.NotFound:
                    case DescriptorLockedPreconditionResult.MissingDocument:
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        // RFC 7232 If-Match: * requires the target to exist; a wildcard If-Match against
                        // a missing PUT target yields the precondition-failed (412) result rather than
                        // not-exists (404). A wildcard If-None-Match against a missing target is the
                        // success case, so it falls through to the normal not-exists (404) result.
                        return request.WritePrecondition is WritePrecondition.IfMatch { IsWildcard: true }
                            ? new UpdateResult.UpdateFailureETagMisMatch()
                            : new UpdateResult.UpdateFailureNotExists();

                    case DescriptorLockedPreconditionResult.MissingDescriptor(
                        var missingDescriptorDocumentId
                    ):
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return new UpdateResult.UnknownFailure(
                            BuildMissingDescriptorMessage(request.Resource, missingDescriptorDocumentId)
                        );

                    case DescriptorLockedPreconditionResult.NamespaceNotAuthorized(var namespaceFailure):
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return new UpdateResult.UpdateFailureNamespaceNotAuthorized(namespaceFailure);

                    case DescriptorLockedPreconditionResult.NamespaceAuthorizationInvalid(
                        var namespaceFailureMessage,
                        var diagnostics
                    ):
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return new UpdateResult.UpdateFailureSecurityConfiguration(
                            [namespaceFailureMessage],
                            diagnostics
                        );

                    case DescriptorLockedPreconditionResult.Mismatch:
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return new UpdateResult.UpdateFailureETagMisMatch();

                    case DescriptorLockedPreconditionResult.Loaded(
                        var sessionTargetContext,
                        var persisted,
                        var currentEtag
                    ):
                        return await ApplyLockedDescriptorPutAsync(
                                request,
                                body,
                                sessionTargetContext.DocumentId,
                                sessionTargetContext.DocumentUuid,
                                persisted,
                                currentEtag,
                                writeSession,
                                cancellationToken
                            )
                            .ConfigureAwait(false);

                    default:
                        throw new InvalidOperationException(
                            $"Unexpected locked descriptor precondition result type '{preconditionResult.GetType().Name}'."
                        );
                }
            }

            _logger.LogDebug(
                "Resolving descriptor PUT target context for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                request.TraceId.Value
            );

            var targetLookupResult = await _targetLookupService
                .ResolveForPutAsync(
                    request.MappingSet,
                    request.Resource,
                    request.DocumentUuid,
                    cancellationToken
                )
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

            return await ApplyDescriptorPutWithLockedCurrentStateAsync(
                    request,
                    body,
                    documentId,
                    documentUuid,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex) when (_writeExceptionClassifier.IsTransientFailure(ex))
        {
            if (writeSession is not null)
            {
                await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogDebug(
                ex,
                "Transient conflict on descriptor PUT for {Resource} - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
            );

            return new UpdateResult.UpdateFailureWriteConflict();
        }
        catch (DbException ex)
        {
            if (writeSession is not null)
            {
                await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);
            }

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
        finally
        {
            if (writeSession is not null)
            {
                await writeSession.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<DeleteResult> HandleDeleteAsync(
        DescriptorDeleteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogDebug(
            "Deleting descriptor document {DocumentUuid} for {Resource} - {TraceId}",
            request.DocumentUuid.Value,
            RelationalWriteSupport.FormatResource(request.Resource),
            LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
        );

        // Namespace planner terminals (no usable root column, no prefixes, MSSQL prefix cap) and
        // unsupported strategies resolve before the write session opens, so a denial issues no DB
        // roundtrip and never locks the target. The stored namespace check itself runs inside the
        // delete session against the resolved target (see the stored-namespace check below).
        var authorizationPreflight = AuthorizeDescriptorDeletePreflight(request);

        if (authorizationPreflight is DescriptorDeleteAuthorizationPreflightResult.Stop stop)
        {
            return stop.Result;
        }

        var storedNamespaceAuthorization = (
            (DescriptorDeleteAuthorizationPreflightResult.Proceed)authorizationPreflight
        ).StoredNamespaceAuthorization;

        // Scope the DELETE by ResourceKeyId so a UUID belonging to a different descriptor
        // (or a non-descriptor document) cannot be deleted through this resource endpoint.
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(
            request.MappingSet,
            request.Resource
        );

        var ifMatch = request.WritePrecondition as WritePrecondition.IfMatch;

        IRelationalWriteSession writeSession;

        try
        {
            writeSession = await _writeSessionFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex) when (_writeExceptionClassifier.IsTransientFailure(ex))
        {
            _logger.LogDebug(
                ex,
                "Transient conflict creating write session for descriptor DELETE on {DocumentUuid} - {TraceId}",
                request.DocumentUuid.Value,
                LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
            );

            return new DeleteResult.DeleteFailureWriteConflict();
        }
        catch (DbException ex)
        {
            _logger.LogError(
                ex,
                "Database error creating write session for descriptor DELETE on {DocumentUuid} - {TraceId}",
                request.DocumentUuid.Value,
                LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
            );

            return new DeleteResult.UnknownFailure(
                "An unexpected error occurred while processing the delete request."
            );
        }

        await using (writeSession)
        {
            var sessionCommandExecutor = writeSession.CreateCommandExecutor();
            DeleteResult outcome;

            try
            {
                if (ifMatch is null)
                {
                    // The stored namespace check (when configured) AND-composes before the delete and
                    // must read the resolved target's namespace, so resolve the document first, then
                    // lock the resolved row before authorizing. Locking between resolve and auth
                    // closes the race where a concurrent committed delete would let auth fire against
                    // a stale view and the subsequent delete silently no-op. The public DELETE route
                    // addresses a server-generated, unique DocumentUuid; clients cannot create or
                    // replace a descriptor under an arbitrary existing UUID, and descriptor PUT cannot
                    // change Namespace/CodeValue identity. Deleting by the same DocumentUuid plus
                    // ResourceKeyId after authorizing the locked row therefore does not allow an
                    // unauthorized replacement-delete path.
                    if (storedNamespaceAuthorization is not null)
                    {
                        var resolvedDeleteTarget = await RelationalDocumentUuidLookupSupport
                            .TryResolveDeleteTargetAsync(
                                sessionCommandExecutor,
                                request.MappingSet,
                                request.Resource,
                                request.DocumentUuid,
                                cancellationToken
                            )
                            .ConfigureAwait(false);

                        if (resolvedDeleteTarget is null)
                        {
                            outcome = new DeleteResult.DeleteFailureNotExists();
                        }
                        else if (
                            await RelationalWriteTargetLocking
                                .TryLockExistingTargetAsync(
                                    request.MappingSet.Key.Dialect,
                                    resolvedDeleteTarget.DocumentId,
                                    writeSession,
                                    cancellationToken
                                )
                                .ConfigureAwait(false)
                            is null
                        )
                        {
                            outcome = new DeleteResult.DeleteFailureNotExists();
                        }
                        else
                        {
                            var namespaceFailure = MapDeleteNamespaceAuthorizationResult(
                                await ExecuteDescriptorNamespaceAuthorizationAsync(
                                        request.MappingSet,
                                        resolvedDeleteTarget.DocumentId,
                                        storedNamespaceAuthorization,
                                        proposedNamespace: null,
                                        sessionCommandExecutor,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                            );

                            outcome =
                                namespaceFailure
                                ?? await ExecuteDescriptorDeleteCommandAsync(
                                        request,
                                        resourceKeyId,
                                        sessionCommandExecutor,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        outcome = await ExecuteDescriptorDeleteCommandAsync(
                                request,
                                resourceKeyId,
                                sessionCommandExecutor,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    var preconditionResult = await ResolveLockedDescriptorForPreconditionAsync(
                            request.MappingSet,
                            request.Resource,
                            request.DocumentUuid,
                            referentialId: null,
                            DescriptorPreconditionTargetKind.Delete,
                            ifMatch,
                            writeSession,
                            cancellationToken,
                            // DELETE has no profile lens, so the current etag is unprofiled.
                            profileName: null,
                            storedNamespaceAuthorization: storedNamespaceAuthorization
                        )
                        .ConfigureAwait(false);

                    outcome = preconditionResult switch
                    {
                        // RFC 7232 If-Match: * requires the target to exist; a wildcard against a
                        // missing DELETE target yields the precondition-failed (412) result rather
                        // than not-exists (404).
                        DescriptorLockedPreconditionResult.NotFound
                        or DescriptorLockedPreconditionResult.MissingDocument => ifMatch.IsWildcard
                            ? new DeleteResult.DeleteFailureETagMisMatch(
                                ETagPreconditionFailureReason.TargetDoesNotExist
                            )
                            : new DeleteResult.DeleteFailureNotExists(),
                        DescriptorLockedPreconditionResult.MissingDescriptor(var documentId) =>
                            new DeleteResult.UnknownFailure(
                                BuildMissingDescriptorMessage(request.Resource, documentId)
                            ),
                        DescriptorLockedPreconditionResult.NamespaceNotAuthorized(var namespaceFailure) =>
                            new DeleteResult.DeleteFailureNamespaceNotAuthorized(namespaceFailure),
                        DescriptorLockedPreconditionResult.NamespaceAuthorizationInvalid(
                            var failureMessage,
                            var diagnostics
                        ) => new DeleteResult.DeleteFailureSecurityConfiguration(
                            [failureMessage],
                            diagnostics
                        ),
                        DescriptorLockedPreconditionResult.Mismatch =>
                            new DeleteResult.DeleteFailureETagMisMatch(),
                        DescriptorLockedPreconditionResult.Loaded =>
                            await ExecuteDescriptorDeleteCommandAsync(
                                    request,
                                    resourceKeyId,
                                    sessionCommandExecutor,
                                    cancellationToken
                                )
                                .ConfigureAwait(false),
                        _ => throw new InvalidOperationException(
                            $"Unexpected locked descriptor precondition result type '{preconditionResult.GetType().Name}'."
                        ),
                    };
                }
            }
            catch (DbException ex) when (_writeExceptionClassifier.IsTransientFailure(ex))
            {
                _logger.LogDebug(
                    ex,
                    "Transient conflict resolving descriptor DELETE target for {DocumentUuid} - {TraceId}",
                    request.DocumentUuid.Value,
                    LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
                );

                await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);
                return new DeleteResult.DeleteFailureWriteConflict();
            }
            catch (DbException ex)
            {
                _logger.LogError(
                    ex,
                    "Database error resolving descriptor DELETE target for {DocumentUuid} - {TraceId}",
                    request.DocumentUuid.Value,
                    LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
                );

                await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);
                return new DeleteResult.UnknownFailure(
                    "An unexpected error occurred while processing the delete request."
                );
            }

            if (outcome is DeleteResult.DeleteSuccess)
            {
                try
                {
                    await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbException ex) when (_writeExceptionClassifier.IsTransientFailure(ex))
                {
                    _logger.LogDebug(
                        ex,
                        "Transient conflict committing descriptor DELETE for {DocumentUuid} - {TraceId}",
                        request.DocumentUuid.Value,
                        LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
                    );

                    return new DeleteResult.DeleteFailureWriteConflict();
                }
                catch (DbException ex)
                {
                    _logger.LogError(
                        ex,
                        "Database error committing descriptor DELETE for {DocumentUuid} - {TraceId}",
                        request.DocumentUuid.Value,
                        LoggingSanitizer.SanitizeForLogging(request.TraceId.Value)
                    );

                    return new DeleteResult.UnknownFailure(
                        "An unexpected error occurred while processing the delete request."
                    );
                }
            }
            else
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }
    }

    private async Task<DescriptorLockedPreconditionResult> ResolveLockedDescriptorForPreconditionAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        DocumentUuid documentUuid,
        ReferentialId? referentialId,
        DescriptorPreconditionTargetKind targetKind,
        WritePrecondition precondition,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken,
        string? profileName = null,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization = null,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization = null,
        string? proposedNamespace = null
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentNullException.ThrowIfNull(writeSession);

        var sessionCommandExecutor = writeSession.CreateCommandExecutor();
        RelationalWriteTargetContext targetContext;

        switch (targetKind)
        {
            case DescriptorPreconditionTargetKind.Post:
                if (referentialId is null)
                {
                    throw new InvalidOperationException(
                        "Descriptor POST requires a ReferentialId for target context resolution."
                    );
                }

                var postTargetLookupResult = await RelationalWriteTargetLookupSupport
                    .ResolveForPostAsync(
                        sessionCommandExecutor,
                        mappingSet,
                        resource,
                        referentialId.Value,
                        documentUuid,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                targetContext = TranslateDescriptorTargetContext(postTargetLookupResult, "POST");
                break;

            case DescriptorPreconditionTargetKind.Put:
                var putTargetLookupResult = await RelationalWriteTargetLookupSupport
                    .ResolveForPutAsync(
                        sessionCommandExecutor,
                        mappingSet,
                        resource,
                        documentUuid,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (putTargetLookupResult is RelationalWriteTargetLookupResult.NotFound)
                {
                    return DescriptorLockedPreconditionResult.NotFound.Instance;
                }

                targetContext = TranslateDescriptorTargetContext(putTargetLookupResult, "PUT");
                break;

            case DescriptorPreconditionTargetKind.Delete:
                var resolvedDeleteTarget = await RelationalDocumentUuidLookupSupport
                    .TryResolveDeleteTargetAsync(
                        sessionCommandExecutor,
                        mappingSet,
                        resource,
                        documentUuid,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (resolvedDeleteTarget is null)
                {
                    return DescriptorLockedPreconditionResult.NotFound.Instance;
                }

                targetContext = new RelationalWriteTargetContext.ExistingDocument(
                    resolvedDeleteTarget.DocumentId,
                    documentUuid
                );
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(targetKind), targetKind, null);
        }

        if (targetContext is RelationalWriteTargetContext.CreateNew(var createDocumentUuid))
        {
            // POST with If-Match where no existing document was found normally returns ETagMisMatch,
            // and If-None-Match on the same create proceeds to insert (the caller branches on the
            // precondition type). Either way a configured proposed-value namespace authorization must
            // run first so that a denial (403) precedes the precondition outcome. The proposed check is
            // a single statement against the dialect's namespace authorization SQL and needs no row
            // lookup.
            if (proposedNamespaceAuthorization is not null)
            {
                var preconditionFromProposed = await EvaluateNamespaceAuthorizationAsync(
                        mappingSet,
                        documentId: 0L,
                        proposedNamespaceAuthorization,
                        proposedNamespace,
                        sessionCommandExecutor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (preconditionFromProposed is not null)
                {
                    return preconditionFromProposed;
                }
            }

            return new DescriptorLockedPreconditionResult.CreateNew(createDocumentUuid);
        }

        if (targetContext is not RelationalWriteTargetContext.ExistingDocument existingTargetContext)
        {
            throw new InvalidOperationException(
                $"Unexpected target context type '{targetContext.GetType().Name}' for descriptor {targetKind}."
            );
        }

        var lockedCurrentState = await LoadLockedDescriptorCurrentStateAsync(
                mappingSet.Key.Dialect,
                mappingSet.Key.EffectiveSchemaHash,
                profileName,
                existingTargetContext.DocumentId,
                writeSession,
                cancellationToken
            )
            .ConfigureAwait(false);

        // Namespace authorization AND-composes before the If-Match precondition: run the stored and
        // proposed namespace checks against the locked target so a namespace denial (403) is returned
        // ahead of a stale-ETag mismatch (412). Only run them once the row is loaded; a missing row
        // falls through to the existing not-found/not-exists handling.
        if (lockedCurrentState is DescriptorCurrentStateLoadResult.Loaded)
        {
            if (storedNamespaceAuthorization is not null)
            {
                var preconditionFromStored = await EvaluateNamespaceAuthorizationAsync(
                        mappingSet,
                        existingTargetContext.DocumentId,
                        storedNamespaceAuthorization,
                        proposedNamespace: null,
                        sessionCommandExecutor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (preconditionFromStored is not null)
                {
                    return preconditionFromStored;
                }
            }

            if (proposedNamespaceAuthorization is not null)
            {
                var preconditionFromProposed = await EvaluateNamespaceAuthorizationAsync(
                        mappingSet,
                        existingTargetContext.DocumentId,
                        proposedNamespaceAuthorization,
                        proposedNamespace,
                        sessionCommandExecutor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                if (preconditionFromProposed is not null)
                {
                    return preconditionFromProposed;
                }
            }
        }

        return lockedCurrentState switch
        {
            DescriptorCurrentStateLoadResult.MissingDocument => DescriptorLockedPreconditionResult
                .MissingDocument
                .Instance,
            DescriptorCurrentStateLoadResult.MissingDescriptor =>
                new DescriptorLockedPreconditionResult.MissingDescriptor(existingTargetContext.DocumentId),
            DescriptorCurrentStateLoadResult.Loaded(_, var currentEtag)
                when !EtagPreconditionEvaluator.IsSatisfied(precondition, targetExists: true, currentEtag) =>
                DescriptorLockedPreconditionResult.Mismatch.Instance,
            DescriptorCurrentStateLoadResult.Loaded(var persisted, var currentEtag) =>
                new DescriptorLockedPreconditionResult.Loaded(existingTargetContext, persisted, currentEtag),
            _ => throw new InvalidOperationException(
                $"Unexpected locked descriptor state result type '{lockedCurrentState.GetType().Name}'."
            ),
        };
    }

    private static RelationalWriteTargetContext TranslateDescriptorTargetContext(
        RelationalWriteTargetLookupResult targetLookupResult,
        string operationLabel
    ) =>
        RelationalWriteSupport.TryTranslateTargetContext(targetLookupResult)
        ?? throw new InvalidOperationException(
            $"Unexpected target lookup result type '{targetLookupResult.GetType().Name}' for descriptor {operationLabel}."
        );

    private static RelationalCommand BuildDescriptorDeleteCommand(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        short resourceKeyId
    )
    {
        return OrderedDeleteCommandBuilder.BuildDescriptorDeleteCommand(dialect, documentUuid, resourceKeyId);
    }

    private Task<DeleteResult> ExecuteDescriptorDeleteCommandAsync(
        DescriptorDeleteRequest request,
        short resourceKeyId,
        IRelationalCommandExecutor sessionCommandExecutor,
        CancellationToken cancellationToken
    ) =>
        RelationalDeleteExecution.TryExecuteAsync(
            sessionCommandExecutor,
            BuildDescriptorDeleteCommand(sessionCommandExecutor.Dialect, request.DocumentUuid, resourceKeyId),
            _writeExceptionClassifier,
            _deleteConstraintResolver,
            request.MappingSet.Model,
            _logger,
            request.DocumentUuid,
            request.TraceId,
            DeleteTargetKind.Descriptor,
            cancellationToken
        );

    /// <summary>
    /// Plans descriptor DELETE namespace authorization through the relational authorization
    /// orchestrator before the write session opens. Strategies other than <c>NamespaceBased</c> /
    /// <c>NoFurtherAuthorizationRequired</c> fail closed; the namespace planner terminals
    /// (no configured prefixes, no usable root column, MSSQL prefix cap) short-circuit with no DB
    /// roundtrip.
    /// </summary>
    private static DescriptorDeleteAuthorizationPreflightResult AuthorizeDescriptorDeletePreflight(
        DescriptorDeleteRequest request
    )
    {
        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            request.AuthorizationStrategyEvaluators
        );
        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            request.MappingSet,
            request.MappingSet.GetConcreteResourceModelOrThrow(request.Resource),
            NamespaceAuthorizationOperation.Delete,
            configuredAuthorizationStrategies,
            request.RelationalAuthorizationContext
        );

        return orchestratorOutcome switch
        {
            RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot =>
                new DescriptorDeleteAuthorizationPreflightResult.Stop(
                    new DeleteResult.DeleteFailureSecurityConfiguration(
                        [
                            NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                                RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                            ),
                        ],
                        RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                    )
                ),
            RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes =>
                new DescriptorDeleteAuthorizationPreflightResult.Stop(
                    new DeleteResult.DeleteFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    )
                ),
            RelationalAuthorizationPlanOutcome.Plan plan
                when RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies(
                    plan.NonNamespaceConfiguredStrategies
                ) => new DescriptorDeleteAuthorizationPreflightResult.Stop(
                new DeleteResult.DeleteFailureNotImplemented(
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        request.Resource,
                        request.AuthorizationStrategyEvaluators,
                        "descriptor DELETE",
                        "DELETE"
                    )
                )
            ),
            RelationalAuthorizationPlanOutcome.Plan plan => AuthorizeDescriptorDeletePlanPreflight(
                request,
                plan
            ),
            RelationalAuthorizationPlanOutcome.StillUnsupported =>
                new DescriptorDeleteAuthorizationPreflightResult.Stop(
                    new DeleteResult.DeleteFailureNotImplemented(
                        RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                            request.Resource,
                            request.AuthorizationStrategyEvaluators,
                            "descriptor DELETE",
                            "DELETE"
                        )
                    )
                ),
            RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError =>
                new DescriptorDeleteAuthorizationPreflightResult.Stop(
                    BuildDescriptorDeleteSecurityConfigurationError(
                        request.Resource,
                        securityConfigurationError
                    )
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
            ),
        };
    }

    private static DescriptorDeleteAuthorizationPreflightResult AuthorizeDescriptorDeletePlanPreflight(
        DescriptorDeleteRequest request,
        RelationalAuthorizationPlanOutcome.Plan plan
    )
    {
        if (plan.NamespaceChecks.Count == 0)
        {
            return new DescriptorDeleteAuthorizationPreflightResult.Proceed(null);
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                request.MappingSet.Key.Dialect,
                request.RelationalAuthorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage,
                out var securityConfigurationDiagnostics
            )
        )
        {
            return new DescriptorDeleteAuthorizationPreflightResult.Stop(
                new DeleteResult.DeleteFailureSecurityConfiguration(
                    [securityConfigurationMessage],
                    securityConfigurationDiagnostics
                )
            );
        }

        return new DescriptorDeleteAuthorizationPreflightResult.Proceed(
            new RelationalWriteNamespaceAuthorization(plan.NamespaceChecks, namespacePrefixParameterization)
        );
    }

    /// <summary>
    /// Runs the namespace authorization checks against the descriptor write session's command executor,
    /// composing inside the same transaction that resolved/locked the target document.
    /// </summary>
    private Task<NamespaceAuthorizationExecutionResult> ExecuteDescriptorNamespaceAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationalWriteNamespaceAuthorization namespaceAuthorization,
        string? proposedNamespace,
        IRelationalCommandExecutor sessionCommandExecutor,
        CancellationToken cancellationToken
    )
    {
        var namespaceExecutor = new NamespaceAuthorizationExecutor(
            sessionCommandExecutor,
            _relationshipAuthorizationProviderFailureExtractor
        );

        return namespaceExecutor.ExecuteAsync(
            new NamespaceAuthorizationExecutionRequest(
                mappingSet,
                documentId,
                proposedNamespace,
                namespaceAuthorization.Checks,
                namespaceAuthorization.NamespacePrefixParameterization
            ),
            cancellationToken
        );
    }

    private static DeleteResult? MapDeleteNamespaceAuthorizationResult(
        NamespaceAuthorizationExecutionResult executionResult
    ) =>
        MapNamespaceAuthorizationToResult<DeleteResult>(
            executionResult,
            static failure => new DeleteResult.DeleteFailureNamespaceNotAuthorized(failure),
            static (failureMessage, diagnostics) =>
                new DeleteResult.DeleteFailureSecurityConfiguration([failureMessage], diagnostics),
            static () => new DeleteResult.DeleteFailureNotExists()
        );

    private abstract record DescriptorDeleteAuthorizationPreflightResult
    {
        private DescriptorDeleteAuthorizationPreflightResult() { }

        public sealed record Stop(DeleteResult Result) : DescriptorDeleteAuthorizationPreflightResult;

        public sealed record Proceed(RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization)
            : DescriptorDeleteAuthorizationPreflightResult;
    }

    /// <summary>
    /// Plans descriptor POST/PUT namespace authorization through the relational authorization
    /// orchestrator. Strategies other than <c>NamespaceBased</c> / <c>NoFurtherAuthorizationRequired</c>
    /// fail closed; the namespace planner terminals (no configured prefixes, no usable root column,
    /// MSSQL prefix cap) short-circuit with no DB roundtrip; otherwise the planner's checks are
    /// split into stored-value (locked target) and proposed-value (request body) authorizations
    /// re-indexed from zero, each executed as its own single-record statement inside the write
    /// session.
    /// </summary>
    private static DescriptorWriteAuthorizationPreflightOutcome ResolveDescriptorWriteAuthorization(
        DescriptorWriteRequest request,
        NamespaceAuthorizationOperation operation,
        string operationLabel,
        string actionLabel
    )
    {
        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            request.AuthorizationStrategyEvaluators
        );
        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            request.MappingSet,
            request.MappingSet.GetConcreteResourceModelOrThrow(request.Resource),
            operation,
            configuredAuthorizationStrategies,
            request.RelationalAuthorizationContext
        );

        return orchestratorOutcome switch
        {
            RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot =>
                new DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError(
                    [
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ],
                    RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                ),
            RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes =>
                new DescriptorWriteAuthorizationPreflightOutcome.NamespaceNotAuthorized(
                    NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                ),
            RelationalAuthorizationPlanOutcome.Plan plan
                when RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies(
                    plan.NonNamespaceConfiguredStrategies
                ) => new DescriptorWriteAuthorizationPreflightOutcome.NotImplemented(
                RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                    request.Resource,
                    request.AuthorizationStrategyEvaluators,
                    operationLabel,
                    actionLabel
                )
            ),
            RelationalAuthorizationPlanOutcome.Plan plan => BuildDescriptorWritePlanPreflight(request, plan),
            RelationalAuthorizationPlanOutcome.StillUnsupported =>
                new DescriptorWriteAuthorizationPreflightOutcome.NotImplemented(
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        request.Resource,
                        request.AuthorizationStrategyEvaluators,
                        operationLabel,
                        actionLabel
                    )
                ),
            RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError =>
                BuildDescriptorWriteSecurityConfigurationError(request.Resource, securityConfigurationError),
            _ => throw new InvalidOperationException(
                $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
            ),
        };
    }

    private static DeleteResult.DeleteFailureSecurityConfiguration BuildDescriptorDeleteSecurityConfigurationError(
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError
    )
    {
        var failure = RelationalReadGuardrails.BuildSecurityConfigurationFailure(
            resource,
            securityConfigurationError.NonNamespaceConfiguredStrategies,
            securityConfigurationError.RelationshipClassification
        );

        return new DeleteResult.DeleteFailureSecurityConfiguration(failure.Errors, failure.Diagnostics);
    }

    private static DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError BuildDescriptorWriteSecurityConfigurationError(
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError
    )
    {
        var failure = RelationalReadGuardrails.BuildSecurityConfigurationFailure(
            resource,
            securityConfigurationError.NonNamespaceConfiguredStrategies,
            securityConfigurationError.RelationshipClassification
        );

        return new DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError(
            failure.Errors,
            failure.Diagnostics
        );
    }

    private static DescriptorWriteAuthorizationPreflightOutcome BuildDescriptorWritePlanPreflight(
        DescriptorWriteRequest request,
        RelationalAuthorizationPlanOutcome.Plan plan
    )
    {
        if (plan.NamespaceChecks.Count == 0)
        {
            return DescriptorWriteAuthorizationPreflightOutcome.Proceed.NoAuthorization;
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                request.MappingSet.Key.Dialect,
                request.RelationalAuthorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage,
                out var securityConfigurationDiagnostics
            )
        )
        {
            return new DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError(
                [securityConfigurationMessage],
                securityConfigurationDiagnostics
            );
        }

        var stored = NamespaceAuthorizationFactory.SplitByValueSource(
            plan.NamespaceChecks,
            NamespaceAuthorizationCheckValueSource.Stored,
            namespacePrefixParameterization
        );
        var proposed = NamespaceAuthorizationFactory.SplitByValueSource(
            plan.NamespaceChecks,
            NamespaceAuthorizationCheckValueSource.Proposed,
            namespacePrefixParameterization
        );

        return new DescriptorWriteAuthorizationPreflightOutcome.Proceed(stored, proposed);
    }

    private abstract record DescriptorWriteAuthorizationPreflightOutcome
    {
        private DescriptorWriteAuthorizationPreflightOutcome() { }

        public sealed record NotImplemented(string FailureMessage)
            : DescriptorWriteAuthorizationPreflightOutcome;

        public sealed record SecurityConfigurationError(
            string[] Errors,
            SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
        ) : DescriptorWriteAuthorizationPreflightOutcome;

        public sealed record NamespaceNotAuthorized(NamespaceAuthorizationFailure Failure)
            : DescriptorWriteAuthorizationPreflightOutcome;

        public sealed record Proceed(
            RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization,
            RelationalWriteNamespaceAuthorization? ProposedNamespaceAuthorization
        ) : DescriptorWriteAuthorizationPreflightOutcome
        {
            public static Proceed NoAuthorization { get; } = new(null, null);
        }
    }

    private async Task<UpsertResult> InsertDescriptorAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        IRelationalCommandExecutor commandExecutor,
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

        var persistedContentVersion = await ExecuteWriteReturningContentVersionAsync(
                commandExecutor,
                command,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new UpsertResult.InsertSuccess(
            documentUuid,
            _servedEtagComposer.Compose(
                new ServedEtagContext(
                    request.MappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    request.ProfileName,
                    LinksEnabled: false,
                    persistedContentVersion
                )
            )
        );
    }

    private async Task<UpsertResult> UpdateDescriptorForUpsertAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        DocumentUuid existingDocumentUuid,
        short resourceKeyId,
        IRelationalCommandExecutor commandExecutor,
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

        var persistedContentVersion = await ExecuteWriteReturningContentVersionAsync(
                commandExecutor,
                command,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new UpsertResult.UpdateSuccess(
            existingDocumentUuid,
            _servedEtagComposer.Compose(
                new ServedEtagContext(
                    request.MappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    request.ProfileName,
                    LinksEnabled: false,
                    persistedContentVersion
                )
            )
        );
    }

    private async Task<UpsertResult> ApplyLockedDescriptorPostUpsertAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        DocumentUuid documentUuid,
        short resourceKeyId,
        PersistedDescriptorState persisted,
        string currentEtag,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (IsDescriptorUnchanged(body, persisted))
        {
            _logger.LogDebug(
                "Descriptor POST upsert is a no-op for {Resource} (DocumentId={DocumentId}) - {TraceId}",
                RelationalWriteSupport.FormatResource(request.Resource),
                documentId,
                request.TraceId.Value
            );

            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new UpsertResult.UpdateSuccess(documentUuid, currentEtag);
        }

        var upsertResult = await UpdateDescriptorForUpsertAsync(
                request,
                body,
                documentId,
                documentUuid,
                resourceKeyId,
                writeSession.CreateCommandExecutor(),
                cancellationToken
            )
            .ConfigureAwait(false);

        await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
        return upsertResult;
    }

    private async Task<UpdateResult> ApplyLockedDescriptorPutAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        DocumentUuid documentUuid,
        PersistedDescriptorState persisted,
        string currentEtag,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        if (!string.Equals(body.Uri, persisted.Uri, StringComparison.Ordinal))
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
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

            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new UpdateResult.UpdateSuccess(documentUuid, currentEtag);
        }

        _logger.LogDebug(
            "Updating descriptor {Resource} (DocumentId={DocumentId}) via PUT - {TraceId}",
            RelationalWriteSupport.FormatResource(request.Resource),
            documentId,
            request.TraceId.Value
        );

        var command = request.MappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlUpdateCommand(body, documentId),
            SqlDialect.Mssql => BuildMssqlUpdateCommand(body, documentId),
            _ => throw new NotSupportedException(
                $"Descriptor write does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
            ),
        };

        var persistedContentVersion = await ExecuteWriteReturningContentVersionAsync(
                writeSession.CreateCommandExecutor(),
                command,
                cancellationToken
            )
            .ConfigureAwait(false);

        await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new UpdateResult.UpdateSuccess(
            documentUuid,
            _servedEtagComposer.Compose(
                new ServedEtagContext(
                    request.MappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    request.ProfileName,
                    LinksEnabled: false,
                    persistedContentVersion
                )
            )
        );
    }

    private Task<UpsertResult> ApplyDescriptorPostUpsertWithLockedCurrentStateAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        DocumentUuid existingDocumentUuid,
        short resourceKeyId,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        CancellationToken cancellationToken
    ) =>
        ApplyWithLockedDescriptorCurrentStateAsync<UpsertResult>(
            request,
            body,
            documentId,
            storedNamespaceAuthorization,
            proposedNamespaceAuthorization,
            static () => new UpsertResult.UpsertFailureWriteConflict(),
            missingDescriptorDocumentId => new UpsertResult.UnknownFailure(
                BuildMissingDescriptorMessage(request.Resource, missingDescriptorDocumentId)
            ),
            static failure => new UpsertResult.UpsertFailureNamespaceNotAuthorized(failure),
            static (failureMessage, diagnostics) =>
                new UpsertResult.UpsertFailureSecurityConfiguration([failureMessage], diagnostics),
            static () => new UpsertResult.UpsertFailureWriteConflict(),
            (persisted, currentEtag, writeSession, ct) =>
                ApplyLockedDescriptorPostUpsertAsync(
                    request,
                    body,
                    documentId,
                    existingDocumentUuid,
                    resourceKeyId,
                    persisted,
                    currentEtag,
                    writeSession,
                    ct
                ),
            cancellationToken
        );

    private Task<UpdateResult> ApplyDescriptorPutWithLockedCurrentStateAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        DocumentUuid documentUuid,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        CancellationToken cancellationToken
    ) =>
        ApplyWithLockedDescriptorCurrentStateAsync<UpdateResult>(
            request,
            body,
            documentId,
            storedNamespaceAuthorization,
            proposedNamespaceAuthorization,
            static () => new UpdateResult.UpdateFailureNotExists(),
            missingDescriptorDocumentId => new UpdateResult.UnknownFailure(
                BuildMissingDescriptorMessage(request.Resource, missingDescriptorDocumentId)
            ),
            static failure => new UpdateResult.UpdateFailureNamespaceNotAuthorized(failure),
            static (failureMessage, diagnostics) =>
                new UpdateResult.UpdateFailureSecurityConfiguration([failureMessage], diagnostics),
            static () => new UpdateResult.UpdateFailureNotExists(),
            (persisted, currentEtag, writeSession, ct) =>
                ApplyLockedDescriptorPutAsync(
                    request,
                    body,
                    documentId,
                    documentUuid,
                    persisted,
                    currentEtag,
                    writeSession,
                    ct
                ),
            cancellationToken
        );

    private async Task<TResult> ApplyWithLockedDescriptorCurrentStateAsync<TResult>(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        long documentId,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        Func<TResult> missingDocumentResultFactory,
        Func<long, TResult> missingDescriptorResultFactory,
        Func<NamespaceAuthorizationFailure, TResult> namespaceNotAuthorizedFactory,
        Func<string, SecurityConfigurationFailureDiagnostic[]?, TResult> namespaceAuthorizationInvalidFactory,
        Func<TResult> namespaceStaleTargetFactory,
        Func<
            PersistedDescriptorState,
            string,
            IRelationalWriteSession,
            CancellationToken,
            Task<TResult>
        > applyLoadedAsync,
        CancellationToken cancellationToken
    )
        where TResult : class
    {
        await using var writeSession = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var lockedCurrentState = await LoadLockedDescriptorCurrentStateAsync(
                    request.MappingSet.Key.Dialect,
                    request.MappingSet.Key.EffectiveSchemaHash,
                    request.ProfileName,
                    documentId,
                    writeSession,
                    cancellationToken
                )
                .ConfigureAwait(false);

            switch (lockedCurrentState)
            {
                case DescriptorCurrentStateLoadResult.MissingDocument:
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return missingDocumentResultFactory();

                case DescriptorCurrentStateLoadResult.MissingDescriptor:
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return missingDescriptorResultFactory(documentId);

                case DescriptorCurrentStateLoadResult.Loaded(var persisted, var currentEtag):
                    // AND-compose stored then proposed namespace authorization against the locked target
                    // before applying any change. Either denial returns the namespace 403 with no
                    // INSERT/UPDATE statement, and short-circuits before the no-op or immutable-identity
                    // checks so 403 wins over those outcomes too.
                    var sessionCommandExecutor = writeSession.CreateCommandExecutor();

                    if (storedNamespaceAuthorization is not null)
                    {
                        var storedResult = await ExecuteDescriptorNamespaceAuthorizationAsync(
                                request.MappingSet,
                                documentId,
                                storedNamespaceAuthorization,
                                proposedNamespace: null,
                                sessionCommandExecutor,
                                cancellationToken
                            )
                            .ConfigureAwait(false);

                        var storedFailure = MapNamespaceAuthorizationToResult(
                            storedResult,
                            namespaceNotAuthorizedFactory,
                            namespaceAuthorizationInvalidFactory,
                            namespaceStaleTargetFactory
                        );

                        if (storedFailure is not null)
                        {
                            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                            return storedFailure;
                        }
                    }

                    if (proposedNamespaceAuthorization is not null)
                    {
                        var proposedResult = await ExecuteDescriptorNamespaceAuthorizationAsync(
                                request.MappingSet,
                                documentId,
                                proposedNamespaceAuthorization,
                                body.Namespace,
                                sessionCommandExecutor,
                                cancellationToken
                            )
                            .ConfigureAwait(false);

                        var proposedFailure = MapNamespaceAuthorizationToResult(
                            proposedResult,
                            namespaceNotAuthorizedFactory,
                            namespaceAuthorizationInvalidFactory,
                            namespaceStaleTargetFactory
                        );

                        if (proposedFailure is not null)
                        {
                            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                            return proposedFailure;
                        }
                    }

                    return await applyLoadedAsync(persisted, currentEtag, writeSession, cancellationToken)
                        .ConfigureAwait(false);

                default:
                    throw new InvalidOperationException(
                        $"Unexpected locked descriptor state result type '{lockedCurrentState.GetType().Name}'."
                    );
            }
        }
        catch
        {
            await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Opens a write session for a descriptor POST create, runs the configured proposed namespace
    /// check before the insert, and rolls back without inserting on namespace denial so the create
    /// path never produces a partially-written document on a 403.
    /// </summary>
    private async Task<UpsertResult> ExecuteDescriptorInsertWithProposedNamespaceCheckAsync(
        DescriptorWriteRequest request,
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        CancellationToken cancellationToken
    )
    {
        await using var writeSession = await _writeSessionFactory
            .CreateAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var sessionCommandExecutor = writeSession.CreateCommandExecutor();

            if (proposedNamespaceAuthorization is not null)
            {
                var proposedResult = await ExecuteDescriptorNamespaceAuthorizationAsync(
                        request.MappingSet,
                        documentId: 0L,
                        proposedNamespaceAuthorization,
                        body.Namespace,
                        sessionCommandExecutor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                var proposedFailure = MapNamespaceAuthorizationToResult<UpsertResult>(
                    proposedResult,
                    static failure => new UpsertResult.UpsertFailureNamespaceNotAuthorized(failure),
                    static (failureMessage, diagnostics) =>
                        new UpsertResult.UpsertFailureSecurityConfiguration([failureMessage], diagnostics),
                    static () => new UpsertResult.UpsertFailureWriteConflict()
                );

                if (proposedFailure is not null)
                {
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return proposedFailure;
                }
            }

            var insertResult = await InsertDescriptorAsync(
                    request,
                    body,
                    documentUuid,
                    resourceKeyId,
                    sessionCommandExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
            return insertResult;
        }
        catch
        {
            await TryRollbackAsync(writeSession, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static TResult? MapNamespaceAuthorizationToResult<TResult>(
        NamespaceAuthorizationExecutionResult executionResult,
        Func<NamespaceAuthorizationFailure, TResult> namespaceNotAuthorizedFactory,
        Func<string, SecurityConfigurationFailureDiagnostic[]?, TResult> namespaceAuthorizationInvalidFactory,
        Func<TResult> staleTargetFactory
    )
        where TResult : class =>
        executionResult switch
        {
            NamespaceAuthorizationExecutionResult.Authorized => null,
            NamespaceAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                namespaceNotAuthorizedFactory(notAuthorized.Failure),
            NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                namespaceAuthorizationInvalidFactory(
                    invalidFailure.FailureMessage,
                    invalidFailure.Diagnostics
                ),
            // Descriptor write/delete paths row-lock the target before the namespace check, so a stale
            // target is not expected; the caller maps it defensively to its conflict/not-exists outcome.
            NamespaceAuthorizationExecutionResult.StaleTarget => staleTargetFactory(),
            _ => throw new InvalidOperationException(
                $"Unsupported namespace authorization execution result '{executionResult.GetType().Name}'."
            ),
        };

    private async Task<DescriptorLockedPreconditionResult?> EvaluateNamespaceAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationalWriteNamespaceAuthorization namespaceAuthorization,
        string? proposedNamespace,
        IRelationalCommandExecutor sessionCommandExecutor,
        CancellationToken cancellationToken
    )
    {
        var result = await ExecuteDescriptorNamespaceAuthorizationAsync(
                mappingSet,
                documentId,
                namespaceAuthorization,
                proposedNamespace,
                sessionCommandExecutor,
                cancellationToken
            )
            .ConfigureAwait(false);

        return MapNamespaceAuthorizationToResult<DescriptorLockedPreconditionResult>(
            result,
            static failure => new DescriptorLockedPreconditionResult.NamespaceNotAuthorized(failure),
            static (failureMessage, diagnostics) =>
                new DescriptorLockedPreconditionResult.NamespaceAuthorizationInvalid(
                    failureMessage,
                    diagnostics
                ),
            // The If-Match path locks the target before this check, so a stale target maps to the same
            // missing-document precondition the caller already resolves to not-exists/conflict.
            static () => DescriptorLockedPreconditionResult.MissingDocument.Instance
        );
    }

    /// <summary>
    /// Executes a descriptor write whose final statement surfaces the owning document's
    /// <c>ContentVersion</c> and returns that value for etag composition. Every descriptor write whose
    /// success result carries an etag (INSERT plus both UPDATE variants) surfaces ContentVersion:
    /// the INSERT returns the insert-time value (the stamp trigger only mirrors it on descriptor
    /// insert), and each UPDATE re-selects the post-trigger bumped value that a later GET reads.
    /// </summary>
    private static Task<long> ExecuteWriteReturningContentVersionAsync(
        IRelationalCommandExecutor commandExecutor,
        RelationalCommand command,
        CancellationToken cancellationToken
    ) =>
        commandExecutor.ExecuteReaderAsync(
            command,
            static async (reader, ct) =>
            {
                // Every descriptor write batch ends with the row-producing SELECT "ContentVersion".
                // Neither Npgsql nor SqlClient surfaces the preceding UPDATE/INSERT/MERGE as a
                // row-bearing result set, so in practice the trailing SELECT is the first exposed
                // result set. Scan defensively anyway rather than depending on that ordering:
                // advance past any leading result set a driver might expose and stop at the first
                // row, which is the ContentVersion the trailing SELECT produces.
                do
                {
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return reader.GetRequiredFieldValue<long>("ContentVersion");
                    }
                } while (await reader.NextResultAsync(ct).ConfigureAwait(false));

                throw new InvalidOperationException(
                    "Descriptor write did not surface a ContentVersion value for etag composition."
                );
            },
            cancellationToken
        );

    // ── PostgreSQL SQL builders ──────────────────────────────────────────

    private static RelationalCommand BuildPostgresqlInsertCommand(
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        ReferentialId referentialId
    )
    {
        // The Document CTE surfaces the insert-time ContentVersion (the descriptor stamp trigger only
        // mirrors that value on INSERT and does not bump it), so the final SELECT returns exactly what
        // a later GET reads. The ReferentialIdentity insert is wrapped in its own data-modifying CTE so
        // it still executes even though the primary query reads only ContentVersion.
        const string Sql = """
            WITH new_doc AS (
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
                VALUES (@documentUuid, @resourceKeyId)
                RETURNING "DocumentId", "ContentVersion"
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
            , new_referential AS (
                INSERT INTO dms."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
                SELECT @referentialId, "DocumentId", @resourceKeyId
                FROM new_doc
            )
            SELECT "ContentVersion" FROM new_doc;
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
        // Capture the insert-time ContentVersion into a table variable via OUTPUT ... INTO, run every
        // insert, then return it with a trailing SELECT so the row-producing statement is the final
        // one (matching the PG insert CTE and every UPDATE builder). This keeps the reader's single
        // result set unambiguous rather than relying on the batch fully executing after the first
        // statement's OUTPUT is read. [dms].[Document] carries no trigger, so OUTPUT is legal there,
        // and the descriptor stamp trigger only mirrors (never bumps) ContentVersion on descriptor
        // INSERT, so the captured value is exactly what a later GET reads.
        const string Sql = """
            DECLARE @newDocumentId BIGINT;
            DECLARE @insertedContentVersion TABLE ([ContentVersion] BIGINT);

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[ContentVersion] INTO @insertedContentVersion ([ContentVersion])
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

            SELECT [ContentVersion] FROM @insertedContentVersion;
            """;

        return new RelationalCommand(
            Sql,
            BuildInsertParameters(body, documentUuid, resourceKeyId, referentialId)
        );
    }

    // Update SQL builders (POST as upsert-as-update)

    private static RelationalCommand BuildPostgresqlUpdateCommand(
        ExtractedDescriptorBody body,
        long documentId
    )
    {
        // The descriptor stamp trigger bumps dms."Document"."ContentVersion" in an AFTER UPDATE trigger,
        // so it is not visible to a RETURNING on the descriptor UPDATE; re-select the post-trigger value.
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

            SELECT "ContentVersion" FROM dms."Document" WHERE "DocumentId" = @documentId;
            """;

        return new RelationalCommand(Sql, BuildUpdateParameters(body, documentId));
    }

    private static RelationalCommand BuildMssqlUpdateCommand(ExtractedDescriptorBody body, long documentId)
    {
        // The descriptor stamp trigger bumps [dms].[Document].[ContentVersion] in an AFTER UPDATE
        // trigger, so OUTPUT on the descriptor UPDATE would return the pre-trigger value (and MSSQL
        // disallows a plain OUTPUT on a trigger-bearing table); re-select the post-trigger value.
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

            SELECT [ContentVersion] FROM [dms].[Document] WHERE [DocumentId] = @documentId;
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
        // The descriptor stamp trigger bumps dms."Document"."ContentVersion" in an AFTER UPDATE trigger,
        // so it is not visible to a RETURNING on the descriptor UPDATE; re-select the post-trigger value.
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

            INSERT INTO dms."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
            VALUES (@referentialId, @documentId, @resourceKeyId)
            ON CONFLICT ("ReferentialId") DO UPDATE
            SET "DocumentId" = EXCLUDED."DocumentId",
                "ResourceKeyId" = EXCLUDED."ResourceKeyId";

            SELECT "ContentVersion" FROM dms."Document" WHERE "DocumentId" = @documentId;
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
        // The descriptor stamp trigger bumps [dms].[Document].[ContentVersion] in an AFTER UPDATE
        // trigger, so OUTPUT on the descriptor UPDATE would return the pre-trigger value (and MSSQL
        // disallows a plain OUTPUT on a trigger-bearing table); re-select the post-trigger value.
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

            SELECT [ContentVersion] FROM [dms].[Document] WHERE [DocumentId] = @documentId;
            """;

        return new RelationalCommand(
            Sql,
            BuildUpsertUpdateParameters(body, documentId, resourceKeyId, referentialId)
        );
    }

    // ── Persisted descriptor read ──────────────────────────────────────────

    private static async Task<PersistedDescriptorState?> ReadPersistedDescriptorAsync(
        IRelationalCommandExecutor commandExecutor,
        long documentId,
        CancellationToken cancellationToken
    )
    {
        var command = commandExecutor.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlReadCommand(documentId),
            SqlDialect.Mssql => BuildMssqlReadCommand(documentId),
            _ => throw new NotSupportedException(
                $"Descriptor read does not support SQL dialect '{commandExecutor.Dialect}'."
            ),
        };

        return await commandExecutor
            .ExecuteReaderAsync(
                command,
                static async (reader, ct) =>
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new PersistedDescriptorState(
                        // dms.Descriptor.Namespace is NOT NULL in both the generated PostgreSQL and
                        // SQL Server DDL, so a persisted descriptor row always carries a namespace.
                        // Read it required: there is no stored-NULL descriptor namespace to route to
                        // the namespace-authorization uninitialized branch.
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

    // ── No-op detection ─────────────────────────────────────────────────

    private static bool IsDescriptorUnchanged(
        ExtractedDescriptorBody body,
        PersistedDescriptorState persisted
    )
    {
        return DescriptorNoOpComparer.IsUnchanged(
            body,
            persisted.Namespace,
            persisted.CodeValue,
            persisted.ShortDescription,
            persisted.Description,
            persisted.EffectiveBeginDate,
            persisted.EffectiveEndDate
        );
    }

    private sealed record PersistedDescriptorState(
        // Namespace is non-null: dms.Descriptor.Namespace is NOT NULL in the generated PostgreSQL and
        // SQL Server DDL, so a persisted descriptor row always has a namespace. (Generic resources can
        // carry a stored-null namespace that namespace authorization surfaces as the
        // stored-namespace-uninitialized 403; descriptors cannot reach that state.)
        string Namespace,
        string CodeValue,
        string Uri,
        string? ShortDescription,
        string? Description,
        DateOnly? EffectiveBeginDate,
        DateOnly? EffectiveEndDate
    );

    private enum DescriptorPreconditionTargetKind
    {
        Post,
        Put,
        Delete,
    }

    private abstract record DescriptorLockedPreconditionResult
    {
        private DescriptorLockedPreconditionResult() { }

        public sealed record CreateNew(DocumentUuid DocumentUuid) : DescriptorLockedPreconditionResult;

        public sealed record NotFound : DescriptorLockedPreconditionResult
        {
            private NotFound() { }

            public static NotFound Instance { get; } = new();
        }

        public sealed record MissingDocument : DescriptorLockedPreconditionResult
        {
            private MissingDocument() { }

            public static MissingDocument Instance { get; } = new();
        }

        public sealed record MissingDescriptor(long DocumentId) : DescriptorLockedPreconditionResult;

        public sealed record NamespaceNotAuthorized(NamespaceAuthorizationFailure Failure)
            : DescriptorLockedPreconditionResult;

        public sealed record NamespaceAuthorizationInvalid(
            string FailureMessage,
            SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
        ) : DescriptorLockedPreconditionResult;

        public sealed record Mismatch : DescriptorLockedPreconditionResult
        {
            private Mismatch() { }

            public static Mismatch Instance { get; } = new();
        }

        public sealed record Loaded(
            RelationalWriteTargetContext.ExistingDocument TargetContext,
            PersistedDescriptorState Persisted,
            string CurrentEtag
        ) : DescriptorLockedPreconditionResult;
    }

    private abstract record DescriptorCurrentStateLoadResult
    {
        private DescriptorCurrentStateLoadResult() { }

        public sealed record MissingDocument : DescriptorCurrentStateLoadResult
        {
            private MissingDocument() { }

            public static MissingDocument Instance { get; } = new();
        }

        public sealed record MissingDescriptor : DescriptorCurrentStateLoadResult
        {
            private MissingDescriptor() { }

            public static MissingDescriptor Instance { get; } = new();
        }

        public sealed record Loaded(PersistedDescriptorState State, string Etag)
            : DescriptorCurrentStateLoadResult;
    }

    private async Task<DescriptorCurrentStateLoadResult> LoadLockedDescriptorCurrentStateAsync(
        SqlDialect dialect,
        string effectiveSchemaHash,
        string? profileName,
        long documentId,
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        var lockedContentVersion = await RelationalWriteTargetLocking
            .TryLockExistingTargetAsync(dialect, documentId, writeSession, cancellationToken)
            .ConfigureAwait(false);

        if (lockedContentVersion is null)
        {
            return DescriptorCurrentStateLoadResult.MissingDocument.Instance;
        }

        var persistedDescriptor = await ReadPersistedDescriptorAsync(
                writeSession.CreateCommandExecutor(),
                documentId,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (persistedDescriptor is null)
        {
            return DescriptorCurrentStateLoadResult.MissingDescriptor.Instance;
        }

        // The current etag is composed from the locked ContentVersion and the active profile so a
        // no-op write returns the same profile-sensitive etag a GET of that representation would.
        // If-Match comparison stays profile-insensitive because EtagMatchProjection projects the
        // profileCode out, retaining only ContentVersion and schemaEpoch.
        return new DescriptorCurrentStateLoadResult.Loaded(
            persistedDescriptor,
            _servedEtagComposer.Compose(
                new ServedEtagContext(
                    effectiveSchemaHash,
                    ResponseFormat.Json,
                    profileName,
                    LinksEnabled: false,
                    lockedContentVersion.Value
                )
            )
        );
    }

    private static string BuildMissingDescriptorMessage(QualifiedResourceName resource, long documentId) =>
        $"Descriptor row not found for DocumentId {documentId} on resource "
        + $"'{RelationalWriteSupport.FormatResource(resource)}'.";

    private static async Task TryRollbackAsync(
        IRelationalWriteSession writeSession,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Best-effort rollback in exception handlers: ignore sessions already completed.
        }
    }

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
}
