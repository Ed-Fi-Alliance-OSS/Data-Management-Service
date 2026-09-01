// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
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
        null,
    IDocumentCacheWriterTelemetry? documentCacheWriterTelemetry = null,
    IDataStoreSelection? dataStoreSelection = null,
    IDocumentCacheEnqueueTelemetry? documentCacheEnqueueTelemetry = null,
    IDocumentCacheTargetRegistry? documentCacheTargetRegistry = null,
    IRelationalCommandExecutor? customViewValidationCommandExecutor = null,
    IDocumentCacheProviderCommandTimeoutClassifier? documentCacheProviderCommandTimeoutClassifier = null
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

    /// <summary>
    /// Validates <c>auth.{StrategyName}</c> for views that execute ahead of a preflight terminal. Terminals
    /// resolve before the write session opens, so the probe cannot use the session: opening one just to read a
    /// catalog would take locks the denied request never needed. Null skips validation rather than probing on a
    /// session that does not exist yet.
    /// </summary>
    private readonly IRelationalCommandExecutor? _customViewValidationCommandExecutor =
        customViewValidationCommandExecutor;
    private readonly IServedEtagComposer _servedEtagComposer =
        servedEtagComposer ?? throw new ArgumentNullException(nameof(servedEtagComposer));
    private readonly IRelationshipAuthorizationProviderFailureExtractor _relationshipAuthorizationProviderFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;
    private readonly IDocumentCacheWriterTelemetry _documentCacheWriterTelemetry =
        documentCacheWriterTelemetry ?? NoOpDocumentCacheWriterTelemetry.Instance;
    private readonly IDataStoreSelection? _dataStoreSelection = dataStoreSelection;
    private readonly IDocumentCacheEnqueueTelemetry _documentCacheEnqueueTelemetry =
        documentCacheEnqueueTelemetry ?? NoOpDocumentCacheEnqueueTelemetry.Instance;
    private readonly IDocumentCacheTargetRegistry? _documentCacheTargetRegistry = documentCacheTargetRegistry;
    private readonly IDocumentCacheProviderCommandTimeoutClassifier _documentCacheProviderCommandTimeoutClassifier =
        documentCacheProviderCommandTimeoutClassifier
        ?? NoOpDocumentCacheProviderCommandTimeoutClassifier.Instance;
    private const string EnqueueOutcomeNoWorkQueuedParameterName = "@enqueueOutcomeNoWorkQueued";
    private const string EnqueueOutcomeAlreadySatisfiedParameterName = "@enqueueOutcomeAlreadySatisfied";

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
                await ValidateDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        notImplemented.CustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                await ValidateSingleRecordDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        notImplemented.SingleRecordCustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new UpsertResult.UpsertFailureNotImplemented(
                    notImplemented.FailureMessage,
                    UpsertFailureNotImplementedReason.StrategyNotEnabled
                );
            case DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                await ValidateDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        configError.CustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                await ValidateSingleRecordDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        configError.SingleRecordCustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new UpsertResult.UpsertFailureSecurityConfiguration(
                    configError.Errors,
                    configError.Diagnostics
                );
            case DescriptorWriteAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                await ValidateDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        namespaceNotAuthorized.CustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                await ValidateSingleRecordDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        namespaceNotAuthorized.SingleRecordCustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new UpsertResult.UpsertFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure);
        }

        var proceed = (DescriptorWriteAuthorizationPreflightOutcome.Proceed)authorizationPreflight;
        var storedNamespaceAuthorization = proceed.StoredNamespaceAuthorization;
        var proposedNamespaceAuthorization = proceed.ProposedNamespaceAuthorization;
        var customViewAuthorization = proceed.CustomViewAuthorization;

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
                                customViewAuthorization,
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
                                customViewAuthorization,
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
                    body.Namespace,
                    customViewAuthorization
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
                        RecordDescriptorEnqueueSuccessIfApplicable(
                            request,
                            DocumentCacheEnqueueTelemetryCanonicalOperation.Insert,
                            insertResult.DocumentCacheEnqueueOutcome
                        );
                        return insertResult.Result;
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

                case DescriptorLockedPreconditionResult.CustomViewNotAuthorized(var customViewFailure):
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UpsertFailureCustomViewNotAuthorized(customViewFailure);

                case DescriptorLockedPreconditionResult.CustomViewAuthorizationInvalid(
                    var customViewFailureMessage,
                    var customViewDiagnostics
                ):
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UpsertFailureSecurityConfiguration(
                        [customViewFailureMessage],
                        customViewDiagnostics
                    );

                case DescriptorLockedPreconditionResult.Mismatch(var reason):
                    await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new UpsertResult.UpsertFailureETagMisMatch(reason);

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

            // A unique violation can come from the generated DocumentUuid rather than the descriptor
            // identity, so it does not prove that the guarded target now exists. Returning a retryable
            // conflict replays the whole POST with a fresh candidate UUID; a true target race is then
            // re-resolved and evaluated against the precondition.
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
                await ValidateDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        notImplemented.CustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                await ValidateSingleRecordDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        notImplemented.SingleRecordCustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new UpdateResult.UpdateFailureNotImplemented(notImplemented.FailureMessage);
            case DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError configError:
                await ValidateDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        configError.CustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                await ValidateSingleRecordDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        configError.SingleRecordCustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new UpdateResult.UpdateFailureSecurityConfiguration(
                    configError.Errors,
                    configError.Diagnostics
                );
            case DescriptorWriteAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized:
                await ValidateDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        namespaceNotAuthorized.CustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                await ValidateSingleRecordDescriptorWriteCustomViewsAsync(
                        request.MappingSet,
                        namespaceNotAuthorized.SingleRecordCustomViewChecksToValidate,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                return new UpdateResult.UpdateFailureNamespaceNotAuthorized(namespaceNotAuthorized.Failure);
        }

        var proceed = (DescriptorWriteAuthorizationPreflightOutcome.Proceed)authorizationPreflight;
        var storedNamespaceAuthorization = proceed.StoredNamespaceAuthorization;
        var proposedNamespaceAuthorization = proceed.ProposedNamespaceAuthorization;
        var customViewAuthorization = proceed.CustomViewAuthorization;

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
                        body.Namespace,
                        customViewAuthorization
                    )
                    .ConfigureAwait(false);

                switch (preconditionResult)
                {
                    case DescriptorLockedPreconditionResult.NotFound:
                    case DescriptorLockedPreconditionResult.MissingDocument:
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        // RFC 9110 §13.1.1 If-Match: * requires the target to exist; a wildcard If-Match against
                        // a missing PUT target yields the precondition-failed (412) result rather than
                        // not-exists (404). A wildcard If-None-Match against a missing target is the
                        // success case, so it falls through to the normal not-exists (404) result.
                        return request.WritePrecondition is WritePrecondition.IfMatch { IsWildcard: true }
                            ? new UpdateResult.UpdateFailureETagMisMatch(
                                ETagPreconditionFailureReason.TargetDoesNotExist
                            )
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

                    case DescriptorLockedPreconditionResult.CustomViewNotAuthorized(var customViewFailure):
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return new UpdateResult.UpdateFailureCustomViewNotAuthorized(customViewFailure);

                    case DescriptorLockedPreconditionResult.CustomViewAuthorizationInvalid(
                        var customViewFailureMessage,
                        var customViewDiagnostics
                    ):
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return new UpdateResult.UpdateFailureSecurityConfiguration(
                            [customViewFailureMessage],
                            customViewDiagnostics
                        );

                    case DescriptorLockedPreconditionResult.Mismatch(var reason):
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return new UpdateResult.UpdateFailureETagMisMatch(reason);

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
                    customViewAuthorization,
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
            // Views configured ahead of this terminal execute first, so a missing or non-conforming view keeps
            // its own 500 rather than being hidden by the terminal's response.
            await ValidateDescriptorWriteCustomViewsAsync(
                    request.MappingSet,
                    stop.CustomViewChecksToValidate,
                    cancellationToken
                )
                .ConfigureAwait(false);
            await ValidateSingleRecordDescriptorWriteCustomViewsAsync(
                    request.MappingSet,
                    stop.SingleRecordCustomViewChecksToValidate,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return stop.Result;
        }

        var proceed = (DescriptorDeleteAuthorizationPreflightResult.Proceed)authorizationPreflight;
        var storedNamespaceAuthorization = proceed.StoredNamespaceAuthorization;
        var customViewAuthorization = proceed.CustomViewAuthorization;
        var (customViewsBeforeNamespace, customViewsAfterNamespace) = PartitionDescriptorCustomViewRuns(
            customViewAuthorization,
            storedNamespaceAuthorization
        );

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
                    if (storedNamespaceAuthorization is not null || customViewAuthorization is not null)
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
                            // Configured order: the views configured at or before NamespaceBased, then the
                            // namespace check, then the rest. The first failure is the one reported.
                            outcome =
                                await AuthorizeLockedDescriptorDeleteCustomViewsAsync(
                                        request,
                                        resolvedDeleteTarget.DocumentId,
                                        customViewsBeforeNamespace,
                                        customViewAuthorization,
                                        sessionCommandExecutor,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
                                ?? (
                                    storedNamespaceAuthorization is null
                                        ? null
                                        : MapDeleteNamespaceAuthorizationResult(
                                            await ExecuteDescriptorNamespaceAuthorizationAsync(
                                                    request.MappingSet,
                                                    resolvedDeleteTarget.DocumentId,
                                                    storedNamespaceAuthorization,
                                                    proposedNamespace: null,
                                                    sessionCommandExecutor,
                                                    cancellationToken
                                                )
                                                .ConfigureAwait(false)
                                        )
                                )
                                ?? await AuthorizeLockedDescriptorDeleteCustomViewsAsync(
                                        request,
                                        resolvedDeleteTarget.DocumentId,
                                        customViewsAfterNamespace,
                                        customViewAuthorization,
                                        sessionCommandExecutor,
                                        cancellationToken
                                    )
                                    .ConfigureAwait(false)
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
                            storedNamespaceAuthorization: storedNamespaceAuthorization,
                            customViewAuthorization: customViewAuthorization
                        )
                        .ConfigureAwait(false);

                    outcome = preconditionResult switch
                    {
                        // RFC 9110 §13.1.1 If-Match: * requires the target to exist; a wildcard against a
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
                        DescriptorLockedPreconditionResult.CustomViewNotAuthorized(var customViewFailure) =>
                            new DeleteResult.DeleteFailureCustomViewNotAuthorized(customViewFailure),
                        DescriptorLockedPreconditionResult.CustomViewAuthorizationInvalid(
                            var failureMessage,
                            var diagnostics
                        ) => new DeleteResult.DeleteFailureSecurityConfiguration(
                            [failureMessage],
                            diagnostics
                        ),
                        DescriptorLockedPreconditionResult.Mismatch(var reason) =>
                            new DeleteResult.DeleteFailureETagMisMatch(reason),
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
        string? proposedNamespace = null,
        RelationalCustomViewAuthorization? customViewAuthorization = null
    )
    {
        var (customViewsBeforeNamespace, customViewsAfterNamespace) = PartitionDescriptorCustomViewRuns(
            customViewAuthorization,
            storedNamespaceAuthorization
        );
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
            // precondition type). Either way the configured proposed-value filters run before the
            // precondition outcome, so an authorization denial (403) precedes it. Which of those filters
            // answers is decided by CMS-configured order, below. The proposed namespace check is a single
            // statement against the dialect's namespace authorization SQL and needs no row lookup.
            // Same deterministic denial as the non-precondition create path: no row exists yet, so no view row
            // can reference it. It and the proposed namespace check run in CMS-configured order, so whichever is
            // configured first is the one that answers.
            var createSelfBasisDenial = FindSelfBasisProposedCheck(customViewAuthorization);

            if (
                createSelfBasisDenial is not null
                && !NamespacePrecedesSelfBasisDenial(proposedNamespaceAuthorization, createSelfBasisDenial)
            )
            {
                return await BuildSelfBasisCreateDenialResultAsync(
                        mappingSet,
                        createSelfBasisDenial,
                        static failure => new DescriptorLockedPreconditionResult.CustomViewNotAuthorized(
                            failure
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

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

            if (createSelfBasisDenial is not null)
            {
                return await BuildSelfBasisCreateDenialResultAsync(
                        mappingSet,
                        createSelfBasisDenial,
                        static failure => new DescriptorLockedPreconditionResult.CustomViewNotAuthorized(
                            failure
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
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
            // Custom views AND-compose with NamespaceBased in CMS-configured order, so those configured at or
            // before it run first and those configured after it run once the stored namespace check has
            // authorized. The whole stored sequence completes before any proposed check runs.
            var preconditionFromEarlyCustomViews = await EvaluateLockedCustomViewAuthorizationAsync(
                    mappingSet,
                    existingTargetContext.DocumentId,
                    customViewsBeforeNamespace,
                    customViewAuthorization,
                    sessionCommandExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (preconditionFromEarlyCustomViews is not null)
            {
                return preconditionFromEarlyCustomViews;
            }

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

            var preconditionFromLateCustomViews = await EvaluateLockedCustomViewAuthorizationAsync(
                    mappingSet,
                    existingTargetContext.DocumentId,
                    customViewsAfterNamespace,
                    customViewAuthorization,
                    sessionCommandExecutor,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (preconditionFromLateCustomViews is not null)
            {
                return preconditionFromLateCustomViews;
            }

            // Last, because it reads the request's proposed value: every stored check has to have answered
            // against the locked row first, or a proposed denial would mask the stored custom-view answer
            // configured after NamespaceBased — including the 500 a nonconforming view behind it owes.
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
            DescriptorCurrentStateLoadResult.Loaded(var persisted, var currentEtag) =>
                EvaluateDescriptorPreconditionWithLogging(
                    precondition,
                    existingTargetContext,
                    persisted,
                    currentEtag,
                    _logger
                ),
            _ => throw new InvalidOperationException(
                $"Unexpected locked descriptor state result type '{lockedCurrentState.GetType().Name}'."
            ),
        };
    }

    private static DescriptorLockedPreconditionResult EvaluateDescriptorPreconditionWithLogging(
        WritePrecondition precondition,
        RelationalWriteTargetContext.ExistingDocument targetContext,
        PersistedDescriptorState persisted,
        string currentEtag,
        ILogger logger
    )
    {
        var isSatisfied = EtagPreconditionEvaluator.IsSatisfied(
            precondition,
            targetExists: true,
            currentEtag
        );

        if (logger.IsEnabled(LogLevel.Debug))
        {
            var clientTag = precondition switch
            {
                WritePrecondition.IfMatch m => m.IsWildcard ? "*" : m.Value,
                WritePrecondition.IfNoneMatch n => n.IsWildcard ? "*" : string.Join(", ", n.Values),
                _ => "(none)",
            };
            logger.LogDebug(
                "Descriptor etag precondition for document {DocumentId}: "
                    + "clientTag={ClientTag}, currentTag={CurrentTag}, satisfied={IsSatisfied}",
                targetContext.DocumentId,
                LoggingSanitizer.SanitizeForLogging(clientTag),
                currentEtag,
                isSatisfied
            );
        }

        return isSatisfied
            ? new DescriptorLockedPreconditionResult.Loaded(targetContext, persisted, currentEtag)
            : new DescriptorLockedPreconditionResult.Mismatch(
                EtagPreconditionEvaluator.GetFailureReason(precondition)
            );
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
            RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot => DeleteTerminal(
                request,
                new DeleteResult.DeleteFailureSecurityConfiguration(
                    [
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ],
                    RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                ),
                noUsableRoot.CustomViewStrategies,
                noUsableRoot.RawConfiguredIndex
            ),
            RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes => DeleteTerminal(
                request,
                new DeleteResult.DeleteFailureNamespaceNotAuthorized(
                    NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                ),
                noPrefixes.CustomViewStrategies,
                noPrefixes.RawConfiguredIndex
            ),
            RelationalAuthorizationPlanOutcome.Plan plan
                when RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies(
                    plan.NonNamespaceConfiguredStrategies
                ) => DeleteTerminal(
                request,
                new DeleteResult.DeleteFailureNotImplemented(
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        request.Resource,
                        request.AuthorizationStrategyEvaluators,
                        "descriptor DELETE",
                        "DELETE",
                        plan.CustomViewStrategies
                    )
                ),
                plan.CustomViewStrategies,
                // OwnershipBased executes last per auth.md regardless of configured position, so every
                // resolved view runs before this 501.
                int.MaxValue
            ),
            RelationalAuthorizationPlanOutcome.Plan plan => AuthorizeDescriptorDeletePlanPreflight(
                request,
                plan
            ),
            RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported => DeleteTerminal(
                request,
                new DeleteResult.DeleteFailureNotImplemented(
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        request.Resource,
                        request.AuthorizationStrategyEvaluators,
                        "descriptor DELETE",
                        "DELETE",
                        stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies
                    )
                ),
                stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies,
                int.MaxValue
            ),
            RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError =>
                DeleteTerminal(
                    request,
                    BuildDescriptorDeleteSecurityConfigurationError(
                        request.Resource,
                        securityConfigurationError
                    ),
                    securityConfigurationError.RelationshipClassification.SupportedCustomViewStrategies,
                    RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                        securityConfigurationError.RelationshipClassification.SecurityConfigurationFailures
                    )
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
            ),
        };
    }

    /// <summary>
    /// Plans the single-record stored custom-view checks descriptor DELETE executes, or reports the
    /// security-configuration failure that stops the delete.
    /// </summary>
    private static bool TryPlanDescriptorDeleteCustomViews(
        DescriptorDeleteRequest request,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        out RelationalCustomViewAuthorization? customViewAuthorization,
        out DeleteResult? securityConfigurationFailure,
        out IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checksToValidateBeforeFailure
    )
    {
        customViewAuthorization = null;
        securityConfigurationFailure = null;
        checksToValidateBeforeFailure = [];

        if (customViewStrategies.Count == 0)
        {
            return true;
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            request.MappingSet,
            request.MappingSet.GetConcreteResourceModelOrThrow(request.Resource),
            customViewStrategies,
            NamespaceAuthorizationOperation.Delete
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            securityConfigurationFailure = BuildDescriptorDeleteCustomViewSecurityConfigurationFailure(
                request.Resource,
                configurationFailure.Failures
            );
            // Views configured ahead of the earliest planning failure planned successfully and execute
            // first, so they are still validated before this failure is reported.
            checksToValidateBeforeFailure = SingleRecordChecksBeforeFailure(configurationFailure);
            return false;
        }

        var checks = ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks;

        if (checks.Count > 0)
        {
            customViewAuthorization = new RelationalCustomViewAuthorization(checks);
        }

        return true;
    }

    /// <summary>
    /// Builds the DELETE terminal carrying the views configured strictly before
    /// <paramref name="terminalIndex"/>, so those views are validated before the terminal is reported. A
    /// planning failure among them replaces the terminal, matching how the descriptor read path orders the two.
    /// </summary>
    private static DescriptorDeleteAuthorizationPreflightResult DeleteTerminal(
        DescriptorDeleteRequest request,
        DeleteResult result,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        int terminalIndex
    )
    {
        var strategiesToValidate = CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
            customViewStrategies,
            terminalIndex
        );

        if (strategiesToValidate.Count == 0)
        {
            return new DescriptorDeleteAuthorizationPreflightResult.Stop(result);
        }

        var outcome = CustomViewAuthorizationPlanner.Plan(
            request.MappingSet,
            request.MappingSet.GetConcreteResourceModelOrThrow(request.Resource),
            strategiesToValidate
        );

        if (outcome is CustomViewAuthorizationPlanOutcome.SecurityConfiguration customViewSecurity)
        {
            return new DescriptorDeleteAuthorizationPreflightResult.Stop(
                BuildDescriptorDeleteCustomViewSecurityConfigurationFailure(
                    request.Resource,
                    customViewSecurity.Failures
                ),
                PageDocumentIdCustomViewAdapter.AdaptFromChecks(
                    CustomViewAuthorizationTerminalOrdering.ChecksBeforeTerminal(
                        customViewSecurity.PlannedChecks,
                        RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                            customViewSecurity.Failures
                        )
                    )
                )
            );
        }

        return new DescriptorDeleteAuthorizationPreflightResult.Stop(
            result,
            PageDocumentIdCustomViewAdapter.AdaptFromChecks(
                ((CustomViewAuthorizationPlanOutcome.Plan)outcome).Checks
            )
        );
    }

    /// <summary>
    /// A custom-view planning failure reported as a descriptor DELETE security-configuration result.
    /// <see cref="RelationshipAuthorizationFailureKind.NoCustomViewJoinPath"/> keeps the specific join-path
    /// message; every other kind keeps the guardrail's unknown-strategy wording.
    /// </summary>
    private static DeleteResult.DeleteFailureSecurityConfiguration BuildDescriptorDeleteCustomViewSecurityConfigurationFailure(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        var guardrailFailure = RelationalReadGuardrails.BuildSecurityConfigurationFailure(
            resource,
            [],
            new RelationshipAuthorizationClassification(
                RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError,
                [],
                [],
                [],
                [],
                failures
            )
        );

        string[] joinPathErrors =
        [
            .. failures
                .Where(static failure =>
                    failure.FailureKind is RelationshipAuthorizationFailureKind.NoCustomViewJoinPath
                )
                .Select(static failure =>
                    CustomViewAuthorizationFailureMessages.NoJoinPath(failure, "descriptor DELETE")
                ),
        ];

        return new DeleteResult.DeleteFailureSecurityConfiguration(
            joinPathErrors.Length == 0 ? guardrailFailure.Errors : joinPathErrors,
            guardrailFailure.Diagnostics
        );
    }

    /// <summary>
    /// Every planned single-record check, for terminals that all resolved views execute ahead of.
    /// </summary>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> AllSingleRecordChecks(
        RelationalCustomViewAuthorization? customViewAuthorization
    ) => customViewAuthorization?.Checks ?? [];

    /// <summary>
    /// The planned single-record checks configured strictly before the earliest planning failure. Those views
    /// planned successfully and execute first, so they are validated even though a later one cannot plan.
    /// </summary>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> SingleRecordChecksBeforeFailure(
        SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
    )
    {
        var earliestFailureIndex = RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
            configurationFailure.Failures
        );

        return
        [
            .. configurationFailure.PlannedChecks.Where(check =>
                check.ConfiguredStrategy.RawConfiguredIndex < earliestFailureIndex
            ),
        ];
    }

    /// <summary>
    /// Carries single-record checks on a write planning failure so the async caller validates them before
    /// reporting it. Only the security-configuration outcome can carry them, which is the only outcome the
    /// single-record planner produces on failure.
    /// </summary>
    private static DescriptorWriteAuthorizationPreflightOutcome WithSingleRecordChecksToValidate(
        DescriptorWriteAuthorizationPreflightOutcome outcome,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks
    ) =>
        outcome is DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError configurationError
            ? configurationError with
            {
                SingleRecordCustomViewChecksToValidate = checks,
            }
            : outcome;

    /// <summary>
    /// Validates single-record checks that execute ahead of a descriptor write planning failure. These are
    /// single-record specs, so they take the single-record validator rather than the page-query one. A null
    /// validation executor keeps the existing no-op behavior.
    /// </summary>
    private Task ValidateSingleRecordDescriptorWriteCustomViewsAsync(
        MappingSet mappingSet,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks,
        CancellationToken cancellationToken
    ) =>
        _customViewValidationCommandExecutor is null
            ? Task.CompletedTask
            : CustomViewAuthorizationValidator.ValidateSingleRecordAsync(
                _customViewValidationCommandExecutor,
                mappingSet.Key.Dialect,
                checks,
                cancellationToken
            );

    /// <summary>
    /// Validates the views a terminal carries. A null or empty list is a no-op, so every terminal can route
    /// through this unconditionally.
    /// </summary>
    private Task ValidateDescriptorWriteCustomViewsAsync(
        MappingSet mappingSet,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        CancellationToken cancellationToken
    ) =>
        _customViewValidationCommandExecutor is null
            ? Task.CompletedTask
            : CustomViewAuthorizationValidator.ValidateAsync(
                _customViewValidationCommandExecutor,
                mappingSet.Key.Dialect,
                customViewChecks,
                cancellationToken
            );

    /// <summary>
    /// Runs one ordered segment of stored custom-view membership checks against the locked target.
    /// </summary>
    /// <remarks>
    /// The executor is bound to the write session, not to a fresh connection: the target row is locked inside
    /// this transaction, so a check issued on any other connection would either block or read a row the
    /// transaction has not committed. Its validation probe is the opposite — it reads the catalog rather than the
    /// locked row, so it takes the fresh executor the terminals already validate on.
    /// </remarks>
    private Task<CustomViewAuthorizationExecutionResult> ExecuteDescriptorCustomViewAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> runChecks,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks,
        IRelationalCommandExecutor sessionCommandExecutor,
        CancellationToken cancellationToken
    ) =>
        new CustomViewAuthorizationExecutor(
            sessionCommandExecutor,
            _relationshipAuthorizationProviderFailureExtractor,
            _customViewValidationCommandExecutor,
            _writeExceptionClassifier
        ).ExecuteAsync(
            new CustomViewAuthorizationExecutionRequest(mappingSet, documentId, runChecks, plannedChecks),
            cancellationToken
        );

    /// <summary>
    /// Splits a delete's planned custom-view checks around the namespace check's configured position. Both are
    /// AND filters executing in CMS-configured order, and the first failure is the one reported.
    /// </summary>
    private static (
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> Before,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> After
    ) PartitionDescriptorCustomViewRuns(
        RelationalCustomViewAuthorization? customViewAuthorization,
        RelationalWriteNamespaceAuthorization? namespaceAuthorization
    )
    {
        if (customViewAuthorization is null)
        {
            return ([], []);
        }

        return namespaceAuthorization is { Checks.Count: > 0 } namespaceChecks
            ? CustomViewAuthorizationCheckSplitter.PartitionByConfiguredIndex(
                customViewAuthorization.StoredChecks,
                namespaceChecks.Checks[0].RawConfiguredIndex
            )
            : (customViewAuthorization.StoredChecks, []);
    }

    /// <summary>
    /// Runs one ordered custom-view segment inside a descriptor DELETE's locked-target boundary, answering
    /// with the caller-visible failure or <see langword="null"/> when the segment authorizes.
    /// </summary>
    private async Task<DeleteResult?> AuthorizeLockedDescriptorDeleteCustomViewsAsync(
        DescriptorDeleteRequest request,
        long documentId,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> runChecks,
        RelationalCustomViewAuthorization? customViewAuthorization,
        IRelationalCommandExecutor sessionCommandExecutor,
        CancellationToken cancellationToken
    )
    {
        if (runChecks.Count == 0 || customViewAuthorization is null)
        {
            return null;
        }

        var result = await ExecuteDescriptorCustomViewAuthorizationAsync(
                request.MappingSet,
                documentId,
                runChecks,
                customViewAuthorization.Checks,
                sessionCommandExecutor,
                cancellationToken
            )
            .ConfigureAwait(false);

        return result switch
        {
            CustomViewAuthorizationExecutionResult.Authorized => null,
            CustomViewAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                new DeleteResult.DeleteFailureCustomViewNotAuthorized(notAuthorized.Failure),
            CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure invalid =>
                new DeleteResult.DeleteFailureSecurityConfiguration(
                    [invalid.FailureMessage],
                    invalid.Diagnostics
                ),
            // The target was deleted between the resolve and this check, so there is nothing left to delete.
            CustomViewAuthorizationExecutionResult.StaleTarget => new DeleteResult.DeleteFailureNotExists(),
            _ => throw new InvalidOperationException(
                $"Unsupported custom view authorization execution result '{result.GetType().Name}'."
            ),
        };
    }

    /// <summary>
    /// The locked-precondition counterpart of
    /// <see cref="AuthorizeLockedDescriptorDeleteCustomViewsAsync"/>, shared by the verbs that authorize
    /// through the If-Match precondition helper.
    /// </summary>
    private async Task<DescriptorLockedPreconditionResult?> EvaluateLockedCustomViewAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> runChecks,
        RelationalCustomViewAuthorization? customViewAuthorization,
        IRelationalCommandExecutor sessionCommandExecutor,
        CancellationToken cancellationToken
    )
    {
        if (runChecks.Count == 0 || customViewAuthorization is null)
        {
            return null;
        }

        var result = await ExecuteDescriptorCustomViewAuthorizationAsync(
                mappingSet,
                documentId,
                runChecks,
                customViewAuthorization.Checks,
                sessionCommandExecutor,
                cancellationToken
            )
            .ConfigureAwait(false);

        return result switch
        {
            CustomViewAuthorizationExecutionResult.Authorized => null,
            CustomViewAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                new DescriptorLockedPreconditionResult.CustomViewNotAuthorized(notAuthorized.Failure),
            CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure invalid =>
                new DescriptorLockedPreconditionResult.CustomViewAuthorizationInvalid(
                    invalid.FailureMessage,
                    invalid.Diagnostics
                ),
            CustomViewAuthorizationExecutionResult.StaleTarget => DescriptorLockedPreconditionResult
                .NotFound
                .Instance,
            _ => throw new InvalidOperationException(
                $"Unsupported custom view authorization execution result '{result.GetType().Name}'."
            ),
        };
    }

    private static DescriptorDeleteAuthorizationPreflightResult AuthorizeDescriptorDeletePlanPreflight(
        DescriptorDeleteRequest request,
        RelationalAuthorizationPlanOutcome.Plan plan
    )
    {
        if (
            !TryPlanDescriptorDeleteCustomViews(
                request,
                plan.CustomViewStrategies,
                out var customViewAuthorization,
                out var customViewPlanFailure,
                out var customViewChecksBeforePlanFailure
            )
        )
        {
            return new DescriptorDeleteAuthorizationPreflightResult.Stop(
                customViewPlanFailure!,
                [],
                customViewChecksBeforePlanFailure
            );
        }

        if (plan.NamespaceChecks.Count == 0)
        {
            return new DescriptorDeleteAuthorizationPreflightResult.Proceed(null, customViewAuthorization);
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
            return DeleteTerminal(
                request,
                new DeleteResult.DeleteFailureSecurityConfiguration(
                    [securityConfigurationMessage],
                    securityConfigurationDiagnostics
                ),
                plan.CustomViewStrategies,
                plan.NamespaceChecks[0].RawConfiguredIndex
            );
        }

        return new DescriptorDeleteAuthorizationPreflightResult.Proceed(
            new RelationalWriteNamespaceAuthorization(plan.NamespaceChecks, namespacePrefixParameterization),
            customViewAuthorization
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

        /// <param name="CustomViewChecksToValidate">
        /// The views configured ahead of this terminal. They execute before it, so a missing or non-conforming
        /// view keeps its own 500 rather than being hidden by the terminal's response.
        /// </param>
        /// <param name="SingleRecordCustomViewChecksToValidate">
        /// Checks planned by the single-record planner, which the terminals reached through the page planner
        /// never carry. They take the single-record validator rather than the page-query one.
        /// </param>
        public sealed record Stop(
            DeleteResult Result,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecksToValidate,
            IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> SingleRecordCustomViewChecksToValidate
        ) : DescriptorDeleteAuthorizationPreflightResult
        {
            public Stop(DeleteResult result)
                : this(result, [], []) { }

            public Stop(
                DeleteResult result,
                IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> customViewChecksToValidate
            )
                : this(result, customViewChecksToValidate, []) { }
        }

        public sealed record Proceed(
            RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization,
            RelationalCustomViewAuthorization? CustomViewAuthorization
        ) : DescriptorDeleteAuthorizationPreflightResult
        {
            public Proceed(RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization)
                : this(storedNamespaceAuthorization, null) { }
        }
    }

    /// <summary>
    /// Plans descriptor POST/PUT namespace authorization through the relational authorization
    /// orchestrator. Strategies other than <c>NamespaceBased</c> / <c>NoFurtherAuthorizationRequired</c>
    /// fail closed; the namespace planner terminals (no configured prefixes, no usable root column,
    /// MSSQL prefix cap) short-circuit with no DB roundtrip; otherwise the planner's checks are
    /// split into stored-value (locked target) and proposed-value (request body) namespace authorizations
    /// re-indexed from zero — custom-view checks keep their request-wide indexes instead, so both value
    /// sources can share one payload space — each executed as its own single-record statement inside the write
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
            RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot => WriteTerminal(
                request,
                new DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError(
                    [
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ],
                    RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                ),
                noUsableRoot.CustomViewStrategies,
                noUsableRoot.RawConfiguredIndex
            ),
            RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes => WriteTerminal(
                request,
                new DescriptorWriteAuthorizationPreflightOutcome.NamespaceNotAuthorized(
                    NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                ),
                noPrefixes.CustomViewStrategies,
                noPrefixes.RawConfiguredIndex
            ),
            RelationalAuthorizationPlanOutcome.Plan plan
                when RelationalReadGuardrails.HasDescriptorUnsupportedNonNamespaceStrategies(
                    plan.NonNamespaceConfiguredStrategies
                ) => WriteTerminal(
                request,
                new DescriptorWriteAuthorizationPreflightOutcome.NotImplemented(
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        request.Resource,
                        request.AuthorizationStrategyEvaluators,
                        operationLabel,
                        actionLabel,
                        plan.CustomViewStrategies
                    )
                ),
                plan.CustomViewStrategies,
                // OwnershipBased executes last per auth.md regardless of configured position, so every
                // resolved view runs before this 501.
                int.MaxValue
            ),
            RelationalAuthorizationPlanOutcome.Plan plan => BuildDescriptorWritePlanPreflight(request, plan),
            RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported => WriteTerminal(
                request,
                new DescriptorWriteAuthorizationPreflightOutcome.NotImplemented(
                    RelationalReadGuardrails.BuildAuthorizationNotImplementedMessage(
                        request.Resource,
                        request.AuthorizationStrategyEvaluators,
                        operationLabel,
                        actionLabel,
                        stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies
                    )
                ),
                stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies,
                int.MaxValue
            ),
            RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError =>
                WriteTerminal(
                    request,
                    BuildDescriptorWriteSecurityConfigurationError(
                        request.Resource,
                        securityConfigurationError
                    ),
                    securityConfigurationError.RelationshipClassification.SupportedCustomViewStrategies,
                    RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                        securityConfigurationError.RelationshipClassification.SecurityConfigurationFailures
                    )
                ),
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

    /// <summary>
    /// Runs one ordered custom-view segment inside a descriptor write's locked-target boundary, projecting the
    /// outcome through the caller's result factories so POST and PUT keep their own result types.
    /// </summary>
    private async Task<TResult?> EvaluateLockedDescriptorWriteCustomViewsAsync<TResult>(
        MappingSet mappingSet,
        long documentId,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> runChecks,
        RelationalCustomViewAuthorization? customViewAuthorization,
        IRelationalCommandExecutor sessionCommandExecutor,
        Func<CustomViewAuthorizationFailure, TResult> customViewNotAuthorizedFactory,
        Func<string, SecurityConfigurationFailureDiagnostic[]?, TResult> authorizationInvalidFactory,
        Func<TResult> staleTargetFactory,
        CancellationToken cancellationToken
    )
        where TResult : class
    {
        if (runChecks.Count == 0 || customViewAuthorization is null)
        {
            return null;
        }

        var result = await ExecuteDescriptorCustomViewAuthorizationAsync(
                mappingSet,
                documentId,
                runChecks,
                customViewAuthorization.Checks,
                sessionCommandExecutor,
                cancellationToken
            )
            .ConfigureAwait(false);

        return result switch
        {
            CustomViewAuthorizationExecutionResult.Authorized => null,
            CustomViewAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                customViewNotAuthorizedFactory(notAuthorized.Failure),
            CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure invalid =>
                authorizationInvalidFactory(invalid.FailureMessage, invalid.Diagnostics),
            CustomViewAuthorizationExecutionResult.StaleTarget => staleTargetFactory(),
            _ => throw new InvalidOperationException(
                $"Unsupported custom view authorization execution result '{result.GetType().Name}'."
            ),
        };
    }

    /// <summary>
    /// The first self-basis proposed check whose configured strategy planned no stored check, or
    /// <see langword="null"/> when every one of them is paired. A self-basis proposed check is only satisfied
    /// by its stored pair, so an unpaired one has proven nothing and must fail closed.
    /// </summary>
    private static SingleRecordCustomViewAuthorizationCheckSpec? FindUnpairedSelfBasisProposedCheck(
        RelationalCustomViewAuthorization? customViewAuthorization
    )
    {
        if (customViewAuthorization is null)
        {
            return null;
        }

        foreach (var check in customViewAuthorization.ProposedChecks)
        {
            if (check.CheckTarget is not CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable)
            {
                continue;
            }

            var hasPairedStoredCheck = customViewAuthorization.Checks.Any(planned =>
                planned.ValueSource is CustomViewAuthorizationCheckValueSource.Stored
                && planned.ConfiguredStrategy == check.ConfiguredStrategy
            );

            if (!hasPairedStoredCheck)
            {
                return check;
            }
        }

        return null;
    }

    /// <summary>
    /// The first proposed check that is not self-basis, or <see langword="null"/> when every one is. Descriptor
    /// writes have no finalized root row to read a bound basis value from, so such a check cannot be executed
    /// and must fail closed rather than be skipped.
    /// </summary>
    private static SingleRecordCustomViewAuthorizationCheckSpec? FindNonSelfBasisProposedCheck(
        RelationalCustomViewAuthorization? customViewAuthorization
    ) =>
        customViewAuthorization?.ProposedChecks.FirstOrDefault(check =>
            check.CheckTarget is not CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable
        );

    /// <summary>
    /// Whether the proposed namespace check is configured strictly before a self-basis denial, and so still
    /// runs ahead of it. Custom views and <c>NamespaceBased</c> are AND filters executing in CMS-configured
    /// order and the first failure is the one reported, so a denial configured at or before the namespace
    /// position preempts it — matching the tie rule that puts a custom view first at an equal index.
    /// </summary>
    private static bool NamespacePrecedesSelfBasisDenial(
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        SingleRecordCustomViewAuthorizationCheckSpec selfBasisDenial
    ) =>
        proposedNamespaceAuthorization is { Checks.Count: > 0 } namespaceAuthorization
        && namespaceAuthorization.Checks[0].RawConfiguredIndex
            < selfBasisDenial.ConfiguredStrategy.RawConfiguredIndex;

    /// <summary>
    /// The first self-basis proposed check, or <see langword="null"/> when none is planned.
    /// </summary>
    private static SingleRecordCustomViewAuthorizationCheckSpec? FindSelfBasisProposedCheck(
        RelationalCustomViewAuthorization? customViewAuthorization
    ) =>
        customViewAuthorization?.ProposedChecks.FirstOrDefault(check =>
            check.CheckTarget is CustomViewAuthorizationCheckTarget.ProposedSelfBasisUnavailable
        );

    /// <summary>
    /// Validates the denying view, then projects its auth.md §2.4 access-denied failure through the caller's
    /// result factory. Validating first is what keeps a missing or non-conforming view a 500 rather than being
    /// reported as this denial.
    /// </summary>
    private async Task<TResult> BuildSelfBasisCreateDenialResultAsync<TResult>(
        MappingSet mappingSet,
        SingleRecordCustomViewAuthorizationCheckSpec selfBasisDenial,
        Func<CustomViewAuthorizationFailure, TResult> customViewNotAuthorizedFactory,
        CancellationToken cancellationToken
    )
    {
        await ValidateDescriptorWriteCustomViewsSingleRecordAsync(
                mappingSet,
                [selfBasisDenial],
                cancellationToken
            )
            .ConfigureAwait(false);

        return customViewNotAuthorizedFactory(BuildSelfBasisCreateDenial(selfBasisDenial));
    }

    /// <summary>
    /// The auth.md §2.4 access-denied failure a self-basis proposed check produces on a create.
    /// </summary>
    private static CustomViewAuthorizationFailure BuildSelfBasisCreateDenial(
        SingleRecordCustomViewAuthorizationCheckSpec check
    ) =>
        new(
            CustomViewAuthorizationFailureKind.NoMatchingRow,
            CustomViewAuthorizationFailureValueSource.Proposed,
            check.Index,
            check.ConfiguredStrategy.StrategyName,
            [.. check.ReadableSecurableElements],
            check.FailureHint
        );

    /// <summary>
    /// Validates the views behind single-record checks decided in C#, where no membership statement runs.
    /// </summary>
    private Task ValidateDescriptorWriteCustomViewsSingleRecordAsync(
        MappingSet mappingSet,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks,
        CancellationToken cancellationToken
    ) =>
        _customViewValidationCommandExecutor is null
            ? Task.CompletedTask
            : CustomViewAuthorizationValidator.ValidateSingleRecordAsync(
                _customViewValidationCommandExecutor,
                mappingSet.Key.Dialect,
                checks,
                cancellationToken
            );

    /// <summary>
    /// Builds a write terminal carrying the views configured strictly before <paramref name="terminalIndex"/>,
    /// so those views are validated before the terminal is reported. A planning failure among them replaces the
    /// terminal, matching how the descriptor read and DELETE paths order the two.
    /// </summary>
    private static DescriptorWriteAuthorizationPreflightOutcome WriteTerminal(
        DescriptorWriteRequest request,
        DescriptorWriteAuthorizationPreflightOutcome terminal,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        int terminalIndex
    )
    {
        var strategiesToValidate = CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
            customViewStrategies,
            terminalIndex
        );

        if (strategiesToValidate.Count == 0)
        {
            return terminal;
        }

        // Planned through the same single-record descriptor-write path the Plan outcome uses, not the page
        // planner: these checks execute on a descriptor write, so they owe the non-self proposed-basis guard
        // that path enforces. Page planning bypassed it, so a view this path cannot execute was attached to
        // the terminal and validated as though it were runnable.
        if (
            !TryPlanDescriptorWriteCustomViews(
                request,
                strategiesToValidate,
                out var customViewAuthorization,
                out var customViewPlanFailure,
                out var customViewChecksBeforePlanFailure
            )
        )
        {
            return WithSingleRecordChecksToValidate(
                customViewPlanFailure!,
                customViewChecksBeforePlanFailure
            );
        }

        var checksToValidate = AllSingleRecordChecks(customViewAuthorization);

        return terminal switch
        {
            DescriptorWriteAuthorizationPreflightOutcome.NotImplemented notImplemented => notImplemented with
            {
                SingleRecordCustomViewChecksToValidate = checksToValidate,
            },
            DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError configError =>
                configError with
                {
                    SingleRecordCustomViewChecksToValidate = checksToValidate,
                },
            DescriptorWriteAuthorizationPreflightOutcome.NamespaceNotAuthorized namespaceNotAuthorized =>
                namespaceNotAuthorized with
                {
                    SingleRecordCustomViewChecksToValidate = checksToValidate,
                },
            _ => throw new InvalidOperationException(
                $"Descriptor write terminal '{terminal.GetType().Name}' cannot carry custom-view checks."
            ),
        };
    }

    /// <summary>
    /// A custom-view planning failure reported as a descriptor write security-configuration result.
    /// <see cref="RelationshipAuthorizationFailureKind.NoCustomViewJoinPath"/> keeps the specific join-path
    /// message; every other kind keeps the guardrail's unknown-strategy wording.
    /// </summary>
    private static RelationalReadSecurityConfigurationFailure BuildDescriptorWriteCustomViewSecurityConfigurationFailure(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        var guardrailFailure = RelationalReadGuardrails.BuildSecurityConfigurationFailure(
            resource,
            [],
            new RelationshipAuthorizationClassification(
                RelationshipAuthorizationClassificationOutcome.SecurityConfigurationError,
                [],
                [],
                [],
                [],
                failures
            )
        );

        string[] joinPathErrors =
        [
            .. failures
                .Where(static failure =>
                    failure.FailureKind is RelationshipAuthorizationFailureKind.NoCustomViewJoinPath
                )
                .Select(static failure =>
                    CustomViewAuthorizationFailureMessages.NoJoinPath(failure, "descriptor write")
                ),
        ];

        return joinPathErrors.Length == 0
            ? guardrailFailure
            : guardrailFailure with
            {
                Errors = joinPathErrors,
            };
    }

    /// <summary>
    /// Plans the single-record custom-view checks a descriptor POST or PUT owes, across both value sources, or
    /// reports the security-configuration failure that stops the write.
    /// </summary>
    private static bool TryPlanDescriptorWriteCustomViews(
        DescriptorWriteRequest request,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        out RelationalCustomViewAuthorization? customViewAuthorization,
        out DescriptorWriteAuthorizationPreflightOutcome? securityConfigurationFailure,
        out IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checksToValidateBeforeFailure
    )
    {
        customViewAuthorization = null;
        securityConfigurationFailure = null;
        checksToValidateBeforeFailure = [];

        if (customViewStrategies.Count == 0)
        {
            return true;
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            request.MappingSet,
            request.MappingSet.GetConcreteResourceModelOrThrow(request.Resource),
            customViewStrategies,
            NamespaceAuthorizationOperation.Update
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            var failure = BuildDescriptorWriteCustomViewSecurityConfigurationFailure(
                request.Resource,
                configurationFailure.Failures
            );

            securityConfigurationFailure =
                new DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError(
                    failure.Errors,
                    failure.Diagnostics
                );
            // Views configured ahead of the earliest planning failure planned successfully and execute first,
            // so they are still validated before this failure is reported.
            checksToValidateBeforeFailure = SingleRecordChecksBeforeFailure(configurationFailure);
            return false;
        }

        var checks = ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks;

        if (checks.Count == 0)
        {
            return true;
        }

        var planned = new RelationalCustomViewAuthorization(checks);

        // Descriptor writes bind no document references, so there is no finalized root row to read a proposed
        // basis value from. Only a self-basis proposed check can be settled here; anything else is a
        // configuration defect this path cannot execute, and must fail closed rather than be skipped. No
        // shipped ApiSchema produces it — see the pinning test — but nothing in the model builder prevents it.
        if (FindNonSelfBasisProposedCheck(planned) is { } unsupported)
        {
            securityConfigurationFailure =
                new DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError(
                    [
                        CustomViewAuthorizationSecurityConfigurationMessages.UnsupportedProposedBasisForDescriptorWrite(
                            unsupported.ConfiguredStrategy.StrategyName
                        ),
                    ],
                    AuthorizationSecurityConfigurationDiagnostics.ForCustomViewProposedValueExtraction(checks)
                );
            // Every view configured before the unsupported one planned and executes first, so they are
            // validated before this failure is reported.
            checksToValidateBeforeFailure =
            [
                .. checks.Where(check =>
                    check.ConfiguredStrategy.RawConfiguredIndex
                    < unsupported.ConfiguredStrategy.RawConfiguredIndex
                ),
            ];
            return false;
        }

        customViewAuthorization = planned;

        return true;
    }

    private static DescriptorWriteAuthorizationPreflightOutcome BuildDescriptorWritePlanPreflight(
        DescriptorWriteRequest request,
        RelationalAuthorizationPlanOutcome.Plan plan
    )
    {
        if (
            !TryPlanDescriptorWriteCustomViews(
                request,
                plan.CustomViewStrategies,
                out var customViewAuthorization,
                out var customViewPlanFailure,
                out var customViewChecksBeforePlanFailure
            )
        )
        {
            return WithSingleRecordChecksToValidate(
                customViewPlanFailure!,
                customViewChecksBeforePlanFailure
            );
        }

        if (plan.NamespaceChecks.Count == 0)
        {
            return customViewAuthorization is null
                ? DescriptorWriteAuthorizationPreflightOutcome.Proceed.NoAuthorization
                : new DescriptorWriteAuthorizationPreflightOutcome.Proceed(
                    null,
                    null,
                    customViewAuthorization
                );
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
            return WriteTerminal(
                request,
                new DescriptorWriteAuthorizationPreflightOutcome.SecurityConfigurationError(
                    [securityConfigurationMessage],
                    securityConfigurationDiagnostics
                ),
                plan.CustomViewStrategies,
                plan.NamespaceChecks[0].RawConfiguredIndex
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

        return new DescriptorWriteAuthorizationPreflightOutcome.Proceed(
            stored,
            proposed,
            customViewAuthorization
        );
    }

    private abstract record DescriptorWriteAuthorizationPreflightOutcome
    {
        private DescriptorWriteAuthorizationPreflightOutcome() { }

        /// <param name="CustomViewChecksToValidate">
        /// The views configured strictly before this terminal. They execute first, so a missing or
        /// non-conforming view keeps its own 500 rather than being hidden by the terminal's response.
        /// </param>
        /// <inheritdoc cref="DescriptorDeleteAuthorizationPreflightResult.Stop.SingleRecordCustomViewChecksToValidate"/>
        public sealed record NotImplemented(
            string FailureMessage,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecksToValidate,
            IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> SingleRecordCustomViewChecksToValidate
        ) : DescriptorWriteAuthorizationPreflightOutcome
        {
            public NotImplemented(string failureMessage)
                : this(failureMessage, [], []) { }
        }

        /// <inheritdoc cref="NotImplemented.CustomViewChecksToValidate"/>
        /// <inheritdoc cref="DescriptorDeleteAuthorizationPreflightResult.Stop.SingleRecordCustomViewChecksToValidate"/>
        public sealed record SecurityConfigurationError(
            string[] Errors,
            SecurityConfigurationFailureDiagnostic[]? Diagnostics,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecksToValidate,
            IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> SingleRecordCustomViewChecksToValidate
        ) : DescriptorWriteAuthorizationPreflightOutcome
        {
            public SecurityConfigurationError(
                string[] errors,
                SecurityConfigurationFailureDiagnostic[]? diagnostics = null
            )
                : this(errors, diagnostics, [], []) { }

            public SecurityConfigurationError(
                string[] errors,
                SecurityConfigurationFailureDiagnostic[]? diagnostics,
                IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> customViewChecksToValidate
            )
                : this(errors, diagnostics, customViewChecksToValidate, []) { }
        }

        /// <inheritdoc cref="NotImplemented.CustomViewChecksToValidate"/>
        /// <inheritdoc cref="DescriptorDeleteAuthorizationPreflightResult.Stop.SingleRecordCustomViewChecksToValidate"/>
        public sealed record NamespaceNotAuthorized(
            NamespaceAuthorizationFailure Failure,
            IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck> CustomViewChecksToValidate,
            IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> SingleRecordCustomViewChecksToValidate
        ) : DescriptorWriteAuthorizationPreflightOutcome
        {
            public NamespaceNotAuthorized(NamespaceAuthorizationFailure failure)
                : this(failure, [], []) { }
        }

        public sealed record Proceed(
            RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization,
            RelationalWriteNamespaceAuthorization? ProposedNamespaceAuthorization,
            RelationalCustomViewAuthorization? CustomViewAuthorization
        ) : DescriptorWriteAuthorizationPreflightOutcome
        {
            public Proceed(
                RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
                RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization
            )
                : this(storedNamespaceAuthorization, proposedNamespaceAuthorization, null) { }

            public static Proceed NoAuthorization { get; } = new(null, null, null);
        }
    }

    private async Task<DescriptorWriteAppliedResult<UpsertResult>> InsertDescriptorAsync(
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

        // Stamped on every descriptor create, exactly as it is for a regular resource, and independently of
        // whether descriptor ownership enforcement is available: stamping never consults configured
        // strategies. Descriptor requests configured with OwnershipBased still fail closed with a 501 before
        // reaching here, so a stamped descriptor row is only ever produced by a request this handler accepts.
        var createdByOwnershipTokenId = request.RelationalAuthorizationContext.CreatorOwnershipTokenId;

        var command = request.MappingSet.Key.Dialect switch
        {
            SqlDialect.Pgsql => BuildPostgresqlInsertCommand(
                body,
                documentUuid,
                resourceKeyId,
                request.ReferentialId!.Value,
                createdByOwnershipTokenId
            ),
            SqlDialect.Mssql => BuildMssqlInsertCommand(
                body,
                documentUuid,
                resourceKeyId,
                request.ReferentialId!.Value,
                createdByOwnershipTokenId
            ),
            _ => throw new NotSupportedException(
                $"Descriptor write does not support SQL dialect '{request.MappingSet.Key.Dialect}'."
            ),
        };

        var persistedWrite = await ExecuteDescriptorWriteReturningContentVersionWithTelemetryAsync(
                request,
                commandExecutor,
                command,
                DocumentCacheEnqueueTelemetryCanonicalOperation.Insert,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new DescriptorWriteAppliedResult<UpsertResult>(
            new UpsertResult.InsertSuccess(
                documentUuid,
                _servedEtagComposer.Compose(
                    new ServedEtagContext(
                        request.MappingSet.Key.EffectiveSchemaHash,
                        ResponseFormat.Json,
                        request.ProfileName,
                        LinksEnabled: false,
                        persistedWrite.ContentVersion
                    )
                )
            ),
            persistedWrite.DocumentCacheEnqueueOutcome
        );
    }

    private async Task<DescriptorWriteAppliedResult<UpsertResult>> UpdateDescriptorForUpsertAsync(
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

        var persistedWrite = await ExecuteDescriptorWriteReturningContentVersionWithTelemetryAsync(
                request,
                commandExecutor,
                command,
                DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
                cancellationToken
            )
            .ConfigureAwait(false);

        return new DescriptorWriteAppliedResult<UpsertResult>(
            new UpsertResult.UpdateSuccess(
                existingDocumentUuid,
                _servedEtagComposer.Compose(
                    new ServedEtagContext(
                        request.MappingSet.Key.EffectiveSchemaHash,
                        ResponseFormat.Json,
                        request.ProfileName,
                        LinksEnabled: false,
                        persistedWrite.ContentVersion
                    )
                )
            ),
            persistedWrite.DocumentCacheEnqueueOutcome
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
        RecordDescriptorEnqueueSuccessIfApplicable(
            request,
            DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
            upsertResult.DocumentCacheEnqueueOutcome
        );
        return upsertResult.Result;
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

        var persistedWrite = await ExecuteDescriptorWriteReturningContentVersionWithTelemetryAsync(
                request,
                writeSession.CreateCommandExecutor(),
                command,
                DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
                cancellationToken
            )
            .ConfigureAwait(false);

        await writeSession.CommitAsync(cancellationToken).ConfigureAwait(false);
        RecordDescriptorEnqueueSuccessIfApplicable(
            request,
            DocumentCacheEnqueueTelemetryCanonicalOperation.Update,
            persistedWrite.DocumentCacheEnqueueOutcome
        );

        return new UpdateResult.UpdateSuccess(
            documentUuid,
            _servedEtagComposer.Compose(
                new ServedEtagContext(
                    request.MappingSet.Key.EffectiveSchemaHash,
                    ResponseFormat.Json,
                    request.ProfileName,
                    LinksEnabled: false,
                    persistedWrite.ContentVersion
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
        RelationalCustomViewAuthorization? customViewAuthorization,
        CancellationToken cancellationToken
    ) =>
        ApplyWithLockedDescriptorCurrentStateAsync<UpsertResult>(
            request,
            body,
            documentId,
            storedNamespaceAuthorization,
            proposedNamespaceAuthorization,
            customViewAuthorization,
            static () => new UpsertResult.UpsertFailureWriteConflict(),
            missingDescriptorDocumentId => new UpsertResult.UnknownFailure(
                BuildMissingDescriptorMessage(request.Resource, missingDescriptorDocumentId)
            ),
            static failure => new UpsertResult.UpsertFailureNamespaceNotAuthorized(failure),
            static (failureMessage, diagnostics) =>
                new UpsertResult.UpsertFailureSecurityConfiguration([failureMessage], diagnostics),
            static () => new UpsertResult.UpsertFailureWriteConflict(),
            static customViewFailure => new UpsertResult.UpsertFailureCustomViewNotAuthorized(
                customViewFailure
            ),
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
        RelationalCustomViewAuthorization? customViewAuthorization,
        CancellationToken cancellationToken
    ) =>
        ApplyWithLockedDescriptorCurrentStateAsync<UpdateResult>(
            request,
            body,
            documentId,
            storedNamespaceAuthorization,
            proposedNamespaceAuthorization,
            customViewAuthorization,
            static () => new UpdateResult.UpdateFailureNotExists(),
            missingDescriptorDocumentId => new UpdateResult.UnknownFailure(
                BuildMissingDescriptorMessage(request.Resource, missingDescriptorDocumentId)
            ),
            static failure => new UpdateResult.UpdateFailureNamespaceNotAuthorized(failure),
            static (failureMessage, diagnostics) =>
                new UpdateResult.UpdateFailureSecurityConfiguration([failureMessage], diagnostics),
            static () => new UpdateResult.UpdateFailureNotExists(),
            static customViewFailure => new UpdateResult.UpdateFailureCustomViewNotAuthorized(
                customViewFailure
            ),
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
        RelationalCustomViewAuthorization? customViewAuthorization,
        Func<TResult> missingDocumentResultFactory,
        Func<long, TResult> missingDescriptorResultFactory,
        Func<NamespaceAuthorizationFailure, TResult> namespaceNotAuthorizedFactory,
        Func<string, SecurityConfigurationFailureDiagnostic[]?, TResult> namespaceAuthorizationInvalidFactory,
        Func<TResult> namespaceStaleTargetFactory,
        Func<CustomViewAuthorizationFailure, TResult> customViewNotAuthorizedFactory,
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
                    // AND-compose the configured filters against the locked target before applying any
                    // change: the stored sequence in configured order — the custom views at or before
                    // NamespaceBased, the stored namespace check, then the views after it — and only then
                    // the proposed namespace check. Any denial returns its 403 with no INSERT/UPDATE
                    // statement, and short-circuits before the no-op or immutable-identity checks so 403
                    // wins over those outcomes too.
                    var sessionCommandExecutor = writeSession.CreateCommandExecutor();
                    var (customViewsBeforeNamespace, customViewsAfterNamespace) =
                        PartitionDescriptorCustomViewRuns(
                            customViewAuthorization,
                            storedNamespaceAuthorization
                        );

                    var earlyCustomViewFailure = await EvaluateLockedDescriptorWriteCustomViewsAsync(
                            request.MappingSet,
                            documentId,
                            customViewsBeforeNamespace,
                            customViewAuthorization,
                            sessionCommandExecutor,
                            customViewNotAuthorizedFactory,
                            namespaceAuthorizationInvalidFactory,
                            namespaceStaleTargetFactory,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    if (earlyCustomViewFailure is not null)
                    {
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return earlyCustomViewFailure;
                    }

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

                    var lateCustomViewFailure = await EvaluateLockedDescriptorWriteCustomViewsAsync(
                            request.MappingSet,
                            documentId,
                            customViewsAfterNamespace,
                            customViewAuthorization,
                            sessionCommandExecutor,
                            customViewNotAuthorizedFactory,
                            namespaceAuthorizationInvalidFactory,
                            namespaceStaleTargetFactory,
                            cancellationToken
                        )
                        .ConfigureAwait(false);

                    if (lateCustomViewFailure is not null)
                    {
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return lateCustomViewFailure;
                    }

                    // Last, because it reads the request's proposed value: every stored check has to have
                    // answered against the locked row first, or a proposed denial would mask the stored
                    // custom-view answer configured after NamespaceBased — including the 500 a nonconforming
                    // view behind it owes.
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

                    // A self-basis proposed check against an existing target is satisfied by the paired stored
                    // check that just authorized this row: a document's own DocumentId is immutable, so the
                    // proposed basis value is the value already authorized. Without that pair nothing proved
                    // it, so the plan fails closed.
                    var unpairedSelfBasis = FindUnpairedSelfBasisProposedCheck(customViewAuthorization);

                    if (unpairedSelfBasis is not null)
                    {
                        await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        return namespaceAuthorizationInvalidFactory(
                            CustomViewAuthorizationSecurityConfigurationMessages.UnpairedSelfBasisProposedCheck(
                                unpairedSelfBasis.ConfiguredStrategy.StrategyName
                            ),
                            null
                        );
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
        RelationalCustomViewAuthorization? customViewAuthorization,
        CancellationToken cancellationToken
    )
    {
        // A self-basis proposed check on a create has no DocumentId to prove membership for — the row does not
        // exist yet, so no view row can reference it. The denial is deterministic, so when nothing is configured
        // ahead of it no session is opened at all; the view is still validated so a misconfigured view keeps its
        // own 500.
        var selfBasisDenial = FindSelfBasisProposedCheck(customViewAuthorization);

        if (
            selfBasisDenial is not null
            && !NamespacePrecedesSelfBasisDenial(proposedNamespaceAuthorization, selfBasisDenial)
        )
        {
            return await BuildSelfBasisCreateDenialResultAsync(
                    request.MappingSet,
                    selfBasisDenial,
                    static failure => new UpsertResult.UpsertFailureCustomViewNotAuthorized(failure),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

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

            // The namespace check was configured first and authorized, so the denial configured after it is now
            // the first failure.
            if (selfBasisDenial is not null)
            {
                await writeSession.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return await BuildSelfBasisCreateDenialResultAsync(
                        request.MappingSet,
                        selfBasisDenial,
                        static failure => new UpsertResult.UpsertFailureCustomViewNotAuthorized(failure),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
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
            RecordDescriptorEnqueueSuccessIfApplicable(
                request,
                DocumentCacheEnqueueTelemetryCanonicalOperation.Insert,
                insertResult.DocumentCacheEnqueueOutcome
            );
            return insertResult.Result;
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
    /// Executes a descriptor write and records canonical writer wait telemetry for the applied or
    /// failed outcome.
    /// </summary>
    private async Task<DescriptorWritePersistResult> ExecuteDescriptorWriteReturningContentVersionWithTelemetryAsync(
        DescriptorWriteRequest request,
        IRelationalCommandExecutor commandExecutor,
        RelationalCommand command,
        DocumentCacheEnqueueTelemetryCanonicalOperation canonicalOperation,
        CancellationToken cancellationToken
    )
    {
        long canonicalPersistStartTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var persistedWrite = await ExecuteWriteReturningPersistResultAsync(
                    commandExecutor,
                    command,
                    cancellationToken
                )
                .ConfigureAwait(false);

            RecordDescriptorCanonicalWriterWait(
                request,
                DocumentCacheWriterTelemetryLabel.AppliedWrite,
                canonicalPersistStartTimestamp
            );

            return persistedWrite;
        }
        catch (DbException ex)
        {
            RecordDescriptorCanonicalWriterWait(
                request,
                DocumentCacheWriterTelemetryLabel.Failed,
                canonicalPersistStartTimestamp
            );

            RecordDescriptorEnqueueFailureIfClassified(request, canonicalOperation, ex);
            throw;
        }
        catch
        {
            RecordDescriptorCanonicalWriterWait(
                request,
                DocumentCacheWriterTelemetryLabel.Failed,
                canonicalPersistStartTimestamp
            );

            throw;
        }
    }

    private void RecordDescriptorEnqueueSuccessIfApplicable(
        DescriptorWriteRequest request,
        DocumentCacheEnqueueTelemetryCanonicalOperation canonicalOperation,
        DocumentCacheEnqueueOutcome enqueueOutcome
    )
    {
        DocumentCacheEnqueueTelemetryWriteBoundary.RecordSuccessIfEnqueueSucceededBestEffort(
            _documentCacheEnqueueTelemetry,
            _dataStoreSelection,
            _documentCacheTargetRegistry,
            request.TenantKey,
            request.MappingSet.Key.Dialect,
            enqueueOutcome,
            canonicalOperation,
            DocumentCacheEnqueueTelemetryResourceKind.Descriptor,
            _logger
        );
    }

    private void RecordDescriptorEnqueueFailureIfClassified(
        DescriptorWriteRequest request,
        DocumentCacheEnqueueTelemetryCanonicalOperation canonicalOperation,
        DbException exception
    )
    {
        DocumentCacheEnqueueTelemetryWriteBoundary.RecordFailureIfClassifiedBestEffort(
            _documentCacheEnqueueTelemetry,
            _documentCacheProviderCommandTimeoutClassifier,
            _dataStoreSelection,
            _documentCacheTargetRegistry,
            request.TenantKey,
            request.MappingSet.Key.Dialect,
            canonicalOperation,
            DocumentCacheEnqueueTelemetryResourceKind.Descriptor,
            exception,
            _logger
        );
    }

    private void RecordDescriptorCanonicalWriterWait(
        DescriptorWriteRequest request,
        string outcome,
        long startTimestamp
    )
    {
        _documentCacheWriterTelemetry.RecordSameDocumentWait(
            DocumentCacheWriterMetricContext.ForCanonicalWriter(
                request.MappingSet.Key.Dialect,
                DocumentCacheTelemetryTargetKeyResolver.Resolve(_dataStoreSelection, request.TenantKey),
                DocumentCacheWriterTelemetryLabel.CanonicalWrite,
                outcome
            ),
            DocumentCacheWriterContentionParticipant.CanonicalWriter,
            DocumentCacheWriterContentionPhase.CanonicalPersist,
            DocumentCacheWriterTelemetry.GetElapsedTime(startTimestamp)
        );
    }

    /// <summary>
    /// Executes a descriptor write whose final statement surfaces the owning document's
    /// <c>ContentVersion</c> and returns that value for etag composition. Every descriptor write whose
    /// success result carries an etag (INSERT plus both UPDATE variants) surfaces ContentVersion:
    /// the INSERT returns the insert-time value (the stamp trigger only mirrors it on descriptor
    /// insert), and each UPDATE re-selects the post-trigger bumped value that a later GET reads.
    /// </summary>
    private static Task<DescriptorWritePersistResult> ExecuteWriteReturningPersistResultAsync(
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
                        return new DescriptorWritePersistResult(
                            reader.GetRequiredFieldValue<long>("ContentVersion"),
                            DocumentCacheEnqueueOutcomeConversion.FromDescriptorWrite(
                                reader.GetRequiredFieldValue<int>("DocumentCacheEnqueueOutcome")
                            )
                        );
                    }
                } while (await reader.NextResultAsync(ct).ConfigureAwait(false));

                throw new InvalidOperationException(
                    "Descriptor write did not surface a ContentVersion value for etag composition."
                );
            },
            cancellationToken
        );

    private sealed record DescriptorWritePersistResult(
        long ContentVersion,
        DocumentCacheEnqueueOutcome DocumentCacheEnqueueOutcome
    );

    private sealed record DescriptorWriteAppliedResult<TResult>(
        TResult Result,
        DocumentCacheEnqueueOutcome DocumentCacheEnqueueOutcome
    );

    // ── PostgreSQL SQL builders ──────────────────────────────────────────

    private static RelationalCommand BuildPostgresqlInsertCommand(
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        ReferentialId referentialId,
        short? createdByOwnershipTokenId
    )
    {
        // The data-modifying CTE performs the insert graph, then a separate statement reads the inserted
        // document and projection work row so PostgreSQL observes trigger side effects in this transaction.
        const string Sql = """
            WITH new_doc AS (
                INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId", "CreatedByOwnershipTokenId")
                VALUES (@documentUuid, @resourceKeyId, @createdByOwnershipTokenId)
                RETURNING "DocumentId"
            )
            , new_descriptor AS (
                INSERT INTO dms."Descriptor" (
                    "DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription",
                    "Description", "EffectiveBeginDate", "EffectiveEndDate",
                    "Discriminator", "Uri"
                )
                SELECT
                    "DocumentId", @resourceKeyId, @namespace, @codeValue, @shortDescription,
                    @description, @effectiveBeginDate::date, @effectiveEndDate::date,
                    @discriminator, @uri
                FROM new_doc
            )
            , new_referential AS (
                INSERT INTO dms."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId")
                SELECT @referentialId, "DocumentId", @resourceKeyId
                FROM new_doc
            )
            SELECT 1 WHERE false;

            SELECT
                document."ContentVersion" AS "ContentVersion",
                CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM dms."DocumentProjectionWork" work
                        WHERE work."DocumentId" = document."DocumentId"
                          AND work."RequiredContentVersion" >= document."ContentVersion"
                    )
                    THEN @enqueueOutcomeAlreadySatisfied
                    ELSE @enqueueOutcomeNoWorkQueued
                END AS "DocumentCacheEnqueueOutcome"
            FROM dms."Document" document
            WHERE document."DocumentUuid" = @documentUuid;
            """;

        return new RelationalCommand(
            Sql,
            BuildInsertParameters(body, documentUuid, resourceKeyId, referentialId, createdByOwnershipTokenId)
        );
    }

    private static RelationalCommand BuildMssqlInsertCommand(
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        ReferentialId referentialId,
        short? createdByOwnershipTokenId
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

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId], [CreatedByOwnershipTokenId])
            OUTPUT INSERTED.[ContentVersion] INTO @insertedContentVersion ([ContentVersion])
            VALUES (@documentUuid, @resourceKeyId, @createdByOwnershipTokenId);

            SET @newDocumentId = SCOPE_IDENTITY();

            INSERT INTO [dms].[Descriptor] (
                [DocumentId], [ResourceKeyId], [Namespace], [CodeValue], [ShortDescription],
                [Description], [EffectiveBeginDate], [EffectiveEndDate],
                [Discriminator], [Uri]
            )
            VALUES (
                @newDocumentId, @resourceKeyId, @namespace, @codeValue, @shortDescription,
                @description, @effectiveBeginDate, @effectiveEndDate,
                @discriminator, @uri
            );

            INSERT INTO [dms].[ReferentialIdentity] ([ReferentialId], [DocumentId], [ResourceKeyId])
            VALUES (@referentialId, @newDocumentId, @resourceKeyId);

            SELECT
                inserted.[ContentVersion],
                CAST(CASE
                    WHEN EXISTS (
                        SELECT TOP (1) 1
                        FROM [dms].[DocumentProjectionWork] work
                        WHERE work.[DocumentId] = @newDocumentId
                          AND work.[RequiredContentVersion] >= inserted.[ContentVersion]
                    )
                    THEN @enqueueOutcomeAlreadySatisfied
                    ELSE @enqueueOutcomeNoWorkQueued
                END AS int) AS [DocumentCacheEnqueueOutcome]
            FROM @insertedContentVersion inserted;
            """;

        return new RelationalCommand(
            Sql,
            BuildInsertParameters(body, documentUuid, resourceKeyId, referentialId, createdByOwnershipTokenId)
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

            SELECT
                document."ContentVersion" AS "ContentVersion",
                CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM dms."DocumentProjectionWork" work
                        WHERE work."DocumentId" = document."DocumentId"
                          AND work."RequiredContentVersion" >= document."ContentVersion"
                    )
                    THEN @enqueueOutcomeAlreadySatisfied
                    ELSE @enqueueOutcomeNoWorkQueued
                END AS "DocumentCacheEnqueueOutcome"
            FROM dms."Document" document
            WHERE document."DocumentId" = @documentId;
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

            SELECT
                document.[ContentVersion] AS [ContentVersion],
                CAST(CASE
                    WHEN EXISTS (
                        SELECT TOP (1) 1
                        FROM [dms].[DocumentProjectionWork] work
                        WHERE work.[DocumentId] = document.[DocumentId]
                          AND work.[RequiredContentVersion] >= document.[ContentVersion]
                    )
                    THEN @enqueueOutcomeAlreadySatisfied
                    ELSE @enqueueOutcomeNoWorkQueued
                END AS int) AS [DocumentCacheEnqueueOutcome]
            FROM [dms].[Document] document
            WHERE document.[DocumentId] = @documentId;
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

            SELECT
                document."ContentVersion" AS "ContentVersion",
                CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM dms."DocumentProjectionWork" work
                        WHERE work."DocumentId" = document."DocumentId"
                          AND work."RequiredContentVersion" >= document."ContentVersion"
                    )
                    THEN @enqueueOutcomeAlreadySatisfied
                    ELSE @enqueueOutcomeNoWorkQueued
                END AS "DocumentCacheEnqueueOutcome"
            FROM dms."Document" document
            WHERE document."DocumentId" = @documentId;
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

            SELECT
                document.[ContentVersion] AS [ContentVersion],
                CAST(CASE
                    WHEN EXISTS (
                        SELECT TOP (1) 1
                        FROM [dms].[DocumentProjectionWork] work
                        WHERE work.[DocumentId] = document.[DocumentId]
                          AND work.[RequiredContentVersion] >= document.[ContentVersion]
                    )
                    THEN @enqueueOutcomeAlreadySatisfied
                    ELSE @enqueueOutcomeNoWorkQueued
                END AS int) AS [DocumentCacheEnqueueOutcome]
            FROM [dms].[Document] document
            WHERE document.[DocumentId] = @documentId;
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

        public sealed record CustomViewNotAuthorized(CustomViewAuthorizationFailure Failure)
            : DescriptorLockedPreconditionResult;

        public sealed record CustomViewAuthorizationInvalid(
            string FailureMessage,
            SecurityConfigurationFailureDiagnostic[]? Diagnostics = null
        ) : DescriptorLockedPreconditionResult;

        public sealed record Mismatch(ETagPreconditionFailureReason Reason)
            : DescriptorLockedPreconditionResult;

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

    /// <param name="createdByOwnershipTokenId">
    /// The API client's <c>CreatorOwnershipTokenId</c>, or <see langword="null"/> when it has none. Bound
    /// either way so each dialect keeps one insert statement text, and typed explicitly for the same reason
    /// the regular-resource insert types it: a null reaches the driver as <c>DBNull</c>, which carries no
    /// type of its own.
    /// </param>
    private static List<RelationalParameter> BuildInsertParameters(
        ExtractedDescriptorBody body,
        DocumentUuid documentUuid,
        short resourceKeyId,
        ReferentialId referentialId,
        short? createdByOwnershipTokenId
    )
    {
        var parameters = BuildInsertFieldParameters(body);
        parameters.Add(new RelationalParameter("@documentUuid", documentUuid.Value));
        parameters.Add(new RelationalParameter("@resourceKeyId", resourceKeyId));
        parameters.Add(new RelationalParameter("@referentialId", referentialId.Value));
        parameters.Add(
            new RelationalParameter(
                "@createdByOwnershipTokenId",
                createdByOwnershipTokenId,
                static parameter => parameter.DbType = DbType.Int16
            )
        );
        AddEnqueueOutcomeParameters(parameters);
        return parameters;
    }

    private static List<RelationalParameter> BuildUpdateParameters(
        ExtractedDescriptorBody body,
        long documentId
    )
    {
        var parameters = BuildCommonFieldParameters(body);
        parameters.Add(new RelationalParameter("@documentId", documentId));
        AddEnqueueOutcomeParameters(parameters);
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
        AddEnqueueOutcomeParameters(parameters);
        return parameters;
    }

    private static void AddEnqueueOutcomeParameters(List<RelationalParameter> parameters)
    {
        parameters.Add(
            new RelationalParameter(
                EnqueueOutcomeNoWorkQueuedParameterName,
                (int)DocumentCacheEnqueueOutcome.NoWorkQueued
            )
        );
        parameters.Add(
            new RelationalParameter(
                EnqueueOutcomeAlreadySatisfiedParameterName,
                (int)DocumentCacheEnqueueOutcome.AlreadySatisfied
            )
        );
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
