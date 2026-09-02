// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using JsonArray = System.Text.Json.Nodes.JsonArray;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalDocumentStoreRepository(
    ILogger<RelationalDocumentStoreRepository> logger,
    IRelationalWriteExecutor writeExecutor,
    IRelationalDeleteEtagPreconditionChecker deleteEtagPreconditionChecker,
    IDescriptorWriteHandler descriptorWriteHandler,
    IDescriptorReadHandler descriptorReadHandler,
    IReferenceResolver referenceResolver,
    IDocumentHydrator documentHydrator,
    IRelationalReadTargetLookupService readTargetLookupService,
    IRelationalReadMaterializer readMaterializer,
    IReadableProfileProjector readableProfileProjector,
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IRelationalDeleteConstraintResolver deleteConstraintResolver,
    IRelationalWriteSessionFactory writeSessionFactory,
    RelationalEdOrgAuthorizationSubjectSelector edOrgAuthorizationSubjectSelector,
    ISingleRecordRelationshipAuthorizationExecutor singleRecordRelationshipAuthorizationExecutor,
    INamespaceAuthorizationExecutor namespaceAuthorizationExecutor,
    ICustomViewAuthorizationExecutor customViewAuthorizationExecutor,
    IOwnershipAuthorizationExecutor ownershipAuthorizationExecutor,
    IRelationalCommandExecutor commandExecutor,
    IDocumentCacheReadAccelerationCoordinator readAccelerationCoordinator,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null
) : IDocumentStoreRepository, IQueryHandler, IPartitionQueryHandler
{
    private const int GetByIdRelationshipAuthorizationAuth1Index = 0;
    internal const int PostRelationshipAuthorizationAuth1Index = 0;
    internal const int PutRelationshipAuthorizationAuth1Index = 0;
    private const int GetByIdReadBoundaryAttemptCount = 2;

    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRelationalWriteExecutor _writeExecutor =
        writeExecutor ?? throw new ArgumentNullException(nameof(writeExecutor));
    private readonly IRelationalDeleteEtagPreconditionChecker _deleteEtagPreconditionChecker =
        deleteEtagPreconditionChecker
        ?? throw new ArgumentNullException(nameof(deleteEtagPreconditionChecker));
    private readonly IDescriptorWriteHandler _descriptorWriteHandler =
        descriptorWriteHandler ?? throw new ArgumentNullException(nameof(descriptorWriteHandler));
    private readonly IDescriptorReadHandler _descriptorReadHandler =
        descriptorReadHandler ?? throw new ArgumentNullException(nameof(descriptorReadHandler));
    private readonly IReferenceResolver _referenceResolver =
        referenceResolver ?? throw new ArgumentNullException(nameof(referenceResolver));
    private readonly IDocumentHydrator _documentHydrator =
        documentHydrator ?? throw new ArgumentNullException(nameof(documentHydrator));
    private readonly IRelationalReadTargetLookupService _readTargetLookupService =
        readTargetLookupService ?? throw new ArgumentNullException(nameof(readTargetLookupService));
    private readonly IRelationalReadMaterializer _readMaterializer =
        readMaterializer ?? throw new ArgumentNullException(nameof(readMaterializer));
    private readonly IReadableProfileProjector _readableProfileProjector =
        readableProfileProjector ?? throw new ArgumentNullException(nameof(readableProfileProjector));
    private readonly IRelationalWriteExceptionClassifier _writeExceptionClassifier =
        writeExceptionClassifier ?? throw new ArgumentNullException(nameof(writeExceptionClassifier));
    private readonly IRelationalDeleteConstraintResolver _deleteConstraintResolver =
        deleteConstraintResolver ?? throw new ArgumentNullException(nameof(deleteConstraintResolver));
    private readonly IRelationalWriteSessionFactory _writeSessionFactory =
        writeSessionFactory ?? throw new ArgumentNullException(nameof(writeSessionFactory));
    private readonly ISingleRecordRelationshipAuthorizationExecutor _singleRecordRelationshipAuthorizationExecutor =
        singleRecordRelationshipAuthorizationExecutor
        ?? throw new ArgumentNullException(nameof(singleRecordRelationshipAuthorizationExecutor));
    private readonly INamespaceAuthorizationExecutor _namespaceAuthorizationExecutor =
        namespaceAuthorizationExecutor
        ?? throw new ArgumentNullException(nameof(namespaceAuthorizationExecutor));
    private readonly ICustomViewAuthorizationExecutor _customViewAuthorizationExecutor =
        customViewAuthorizationExecutor
        ?? throw new ArgumentNullException(nameof(customViewAuthorizationExecutor));
    private readonly IOwnershipAuthorizationExecutor _ownershipAuthorizationExecutor =
        ownershipAuthorizationExecutor
        ?? throw new ArgumentNullException(nameof(ownershipAuthorizationExecutor));

    // The read executor. Custom view-based GET-many validation runs on it rather than on a write session:
    // each call takes a separate read connection and round trip, but a read never opens a write
    // transaction just to probe the configured auth views.
    private readonly IRelationalCommandExecutor _commandExecutor =
        commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;
    private readonly IRelationshipAuthorizationProviderFailureExtractor _relationshipAuthorizationProviderFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;
    private readonly IDocumentCacheReadAccelerationCoordinator _readAccelerationCoordinator =
        readAccelerationCoordinator ?? throw new ArgumentNullException(nameof(readAccelerationCoordinator));
    private readonly RelationshipAuthorizationPlanner _relationshipAuthorizationPlanner = new(
        edOrgAuthorizationSubjectSelector
    );

    /// <remarks>
    /// Carries no anchor of its own. <see cref="PlannedQuery" /> was compiled against one and reports
    /// it, and the selected-keyset result set this preparation leads to is shaped by that same value,
    /// so a second copy here could name a different column than the one the keyset actually projects.
    /// </remarks>
    private sealed record RelationalQueryPreparation(
        QualifiedResourceName Resource,
        ResourceReadPlan ReadPlan,
        PageKeysetSpec.Query PlannedQuery,
        PageDocumentIdAuthorizationSpec? Authorization
    );

    private abstract record RelationalQueryPreparationResult
    {
        private RelationalQueryPreparationResult() { }

        public sealed record Complete(QueryResult Result) : RelationalQueryPreparationResult;

        public sealed record Prepared(RelationalQueryPreparation Preparation)
            : RelationalQueryPreparationResult;
    }

    /// <param name="Authorization">
    /// Carried so the custom-view relabel around execution can tell whether any view participated,
    /// exactly as the page path does.
    /// </param>
    private sealed record RelationalPartitionPreparation(
        PartitionWindowPlan PartitionPlan,
        PageDocumentIdAuthorizationSpec? Authorization
    );

    private abstract record RelationalPartitionPreparationResult
    {
        private RelationalPartitionPreparationResult() { }

        public sealed record Complete(PartitionResult Result) : RelationalPartitionPreparationResult;

        public sealed record Prepared(RelationalPartitionPreparation Preparation)
            : RelationalPartitionPreparationResult;
    }

    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        ArgumentNullException.ThrowIfNull(upsertRequest);
        var mappingSet = upsertRequest.MappingSet;

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpsertDocument - {TraceId}",
            upsertRequest.TraceId.Value
        );

        var resource = RelationalWriteSupport.ToQualifiedResourceName(upsertRequest.ResourceInfo);
        var writePrecondition = NormalizeWritePrecondition(upsertRequest.WritePrecondition);

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await _descriptorWriteHandler
                .HandlePostAsync(
                    new DescriptorWriteRequest(
                        mappingSet,
                        resource,
                        upsertRequest.EdfiDoc,
                        upsertRequest.DocumentUuid,
                        upsertRequest.DocumentInfo.ReferentialId,
                        upsertRequest.TraceId,
                        upsertRequest.AuthorizationStrategyEvaluators,
                        upsertRequest.AuthorizationContext,
                        upsertRequest.TenantKey
                    )
                    {
                        WritePrecondition = writePrecondition,
                        ProfileName = upsertRequest.BackendProfileWriteContext?.ProfileName,
                    }
                )
                .ConfigureAwait(false);
        }

        var profileWriteContext = upsertRequest.BackendProfileWriteContext;
        var selectedBody = profileWriteContext?.Request.WritableRequestBody ?? upsertRequest.EdfiDoc;

        // References and descriptors are extracted from the raw submitted body, but a
        // writable profile may hide submitted members that the shaper strips from selectedBody.
        // Restrict resolution to the references/descriptors still present in the shaped body so
        // hidden ones are accepted and ignored rather than resolved/written or rejected as
        // unresolved. Identity references preserved by the shaper remain present and are retained.
        // Authorization is computed from selectedBody and the shaped write plan so profile-hidden
        // submitted security fields are not resolved, written, or authorized.
        var documentReferences = ResolveProfileShapedReferences(
            profileWriteContext,
            upsertRequest.DocumentInfo.DocumentReferences,
            selectedBody
        );
        var descriptorReferences = ResolveProfileShapedDescriptors(
            profileWriteContext,
            upsertRequest.DocumentInfo.DescriptorReferences,
            selectedBody
        );

        var result = await ExecuteWriteGuardRails<UpsertResult>(
                requestBody: selectedBody,
                writePrecondition: writePrecondition,
                traceId: upsertRequest.TraceId,
                tenantKey: upsertRequest.TenantKey,
                mappingSet,
                upsertRequest.ResourceInfo,
                RelationalWriteOperationKind.Post,
                new RelationalWriteTargetRequest.Post(
                    upsertRequest.DocumentInfo.ReferentialId,
                    upsertRequest.DocumentUuid
                ),
                documentReferences,
                descriptorReferences,
                static failureMessage => new UpsertResult.UnknownFailure(failureMessage),
                static executorResult =>
                    executorResult switch
                    {
                        RelationalWriteExecutorResult.Upsert(var result) => result,
                        RelationalWriteExecutorResult.Update => throw new InvalidOperationException(
                            "Relational write executor returned an update result for a POST request."
                        ),
                        _ => throw new InvalidOperationException(
                            $"Relational write executor returned unsupported result type '{executorResult.GetType().Name}' for a POST request."
                        ),
                    },
                profileWriteContext,
                writePlan =>
                    AuthorizePostRelationshipIfRequired(upsertRequest, mappingSet, resource, writePlan),
                // Stamped onto dms.Document when this POST resolves to a create, and ignored when it
                // resolves to an upsert-as-update. Supplied unconditionally: stamping never consults the
                // resource's configured authorization strategies, which is what lets a claim set later
                // enforce ownership over data written before it was configured. Null when the API client
                // has no creator token, which stamps null.
                creatorOwnershipTokenId: upsertRequest.AuthorizationContext.CreatorOwnershipTokenId
            )
            .ConfigureAwait(false);

        return result;
    }

    public Task<GetResult> GetDocumentById(
        IGetRequest getRequest,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(getRequest);

        if (getRequest.ReadMode != RelationalGetRequestReadMode.ExternalResponse)
        {
            return GetDocumentByIdRelationalAsync(getRequest, cancellationToken);
        }

        var resource = RelationalWriteSupport.ToQualifiedResourceName(getRequest.ResourceInfo);

        if (getRequest.MappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return GetDocumentByIdRelationalAsync(getRequest, cancellationToken);
        }

        return _readAccelerationCoordinator.GetByIdAsync(
            new DocumentCacheReadAccelerationGetByIdRequest(
                getRequest.TenantKey,
                getRequest.MappingSet,
                resource,
                getRequest.DocumentUuid,
                DocumentCacheReadAccelerationResourceKind.Resource,
                fallbackCancellationToken =>
                    GetDocumentByIdRelationalAsync(getRequest, fallbackCancellationToken),
                selectionCancellationToken =>
                    SelectGetByIdReadAccelerationCandidateAsync(
                        getRequest,
                        resource,
                        selectionCancellationToken
                    )
            )
            {
                ReadableProfileProjectionContext = getRequest.ReadableProfileProjectionContext,
                ResponseContentCoding = getRequest.ResponseContentCoding,
            },
            cancellationToken
        );
    }

    private Task<GetResult> GetDocumentByIdRelationalAsync(
        IGetRequest getRequest,
        CancellationToken cancellationToken = default
    )
    {
        var mappingSet = getRequest.MappingSet;
        var resource = RelationalWriteSupport.ToQualifiedResourceName(getRequest.ResourceInfo);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.GetDocumentById - {TraceId}",
            getRequest.TraceId.Value
        );

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return _descriptorReadHandler.HandleGetByIdAsync(
                new DescriptorGetByIdRequest(
                    mappingSet,
                    resource,
                    getRequest.DocumentUuid,
                    getRequest.ReadMode,
                    getRequest.AuthorizationStrategyEvaluators,
                    getRequest.ReadableProfileProjectionContext,
                    getRequest.TraceId,
                    getRequest.AuthorizationContext,
                    getRequest.ResponseContentCoding,
                    getRequest.TenantKey
                ),
                cancellationToken
            );
        }

        ResourceReadPlan readPlan;

        try
        {
            readPlan = mappingSet.GetReadPlanOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult<GetResult>(new GetResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult<GetResult>(new GetResult.UnknownFailure(ex.Message));
        }

        return GetDocumentByIdAsync(getRequest, mappingSet, resource, readPlan, cancellationToken);
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        ArgumentNullException.ThrowIfNull(updateRequest);
        var mappingSet = updateRequest.MappingSet;

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpdateDocumentById - {TraceId}",
            updateRequest.TraceId.Value
        );

        var resource = RelationalWriteSupport.ToQualifiedResourceName(updateRequest.ResourceInfo);
        var writePrecondition = NormalizeWritePrecondition(updateRequest.WritePrecondition);

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await _descriptorWriteHandler
                .HandlePutAsync(
                    new DescriptorWriteRequest(
                        mappingSet,
                        resource,
                        updateRequest.EdfiDoc,
                        updateRequest.DocumentUuid,
                        referentialId: null,
                        updateRequest.TraceId,
                        updateRequest.AuthorizationStrategyEvaluators,
                        updateRequest.AuthorizationContext,
                        updateRequest.TenantKey
                    )
                    {
                        WritePrecondition = writePrecondition,
                        ProfileName = updateRequest.BackendProfileWriteContext?.ProfileName,
                    }
                )
                .ConfigureAwait(false);
        }

        var profileWriteContext = updateRequest.BackendProfileWriteContext;
        var selectedBody = profileWriteContext?.Request.WritableRequestBody ?? updateRequest.EdfiDoc;

        // Restrict reference/descriptor resolution to those still present in the
        // profile-shaped body (see the POST path for the full rationale). Hidden submitted
        // references/descriptors are accepted and ignored; preserved identity references remain.
        var documentReferences = ResolveProfileShapedReferences(
            profileWriteContext,
            updateRequest.DocumentInfo.DocumentReferences,
            selectedBody
        );
        var descriptorReferences = ResolveProfileShapedDescriptors(
            profileWriteContext,
            updateRequest.DocumentInfo.DescriptorReferences,
            selectedBody
        );

        var result = await ExecuteWriteGuardRails<UpdateResult>(
                requestBody: selectedBody,
                writePrecondition: writePrecondition,
                traceId: updateRequest.TraceId,
                tenantKey: updateRequest.TenantKey,
                mappingSet,
                updateRequest.ResourceInfo,
                RelationalWriteOperationKind.Put,
                new RelationalWriteTargetRequest.Put(updateRequest.DocumentUuid),
                documentReferences,
                descriptorReferences,
                static failureMessage => new UpdateResult.UnknownFailure(failureMessage),
                static executorResult =>
                    executorResult switch
                    {
                        RelationalWriteExecutorResult.Update(var result) => result,
                        RelationalWriteExecutorResult.Upsert => throw new InvalidOperationException(
                            "Relational write executor returned an upsert result for a PUT request."
                        ),
                        _ => throw new InvalidOperationException(
                            $"Relational write executor returned unsupported result type '{executorResult.GetType().Name}' for a PUT request."
                        ),
                    },
                profileWriteContext,
                writePlan =>
                    AuthorizePutRelationshipIfRequired(updateRequest, mappingSet, resource, writePlan),
                // Deliberately null, and stated rather than left to the default. A PUT never creates — the
                // resolved executor request rejects a CreateNew target for any operation other than POST —
                // so there is no row for a creator token to stamp. Passing the client's token here would
                // read as though a PUT might stamp one.
                creatorOwnershipTokenId: null
            )
            .ConfigureAwait(false);

        return result;
    }

    public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        ArgumentNullException.ThrowIfNull(deleteRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.DeleteDocumentById - {TraceId}",
            LoggingSanitizer.SanitizeForLogging(deleteRequest.TraceId.Value)
        );

        var mappingSet = deleteRequest.MappingSet;

        var resource = RelationalWriteSupport.ToQualifiedResourceName(deleteRequest.ResourceInfo);
        var writePrecondition = NormalizeWritePrecondition(deleteRequest.WritePrecondition);

        if (deleteRequest.ResourceInfo.IsDescriptor)
        {
            return _descriptorWriteHandler.HandleDeleteAsync(
                new DescriptorDeleteRequest(
                    mappingSet,
                    resource,
                    deleteRequest.DocumentUuid,
                    deleteRequest.TraceId,
                    deleteRequest.AuthorizationStrategyEvaluators,
                    deleteRequest.AuthorizationContext
                )
                {
                    WritePrecondition = writePrecondition,
                }
            );
        }

        // Planner terminals (namespace setup failures, the ownership token cap, relationship
        // security-configuration failures, and known unsupported relationship composition) resolve before
        // the write session opens, so those denials issue no DB roundtrip and never lock the target. The
        // target-dependent custom-view, namespace, ownership and relationship checks run inside the delete
        // session against the locked target, co-batched with the deletes or as ordered segments ahead of
        // them (see CompositeRelationalDeleteCommand).
        var authorizationPreflight = AuthorizeDeletePreflight(deleteRequest, mappingSet, resource);

        return authorizationPreflight switch
        {
            // Views configured ahead of the terminal execute first, so a missing or non-conforming view keeps
            // its own 500 rather than being hidden by the terminal's response.
            DeleteAuthorizationPreflightResult.Stop stop => ValidateThenReportDeleteTerminalAsync(
                mappingSet,
                stop
            ),
            DeleteAuthorizationPreflightResult.Proceed proceed => DeleteDocumentByIdAsync(
                deleteRequest,
                mappingSet,
                resource,
                writePrecondition,
                proceed.StoredNamespaceAuthorization,
                proceed.StoredCustomViewAuthorization,
                proceed.StoredOwnershipAuthorization,
                proceed.StoredRelationshipAuthorization
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational delete authorization preflight result '{authorizationPreflight.GetType().Name}'."
            ),
        };
    }

    private async Task<DeleteResult> DeleteDocumentByIdAsync(
        IDeleteRequest relationalDeleteRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        WritePrecondition writePrecondition,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalCustomViewAuthorization? storedCustomViewAuthorization,
        RelationalOwnershipAuthorization? storedOwnershipAuthorization,
        RelationshipAuthorizationResult storedRelationshipAuthorization
    )
    {
        var documentUuid = relationalDeleteRequest.DocumentUuid;
        var traceId = relationalDeleteRequest.TraceId;

        IRelationalWriteSession writeSession;
        try
        {
            writeSession = await _writeSessionFactory.CreateAsync().ConfigureAwait(false);
        }
        catch (DbException ex) when (_writeExceptionClassifier.IsTransientFailure(ex))
        {
            _logger.LogDebug(
                ex,
                "Transient conflict creating write session for relational DELETE on {DocumentUuid} - {TraceId}",
                documentUuid.Value,
                LoggingSanitizer.SanitizeForLogging(traceId.Value)
            );
            return new DeleteResult.DeleteFailureWriteConflict();
        }
        catch (DbException ex)
        {
            _logger.LogError(
                ex,
                "Database error creating write session for relational DELETE on {DocumentUuid} - {TraceId}",
                documentUuid.Value,
                LoggingSanitizer.SanitizeForLogging(traceId.Value)
            );
            return new DeleteResult.UnknownFailure(
                "An unexpected error occurred while processing the delete request."
            );
        }

        await using (writeSession)
        {
            DeleteResult outcome;

            try
            {
                outcome = await new CompositeRelationalDeleteCommand(
                    _deleteEtagPreconditionChecker,
                    _writeExceptionClassifier,
                    _deleteConstraintResolver,
                    _relationalParameterConfigurator,
                    _relationshipAuthorizationProviderFailureExtractor,
                    _logger,
                    // Opens a connection per command, so the custom-view validation probe never joins the write
                    // session's transaction.
                    customViewValidationCommandExecutor: _commandExecutor
                )
                    .ExecuteAsync(
                        new RelationalDeleteCommandRequest(
                            mappingSet,
                            resource,
                            documentUuid,
                            traceId,
                            storedNamespaceAuthorization,
                            storedRelationshipAuthorization
                        )
                        {
                            CustomViewAuthorization = storedCustomViewAuthorization,
                            StoredOwnershipAuthorization = storedOwnershipAuthorization,
                            WritePrecondition = writePrecondition,
                            DeferredRelationshipDenial = BuildDeferredDeleteRelationshipDenial(
                                storedRelationshipAuthorization,
                                relationalDeleteRequest.AuthorizationContext
                            ),
                        },
                        writeSession
                    )
                    .ConfigureAwait(false);
            }
            catch (DbException ex) when (_writeExceptionClassifier.IsTransientFailure(ex))
            {
                _logger.LogDebug(
                    ex,
                    "Transient conflict resolving delete target for {DocumentUuid} - {TraceId}",
                    documentUuid.Value,
                    LoggingSanitizer.SanitizeForLogging(traceId.Value)
                );

                await writeSession.RollbackAsync().ConfigureAwait(false);
                return new DeleteResult.DeleteFailureWriteConflict();
            }
            catch (DbException ex)
            {
                _logger.LogError(
                    ex,
                    "Database error resolving delete target for {DocumentUuid} - {TraceId}",
                    documentUuid.Value,
                    LoggingSanitizer.SanitizeForLogging(traceId.Value)
                );

                await writeSession.RollbackAsync().ConfigureAwait(false);
                return new DeleteResult.UnknownFailure(
                    "An unexpected error occurred while processing the delete request."
                );
            }

            if (outcome is DeleteResult.DeleteSuccess)
            {
                try
                {
                    await writeSession.CommitAsync().ConfigureAwait(false);
                }
                catch (DbException ex) when (_writeExceptionClassifier.IsTransientFailure(ex))
                {
                    _logger.LogDebug(
                        ex,
                        "Transient conflict committing relational DELETE for {DocumentUuid} - {TraceId}",
                        documentUuid.Value,
                        LoggingSanitizer.SanitizeForLogging(traceId.Value)
                    );

                    // Commit-phase failures leave the transaction in an ambiguous state: do not call
                    // RollbackAsync (the session would throw InvalidOperationException if the commit
                    // already began). The `await using writeSession` disposes the DbTransaction, which
                    // rolls back any still-pending state.
                    return new DeleteResult.DeleteFailureWriteConflict();
                }
                catch (DbException ex)
                {
                    _logger.LogError(
                        ex,
                        "Database error committing relational DELETE for {DocumentUuid} - {TraceId}",
                        documentUuid.Value,
                        LoggingSanitizer.SanitizeForLogging(traceId.Value)
                    );

                    return new DeleteResult.UnknownFailure(
                        "An unexpected error occurred while processing the delete request."
                    );
                }
            }
            else
            {
                await writeSession.RollbackAsync().ConfigureAwait(false);
            }

            return outcome;
        }
    }

    /// <summary>
    /// The relationship denial a caller holding no usable claims has already earned. It needs no statement,
    /// so it is handed to the delete command to apply once the capture proves the target exists — a missing
    /// target still answers not-found rather than forbidden.
    /// </summary>
    private static DeleteResult? BuildDeferredDeleteRelationshipDenial(
        RelationshipAuthorizationResult storedRelationshipAuthorization,
        RelationalAuthorizationContext authorizationContext
    )
    {
        if (storedRelationshipAuthorization is not RelationshipAuthorizationResult.NoClaims noClaims)
        {
            return null;
        }

        if (
            !TryCreateNoClaimsRelationshipAuthorizationFailure(
                noClaims,
                authorizationContext.ClaimEducationOrganizationIds,
                CompositeRelationalDeleteCommand.RelationshipAuthorizationAuth1Index,
                out var noClaimsFailure
            ) || noClaimsFailure is null
        )
        {
            return new DeleteResult.UnknownFailure(
                "Relationship authorization required caller EducationOrganizationIds, but denial metadata could not be built."
            );
        }

        return new DeleteResult.DeleteFailureRelationshipNotAuthorized(noClaimsFailure);
    }

    private async Task<DeleteResult> ValidateThenReportDeleteTerminalAsync(
        MappingSet mappingSet,
        DeleteAuthorizationPreflightResult.Stop stop
    )
    {
        await ValidateSingleRecordCustomViewsAsync(mappingSet, stop.CustomViewChecksToValidate)
            .ConfigureAwait(false);

        return stop.Result;
    }

    /// <inheritdoc cref="GetByIdTerminal"/>
    private static DeleteAuthorizationPreflightResult DeleteTerminal(
        MappingSet mappingSet,
        QualifiedResourceName resource,
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
            return new DeleteAuthorizationPreflightResult.Stop(result);
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            strategiesToValidate,
            NamespaceAuthorizationOperation.Delete
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            return new DeleteAuthorizationPreflightResult.Stop(
                BuildDeleteAuthorizationSecurityConfigurationFailure(
                    mappingSet,
                    resource,
                    configurationFailure.Failures
                ),
                SingleRecordChecksBeforeTerminal(
                    configurationFailure.PlannedChecks,
                    RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                        configurationFailure.Failures
                    )
                )
            );
        }

        return new DeleteAuthorizationPreflightResult.Stop(
            result,
            ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks
        );
    }

    /// <summary>
    /// The write counterpart of <see cref="DeleteTerminal"/>. Every POST/PUT preflight terminal that can be
    /// preceded by a custom view routes through this, so attaching the views is not a per-arm decision.
    /// </summary>
    private static WriteGuardRailPreflightResult<TResult> WriteTerminal<TResult>(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        TResult result,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        int terminalIndex,
        Func<IReadOnlyList<RelationshipAuthorizationFailureMetadata>, TResult> securityConfigurationFactory
    )
    {
        var strategiesToValidate = CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
            customViewStrategies,
            terminalIndex
        );

        if (strategiesToValidate.Count == 0)
        {
            return new WriteGuardRailPreflightResult<TResult>.Stop(result);
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            strategiesToValidate,
            NamespaceAuthorizationOperation.Update
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            return new WriteGuardRailPreflightResult<TResult>.Stop(
                securityConfigurationFactory(configurationFailure.Failures),
                SingleRecordChecksBeforeTerminal(
                    configurationFailure.PlannedChecks,
                    RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                        configurationFailure.Failures
                    )
                )
            );
        }

        return new WriteGuardRailPreflightResult<TResult>.Stop(
            result,
            ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks
        );
    }

    private DeleteAuthorizationPreflightResult AuthorizeDeletePreflight(
        IDeleteRequest relationalDeleteRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            relationalDeleteRequest.AuthorizationStrategyEvaluators
        );
        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            NamespaceAuthorizationOperation.Delete,
            configuredAuthorizationStrategies,
            relationalDeleteRequest.AuthorizationContext
        );

        switch (orchestratorOutcome)
        {
            case RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot:
                return DeleteTerminal(
                    mappingSet,
                    resource,
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
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return DeleteTerminal(
                    mappingSet,
                    resource,
                    new DeleteResult.DeleteFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    ),
                    noPrefixes.CustomViewStrategies,
                    noPrefixes.RawConfiguredIndex
                );

            // The ownership token list reaches the defensive limit. Reported before the write session opens,
            // so the target is never locked, and after the namespace terminals — the planner has already
            // resolved custom-view configuration failures by not returning this outcome in that case.
            case RelationalAuthorizationPlanOutcome.OwnershipTokenCapExceeded ownershipTokenCapExceeded:
                return DeleteTerminal(
                    mappingSet,
                    resource,
                    new DeleteResult.DeleteFailureSecurityConfiguration(
                        [
                            OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                                ownershipTokenCapExceeded.OwnershipTokenCount
                            ),
                        ],
                        AuthorizationSecurityConfigurationDiagnostics.ForOwnershipTokenParameterization(
                            AuthorizationSecurityConfigurationDiagnostics.OwnershipTokenCapExceeded
                        )
                    ),
                    ownershipTokenCapExceeded.CustomViewStrategies,
                    // Every configured view runs before this terminal: OwnershipBased executes last among
                    // the AND strategies whatever position it is configured at.
                    int.MaxValue
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return AuthorizeDeleteRelationshipPreflight(
                    mappingSet,
                    resource,
                    null,
                    null,
                    null,
                    securityConfigurationError.RelationshipClassification.SupportedCustomViewStrategies,
                    securityConfigurationError.NonNamespaceConfiguredStrategies,
                    relationalDeleteRequest.AuthorizationContext
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return AuthorizeDeleteRelationshipPreflight(
                    mappingSet,
                    resource,
                    null,
                    null,
                    null,
                    stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies,
                    stillUnsupported.NonNamespaceConfiguredStrategies,
                    relationalDeleteRequest.AuthorizationContext
                );

            case RelationalAuthorizationPlanOutcome.Plan plan:
                return AuthorizeDeletePlanPreflight(
                    mappingSet,
                    resource,
                    plan,
                    relationalDeleteRequest.AuthorizationContext
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
                );
        }
    }

    private DeleteAuthorizationPreflightResult AuthorizeDeletePlanPreflight(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.Plan plan,
        RelationalAuthorizationContext authorizationContext
    )
    {
        if (
            !TryPlanDeleteCustomViewAuthorization(
                mappingSet,
                resource,
                plan.CustomViewStrategies,
                out var storedCustomViewAuthorization,
                out var customViewSecurityConfigurationFailure,
                out var customViewChecksToValidate
            )
        )
        {
            return new DeleteAuthorizationPreflightResult.Stop(
                customViewSecurityConfigurationFailure!,
                customViewChecksToValidate
            );
        }

        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization = null;

        if (plan.NamespaceChecks.Count > 0)
        {
            if (
                !NamespacePrefixParameterizationPreflight.TryCreate(
                    mappingSet.Key.Dialect,
                    authorizationContext.NamespacePrefixes,
                    out var namespacePrefixParameterization,
                    out var securityConfigurationMessage,
                    out var securityConfigurationDiagnostics
                )
            )
            {
                return DeleteTerminal(
                    mappingSet,
                    resource,
                    new DeleteResult.DeleteFailureSecurityConfiguration(
                        [securityConfigurationMessage],
                        securityConfigurationDiagnostics
                    ),
                    plan.CustomViewStrategies,
                    plan.NamespaceChecks[0].RawConfiguredIndex
                );
            }

            storedNamespaceAuthorization = new RelationalWriteNamespaceAuthorization(
                plan.NamespaceChecks,
                namespacePrefixParameterization
            );
        }

        // After the namespace parameterization, as on the GET-by-id path: both are setup failures reported
        // as the same security-configuration 500, and NamespaceBased executes ahead of OwnershipBased, so a
        // request that would fail both must report the namespace one.
        if (
            !TryPlanStoredOwnershipAuthorization(
                mappingSet,
                plan.OwnershipCheck,
                authorizationContext,
                out var storedOwnershipAuthorization,
                out var ownershipSecurityConfigurationMessage,
                out var ownershipSecurityConfigurationDiagnostics
            )
        )
        {
            return DeleteTerminal(
                mappingSet,
                resource,
                new DeleteResult.DeleteFailureSecurityConfiguration(
                    [ownershipSecurityConfigurationMessage],
                    ownershipSecurityConfigurationDiagnostics
                ),
                plan.CustomViewStrategies,
                int.MaxValue
            );
        }

        return AuthorizeDeleteRelationshipPreflight(
            mappingSet,
            resource,
            storedNamespaceAuthorization,
            storedCustomViewAuthorization,
            storedOwnershipAuthorization,
            customViewStrategiesToValidate: null,
            plan.NonNamespaceConfiguredStrategies,
            authorizationContext
        );
    }

    /// <summary>
    /// Plans the custom-view checks a POST or PUT owes, across both value sources, or reports the
    /// security-configuration failures that stop the write.
    /// </summary>
    private static bool TryPlanWriteCustomViewAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        out RelationalCustomViewAuthorization? customViewAuthorization,
        out IReadOnlyList<RelationshipAuthorizationFailureMetadata>? securityConfigurationFailures,
        out IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checksToValidateBeforeFailure
    )
    {
        customViewAuthorization = null;
        securityConfigurationFailures = null;
        checksToValidateBeforeFailure = [];

        if (customViewStrategies.Count == 0)
        {
            return true;
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            customViewStrategies,
            NamespaceAuthorizationOperation.Update
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            securityConfigurationFailures = configurationFailure.Failures;
            // Views configured ahead of the earliest planning failure planned successfully and execute
            // first, so they are still validated before this failure is reported.
            checksToValidateBeforeFailure = SingleRecordChecksBeforeTerminal(
                configurationFailure.PlannedChecks,
                RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                    configurationFailure.Failures
                )
            );
            return false;
        }

        var checks = ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks;

        if (checks.Count > 0)
        {
            customViewAuthorization = new RelationalCustomViewAuthorization(checks);
        }

        return true;
    }

    private static bool TryPlanDeleteCustomViewAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        out RelationalCustomViewAuthorization? storedCustomViewAuthorization,
        out DeleteResult? securityConfigurationFailure,
        out IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checksToValidateBeforeFailure
    )
    {
        storedCustomViewAuthorization = null;
        securityConfigurationFailure = null;
        checksToValidateBeforeFailure = [];

        if (customViewStrategies.Count == 0)
        {
            return true;
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            customViewStrategies,
            NamespaceAuthorizationOperation.Delete
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            securityConfigurationFailure = BuildDeleteAuthorizationSecurityConfigurationFailure(
                mappingSet,
                resource,
                configurationFailure.Failures
            );
            // Views configured ahead of the earliest planning failure planned successfully and execute first,
            // so they are still validated before this failure is reported.
            checksToValidateBeforeFailure = SingleRecordChecksBeforeTerminal(
                configurationFailure.PlannedChecks,
                RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                    configurationFailure.Failures
                )
            );
            return false;
        }

        var checks = ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks;

        if (checks.Count > 0)
        {
            storedCustomViewAuthorization = new RelationalCustomViewAuthorization(checks);
        }

        return true;
    }

    private DeleteAuthorizationPreflightResult AuthorizeDeleteRelationshipPreflight(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalCustomViewAuthorization? storedCustomViewAuthorization,
        RelationalOwnershipAuthorization? storedOwnershipAuthorization,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy>? customViewStrategiesToValidate,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext
    )
    {
        var storedRelationshipAuthorization = _relationshipAuthorizationPlanner.PlanStoredValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext
        );

        return storedRelationshipAuthorization switch
        {
            // OwnershipBased executes last per auth.md regardless of configured position, so every resolved
            // view runs before this 501.
            RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled =>
                storedCustomViewAuthorization is { } plannedForNotImplemented
                    ? new DeleteAuthorizationPreflightResult.Stop(
                        new DeleteResult.DeleteFailureNotImplemented(
                            BuildKnownButNotEnabledDeleteAuthorizationMessage(
                                resource,
                                knownButNotEnabled.Failures
                            )
                        ),
                        plannedForNotImplemented.Checks
                    )
                    : DeleteTerminal(
                        mappingSet,
                        resource,
                        new DeleteResult.DeleteFailureNotImplemented(
                            BuildKnownButNotEnabledDeleteAuthorizationMessage(
                                resource,
                                knownButNotEnabled.Failures
                            )
                        ),
                        customViewStrategiesToValidate ?? [],
                        int.MaxValue
                    ),

            RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError =>
                storedCustomViewAuthorization is { } plannedForConfigError
                    ? new DeleteAuthorizationPreflightResult.Stop(
                        BuildDeleteAuthorizationSecurityConfigurationFailure(
                            mappingSet,
                            resource,
                            securityConfigurationError.Failures
                        ),
                        SingleRecordChecksBeforeTerminal(
                            plannedForConfigError.Checks,
                            RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                                securityConfigurationError.Failures
                            )
                        )
                    )
                    : DeleteTerminal(
                        mappingSet,
                        resource,
                        BuildDeleteAuthorizationSecurityConfigurationFailure(
                            mappingSet,
                            resource,
                            securityConfigurationError.Failures
                        ),
                        customViewStrategiesToValidate ?? [],
                        RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                            securityConfigurationError.Failures
                        )
                    ),

            _ => new DeleteAuthorizationPreflightResult.Proceed(
                storedNamespaceAuthorization,
                storedCustomViewAuthorization,
                storedOwnershipAuthorization,
                storedRelationshipAuthorization
            ),
        };
    }

    private abstract record DeleteAuthorizationPreflightResult
    {
        private DeleteAuthorizationPreflightResult() { }

        /// <inheritdoc cref="GetByIdAuthorizationPreflightResult.Stop.CustomViewChecksToValidate"/>
        public sealed record Stop(
            DeleteResult Result,
            IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> CustomViewChecksToValidate
        ) : DeleteAuthorizationPreflightResult
        {
            public Stop(DeleteResult result)
                : this(result, []) { }
        }

        /// <inheritdoc cref="GetByIdAuthorizationPreflightResult.Proceed.StoredOwnershipAuthorization"/>
        public sealed record Proceed(
            RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization,
            RelationalCustomViewAuthorization? StoredCustomViewAuthorization,
            RelationalOwnershipAuthorization? StoredOwnershipAuthorization,
            RelationshipAuthorizationResult StoredRelationshipAuthorization
        ) : DeleteAuthorizationPreflightResult;
    }

    public async Task<QueryResult> QueryDocuments(
        IQueryRequest queryRequest,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(queryRequest);

        var resource = RelationalWriteSupport.ToQualifiedResourceName(queryRequest.ResourceInfo);

        if (queryRequest.MappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await QueryDocumentsRelationalAsync(queryRequest, cancellationToken).ConfigureAwait(false);
        }

        // Cursor pages select their keyset through the relational path rather than read acceleration. A
        // cursor walk depends on every page reporting the selected-keyset boundary its successor resumes
        // from, and only traditional paging is exercised against the read-acceleration path, so cursor
        // selection keeps to the path whose boundary reporting is covered end to end.
        if (queryRequest.Paging is CollectionPaging.Cursor)
        {
            return await QueryDocumentsRelationalAsync(queryRequest, cancellationToken).ConfigureAwait(false);
        }

        return await _readAccelerationCoordinator
            .QueryAsync(
                new DocumentCacheReadAccelerationQueryRequest(
                    queryRequest.TenantKey,
                    queryRequest.MappingSet,
                    resource,
                    DocumentCacheReadAccelerationResourceKind.Resource,
                    fallbackCancellationToken =>
                        QueryDocumentsRelationalAsync(queryRequest, fallbackCancellationToken),
                    selectionCancellationToken =>
                        SelectQueryReadAccelerationCandidatePageAsync(
                            queryRequest,
                            selectionCancellationToken
                        )
                )
                {
                    ReadableProfileProjectionContext = queryRequest.ReadableProfileProjectionContext,
                    ResponseContentCoding = queryRequest.ResponseContentCoding,
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async Task<QueryResult> QueryDocumentsRelationalAsync(
        IQueryRequest queryRequest,
        CancellationToken cancellationToken = default
    )
    {
        var mappingSet = queryRequest.MappingSet;
        var resource = RelationalWriteSupport.ToQualifiedResourceName(queryRequest.ResourceInfo);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.QueryDocuments - {TraceId}",
            queryRequest.TraceId.Value
        );

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await _descriptorReadHandler
                .HandleQueryAsync(
                    new DescriptorQueryRequest(
                        mappingSet,
                        resource,
                        queryRequest.QueryElements,
                        queryRequest.Paging,
                        queryRequest.AuthorizationStrategyEvaluators,
                        queryRequest.ReadableProfileProjectionContext,
                        queryRequest.TraceId,
                        queryRequest.PageOrderingMode,
                        queryRequest.AuthorizationContext,
                        queryRequest.ChangeVersionRange,
                        queryRequest.ResponseContentCoding,
                        queryRequest.TenantKey
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        RelationalQueryPreparationResult preparationResult = await PrepareQueryReadAsync(
                queryRequest,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (preparationResult is RelationalQueryPreparationResult.Complete complete)
        {
            return complete.Result;
        }

        var preparation = ((RelationalQueryPreparationResult.Prepared)preparationResult).Preparation;

        HydratedPage hydratedPage;

        try
        {
            hydratedPage = await _documentHydrator
                .HydrateAsync(
                    preparation.ReadPlan,
                    preparation.PlannedQuery,
                    new HydrationExecutionOptions(),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        // Trade-off: a provider error raised while executing a custom-view page query is intentionally
        // relabeled as a custom-view validation failure, even though not every such error originates in
        // the view. Validation above already proved the views resolve, so the alternative is letting the
        // DbException escape into the non-ProblemDetails unhandled path and lose the public
        // urn:ed-fi:api:system contract this failure is documented to carry.
        catch (DbException ex) when (preparation.Authorization?.CustomViewChecks is { Count: > 0 })
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }

        return BuildQuerySuccess(queryRequest, preparation.Resource, preparation.ReadPlan, hydratedPage);
    }

    private async Task<RelationalQueryPreparationResult> PrepareQueryReadAsync(
        IQueryRequest queryRequest,
        CancellationToken cancellationToken
    )
    {
        var mappingSet = queryRequest.MappingSet;
        var resource = RelationalWriteSupport.ToQualifiedResourceName(queryRequest.ResourceInfo);

        RelationalQueryCapability queryCapability;

        try
        {
            queryCapability = queryRequest.MappingSet.GetQueryCapabilityOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return new RelationalQueryPreparationResult.Complete(
                new QueryResult.QueryFailureNotImplemented(ex.Message)
            );
        }
        catch (MissingQueryCapabilityLookupGuardRailException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }

        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            queryRequest.AuthorizationStrategyEvaluators
        );
        var authorizationResolution = await ResolveQueryAuthorization(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            queryRequest.AuthorizationContext,
            queryRequest.Paging.IncludesTotalCount,
            cancellationToken
        );

        PageDocumentIdAuthorizationSpec? pageQueryAuthorization;

        switch (authorizationResolution)
        {
            case QueryAuthorizationResolution.Complete complete:
                return new RelationalQueryPreparationResult.Complete(complete.Result);

            case QueryAuthorizationResolution.Proceed proceed:
                pageQueryAuthorization = proceed.Authorization;
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported query authorization resolution '{authorizationResolution.GetType().Name}'."
                );
        }

        RelationalQueryPreprocessingResult preprocessingResult;

        try
        {
            preprocessingResult = await RelationalQueryRequestPreprocessor
                .PreprocessAsync(
                    mappingSet,
                    resource,
                    queryRequest.QueryElements,
                    queryCapability,
                    _referenceResolver,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }

        if (preprocessingResult.Outcome is RelationalQueryPreprocessingOutcome.EmptyPage)
        {
            await ValidateAdaptedCustomViewsAsync(
                    mappingSet,
                    pageQueryAuthorization?.CustomViewChecks,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return new RelationalQueryPreparationResult.Complete(
                new QueryResult.QuerySuccess([], queryRequest.Paging.IncludesTotalCount ? 0 : null)
                {
                    SelectionSkipped = true,
                }
            );
        }

        ResourceReadPlan readPlan;

        try
        {
            readPlan = mappingSet.GetReadPlanOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }

        PageKeysetSpec.Query? plannedQuery;

        // Read off the request rather than resolved here, so the ordering the page is selected with is
        // the ordering Core stamped on the token it will hand back. It is handed to the planner and
        // then read back off the planned keyset wherever the selected-keyset boundary is interpreted,
        // rather than carried alongside it, so the anchor and the column list cannot disagree.
        PageOrderingMode orderingMode = queryRequest.PageOrderingMode;

        try
        {
            var planner = new RelationalQueryPageKeysetPlanner(mappingSet.Key.Dialect);

            if (
                !planner.TryPlan(
                    readPlan.Model.Root,
                    preprocessingResult,
                    queryRequest.Paging,
                    out plannedQuery,
                    out _,
                    authorization: pageQueryAuthorization,
                    changeVersionRange: queryRequest.ChangeVersionRange,
                    orderingMode: orderingMode
                ) || plannedQuery is null
            )
            {
                await ValidateAdaptedCustomViewsAsync(
                        mappingSet,
                        pageQueryAuthorization?.CustomViewChecks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return new RelationalQueryPreparationResult.Complete(
                    new QueryResult.QuerySuccess([], queryRequest.Paging.IncludesTotalCount ? 0 : null)
                    {
                        SelectionSkipped = true,
                    }
                );
            }
        }
        catch (NotSupportedException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return new RelationalQueryPreparationResult.Complete(new QueryResult.UnknownFailure(ex.Message));
        }

        // Fail closed when the planned page query would bind more parameters than SQL Server allows. Keyed
        // off the final planned parameter count so it covers every empty-page short-circuit and reflects
        // the exact command rather than an estimate.
        var nonAuthorizationParameterCount =
            plannedQuery.ParameterValues.Count
            - AuthorizationParameterBudget.CountAuthorizationParameters(
                pageQueryAuthorization?.NamespacePrefixParameterization,
                pageQueryAuthorization?.ClaimEducationOrganizationIdParameterization
            );

        await ValidateAdaptedCustomViewsAsync(
                mappingSet,
                pageQueryAuthorization?.CustomViewChecks,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (
            BuildQueryParameterBudgetFailure(
                mappingSet.Key.Dialect,
                resource,
                pageQueryAuthorization?.NamespacePrefixParameterization,
                pageQueryAuthorization?.ClaimEducationOrganizationIdParameterization,
                nonAuthorizationParameterCount
            ) is
            { } parameterBudgetFailure
        )
        {
            return new RelationalQueryPreparationResult.Complete(parameterBudgetFailure);
        }

        return new RelationalQueryPreparationResult.Prepared(
            new RelationalQueryPreparation(resource, readPlan, plannedQuery, pageQueryAuthorization)
        );
    }

    public Task<PartitionResult> QueryPartitions(
        IPartitionRequest partitionRequest,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(partitionRequest);

        var mappingSet = partitionRequest.MappingSet;
        var resource = RelationalWriteSupport.ToQualifiedResourceName(partitionRequest.ResourceInfo);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.QueryPartitions - {TraceId}",
            LoggingSanitizer.SanitizeForLogging(partitionRequest.TraceId.Value)
        );

        // Read acceleration is not consulted on this path, for either resource kind. The cache holds
        // hydrated documents and the candidate pages that selected them; a boundary calculation ranges
        // over the whole authorized candidate relation and hydrates nothing, so there is no cached
        // artifact it could be served from and no candidate page for it to admit.
        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return _descriptorReadHandler.HandlePartitionsAsync(
                new DescriptorPartitionRequest(
                    mappingSet,
                    resource,
                    partitionRequest.QueryElements,
                    partitionRequest.AuthorizationStrategyEvaluators,
                    partitionRequest.RequestedPartitionCount,
                    partitionRequest.MinimumPartitionSize,
                    partitionRequest.TraceId,
                    partitionRequest.PageOrderingMode,
                    partitionRequest.AuthorizationContext,
                    partitionRequest.ChangeVersionRange,
                    partitionRequest.TenantKey
                ),
                cancellationToken
            );
        }

        return QueryPartitionsRelationalAsync(partitionRequest, mappingSet, resource, cancellationToken);
    }

    private async Task<PartitionResult> QueryPartitionsRelationalAsync(
        IPartitionRequest partitionRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        CancellationToken cancellationToken
    )
    {
        RelationalPartitionPreparationResult preparationResult = await PreparePartitionReadAsync(
                partitionRequest,
                mappingSet,
                resource,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (preparationResult is RelationalPartitionPreparationResult.Complete complete)
        {
            return complete.Result;
        }

        var preparation = ((RelationalPartitionPreparationResult.Prepared)preparationResult).Preparation;

        IReadOnlyList<long> ascendingStarts;

        try
        {
            ascendingStarts = await PartitionBoundaryCommand
                .ExecuteAsync(
                    _commandExecutor,
                    preparation.PartitionPlan,
                    "Partition boundary selection",
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        // Trade-off: a provider error raised while executing a custom-view boundary statement is
        // intentionally relabeled as a custom-view validation failure, even though not every such error
        // originates in the view. This mirrors the page path so the two operations report the same public
        // urn:ed-fi:api:system contract for the same condition.
        catch (DbException ex) when (preparation.Authorization?.CustomViewChecks is { Count: > 0 })
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }

        try
        {
            return new PartitionResult.PartitionSuccess(
                PartitionRangeAssembler.ToInclusiveRanges(ascendingStarts)
            );
        }
        // Non-ascending starts mean the compiled statement changed, not that a client sent something
        // unusual. Reporting it keeps a corrupted boundary set from reaching a client as a walkable one.
        catch (ArgumentException ex)
        {
            return new PartitionResult.UnknownPartitionFailure(ex.Message);
        }
    }

    /// <summary>
    /// Resolves capability and authorization, preprocesses the filter, plans the authorized candidate
    /// relation, and compiles the boundary statement over it — or reports the outcome that stops the
    /// request.
    /// </summary>
    /// <remarks>
    /// Every seam is the one <see cref="PrepareQueryReadAsync" /> uses, in the same order, so a boundary
    /// set is calculated over exactly the rows the equivalent GET-many would page: the same capability
    /// lookup, the same authorization resolution and its custom-view ordering, the same preprocessor, the
    /// same page keyset planner, and the same command parameter budget. The planner is asked for the
    /// unpaged candidate relation rather than a page, which is the only difference.
    /// </remarks>
    private async Task<RelationalPartitionPreparationResult> PreparePartitionReadAsync(
        IPartitionRequest partitionRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        CancellationToken cancellationToken
    )
    {
        RelationalQueryCapability queryCapability;

        try
        {
            queryCapability = mappingSet.GetQueryCapabilityOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return PartitionComplete(new PartitionResult.PartitionFailureNotImplemented(ex.Message));
        }
        catch (MissingQueryCapabilityLookupGuardRailException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }

        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            partitionRequest.AuthorizationStrategyEvaluators
        );
        var authorizationResolution = await ResolveQueryAuthorization(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            partitionRequest.AuthorizationContext,
            // A boundary set carries no count, so the shared resolution's empty terminals must not be
            // asked to produce one.
            totalCount: false,
            cancellationToken
        );

        PageDocumentIdAuthorizationSpec? partitionAuthorization;

        switch (authorizationResolution)
        {
            case QueryAuthorizationResolution.Complete complete:
                return PartitionComplete(RelationalPartitionResultMapping.FromQueryResult(complete.Result));

            case QueryAuthorizationResolution.Proceed proceed:
                partitionAuthorization = proceed.Authorization;
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported query authorization resolution '{authorizationResolution.GetType().Name}'."
                );
        }

        RelationalQueryPreprocessingResult preprocessingResult;

        try
        {
            preprocessingResult = await RelationalQueryRequestPreprocessor
                .PreprocessAsync(
                    mappingSet,
                    resource,
                    partitionRequest.QueryElements,
                    queryCapability,
                    _referenceResolver,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }

        if (preprocessingResult.Outcome is RelationalQueryPreprocessingOutcome.EmptyPage)
        {
            await ValidateAdaptedCustomViewsAsync(
                    mappingSet,
                    partitionAuthorization?.CustomViewChecks,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return PartitionComplete(new PartitionResult.PartitionSuccess([]) { SelectionSkipped = true });
        }

        ResourceReadPlan readPlan;

        try
        {
            readPlan = mappingSet.GetReadPlanOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }

        PartitionWindowPlan partitionPlan;

        try
        {
            var planner = new RelationalQueryPageKeysetPlanner(mappingSet.Key.Dialect);

            if (
                !planner.TryPlanCandidates(
                    readPlan.Model.Root,
                    preprocessingResult,
                    out var plannedCandidates,
                    out _,
                    comparisonOperatorResolver: null,
                    authorization: partitionAuthorization,
                    changeVersionRange: partitionRequest.ChangeVersionRange,
                    orderingMode: partitionRequest.PageOrderingMode
                ) || plannedCandidates is null
            )
            {
                await ValidateAdaptedCustomViewsAsync(
                        mappingSet,
                        partitionAuthorization?.CustomViewChecks,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                return PartitionComplete(
                    new PartitionResult.PartitionSuccess([]) { SelectionSkipped = true }
                );
            }

            partitionPlan = new PartitionWindowPlanner(mappingSet.Key.Dialect).Plan(
                plannedCandidates,
                partitionRequest.RequestedPartitionCount,
                partitionRequest.MinimumPartitionSize
            );
        }
        catch (NotSupportedException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return PartitionComplete(new PartitionResult.UnknownPartitionFailure(ex.Message));
        }

        // Keyed off the compiled statement's own parameter values, so the budget reflects the exact
        // command rather than an estimate, including the two the boundary statement adds.
        var nonAuthorizationParameterCount =
            partitionPlan.ParameterValues.Count
            - AuthorizationParameterBudget.CountAuthorizationParameters(
                partitionAuthorization?.NamespacePrefixParameterization,
                partitionAuthorization?.ClaimEducationOrganizationIdParameterization
            );

        await ValidateAdaptedCustomViewsAsync(
                mappingSet,
                partitionAuthorization?.CustomViewChecks,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (
            BuildQueryParameterBudgetFailure(
                mappingSet.Key.Dialect,
                resource,
                partitionAuthorization?.NamespacePrefixParameterization,
                partitionAuthorization?.ClaimEducationOrganizationIdParameterization,
                nonAuthorizationParameterCount
            ) is
            { } parameterBudgetFailure
        )
        {
            return PartitionComplete(
                RelationalPartitionResultMapping.FromQueryResult(parameterBudgetFailure)
            );
        }

        return new RelationalPartitionPreparationResult.Prepared(
            new RelationalPartitionPreparation(partitionPlan, partitionAuthorization)
        );
    }

    private static RelationalPartitionPreparationResult PartitionComplete(PartitionResult result) =>
        new RelationalPartitionPreparationResult.Complete(result);

    private async Task<DocumentCacheReadAccelerationQuerySelectionResult> SelectQueryReadAccelerationCandidatePageAsync(
        IQueryRequest queryRequest,
        CancellationToken cancellationToken = default
    )
    {
        var mappingSet = queryRequest.MappingSet;
        RelationalQueryPreparationResult preparationResult = await PrepareQueryReadAsync(
                queryRequest,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (preparationResult is RelationalQueryPreparationResult.Complete complete)
        {
            return new DocumentCacheReadAccelerationQuerySelectionResult.Complete(complete.Result);
        }

        var preparation = ((RelationalQueryPreparationResult.Prepared)preparationResult).Preparation;

        DocumentCacheReadAccelerationCandidatePage candidatePage;

        try
        {
            candidatePage = await SelectDocumentCandidatePageAsync(
                    mappingSet,
                    preparation.Resource,
                    preparation.ReadPlan,
                    preparation.PlannedQuery,
                    queryRequest.Paging,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex) when (preparation.Authorization?.CustomViewChecks is { Count: > 0 })
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }

        if (candidatePage.IsEmpty)
        {
            return new DocumentCacheReadAccelerationQuerySelectionResult.Complete(
                new QueryResult.QuerySuccess(
                    [],
                    queryRequest.Paging.IncludesTotalCount
                        ? RelationalReadGuardrails.ConvertTotalCountOrThrow(
                            preparation.Resource,
                            candidatePage.TotalCount,
                            "query candidate selection"
                        )
                        : null,
                    candidatePage.HighestSelectedAnchor
                )
            );
        }

        return new DocumentCacheReadAccelerationQuerySelectionResult.CandidatePage(
            candidatePage,
            fallbackCancellationToken =>
                HydrateSelectedQueryCandidatePageAsync(
                    queryRequest,
                    preparation.Resource,
                    preparation.ReadPlan,
                    preparation.Authorization?.CustomViewChecks,
                    candidatePage,
                    fallbackCancellationToken
                )
        );
    }

    private async Task<QueryResult> HydrateSelectedQueryCandidatePageAsync(
        IQueryRequest queryRequest,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        DocumentCacheReadAccelerationCandidatePage candidatePage,
        CancellationToken cancellationToken
    )
    {
        var selectedDocumentIds = candidatePage
            .Candidates.Select(static candidate => candidate.DocumentId)
            .ToArray();

        HydratedPage hydratedPage;

        try
        {
            hydratedPage = await _documentHydrator
                .HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.SelectedPage(selectedDocumentIds),
                    new HydrationExecutionOptions(),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (DbException ex) when (customViewChecks is { Count: > 0 })
        {
            throw new CustomViewAuthorizationValidationException(ex);
        }

        // The boundary comes from the candidate selection, not from what hydration found, which is what
        // keeps a cache-accelerated page's continuation describing the keys selection chose.
        hydratedPage = hydratedPage with
        {
            TotalCount = candidatePage.TotalCount,
            HighestSelectedAnchor = candidatePage.HighestSelectedAnchor,
        };

        if (!SelectedQueryCandidatePageStillMatches(candidatePage, hydratedPage.DocumentMetadata))
        {
            return await QueryDocumentsRelationalAsync(queryRequest, cancellationToken).ConfigureAwait(false);
        }

        return BuildQuerySuccess(queryRequest, resource, readPlan, hydratedPage);
    }

    private static bool SelectedQueryCandidatePageStillMatches(
        DocumentCacheReadAccelerationCandidatePage candidatePage,
        IReadOnlyList<DocumentMetadataRow> hydratedMetadata
    )
    {
        if (candidatePage.Candidates.Count != hydratedMetadata.Count)
        {
            return false;
        }

        Dictionary<long, DocumentMetadataRow> hydratedMetadataByDocumentId = [];

        foreach (DocumentMetadataRow metadata in hydratedMetadata)
        {
            if (!hydratedMetadataByDocumentId.TryAdd(metadata.DocumentId, metadata))
            {
                return false;
            }
        }

        foreach (DocumentCacheReadAccelerationCandidate candidate in candidatePage.Candidates)
        {
            if (!hydratedMetadataByDocumentId.TryGetValue(candidate.DocumentId, out var metadata))
            {
                return false;
            }

            if (
                metadata.DocumentUuid != candidate.DocumentUuid.Value
                || metadata.ResourceKeyId != candidate.ResourceKeyId
                || metadata.ContentVersion != candidate.ContentVersion
                || metadata.ContentLastModifiedAt != candidate.ContentLastModifiedAt
            )
            {
                return false;
            }
        }

        return true;
    }

    private async Task<DocumentCacheReadAccelerationCandidatePage> SelectDocumentCandidatePageAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan,
        PageKeysetSpec.Query plannedQuery,
        CollectionPaging paging,
        CancellationToken cancellationToken
    )
    {
        var resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        var command = BuildQueryCandidateSelectionCommand(mappingSet, readPlan, plannedQuery);

        return await _commandExecutor
            .ExecuteReaderAsync(
                command,
                (reader, ct) =>
                    ReadDocumentCandidatePageAsync(
                        reader,
                        plannedQuery.Plan.TotalCountSql is not null,
                        paging,
                        // Asked of the same predicate the batch above emitted its column list from,
                        // rather than re-derived from the keyset's anchor here. Both answers would be
                        // the same today; only this one still is if that predicate ever narrows.
                        HydrationBatchBuilder.CarriesSelectedAnchor(plannedQuery),
                        resourceKeyId,
                        ct
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static RelationalCommand BuildQueryCandidateSelectionCommand(
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        PageKeysetSpec.Query plannedQuery
    )
    {
        var commandText = HydrationBatchBuilder.BuildCandidateMetadataBatch(
            readPlan,
            plannedQuery,
            mappingSet.Key.Dialect
        );

        return new RelationalCommand(commandText, BuildQueryCandidateSelectionParameters(plannedQuery));
    }

    private static IReadOnlyList<RelationalParameter> BuildQueryCandidateSelectionParameters(
        PageKeysetSpec.Query plannedQuery
    )
    {
        return
        [
            .. PlannedQueryParameterBinder
                .BindParameters(
                    plannedQuery.Plan,
                    plannedQuery.ParameterValues,
                    "Query candidate selection keyset",
                    "Query candidate selection parameter",
                    "Unsupported query candidate selection parameter binding kind."
                )
                .Select(static binding => new RelationalParameter(
                    binding.Name,
                    binding.Value,
                    binding.ConfigureParameter
                )),
        ];
    }

    private static async Task<DocumentCacheReadAccelerationCandidatePage> ReadDocumentCandidatePageAsync(
        IRelationalCommandReader reader,
        bool hasTotalCount,
        CollectionPaging paging,
        bool carriesSelectedAnchor,
        short resourceKeyId,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        // The keyset materialization returns the keys it selected, ahead of every other result set. The
        // boundary is taken from them rather than from the candidates below, so it stays the keys
        // selection chose even when a row is deleted before the metadata select reaches it.
        long? selectedMaximum = await ReadSelectedAnchorMaximumAsync(
                reader,
                carriesSelectedAnchor,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Expected a query candidate result set after the selected page keyset ids but no more result sets were available."
            );
        }

        long? totalCount = null;

        if (hasTotalCount)
        {
            totalCount = await ReadCandidatePageTotalCountAsync(reader, cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Expected query candidate metadata result set after total count but no more result sets were available."
                );
            }
        }

        var candidates = new List<DocumentCacheReadAccelerationCandidate>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(
                new DocumentCacheReadAccelerationCandidate(
                    reader.GetRequiredFieldValue<long>("DocumentId"),
                    new DocumentUuid(reader.GetRequiredFieldValue<Guid>("DocumentUuid")),
                    resourceKeyId,
                    reader.GetRequiredFieldValue<long>("ContentVersion"),
                    ReadCandidatePageDateTimeOffsetField(reader, "ContentLastModifiedAt")
                )
            );
        }

        return new DocumentCacheReadAccelerationCandidatePage(
            candidates,
            totalCount,
            selectedMaximum,
            IncludesTotalCount: paging.IncludesTotalCount
        );
    }

    /// <summary>
    /// Reads the selected page keyset from the current result set and returns the maximum value of its
    /// continuation anchor, or <see langword="null"/> when the selection was empty. Taken across every
    /// returned row because neither <c>RETURNING</c> nor <c>OUTPUT</c> promises an order.
    /// </summary>
    /// <remarks>
    /// A <c>ContentVersion</c>-anchored selection projects the anchor beside the ids. This is the
    /// read-acceleration twin of the hydration reader, over the candidate-metadata batch rather than the
    /// hydration batch, and it reports a shape disagreement the same way that twin does: a
    /// materialization that stopped carrying the anchor is a defect in this code, and naming it beats a
    /// bare ordinal fault raised from inside the row loop.
    /// <para>
    /// The anchor is located by name, from the same constant the batch builder projected it under, so
    /// the two cannot disagree about which column it is. <c>DocumentId</c> keeps its fixed ordinal: it
    /// is the first column of every keyset result set, anchored or not.
    /// </para>
    /// </remarks>
    private static async Task<long?> ReadSelectedAnchorMaximumAsync(
        IRelationalCommandReader reader,
        bool carriesAnchorColumn,
        CancellationToken cancellationToken
    )
    {
        var anchorOrdinal = SelectedDocumentIdOrdinal;

        if (carriesAnchorColumn)
        {
            try
            {
                anchorOrdinal = reader.GetOrdinal(HydrationSqlConventions.SelectedAnchorColumnName);
            }
            catch (IndexOutOfRangeException ex)
            {
                throw new InvalidOperationException(
                    "Expected the selected page keyset result set to carry the continuation anchor as its "
                        + $"'{HydrationSqlConventions.SelectedAnchorColumnName}' column, but it carries no "
                        + "such column. The materialization SQL and this reader disagree about the keyset "
                        + "shape.",
                    ex
                );
            }
        }

        long? selectedMaximum = null;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var selectedAnchor = reader.GetFieldValue<long>(anchorOrdinal);

            if (selectedMaximum is null || selectedAnchor > selectedMaximum)
            {
                selectedMaximum = selectedAnchor;
            }
        }

        return selectedMaximum;
    }

    /// <summary>
    /// <c>DocumentId</c>'s ordinal in the selected page keyset result set. Always first, on an anchored
    /// page and an unanchored one alike; the anchor beside it is located by name instead.
    /// </summary>
    private const int SelectedDocumentIdOrdinal = 0;

    private static async Task<long> ReadCandidatePageTotalCountAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Expected a query candidate total count row but none was returned."
            );
        }

        var totalCountValue = reader.GetFieldValue<object>(0);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Query candidate total count result set returned multiple rows."
            );
        }

        return Convert.ToInt64(totalCountValue, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ReadCandidatePageDateTimeOffsetField(
        IRelationalCommandReader reader,
        string columnName
    )
    {
        var value = reader.GetFieldValue<object>(reader.GetOrdinal(columnName));

        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime
            ),
            string text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(
                $"Query candidate selection expected a DateTimeOffset-compatible value for dms.Document.{columnName}, "
                    + $"but received '{value.GetType().Name}'."
            ),
        };
    }

    private async Task<QueryAuthorizationResolution> ResolveQueryAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext,
        bool totalCount,
        CancellationToken cancellationToken
    )
    {
        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            NamespaceAuthorizationOperation.ReadMany,
            configuredAuthorizationStrategies,
            authorizationContext
        );

        switch (orchestratorOutcome)
        {
            case RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot:
                if (
                    CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
                        noUsableRoot.CustomViewStrategies,
                        noUsableRoot.RawConfiguredIndex
                    )
                        is { Count: > 0 } customViewsBeforeNoUsableRoot
                    && await ValidateCustomViewsAsync(
                        customViewsBeforeNoUsableRoot,
                        mappingSet,
                        resource,
                        cancellationToken
                    )
                        is { } customViewFailureBeforeNoUsableRoot
                )
                {
                    return new QueryAuthorizationResolution.Complete(customViewFailureBeforeNoUsableRoot);
                }

                return new QueryAuthorizationResolution.Complete(
                    new QueryResult.QueryFailureSecurityConfiguration(
                        [
                            NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                                RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                            ),
                        ],
                        RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                    )
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                if (
                    CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
                        noPrefixes.CustomViewStrategies,
                        noPrefixes.RawConfiguredIndex
                    )
                        is { Count: > 0 } customViewsBeforeNoPrefixes
                    && await ValidateCustomViewsAsync(
                        customViewsBeforeNoPrefixes,
                        mappingSet,
                        resource,
                        cancellationToken
                    )
                        is { } customViewFailureBeforeNoPrefixes
                )
                {
                    return new QueryAuthorizationResolution.Complete(customViewFailureBeforeNoPrefixes);
                }

                return new QueryAuthorizationResolution.Complete(
                    new QueryResult.QueryFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    )
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return await ResolveClassifiedQueryRelationshipAuthorization(
                    mappingSet,
                    resource,
                    securityConfigurationError.NonNamespaceConfiguredStrategies,
                    securityConfigurationError.RelationshipClassification,
                    authorizationContext,
                    totalCount,
                    cancellationToken
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return await ResolveClassifiedQueryRelationshipAuthorization(
                    mappingSet,
                    resource,
                    stillUnsupported.NonNamespaceConfiguredStrategies,
                    stillUnsupported.RelationshipClassification,
                    authorizationContext,
                    totalCount,
                    cancellationToken
                );

            case RelationalAuthorizationPlanOutcome.Plan plan:
                return await ResolveQueryPlanAuthorization(
                    mappingSet,
                    resource,
                    plan,
                    authorizationContext,
                    totalCount,
                    cancellationToken
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
                );
        }
    }

    private async Task<QueryAuthorizationResolution> ResolveClassifiedQueryRelationshipAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationshipAuthorizationClassification relationshipClassification,
        RelationalAuthorizationContext authorizationContext,
        bool totalCount,
        CancellationToken cancellationToken
    )
    {
        // Every resolved custom view is excluded from the relationship bucket regardless of configured
        // position, so a custom view that will not be validated below is not re-classified as a relationship
        // strategy instead.
        var customViewStrategyRawIndexes = relationshipClassification
            .SupportedCustomViewStrategies.Select(static strategy =>
                strategy.ConfiguredStrategy.RawConfiguredIndex
            )
            .ToHashSet();
        IReadOnlyList<ConfiguredAuthorizationStrategy> relationshipConfiguredStrategies =
        [
            .. nonNamespaceConfiguredStrategies.Where(strategy =>
                !customViewStrategyRawIndexes.Contains(strategy.RawConfiguredIndex)
            ),
        ];

        // OwnershipBased — the only known-but-not-enabled strategy — executes last per auth.md "Execution
        // order", regardless of where the CMS configured it. Every resolved custom view therefore precedes
        // its 501 terminal and is validated first. A classifier security-configuration failure (500) is
        // different: it is not an ordered AND term but a defect in the strategy metadata itself, so only
        // custom views configured ahead of it may run.
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategiesToValidate =
            relationshipClassification.SecurityConfigurationFailures.Count == 0
                ? relationshipClassification.SupportedCustomViewStrategies
                : CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
                    relationshipClassification.SupportedCustomViewStrategies,
                    RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                        relationshipClassification.SecurityConfigurationFailures
                    )
                );

        return await ResolveQueryRelationshipAuthorization(
            mappingSet,
            resource,
            relationshipConfiguredStrategies,
            authorizationContext,
            totalCount,
            [],
            null,
            customViewStrategiesToValidate,
            cancellationToken
        );
    }

    private async Task<QueryAuthorizationResolution> ResolveQueryPlanAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.Plan plan,
        RelationalAuthorizationContext authorizationContext,
        bool totalCount,
        CancellationToken cancellationToken
    )
    {
        if (plan.NamespaceChecks.Count == 0)
        {
            return await ResolveQueryRelationshipAuthorization(
                mappingSet,
                resource,
                plan.NonNamespaceConfiguredStrategies,
                authorizationContext,
                totalCount,
                [],
                null,
                plan.CustomViewStrategies,
                cancellationToken
            );
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage,
                out var securityConfigurationDiagnostics
            )
        )
        {
            if (
                CustomViewAuthorizationTerminalOrdering.CustomViewsBeforeTerminal(
                    plan.CustomViewStrategies,
                    plan.NamespaceChecks[0].RawConfiguredIndex
                )
                    is { Count: > 0 } customViewStrategiesToValidate
                && await ValidateCustomViewsAsync(
                    customViewStrategiesToValidate,
                    mappingSet,
                    resource,
                    cancellationToken
                )
                    is { } customViewFailureBeforeNamespaceTerminal
            )
            {
                return new QueryAuthorizationResolution.Complete(customViewFailureBeforeNamespaceTerminal);
            }

            return new QueryAuthorizationResolution.Complete(
                new QueryResult.QueryFailureSecurityConfiguration(
                    [securityConfigurationMessage],
                    securityConfigurationDiagnostics
                )
            );
        }

        return await ResolveQueryRelationshipAuthorization(
            mappingSet,
            resource,
            plan.NonNamespaceConfiguredStrategies,
            authorizationContext,
            totalCount,
            plan.NamespaceChecks,
            namespacePrefixParameterization,
            plan.CustomViewStrategies,
            cancellationToken
        );
    }

    private async Task<QueryAuthorizationResolution> ResolveQueryRelationshipAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext,
        bool totalCount,
        IReadOnlyList<NamespaceAuthorizationCheckSpec> namespaceChecks,
        NamespacePrefixParameterization? namespacePrefixParameterization,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? adaptedCustomViewChecks = null;

        if (customViewStrategies.Count > 0)
        {
            var customViewPlanOutcome = CustomViewAuthorizationPlanner.Plan(
                mappingSet,
                mappingSet.GetConcreteResourceModelOrThrow(resource),
                customViewStrategies
            );

            if (customViewPlanOutcome is CustomViewAuthorizationPlanOutcome.SecurityConfiguration sc)
            {
                // Validate the custom views that planned successfully and are configured ahead of the
                // earliest planning failure first: an earlier missing or non-conforming auth view must
                // surface its own error rather than being hidden by this later planning failure.
                await ValidatePlannedCustomViewsBeforeFailureAsync(mappingSet, sc, cancellationToken)
                    .ConfigureAwait(false);

                return new QueryAuthorizationResolution.Complete(
                    BuildQueryAuthorizationSecurityConfigurationFailure(mappingSet, resource, sc.Failures)
                );
            }

            var plan = (CustomViewAuthorizationPlanOutcome.Plan)customViewPlanOutcome;
            adaptedCustomViewChecks = PageDocumentIdCustomViewAdapter.AdaptFromChecks(plan.Checks);
        }

        var relationshipAuthorizationResult = _relationshipAuthorizationPlanner.PlanStoredValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext
        );

        switch (relationshipAuthorizationResult)
        {
            case RelationshipAuthorizationResult.NoAuthorizationRequired:
            case RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                return new QueryAuthorizationResolution.Proceed(
                    ComposePageQueryAuthorization(
                        null,
                        namespaceChecks,
                        namespacePrefixParameterization,
                        adaptedCustomViewChecks
                    )
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                return new QueryAuthorizationResolution.Proceed(
                    ComposePageQueryAuthorization(
                        PageDocumentIdAuthorizationSpecAdapter.Adapt(authorized),
                        namespaceChecks,
                        namespacePrefixParameterization,
                        adaptedCustomViewChecks
                    )
                );

            case RelationshipAuthorizationResult.NoClaims:
                return await ResolveRelationshipTerminalAfterCustomViewValidation(
                    mappingSet,
                    adaptedCustomViewChecks,
                    new QueryAuthorizationResolution.Complete(
                        new QueryResult.QuerySuccess([], totalCount ? 0 : null) { SelectionSkipped = true }
                    ),
                    cancellationToken
                );

            case RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled:
                return await ResolveRelationshipTerminalAfterCustomViewValidation(
                    mappingSet,
                    adaptedCustomViewChecks,
                    new QueryAuthorizationResolution.Complete(
                        new QueryResult.QueryFailureNotImplemented(
                            BuildKnownButNotEnabledQueryAuthorizationMessage(
                                resource,
                                knownButNotEnabled.Failures
                            )
                        )
                    ),
                    cancellationToken
                );

            case RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError:
                return await ResolveRelationshipTerminalAfterCustomViewValidation(
                    mappingSet,
                    adaptedCustomViewChecks,
                    new QueryAuthorizationResolution.Complete(
                        BuildQueryAuthorizationSecurityConfigurationFailure(
                            mappingSet,
                            resource,
                            securityConfigurationError.Failures
                        )
                    ),
                    cancellationToken
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported relationship authorization result '{relationshipAuthorizationResult.GetType().Name}'."
                );
        }
    }

    private async Task<QueryAuthorizationResolution> ResolveRelationshipTerminalAfterCustomViewValidation(
        MappingSet mappingSet,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        QueryAuthorizationResolution terminalResolution,
        CancellationToken cancellationToken
    )
    {
        await ValidateAdaptedCustomViewsAsync(mappingSet, customViewChecks, cancellationToken)
            .ConfigureAwait(false);
        return terminalResolution;
    }

    private async Task ValidateAdaptedCustomViewsAsync(
        MappingSet mappingSet,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks,
        CancellationToken cancellationToken
    )
    {
        if (customViewChecks is null || customViewChecks.Count == 0)
        {
            return;
        }

        await CustomViewAuthorizationValidator
            .ValidateAsync(_commandExecutor, mappingSet.Key.Dialect, customViewChecks, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the custom views that planned successfully and are configured ahead of the earliest
    /// custom-view planning failure. Preserves CMS-configured AND order: an earlier missing or
    /// non-conforming <c>auth.{StrategyName}</c> raises its own validation failure instead of being masked by
    /// a later strategy's planning failure. Custom views configured after that failure are not validated.
    /// </summary>
    private async Task ValidatePlannedCustomViewsBeforeFailureAsync(
        MappingSet mappingSet,
        CustomViewAuthorizationPlanOutcome.SecurityConfiguration securityConfiguration,
        CancellationToken cancellationToken
    )
    {
        var checksBeforeFailure = CustomViewAuthorizationTerminalOrdering.ChecksBeforeTerminal(
            securityConfiguration.PlannedChecks,
            RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                securityConfiguration.Failures
            )
        );

        await ValidateAdaptedCustomViewsAsync(
                mappingSet,
                PageDocumentIdCustomViewAdapter.AdaptFromChecks(checksBeforeFailure),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Plans and validates <paramref name="customViewStrategies"/>. Returns the terminal
    /// <see cref="QueryResult"/> when planning fails, or <see langword="null"/> when every custom view
    /// planned and validated, leaving the caller to continue to its own terminal.
    /// </summary>
    private async Task<QueryResult?> ValidateCustomViewsAsync(
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        CancellationToken cancellationToken
    )
    {
        CustomViewAuthorizationPlanOutcome customViewOutcome = CustomViewAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            customViewStrategies
        );

        if (customViewOutcome is CustomViewAuthorizationPlanOutcome.SecurityConfiguration customViewSecurity)
        {
            await ValidatePlannedCustomViewsBeforeFailureAsync(
                    mappingSet,
                    customViewSecurity,
                    cancellationToken
                )
                .ConfigureAwait(false);

            return BuildQueryAuthorizationSecurityConfigurationFailure(
                mappingSet,
                resource,
                customViewSecurity.Failures
            );
        }

        await ValidateAdaptedCustomViewsAsync(
                mappingSet,
                PageDocumentIdCustomViewAdapter.AdaptFromChecks(
                    ((CustomViewAuthorizationPlanOutcome.Plan)customViewOutcome).Checks
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return null;
    }

    /// <summary>
    /// Returns a security-configuration failure when the authorization parameters this query binds, plus
    /// its filter and paging parameters, exceed SQL Server's per-command parameter ceiling; otherwise
    /// <see langword="null"/>. Either authorization parameterization may be <see langword="null"/>, so this
    /// covers the namespace-only, relationship-only, and composed query shapes uniformly.
    /// </summary>
    private static QueryResult? BuildQueryParameterBudgetFailure(
        SqlDialect dialect,
        QualifiedResourceName resource,
        NamespacePrefixParameterization? namespacePrefixParameterization,
        AuthorizationClaimEducationOrganizationIdParameterization? claimEducationOrganizationIdParameterization,
        int nonAuthorizationParameterCount
    )
    {
        if (
            !AuthorizationParameterBudget.ExceedsCommandParameterLimit(
                dialect,
                namespacePrefixParameterization,
                claimEducationOrganizationIdParameterization,
                nonAuthorizationParameterCount
            )
        )
        {
            return null;
        }

        return new QueryResult.QueryFailureSecurityConfiguration(
            [
                NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                    namespacePrefixParameterization?.ConfiguredPrefixesInOrder.Count ?? 0,
                    claimEducationOrganizationIdParameterization?.ClaimEducationOrganizationIds.Count ?? 0,
                    nonAuthorizationParameterCount
                ),
            ],
            AuthorizationSecurityConfigurationDiagnostics.ForCommandParameterCapExceeded(resource)
        );
    }

    private static PageDocumentIdAuthorizationSpec? ComposePageQueryAuthorization(
        PageDocumentIdAuthorizationSpec? relationshipAuthorization,
        IReadOnlyList<NamespaceAuthorizationCheckSpec> namespaceChecks,
        NamespacePrefixParameterization? namespacePrefixParameterization,
        IReadOnlyList<PageDocumentIdAuthorizationCustomViewCheck>? customViewChecks = null
    )
    {
        if (namespaceChecks.Count == 0 && (customViewChecks is null || customViewChecks.Count == 0))
        {
            return relationshipAuthorization;
        }

        return (relationshipAuthorization ?? new PageDocumentIdAuthorizationSpec([])) with
        {
            NamespaceChecks = namespaceChecks,
            NamespacePrefixParameterization = namespacePrefixParameterization,
            CustomViewChecks = customViewChecks,
        };
    }

    private abstract record QueryAuthorizationResolution
    {
        private QueryAuthorizationResolution() { }

        public sealed record Proceed(PageDocumentIdAuthorizationSpec? Authorization)
            : QueryAuthorizationResolution;

        public sealed record Complete(QueryResult Result) : QueryAuthorizationResolution;
    }

    private WriteGuardRailPreflightResult<UpsertResult> AuthorizePostRelationshipIfRequired(
        IUpsertRequest relationalUpsertRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceWritePlan writePlan
    )
    {
        var authorizationContext = relationalUpsertRequest.AuthorizationContext;
        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            relationalUpsertRequest.AuthorizationStrategyEvaluators
        );

        // A POST may resolve to create or upsert-as-update in-session, so plan both the stored and
        // proposed namespace checks here; the executor applies the stored check only when the write
        // resolves to an existing target.
        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            NamespaceAuthorizationOperation.Update,
            configuredAuthorizationStrategies,
            authorizationContext
        );

        switch (orchestratorOutcome)
        {
            case RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot:
                return WriteTerminal<UpsertResult>(
                    mappingSet,
                    resource,
                    new UpsertResult.UpsertFailureSecurityConfiguration(
                        [
                            NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                                RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                            ),
                        ],
                        RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                    ),
                    noUsableRoot.CustomViewStrategies,
                    noUsableRoot.RawConfiguredIndex,
                    failures => BuildPostAuthorizationSecurityConfigurationFailure(mappingSet, failures)
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return WriteTerminal<UpsertResult>(
                    mappingSet,
                    resource,
                    new UpsertResult.UpsertFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    ),
                    noPrefixes.CustomViewStrategies,
                    noPrefixes.RawConfiguredIndex,
                    failures => BuildPostAuthorizationSecurityConfigurationFailure(mappingSet, failures)
                );

            // The ownership token list reaches the defensive limit. A planner terminal, so it is reported
            // before the write session opens and after the namespace terminals; the planner has already
            // resolved custom-view configuration failures by not returning this outcome in that case.
            case RelationalAuthorizationPlanOutcome.OwnershipTokenCapExceeded ownershipTokenCapExceeded:
                return WriteTerminal<UpsertResult>(
                    mappingSet,
                    resource,
                    new UpsertResult.UpsertFailureSecurityConfiguration(
                        [
                            OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                                ownershipTokenCapExceeded.OwnershipTokenCount
                            ),
                        ],
                        AuthorizationSecurityConfigurationDiagnostics.ForOwnershipTokenParameterization(
                            AuthorizationSecurityConfigurationDiagnostics.OwnershipTokenCapExceeded
                        )
                    ),
                    ownershipTokenCapExceeded.CustomViewStrategies,
                    // Every configured view runs before this terminal: OwnershipBased executes last among
                    // the AND strategies whatever position it is configured at.
                    int.MaxValue,
                    failures => BuildPostAuthorizationSecurityConfigurationFailure(mappingSet, failures)
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return AuthorizePostRelationshipBucket(
                    mappingSet,
                    resource,
                    writePlan,
                    securityConfigurationError.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    storedNamespaceAuthorization: null,
                    proposedNamespaceAuthorization: null,
                    supportedCustomViewStrategies: securityConfigurationError
                        .RelationshipClassification
                        .SupportedCustomViewStrategies
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return AuthorizePostRelationshipBucket(
                    mappingSet,
                    resource,
                    writePlan,
                    stillUnsupported.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    storedNamespaceAuthorization: null,
                    proposedNamespaceAuthorization: null,
                    supportedCustomViewStrategies: stillUnsupported
                        .RelationshipClassification
                        .SupportedCustomViewStrategies
                );

            case RelationalAuthorizationPlanOutcome.Plan plan:
                return AuthorizePostPlan(mappingSet, resource, writePlan, plan, authorizationContext);

            default:
                throw new InvalidOperationException(
                    $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
                );
        }
    }

    private WriteGuardRailPreflightResult<UpsertResult> AuthorizePostPlan(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceWritePlan writePlan,
        RelationalAuthorizationPlanOutcome.Plan plan,
        RelationalAuthorizationContext authorizationContext
    )
    {
        // A POST may resolve to create or upsert-as-update in-session, so both value sources are planned
        // here; the executor applies the stored checks only when the write resolves to an existing target.
        if (
            !TryPlanWriteCustomViewAuthorization(
                mappingSet,
                resource,
                plan.CustomViewStrategies,
                out var customViewAuthorization,
                out var customViewFailures,
                out var customViewChecksBeforeFailure
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                BuildPostAuthorizationSecurityConfigurationFailure(mappingSet, customViewFailures!),
                customViewChecksBeforeFailure
            );
        }

        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization = null;
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization = null;

        if (plan.NamespaceChecks.Count > 0)
        {
            if (
                !NamespacePrefixParameterizationPreflight.TryCreate(
                    mappingSet.Key.Dialect,
                    authorizationContext.NamespacePrefixes,
                    out var namespacePrefixParameterization,
                    out var securityConfigurationMessage,
                    out var securityConfigurationDiagnostics
                )
            )
            {
                return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                    new UpsertResult.UpsertFailureSecurityConfiguration(
                        [securityConfigurationMessage],
                        securityConfigurationDiagnostics
                    ),
                    CustomViewChecksBeforeNamespaceCheck(customViewAuthorization, plan.NamespaceChecks)
                );
            }

            (storedNamespaceAuthorization, proposedNamespaceAuthorization) = SplitNamespaceAuthorization(
                plan.NamespaceChecks,
                namespacePrefixParameterization
            );
        }

        // After the namespace parameterization, as on every other path: both are setup failures reported as
        // the same security-configuration 500, and NamespaceBased executes ahead of OwnershipBased.
        if (
            !TryPlanStoredOwnershipAuthorization(
                mappingSet,
                plan.OwnershipCheck,
                authorizationContext,
                out var storedOwnershipAuthorization,
                out var ownershipSecurityConfigurationMessage,
                out var ownershipSecurityConfigurationDiagnostics
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                new UpsertResult.UpsertFailureSecurityConfiguration(
                    [ownershipSecurityConfigurationMessage],
                    ownershipSecurityConfigurationDiagnostics
                ),
                // Every configured view runs before this failure: OwnershipBased executes last among the
                // AND strategies whatever position it is configured at.
                AllCustomViewChecks(customViewAuthorization)
            );
        }

        return AuthorizePostRelationshipBucket(
            mappingSet,
            resource,
            writePlan,
            plan.NonNamespaceConfiguredStrategies,
            authorizationContext,
            storedNamespaceAuthorization,
            proposedNamespaceAuthorization,
            customViewAuthorization,
            storedOwnershipAuthorization
        );
    }

    /// <summary>
    /// Splits the planner's namespace checks into a stored-value authorization (evaluated in the
    /// locked-target boundary) and a proposed-value authorization (evaluated after merge). Each group
    /// is re-indexed from zero because the two are executed as independent single-record statements,
    /// so each carries its own AUTH1 payload index.
    /// </summary>
    private static (
        RelationalWriteNamespaceAuthorization? Stored,
        RelationalWriteNamespaceAuthorization? Proposed
    ) SplitNamespaceAuthorization(
        IReadOnlyList<NamespaceAuthorizationCheckSpec> namespaceChecks,
        NamespacePrefixParameterization namespacePrefixParameterization
    )
    {
        var stored = NamespaceAuthorizationFactory.SplitByValueSource(
            namespaceChecks,
            NamespaceAuthorizationCheckValueSource.Stored,
            namespacePrefixParameterization
        );
        var proposed = NamespaceAuthorizationFactory.SplitByValueSource(
            namespaceChecks,
            NamespaceAuthorizationCheckValueSource.Proposed,
            namespacePrefixParameterization
        );

        return (stored, proposed);
    }

    private WriteGuardRailPreflightResult<UpsertResult> AuthorizePostRelationshipBucket(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceWritePlan writePlan,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        RelationalCustomViewAuthorization? customViewAuthorization = null,
        RelationalOwnershipAuthorization? storedOwnershipAuthorization = null,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy>? supportedCustomViewStrategies = null
    )
    {
        supportedCustomViewStrategies ??= [];

        // The strategy-level SecurityConfigurationError and StillUnsupported arms reach this bucket before
        // any custom view has been planned, so plan them here rather than at each terminal below: every Stop
        // in this method then carries the views that execute ahead of it. Callers that already planned pass
        // customViewAuthorization instead and skip this.
        if (
            customViewAuthorization is null
            && supportedCustomViewStrategies.Count > 0
            && !TryPlanWriteCustomViewAuthorization(
                mappingSet,
                resource,
                supportedCustomViewStrategies,
                out customViewAuthorization,
                out var customViewFailures,
                out var customViewChecksBeforeFailure
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                BuildPostAuthorizationSecurityConfigurationFailure(mappingSet, customViewFailures!),
                customViewChecksBeforeFailure
            );
        }

        var existingResourcePlan = _relationshipAuthorizationPlanner.PlanUpdateValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext,
            writePlan
        );

        var securityConfigurationFailures = existingResourcePlan.SecurityConfigurationFailures;

        if (securityConfigurationFailures.Count > 0)
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                BuildPostAuthorizationSecurityConfigurationFailure(mappingSet, securityConfigurationFailures),
                ChecksBeforeRelationshipFailure(customViewAuthorization, securityConfigurationFailures)
            );
        }

        if (existingResourcePlan.KnownButNotEnabledFailures.Count > 0)
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                new UpsertResult.UpsertFailureNotImplemented(
                    BuildKnownButNotEnabledPostAuthorizationMessage(
                        resource,
                        existingResourcePlan.KnownButNotEnabledFailures
                    ),
                    UpsertFailureNotImplementedReason.StrategyNotEnabled
                ),
                AllCustomViewChecks(customViewAuthorization)
            );
        }

        return existingResourcePlan.ProposedValues switch
        {
            RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired =>
                new WriteGuardRailPreflightResult<UpsertResult>.Continue(
                    null,
                    null,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization,
                    customViewAuthorization: customViewAuthorization,
                    storedOwnershipAuthorization: storedOwnershipAuthorization
                ),

            RelationshipAuthorizationResult.Authorized => CreatePostRelationshipAuthorizationContinue(
                mappingSet,
                resource,
                nonNamespaceConfiguredStrategies,
                authorizationContext,
                writePlan,
                existingResourcePlan,
                storedNamespaceAuthorization,
                proposedNamespaceAuthorization,
                customViewAuthorization,
                storedOwnershipAuthorization
            ),

            // NamespaceBased and custom view-based both AND-compose before relationship OR strategies
            // (auth.md). When any of them is planned, defer NoClaims through Continue so those filters get to
            // deny first; the write path's second command emits the NoClaims failure only once they have
            // authorized. With no AND filter planned at all, short-circuit at preflight to avoid a needless
            // executor roundtrip.
            RelationshipAuthorizationResult.NoClaims noClaims => proposedNamespaceAuthorization is null
            && storedNamespaceAuthorization is null
            && customViewAuthorization is null
                ? BuildNoClaimsPostRelationshipAuthorizationFailure(noClaims, authorizationContext)
                : new WriteGuardRailPreflightResult<UpsertResult>.Continue(
                    null,
                    noClaims,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization,
                    customViewAuthorization: customViewAuthorization,
                    storedOwnershipAuthorization: storedOwnershipAuthorization
                ),

            RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled =>
                new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                    new UpsertResult.UpsertFailureNotImplemented(
                        BuildKnownButNotEnabledPostAuthorizationMessage(
                            resource,
                            knownButNotEnabled.Failures
                        ),
                        UpsertFailureNotImplementedReason.StrategyNotEnabled
                    ),
                    AllCustomViewChecks(customViewAuthorization)
                ),

            RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError =>
                new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                    BuildPostAuthorizationSecurityConfigurationFailure(
                        mappingSet,
                        securityConfigurationError.Failures
                    ),
                    ChecksBeforeRelationshipFailure(
                        customViewAuthorization,
                        securityConfigurationError.Failures
                    )
                ),

            _ => throw new InvalidOperationException(
                $"Unsupported relationship authorization result '{existingResourcePlan.ProposedValues.GetType().Name}'."
            ),
        };
    }

    private WriteGuardRailPreflightResult<UpsertResult> CreatePostRelationshipAuthorizationContinue(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext,
        ResourceWritePlan writePlan,
        RelationshipAuthorizationUpdatePlan existingResourcePlan,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        RelationalCustomViewAuthorization? customViewAuthorization = null,
        RelationalOwnershipAuthorization? storedOwnershipAuthorization = null
    )
    {
        var createNewProposedValues = _relationshipAuthorizationPlanner.PlanProposedValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext,
            writePlan
        );

        // Every deferral out of this method owes the executor the same namespace and custom-view plans, and
        // only the create-new relationship result varies. Building them here rather than at each arm keeps a
        // new arm from silently dropping a planned check by omitting a trailing argument.
        WriteGuardRailPreflightResult<UpsertResult> DeferToExecutor(
            RelationshipAuthorizationResult? createNewProposedRelationshipAuthorization,
            RelationalWriteExecutorResult? createNewImmediateResult = null
        ) =>
            new WriteGuardRailPreflightResult<UpsertResult>.Continue(
                null,
                null,
                storedNamespaceAuthorization,
                proposedNamespaceAuthorization,
                new PostRelationshipAuthorizationPlans(
                    existingResourcePlan,
                    createNewProposedRelationshipAuthorization,
                    createNewImmediateResult
                ),
                customViewAuthorization,
                storedOwnershipAuthorization
            );

        return createNewProposedValues switch
        {
            RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired => DeferToExecutor(null),

            RelationshipAuthorizationResult.Authorized createNewAuthorized => DeferToExecutor(
                createNewAuthorized
            ),

            // A pending custom view is an AND filter too, so it has to run before this denial is reported.
            RelationshipAuthorizationResult.NoClaims noClaims => proposedNamespaceAuthorization is null
            && customViewAuthorization is null
                ? BuildNoClaimsPostRelationshipAuthorizationFailure(noClaims, authorizationContext)
                : DeferToExecutor(noClaims),

            RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled =>
                new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                    new UpsertResult.UpsertFailureNotImplemented(
                        BuildKnownButNotEnabledPostAuthorizationMessage(
                            resource,
                            knownButNotEnabled.Failures
                        ),
                        UpsertFailureNotImplementedReason.StrategyNotEnabled
                    ),
                    AllCustomViewChecks(customViewAuthorization)
                ),

            RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError =>
                DeferToExecutor(
                    null,
                    new RelationalWriteExecutorResult.Upsert(
                        BuildPostAuthorizationSecurityConfigurationFailure(
                            mappingSet,
                            securityConfigurationError.Failures
                        )
                    )
                ),

            _ => throw new InvalidOperationException(
                $"Unsupported POST create-new relationship authorization result '{createNewProposedValues.GetType().Name}'."
            ),
        };
    }

    private WriteGuardRailPreflightResult<UpdateResult> AuthorizePutRelationshipIfRequired(
        IUpdateRequest relationalUpdateRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceWritePlan writePlan
    )
    {
        var authorizationContext = relationalUpdateRequest.AuthorizationContext;
        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            relationalUpdateRequest.AuthorizationStrategyEvaluators
        );

        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            NamespaceAuthorizationOperation.Update,
            configuredAuthorizationStrategies,
            authorizationContext
        );

        switch (orchestratorOutcome)
        {
            case RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot:
                return WriteTerminal<UpdateResult>(
                    mappingSet,
                    resource,
                    new UpdateResult.UpdateFailureSecurityConfiguration(
                        [
                            NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                                RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                            ),
                        ],
                        RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                    ),
                    noUsableRoot.CustomViewStrategies,
                    noUsableRoot.RawConfiguredIndex,
                    failures => BuildPutAuthorizationSecurityConfigurationFailure(mappingSet, failures)
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return WriteTerminal<UpdateResult>(
                    mappingSet,
                    resource,
                    new UpdateResult.UpdateFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    ),
                    noPrefixes.CustomViewStrategies,
                    noPrefixes.RawConfiguredIndex,
                    failures => BuildPutAuthorizationSecurityConfigurationFailure(mappingSet, failures)
                );

            // The ownership token list reaches the defensive limit. A planner terminal, so it is reported
            // before the write session opens and after the namespace terminals; the planner has already
            // resolved custom-view configuration failures by not returning this outcome in that case.
            case RelationalAuthorizationPlanOutcome.OwnershipTokenCapExceeded ownershipTokenCapExceeded:
                return WriteTerminal<UpdateResult>(
                    mappingSet,
                    resource,
                    new UpdateResult.UpdateFailureSecurityConfiguration(
                        [
                            OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                                ownershipTokenCapExceeded.OwnershipTokenCount
                            ),
                        ],
                        AuthorizationSecurityConfigurationDiagnostics.ForOwnershipTokenParameterization(
                            AuthorizationSecurityConfigurationDiagnostics.OwnershipTokenCapExceeded
                        )
                    ),
                    ownershipTokenCapExceeded.CustomViewStrategies,
                    // Every configured view runs before this terminal: OwnershipBased executes last among
                    // the AND strategies whatever position it is configured at.
                    int.MaxValue,
                    failures => BuildPutAuthorizationSecurityConfigurationFailure(mappingSet, failures)
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return AuthorizePutRelationshipBucket(
                    mappingSet,
                    resource,
                    writePlan,
                    securityConfigurationError.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    storedNamespaceAuthorization: null,
                    proposedNamespaceAuthorization: null,
                    supportedCustomViewStrategies: securityConfigurationError
                        .RelationshipClassification
                        .SupportedCustomViewStrategies
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return AuthorizePutRelationshipBucket(
                    mappingSet,
                    resource,
                    writePlan,
                    stillUnsupported.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    storedNamespaceAuthorization: null,
                    proposedNamespaceAuthorization: null,
                    supportedCustomViewStrategies: stillUnsupported
                        .RelationshipClassification
                        .SupportedCustomViewStrategies
                );

            case RelationalAuthorizationPlanOutcome.Plan plan:
                return AuthorizePutPlan(mappingSet, resource, writePlan, plan, authorizationContext);

            default:
                throw new InvalidOperationException(
                    $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
                );
        }
    }

    private WriteGuardRailPreflightResult<UpdateResult> AuthorizePutPlan(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceWritePlan writePlan,
        RelationalAuthorizationPlanOutcome.Plan plan,
        RelationalAuthorizationContext authorizationContext
    )
    {
        if (
            !TryPlanWriteCustomViewAuthorization(
                mappingSet,
                resource,
                plan.CustomViewStrategies,
                out var customViewAuthorization,
                out var customViewFailures,
                out var customViewChecksBeforeFailure
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                BuildPutAuthorizationSecurityConfigurationFailure(mappingSet, customViewFailures!),
                customViewChecksBeforeFailure
            );
        }

        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization = null;
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization = null;

        if (plan.NamespaceChecks.Count > 0)
        {
            if (
                !NamespacePrefixParameterizationPreflight.TryCreate(
                    mappingSet.Key.Dialect,
                    authorizationContext.NamespacePrefixes,
                    out var namespacePrefixParameterization,
                    out var securityConfigurationMessage,
                    out var securityConfigurationDiagnostics
                )
            )
            {
                return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                    new UpdateResult.UpdateFailureSecurityConfiguration(
                        [securityConfigurationMessage],
                        securityConfigurationDiagnostics
                    ),
                    CustomViewChecksBeforeNamespaceCheck(customViewAuthorization, plan.NamespaceChecks)
                );
            }

            (storedNamespaceAuthorization, proposedNamespaceAuthorization) = SplitNamespaceAuthorization(
                plan.NamespaceChecks,
                namespacePrefixParameterization
            );
        }

        // After the namespace parameterization, as on every other path: both are setup failures reported as
        // the same security-configuration 500, and NamespaceBased executes ahead of OwnershipBased.
        if (
            !TryPlanStoredOwnershipAuthorization(
                mappingSet,
                plan.OwnershipCheck,
                authorizationContext,
                out var storedOwnershipAuthorization,
                out var ownershipSecurityConfigurationMessage,
                out var ownershipSecurityConfigurationDiagnostics
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                new UpdateResult.UpdateFailureSecurityConfiguration(
                    [ownershipSecurityConfigurationMessage],
                    ownershipSecurityConfigurationDiagnostics
                ),
                // Every configured view runs before this failure: OwnershipBased executes last among the
                // AND strategies whatever position it is configured at.
                AllCustomViewChecks(customViewAuthorization)
            );
        }

        return AuthorizePutRelationshipBucket(
            mappingSet,
            resource,
            writePlan,
            plan.NonNamespaceConfiguredStrategies,
            authorizationContext,
            storedNamespaceAuthorization,
            proposedNamespaceAuthorization,
            customViewAuthorization,
            storedOwnershipAuthorization
        );
    }

    private WriteGuardRailPreflightResult<UpdateResult> AuthorizePutRelationshipBucket(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceWritePlan writePlan,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization,
        RelationalCustomViewAuthorization? customViewAuthorization = null,
        RelationalOwnershipAuthorization? storedOwnershipAuthorization = null,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy>? supportedCustomViewStrategies = null
    )
    {
        supportedCustomViewStrategies ??= [];

        // The strategy-level SecurityConfigurationError and StillUnsupported arms reach this bucket before
        // any custom view has been planned, so plan them here rather than at each terminal below: every Stop
        // in this method then carries the views that execute ahead of it. Callers that already planned pass
        // customViewAuthorization instead and skip this.
        if (
            customViewAuthorization is null
            && supportedCustomViewStrategies.Count > 0
            && !TryPlanWriteCustomViewAuthorization(
                mappingSet,
                resource,
                supportedCustomViewStrategies,
                out customViewAuthorization,
                out var customViewFailures,
                out var customViewChecksBeforeFailure
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                BuildPutAuthorizationSecurityConfigurationFailure(mappingSet, customViewFailures!),
                customViewChecksBeforeFailure
            );
        }

        var relationshipAuthorizationPlan = _relationshipAuthorizationPlanner.PlanUpdateValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext,
            writePlan
        );

        var securityConfigurationFailures = relationshipAuthorizationPlan.SecurityConfigurationFailures;

        if (securityConfigurationFailures.Count > 0)
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                BuildPutAuthorizationSecurityConfigurationFailure(mappingSet, securityConfigurationFailures),
                ChecksBeforeRelationshipFailure(customViewAuthorization, securityConfigurationFailures)
            );
        }

        var knownButNotEnabledFailures = relationshipAuthorizationPlan.KnownButNotEnabledFailures;

        if (knownButNotEnabledFailures.Count > 0)
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                new UpdateResult.UpdateFailureNotImplemented(
                    BuildKnownButNotEnabledPutAuthorizationMessage(resource, knownButNotEnabledFailures),
                    UpdateFailureNotImplementedReason.StrategyNotEnabled
                ),
                AllCustomViewChecks(customViewAuthorization)
            );
        }

        return relationshipAuthorizationPlan.StoredValues switch
        {
            RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired =>
                new WriteGuardRailPreflightResult<UpdateResult>.Continue(
                    null,
                    null,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization,
                    customViewAuthorization: customViewAuthorization,
                    storedOwnershipAuthorization: storedOwnershipAuthorization
                ),

            // NamespaceBased and custom view-based both AND-compose before the relationship OR group
            // (auth.md, 08-namespace-auth-strategy.md). When either is planned, defer the stored relationship
            // NoClaims denial into the proposed-relationship slot so those filters get to deny first; the
            // write path's second command emits the NoClaims denial only after they authorize. Leaving it in
            // the stored slot with custom views pending ends the write at the first phase, before the proposed
            // custom-view checks run at all. With no AND filter planned, keep NoClaims in the stored slot so
            // the stored boundary emits it after the target lock, preserving the existing 404-over-403
            // ordering for a missing PUT target.
            RelationshipAuthorizationResult.NoClaims noClaims => storedNamespaceAuthorization is null
            && proposedNamespaceAuthorization is null
            && customViewAuthorization is null
                ? new WriteGuardRailPreflightResult<UpdateResult>.Continue(
                    noClaims,
                    null,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization,
                    customViewAuthorization: customViewAuthorization,
                    storedOwnershipAuthorization: storedOwnershipAuthorization
                )
                : new WriteGuardRailPreflightResult<UpdateResult>.Continue(
                    null,
                    noClaims,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization,
                    customViewAuthorization: customViewAuthorization,
                    storedOwnershipAuthorization: storedOwnershipAuthorization
                ),

            RelationshipAuthorizationResult.Authorized authorized =>
                new WriteGuardRailPreflightResult<UpdateResult>.Continue(
                    authorized,
                    relationshipAuthorizationPlan.ProposedValues
                        as RelationshipAuthorizationResult.Authorized,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization,
                    customViewAuthorization: customViewAuthorization,
                    storedOwnershipAuthorization: storedOwnershipAuthorization
                ),

            _ => throw new InvalidOperationException(
                $"Unsupported stored relationship authorization result '{relationshipAuthorizationPlan.StoredValues.GetType().Name}' for PUT preflight."
            ),
        };
    }

    private static WriteGuardRailPreflightResult<UpsertResult> BuildNoClaimsPostRelationshipAuthorizationFailure(
        RelationshipAuthorizationResult.NoClaims noClaims,
        RelationalAuthorizationContext authorizationContext
    )
    {
        if (
            !TryCreateNoClaimsRelationshipAuthorizationFailure(
                noClaims,
                authorizationContext.ClaimEducationOrganizationIds,
                PostRelationshipAuthorizationAuth1Index,
                out var noClaimsFailure
            ) || noClaimsFailure is null
        )
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                new UpsertResult.UnknownFailure(
                    "Relationship authorization required caller EducationOrganizationIds, but denial metadata could not be built."
                )
            );
        }

        return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
            CreateUpsertRelationshipNotAuthorized(noClaimsFailure)
        );
    }

    /// <summary>
    /// When a writable profile shaped the body, restricts document references to those
    /// still present in the shaped body; otherwise returns the references unchanged.
    /// </summary>
    private static IReadOnlyList<DocumentReference> ResolveProfileShapedReferences(
        BackendProfileWriteContext? profileWriteContext,
        IReadOnlyList<DocumentReference> documentReferences,
        System.Text.Json.Nodes.JsonNode shapedBody
    ) =>
        profileWriteContext is null
            ? documentReferences
            : ProfileWriteReferenceFilter.RetainPresent(documentReferences, shapedBody);

    /// <summary>
    /// When a writable profile shaped the body, restricts descriptor references to those
    /// still present in the shaped body; otherwise returns the descriptors unchanged.
    /// </summary>
    private static IReadOnlyList<DescriptorReference> ResolveProfileShapedDescriptors(
        BackendProfileWriteContext? profileWriteContext,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        System.Text.Json.Nodes.JsonNode shapedBody
    ) =>
        profileWriteContext is null
            ? descriptorReferences
            : ProfileWriteReferenceFilter.RetainPresent(descriptorReferences, shapedBody);

    private async Task<TResult> ExecuteWriteGuardRails<TResult>(
        System.Text.Json.Nodes.JsonNode requestBody,
        WritePrecondition writePrecondition,
        TraceId traceId,
        string tenantKey,
        MappingSet mappingSet,
        ResourceInfo resourceInfo,
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetRequest targetRequest,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        Func<string, TResult> failureFactory,
        Func<RelationalWriteExecutorResult, TResult> executorResultProjector,
        BackendProfileWriteContext? profileWriteContext = null,
        Func<ResourceWritePlan, WriteGuardRailPreflightResult<TResult>>? preflight = null,
        short? creatorOwnershipTokenId = null
    )
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        ArgumentNullException.ThrowIfNull(writePrecondition);
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(documentReferences);
        ArgumentNullException.ThrowIfNull(descriptorReferences);
        ArgumentNullException.ThrowIfNull(failureFactory);
        ArgumentNullException.ThrowIfNull(executorResultProjector);

        var resource = RelationalWriteSupport.ToQualifiedResourceName(resourceInfo);
        ResourceWritePlan writePlan;

        try
        {
            writePlan = mappingSet.GetWritePlanOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return failureFactory(ex.Message);
        }
        catch (MissingWritePlanLookupGuardRailException ex)
        {
            return failureFactory(ex.Message);
        }

        RelationshipAuthorizationResult? storedRelationshipAuthorization = null;
        RelationshipAuthorizationResult? proposedRelationshipAuthorization = null;
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization = null;
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization = null;
        PostRelationshipAuthorizationPlans? postRelationshipAuthorizationPlans = null;
        RelationalCustomViewAuthorization? customViewAuthorization = null;
        RelationalOwnershipAuthorization? storedOwnershipAuthorization = null;

        if (preflight is not null)
        {
            var preflightResult = preflight(writePlan);

            switch (preflightResult)
            {
                case WriteGuardRailPreflightResult<TResult>.Continue continueResult:
                    storedRelationshipAuthorization = continueResult.StoredRelationshipAuthorization;
                    proposedRelationshipAuthorization = continueResult.ProposedRelationshipAuthorization;
                    storedNamespaceAuthorization = continueResult.StoredNamespaceAuthorization;
                    proposedNamespaceAuthorization = continueResult.ProposedNamespaceAuthorization;
                    postRelationshipAuthorizationPlans = continueResult.PostRelationshipAuthorizationPlans;
                    customViewAuthorization = continueResult.CustomViewAuthorization;
                    storedOwnershipAuthorization = continueResult.StoredOwnershipAuthorization;
                    break;

                case WriteGuardRailPreflightResult<TResult>.Stop stopResult:
                    // Views configured ahead of this terminal execute first, so a missing or non-conforming
                    // one keeps its own failure instead of being masked by the terminal's result.
                    await ValidateSingleRecordCustomViewsAsync(
                            mappingSet,
                            stopResult.CustomViewChecksToValidate
                        )
                        .ConfigureAwait(false);
                    return stopResult.Result;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported relational write preflight result '{preflightResult.GetType().Name}'."
                    );
            }
        }

        var readPlanPreparation = PrepareExistingDocumentReadPlan(mappingSet, resource);

        for (var attemptIndex = 0; attemptIndex < 2; attemptIndex++)
        {
            if (readPlanPreparation.ReadPlan is null)
            {
                return failureFactory(
                    readPlanPreparation.FailureMessage
                        ?? RelationalWriteSupport.BuildMissingExistingDocumentReadPlanMessage(resource)
                );
            }

            // Each attempt hands the executor a fresh input, so every retry resolves its target inside
            // its own write session instead of reusing an observation from the previous attempt.
            var executorResult = await _writeExecutor
                .ExecuteAsync(
                    new RelationalWriteExecutorInput(
                        mappingSet,
                        operationKind,
                        targetRequest,
                        writePlan,
                        readPlanPreparation.ReadPlan,
                        requestBody,
                        resourceInfo.AllowIdentityUpdates,
                        traceId,
                        new ReferenceResolverRequest(
                            MappingSet: mappingSet,
                            RequestResource: resource,
                            DocumentReferences: documentReferences,
                            DescriptorReferences: descriptorReferences
                        ),
                        tenantKey: tenantKey,
                        profileWriteContext: profileWriteContext,
                        writePrecondition: writePrecondition,
                        storedRelationshipAuthorization: storedRelationshipAuthorization,
                        proposedRelationshipAuthorization: proposedRelationshipAuthorization,
                        storedNamespaceAuthorization: storedNamespaceAuthorization,
                        proposedNamespaceAuthorization: proposedNamespaceAuthorization
                    )
                    {
                        PostRelationshipAuthorizationPlans = postRelationshipAuthorizationPlans,
                        CustomViewAuthorization = customViewAuthorization,
                        CreatorOwnershipTokenId = creatorOwnershipTokenId,
                        StoredOwnershipAuthorization = storedOwnershipAuthorization,
                    }
                )
                .ConfigureAwait(false);

            if (
                executorResult.AttemptOutcome is RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare
                && attemptIndex == 0
                // A wildcard If-Match (*) is an existence-only precondition, not a concurrency check, so
                // a stale no-op against a still-existing row is retried like the no-precondition path
                // rather than short-circuiting to a 412. Only a specific-tag If-Match blocks the retry.
                && writePrecondition is not WritePrecondition.IfMatch { IsWildcard: false }
            )
            {
                continue;
            }

            return executorResultProjector(executorResult);
        }

        throw new InvalidOperationException(
            $"Relational {operationKind} write retry loop exited without a final executor result."
        );
    }

    private static ExistingDocumentReadPlanPreparation PrepareExistingDocumentReadPlan(
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        try
        {
            return new ExistingDocumentReadPlanPreparation(mappingSet.GetReadPlanOrThrow(resource), null);
        }
        catch (NotSupportedException ex)
        {
            return new ExistingDocumentReadPlanPreparation(null, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new ExistingDocumentReadPlanPreparation(null, ex.Message);
        }
    }

    private sealed record ExistingDocumentReadPlanPreparation(
        ResourceReadPlan? ReadPlan,
        string? FailureMessage
    );

    private abstract record WriteGuardRailPreflightResult<TResult>
    {
        private WriteGuardRailPreflightResult() { }

        public sealed record Continue : WriteGuardRailPreflightResult<TResult>
        {
            public Continue(
                RelationshipAuthorizationResult? storedRelationshipAuthorization,
                RelationshipAuthorizationResult? proposedRelationshipAuthorization,
                RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization = null,
                RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization = null,
                PostRelationshipAuthorizationPlans? postRelationshipAuthorizationPlans = null,
                RelationalCustomViewAuthorization? customViewAuthorization = null,
                RelationalOwnershipAuthorization? storedOwnershipAuthorization = null
            )
            {
                ValidateStoredRelationshipAuthorization(storedRelationshipAuthorization);
                ValidateProposedRelationshipAuthorization(proposedRelationshipAuthorization);
                StoredRelationshipAuthorization = storedRelationshipAuthorization;
                ProposedRelationshipAuthorization = proposedRelationshipAuthorization;
                StoredNamespaceAuthorization = storedNamespaceAuthorization;
                ProposedNamespaceAuthorization = proposedNamespaceAuthorization;
                PostRelationshipAuthorizationPlans = postRelationshipAuthorizationPlans;
                CustomViewAuthorization = customViewAuthorization;
                StoredOwnershipAuthorization = storedOwnershipAuthorization;
            }

            public RelationshipAuthorizationResult? StoredRelationshipAuthorization { get; }

            public RelationshipAuthorizationResult? ProposedRelationshipAuthorization { get; }

            public RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization { get; }

            public RelationalWriteNamespaceAuthorization? ProposedNamespaceAuthorization { get; }

            /// <summary>
            /// The ownership check planned for this write, or <see langword="null"/> when
            /// <c>OwnershipBased</c> is not configured. Ownership has one value source — the stored token —
            /// so unlike namespace and custom view there is no proposed counterpart: the check decides an
            /// update and is vacuous for a create.
            /// </summary>
            public RelationalOwnershipAuthorization? StoredOwnershipAuthorization { get; }

            /// <summary>
            /// Every custom-view check planned for this write, across both value sources. Null when no custom
            /// view participates.
            /// </summary>
            public RelationalCustomViewAuthorization? CustomViewAuthorization { get; }

            public PostRelationshipAuthorizationPlans? PostRelationshipAuthorizationPlans { get; }

            private static void ValidateStoredRelationshipAuthorization(
                RelationshipAuthorizationResult? storedRelationshipAuthorization
            )
            {
                switch (storedRelationshipAuthorization)
                {
                    case RelationshipAuthorizationResult.KnownButNotEnabled:
                        throw new InvalidOperationException(
                            "Known-but-not-enabled stored relationship authorization results must be stopped by repository preflight."
                        );

                    case RelationshipAuthorizationResult.SecurityConfigurationError:
                        throw new InvalidOperationException(
                            "Security-configuration stored relationship authorization results must be stopped by repository preflight."
                        );
                }
            }

            private static void ValidateProposedRelationshipAuthorization(
                RelationshipAuthorizationResult? proposedRelationshipAuthorization
            )
            {
                // Proposed relationship results other than Authorized and NoClaims are decided at
                // preflight: KnownButNotEnabled and SecurityConfigurationError must short-circuit
                // there, and NoAuthorizationRequired / NoFurtherAuthorizationRequired translate to
                // null. Allow NoClaims through so the proposed namespace check still gets to deny
                // first when both fail.
                switch (proposedRelationshipAuthorization)
                {
                    case null:
                    case RelationshipAuthorizationResult.Authorized:
                    case RelationshipAuthorizationResult.NoClaims:
                        return;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported proposed relationship authorization result '{proposedRelationshipAuthorization.GetType().Name}' for executor entry."
                        );
                }
            }
        }

        /// <inheritdoc cref="GetByIdAuthorizationPreflightResult.Stop.CustomViewChecksToValidate"/>
        public sealed record Stop(
            TResult Result,
            IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> CustomViewChecksToValidate
        ) : WriteGuardRailPreflightResult<TResult>
        {
            public Stop(TResult result)
                : this(result, []) { }
        }
    }

    private async Task<GetResult> GetDocumentByIdAsync(
        IGetRequest relationalGetRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan,
        CancellationToken cancellationToken = default
    )
    {
        // Planner terminals (namespace setup failures, the ownership token cap, relationship
        // security-configuration failures, and known unsupported relationship composition) resolve before
        // the target lookup, so those denials issue no read roundtrip and never depend on document
        // existence. The target-dependent custom-view, namespace, ownership and relationship checks still
        // run per attempt against the resolved target (see AuthorizeGetByIdAgainstTargetAsync).
        var authorizationPreflight = AuthorizeGetByIdPreflight(relationalGetRequest, mappingSet, resource);

        if (authorizationPreflight is GetByIdAuthorizationPreflightResult.Stop preflightStop)
        {
            return await CompleteGetByIdPreflightStopAsync(mappingSet, preflightStop).ConfigureAwait(false);
        }

        for (var attemptIndex = 0; attemptIndex < GetByIdReadBoundaryAttemptCount; attemptIndex++)
        {
            var targetLookupResult = await _readTargetLookupService
                .ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    relationalGetRequest.DocumentUuid,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (
                targetLookupResult
                is RelationalReadTargetLookupResult.NotFound
                    or RelationalReadTargetLookupResult.WrongResource
            )
            {
                return new GetResult.GetFailureNotExists();
            }

            if (targetLookupResult is not RelationalReadTargetLookupResult.ExistingDocument existingDocument)
            {
                throw new InvalidOperationException(
                    $"Relational repository GET target lookup returned unsupported result type '{targetLookupResult.GetType().Name}'."
                );
            }

            var authorizationOutcome = authorizationPreflight switch
            {
                GetByIdAuthorizationPreflightResult.AuthorizationNotRequired =>
                    GetAuthorizationOutcome.NotRequired,
                GetByIdAuthorizationPreflightResult.Proceed proceed =>
                    await AuthorizeGetByIdAgainstTargetAsync(
                            relationalGetRequest,
                            mappingSet,
                            proceed.StoredNamespaceAuthorization,
                            proceed.StoredCustomViewAuthorization,
                            proceed.StoredOwnershipAuthorization,
                            proceed.StoredRelationshipAuthorization,
                            existingDocument.DocumentId,
                            existingDocument.ContentVersion,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Unsupported GET-by-id authorization preflight result '{authorizationPreflight.GetType().Name}'."
                ),
            };

            if (authorizationOutcome.FailureResult is not null)
            {
                return authorizationOutcome.FailureResult;
            }

            if (authorizationOutcome.RetryTargetResolution)
            {
                continue;
            }

            var hydratedPage = await _documentHydrator
                .HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(existingDocument.DocumentId),
                    CreateGetHydrationExecutionOptions(relationalGetRequest),
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (hydratedPage.DocumentMetadata.Count == 0)
            {
                if (authorizationOutcome.ObservedContentVersion is not null)
                {
                    continue;
                }

                return new GetResult.GetFailureNotExists();
            }

            if (hydratedPage.DocumentMetadata.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Relational GET hydration for document id {existingDocument.DocumentId} returned "
                        + $"{hydratedPage.DocumentMetadata.Count} metadata rows, but exactly 1 was expected."
                );
            }

            var documentMetadata = hydratedPage.DocumentMetadata[0];

            if (documentMetadata.DocumentId != existingDocument.DocumentId)
            {
                throw new InvalidOperationException(
                    $"Relational GET hydration returned metadata for document id {documentMetadata.DocumentId}, "
                        + $"but target document id was {existingDocument.DocumentId}."
                );
            }

            if (documentMetadata.DocumentUuid != existingDocument.DocumentUuid.Value)
            {
                throw new InvalidOperationException(
                    $"Relational GET hydration returned document uuid '{documentMetadata.DocumentUuid}', "
                        + $"but target document uuid was '{existingDocument.DocumentUuid.Value}'."
                );
            }

            if (
                authorizationOutcome.ObservedContentVersion is { } observedContentVersion
                && documentMetadata.ContentVersion != observedContentVersion
            )
            {
                continue;
            }

            var shouldRetryPostHydrationReadBoundary = await ShouldRetryPostHydrationReadBoundaryAsync(
                    mappingSet,
                    resource,
                    existingDocument,
                    authorizationOutcome.ObservedContentVersion,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (shouldRetryPostHydrationReadBoundary)
            {
                continue;
            }

            return BuildGetSuccess(
                relationalGetRequest,
                mappingSet,
                readPlan,
                hydratedPage,
                documentMetadata
            );
        }

        return new GetResult.UnknownFailure(
            "Relational GET could not read a stable authorized representation for the requested document."
        );
    }

    private static HydrationExecutionOptions CreateGetHydrationExecutionOptions(
        IGetRequest relationalGetRequest
    )
    {
        // StoredDocument-mode reads do not emit `link`, so the auxiliary document-reference lookup is
        // wasted work. Descriptor URIs are still needed for both read modes.
        return new HydrationExecutionOptions(
            IncludeDocumentReferenceLookup: relationalGetRequest.ReadMode
                == RelationalGetRequestReadMode.ExternalResponse,
            UseSingleDocumentFastPath: true
        );
    }

    private GetResult.GetSuccess BuildGetSuccess(
        IGetRequest relationalGetRequest,
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        HydratedPage hydratedPage,
        DocumentMetadataRow documentMetadata
    )
    {
        var appliesReadableProfileProjection = ShouldApplyReadableProfileProjection(relationalGetRequest);
        var readProfileName = appliesReadableProfileProjection
            ? relationalGetRequest.ReadableProfileProjectionContext!.ProfileName
            : null;

        var edfiDoc = _readMaterializer.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                documentMetadata,
                hydratedPage.TableRowsInDependencyOrder,
                hydratedPage.DescriptorRowsInPlanOrder,
                relationalGetRequest.ReadMode.ToMaterializationMode()
            )
            {
                MappingSet = mappingSet,
                DocumentReferenceLookup = hydratedPage.DocumentReferenceLookup,
                EtagVariant = new EtagVariantInputs(
                    readProfileName,
                    ResponseFormat.Json,
                    relationalGetRequest.ResponseContentCoding
                ),
            }
        );

        if (appliesReadableProfileProjection)
        {
            var projectionContext = relationalGetRequest.ReadableProfileProjectionContext!;
            edfiDoc = _readableProfileProjector.Project(
                edfiDoc,
                projectionContext.ContentTypeDefinition,
                projectionContext.IdentityPropertyNames
            );
        }

        // Final response-shaping pass — strips `link` subtrees when ResourceLinksOptions.Enabled
        // is false. Runs after readable-profile projection so the flag governs the served body,
        // not the cached intermediate. No-op when Enabled is true. See
        // design-docs/link-injection.md §Feature Flag and §Cache and Etag.
        _readMaterializer.StripReferenceLinks(edfiDoc, readPlan);

        return new GetResult.GetSuccess(
            new DocumentUuid(documentMetadata.DocumentUuid),
            edfiDoc,
            documentMetadata.ContentLastModifiedAt.UtcDateTime,
            null
        );
    }

    private async Task<DocumentCacheReadAccelerationGetByIdSelectionResult> SelectGetByIdReadAccelerationCandidateAsync(
        IGetRequest relationalGetRequest,
        QualifiedResourceName resource,
        CancellationToken cancellationToken = default
    )
    {
        var mappingSet = relationalGetRequest.MappingSet;

        ResourceReadPlan readPlan;

        try
        {
            readPlan = mappingSet.GetReadPlanOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
                new GetResult.UnknownFailure(ex.Message)
            );
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
                new GetResult.UnknownFailure(ex.Message)
            );
        }

        var authorizationPreflight = AuthorizeGetByIdPreflight(relationalGetRequest, mappingSet, resource);

        if (authorizationPreflight is GetByIdAuthorizationPreflightResult.Stop preflightStop)
        {
            return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
                await CompleteGetByIdPreflightStopAsync(mappingSet, preflightStop).ConfigureAwait(false)
            );
        }

        short resourceKeyId;

        try
        {
            resourceKeyId = RelationalWriteSupport.GetResourceKeyIdOrThrow(mappingSet, resource);
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
                new GetResult.UnknownFailure(ex.Message)
            );
        }
        catch (KeyNotFoundException ex)
        {
            return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
                new GetResult.UnknownFailure(ex.Message)
            );
        }

        for (var attemptIndex = 0; attemptIndex < GetByIdReadBoundaryAttemptCount; attemptIndex++)
        {
            var targetLookupResult = await _readTargetLookupService
                .ResolveForGetByIdAsync(
                    mappingSet,
                    resource,
                    relationalGetRequest.DocumentUuid,
                    cancellationToken
                )
                .ConfigureAwait(false);

            if (
                targetLookupResult
                is RelationalReadTargetLookupResult.NotFound
                    or RelationalReadTargetLookupResult.WrongResource
            )
            {
                return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
                    new GetResult.GetFailureNotExists()
                );
            }

            if (targetLookupResult is not RelationalReadTargetLookupResult.ExistingDocument existingDocument)
            {
                throw new InvalidOperationException(
                    $"Relational repository GET target lookup returned unsupported result type '{targetLookupResult.GetType().Name}'."
                );
            }

            var authorizationOutcome = authorizationPreflight switch
            {
                GetByIdAuthorizationPreflightResult.AuthorizationNotRequired =>
                    GetAuthorizationOutcome.NotRequired,
                GetByIdAuthorizationPreflightResult.Proceed proceed =>
                    await AuthorizeGetByIdAgainstTargetAsync(
                            relationalGetRequest,
                            mappingSet,
                            proceed.StoredNamespaceAuthorization,
                            proceed.StoredCustomViewAuthorization,
                            proceed.StoredOwnershipAuthorization,
                            proceed.StoredRelationshipAuthorization,
                            existingDocument.DocumentId,
                            existingDocument.ContentVersion,
                            cancellationToken
                        )
                        .ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Unsupported GET-by-id authorization preflight result '{authorizationPreflight.GetType().Name}'."
                ),
            };

            if (authorizationOutcome.FailureResult is not null)
            {
                return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
                    authorizationOutcome.FailureResult
                );
            }

            if (
                authorizationOutcome.RetryTargetResolution
                || (
                    authorizationOutcome.ObservedContentVersion is { } observedContentVersion
                    && existingDocument.ContentVersion != observedContentVersion
                )
            )
            {
                continue;
            }

            var candidate = new DocumentCacheReadAccelerationCandidate(
                existingDocument.DocumentId,
                existingDocument.DocumentUuid,
                resourceKeyId,
                existingDocument.ContentVersion,
                existingDocument.ContentLastModifiedAt
            );

            return new DocumentCacheReadAccelerationGetByIdSelectionResult.Candidate(
                candidate,
                fallbackCancellationToken =>
                    HydrateSelectedGetByIdCandidateAsync(
                        relationalGetRequest,
                        mappingSet,
                        readPlan,
                        candidate,
                        fallbackCancellationToken
                    )
            );
        }

        return new DocumentCacheReadAccelerationGetByIdSelectionResult.Complete(
            new GetResult.UnknownFailure(
                "Relational GET could not select a stable authorized candidate for the requested document."
            )
        );
    }

    private async Task<GetResult> HydrateSelectedGetByIdCandidateAsync(
        IGetRequest relationalGetRequest,
        MappingSet mappingSet,
        ResourceReadPlan readPlan,
        DocumentCacheReadAccelerationCandidate candidate,
        CancellationToken cancellationToken
    )
    {
        var hydratedPage = await _documentHydrator
            .HydrateAsync(
                readPlan,
                new PageKeysetSpec.Single(candidate.DocumentId),
                CreateGetHydrationExecutionOptions(relationalGetRequest),
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!SelectedGetByIdCandidateStillMatches(candidate, hydratedPage.DocumentMetadata))
        {
            return await GetDocumentByIdRelationalAsync(relationalGetRequest, cancellationToken)
                .ConfigureAwait(false);
        }

        DocumentMetadataRow documentMetadata = hydratedPage.DocumentMetadata[0];

        return BuildGetSuccess(relationalGetRequest, mappingSet, readPlan, hydratedPage, documentMetadata);
    }

    private static bool SelectedGetByIdCandidateStillMatches(
        DocumentCacheReadAccelerationCandidate candidate,
        IReadOnlyList<DocumentMetadataRow> hydratedMetadata
    )
    {
        if (hydratedMetadata.Count != 1)
        {
            return false;
        }

        DocumentMetadataRow metadata = hydratedMetadata[0];

        return metadata.DocumentId == candidate.DocumentId
            && metadata.DocumentUuid == candidate.DocumentUuid.Value
            && metadata.ResourceKeyId == candidate.ResourceKeyId
            && metadata.ContentVersion == candidate.ContentVersion
            && metadata.ContentLastModifiedAt == candidate.ContentLastModifiedAt;
    }

    private async Task<bool> ShouldRetryPostHydrationReadBoundaryAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalReadTargetLookupResult.ExistingDocument expectedDocument,
        long? observedContentVersion,
        CancellationToken cancellationToken = default
    )
    {
        if (observedContentVersion is null)
        {
            return false;
        }

        var targetLookupResult = await _readTargetLookupService
            .ResolveForGetByIdAsync(mappingSet, resource, expectedDocument.DocumentUuid, cancellationToken)
            .ConfigureAwait(false);

        if (targetLookupResult is not RelationalReadTargetLookupResult.ExistingDocument currentDocument)
        {
            return true;
        }

        if (
            currentDocument.DocumentId != expectedDocument.DocumentId
            || currentDocument.DocumentUuid != expectedDocument.DocumentUuid
        )
        {
            return true;
        }

        return currentDocument.ContentVersion != observedContentVersion.Value;
    }

    /// <summary>
    /// Builds a GET-by-id terminal carrying the views configured strictly before
    /// <paramref name="terminalIndex"/>, so those views are validated before the terminal is reported. A
    /// planning failure among them replaces the terminal, matching how the descriptor paths order the two.
    /// </summary>
    /// <remarks>
    /// Every terminal on this path routes through here rather than constructing <c>Stop</c> directly, so a
    /// terminal added later cannot quietly skip validation.
    /// </remarks>
    private static GetByIdAuthorizationPreflightResult GetByIdTerminal(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        GetResult result,
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
            return new GetByIdAuthorizationPreflightResult.Stop(result);
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            strategiesToValidate,
            NamespaceAuthorizationOperation.ReadSingle
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            return new GetByIdAuthorizationPreflightResult.Stop(
                BuildGetAuthorizationSecurityConfigurationFailure(
                    mappingSet,
                    resource,
                    configurationFailure.Failures
                ),
                SingleRecordChecksBeforeTerminal(
                    configurationFailure.PlannedChecks,
                    RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                        configurationFailure.Failures
                    )
                )
            );
        }

        return new GetByIdAuthorizationPreflightResult.Stop(
            result,
            ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks
        );
    }

    /// <summary>
    /// The already-planned custom-view checks configured strictly before the earliest relationship
    /// security-configuration failure. Custom views AND-compose ahead of relationship strategies, so the ones
    /// configured before the failure still execute and must be validated before it is reported.
    /// </summary>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> ChecksBeforeRelationshipFailure(
        RelationalCustomViewAuthorization? customViewAuthorization,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    ) =>
        customViewAuthorization is null
            ? []
            : SingleRecordChecksBeforeTerminal(
                customViewAuthorization.Checks,
                RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(failures)
            );

    /// <summary>
    /// Every planned custom-view check. Known-but-not-enabled relationship strategies execute last per
    /// auth.md "Execution order" whatever their configured position, so every resolved view runs ahead of the
    /// 501 rather than only those configured before it.
    /// </summary>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> AllCustomViewChecks(
        RelationalCustomViewAuthorization? customViewAuthorization
    ) => customViewAuthorization?.Checks ?? [];

    /// <summary>
    /// The already-planned custom-view checks configured strictly before the namespace check, for terminals
    /// that fail after custom-view planning succeeded.
    /// </summary>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> CustomViewChecksBeforeNamespaceCheck(
        RelationalCustomViewAuthorization? customViewAuthorization,
        IReadOnlyList<NamespaceAuthorizationCheckSpec> namespaceChecks
    ) =>
        customViewAuthorization is null || namespaceChecks.Count == 0
            ? []
            : SingleRecordChecksBeforeTerminal(
                customViewAuthorization.Checks,
                namespaceChecks[0].RawConfiguredIndex
            );

    /// <summary>
    /// The planned single-record checks configured strictly before <paramref name="terminalRawConfiguredIndex"/>.
    /// </summary>
    private static IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> SingleRecordChecksBeforeTerminal(
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks,
        int terminalRawConfiguredIndex
    ) => [.. checks.Where(check => check.ConfiguredStrategy.RawConfiguredIndex < terminalRawConfiguredIndex)];

    /// <summary>
    /// Completes a GET-by-id preflight terminal: validates the custom views configured ahead of it, then
    /// yields the terminal's result.
    /// </summary>
    /// <remarks>
    /// Shared by both GET-by-id entry points. A terminal added on one of them would otherwise be able to
    /// skip view validation on the other — the accelerated path did exactly that — and the views ahead of a
    /// terminal are AND filters that execute before it, so a missing or non-conforming view must keep its
    /// own 500 rather than be hidden behind the terminal's response. It matters most for the terminals that
    /// carry every configured view, such as the ownership token cap, since <c>OwnershipBased</c> executes
    /// last among the AND strategies and so follows all of them.
    /// </remarks>
    private async Task<GetResult> CompleteGetByIdPreflightStopAsync(
        MappingSet mappingSet,
        GetByIdAuthorizationPreflightResult.Stop preflightStop
    )
    {
        await ValidateSingleRecordCustomViewsAsync(mappingSet, preflightStop.CustomViewChecksToValidate)
            .ConfigureAwait(false);

        return preflightStop.Result;
    }

    /// <summary>
    /// Validates the views a GET-by-id terminal carries. Empty is a no-op, so every terminal can route
    /// through this unconditionally.
    /// </summary>
    private Task ValidateSingleRecordCustomViewsAsync(
        MappingSet mappingSet,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks
    ) =>
        CustomViewAuthorizationValidator.ValidateSingleRecordAsync(
            _commandExecutor,
            mappingSet.Key.Dialect,
            checks
        );

    private GetByIdAuthorizationPreflightResult AuthorizeGetByIdPreflight(
        IGetRequest relationalGetRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource
    )
    {
        if (ShouldBypassSingleRecordAuthorization(relationalGetRequest))
        {
            return GetByIdAuthorizationPreflightResult.AuthorizationNotRequired.Instance;
        }

        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            relationalGetRequest.AuthorizationStrategyEvaluators
        );
        var authorizationContext = relationalGetRequest.AuthorizationContext;
        var orchestratorOutcome = RelationalAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            NamespaceAuthorizationOperation.ReadSingle,
            configuredAuthorizationStrategies,
            authorizationContext
        );

        switch (orchestratorOutcome)
        {
            case RelationalAuthorizationPlanOutcome.NoUsableRootColumn noUsableRoot:
                return GetByIdTerminal(
                    mappingSet,
                    resource,
                    new GetResult.GetFailureSecurityConfiguration(
                        [
                            NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                                RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                            ),
                        ],
                        RelationalReadGuardrails.BuildNoUsableRootColumnDiagnostics(noUsableRoot.Resource)
                    ),
                    noUsableRoot.CustomViewStrategies,
                    noUsableRoot.RawConfiguredIndex
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return GetByIdTerminal(
                    mappingSet,
                    resource,
                    new GetResult.GetFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    ),
                    noPrefixes.CustomViewStrategies,
                    noPrefixes.RawConfiguredIndex
                );

            // The ownership token list reaches the defensive limit. A planner terminal, so it is reported
            // before any statement is emitted and before every relationship terminal, but after the
            // namespace terminals and after any custom-view configuration failure — which the planner has
            // already resolved by not returning this outcome in that case.
            case RelationalAuthorizationPlanOutcome.OwnershipTokenCapExceeded ownershipTokenCapExceeded:
                return GetByIdTerminal(
                    mappingSet,
                    resource,
                    new GetResult.GetFailureSecurityConfiguration(
                        [
                            OwnershipAuthorizationSecurityConfigurationMessages.TokenCapExceeded(
                                ownershipTokenCapExceeded.OwnershipTokenCount
                            ),
                        ],
                        AuthorizationSecurityConfigurationDiagnostics.ForOwnershipTokenParameterization(
                            AuthorizationSecurityConfigurationDiagnostics.OwnershipTokenCapExceeded
                        )
                    ),
                    ownershipTokenCapExceeded.CustomViewStrategies,
                    // Views configured anywhere run before this terminal: OwnershipBased executes last
                    // among the AND strategies whatever position it is configured at.
                    int.MaxValue
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return AuthorizeGetByIdRelationshipPreflight(
                    mappingSet,
                    resource,
                    null,
                    null,
                    null,
                    securityConfigurationError.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    securityConfigurationError.RelationshipClassification.SupportedCustomViewStrategies
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return AuthorizeGetByIdRelationshipPreflight(
                    mappingSet,
                    resource,
                    null,
                    null,
                    null,
                    stillUnsupported.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    stillUnsupported.RelationshipClassification.SupportedCustomViewStrategies
                );

            case RelationalAuthorizationPlanOutcome.Plan plan:
                return AuthorizeGetByIdPlanPreflight(mappingSet, resource, plan, authorizationContext);

            default:
                throw new InvalidOperationException(
                    $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
                );
        }
    }

    private GetByIdAuthorizationPreflightResult AuthorizeGetByIdPlanPreflight(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.Plan plan,
        RelationalAuthorizationContext authorizationContext
    )
    {
        if (
            !TryPlanGetByIdCustomViewAuthorization(
                mappingSet,
                resource,
                plan.CustomViewStrategies,
                out var storedCustomViewAuthorization,
                out var customViewSecurityConfigurationFailure,
                out var customViewChecksToValidate
            )
        )
        {
            return new GetByIdAuthorizationPreflightResult.Stop(
                customViewSecurityConfigurationFailure!,
                customViewChecksToValidate
            );
        }

        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization = null;

        if (plan.NamespaceChecks.Count > 0)
        {
            if (
                !NamespacePrefixParameterizationPreflight.TryCreate(
                    mappingSet.Key.Dialect,
                    authorizationContext.NamespacePrefixes,
                    out var namespacePrefixParameterization,
                    out var securityConfigurationMessage,
                    out var securityConfigurationDiagnostics
                )
            )
            {
                return GetByIdTerminal(
                    mappingSet,
                    resource,
                    new GetResult.GetFailureSecurityConfiguration(
                        [securityConfigurationMessage],
                        securityConfigurationDiagnostics
                    ),
                    plan.CustomViewStrategies,
                    plan.NamespaceChecks[0].RawConfiguredIndex
                );
            }

            storedNamespaceAuthorization = new RelationalWriteNamespaceAuthorization(
                plan.NamespaceChecks,
                namespacePrefixParameterization
            );
        }

        // Built after the namespace parameterization, deliberately. Both are setup failures reported as the
        // same security-configuration 500, so whichever is attempted first is the one reported when a request
        // would fail both. NamespaceBased executes ahead of OwnershipBased, so its failure must win.
        if (
            !TryPlanStoredOwnershipAuthorization(
                mappingSet,
                plan.OwnershipCheck,
                authorizationContext,
                out var storedOwnershipAuthorization,
                out var ownershipSecurityConfigurationMessage,
                out var ownershipSecurityConfigurationDiagnostics
            )
        )
        {
            return GetByIdTerminal(
                mappingSet,
                resource,
                new GetResult.GetFailureSecurityConfiguration(
                    [ownershipSecurityConfigurationMessage],
                    ownershipSecurityConfigurationDiagnostics
                ),
                plan.CustomViewStrategies,
                // Every configured view runs before this failure: OwnershipBased executes last among the
                // AND strategies whatever position it is configured at.
                int.MaxValue
            );
        }

        return AuthorizeGetByIdRelationshipPreflight(
            mappingSet,
            resource,
            storedNamespaceAuthorization,
            storedCustomViewAuthorization,
            storedOwnershipAuthorization,
            plan.NonNamespaceConfiguredStrategies,
            authorizationContext
        );
    }

    /// <summary>
    /// Builds the ownership-token parameterization for the planned ownership check, or reports the
    /// security-configuration failure that stops the request. Shared by the GET-by-id and DELETE
    /// preflights, so the two cannot drift on when an over-limit token list fails closed.
    /// </summary>
    /// <remarks>
    /// Defence in depth rather than the primary gate. The planner already returns its own token-cap
    /// terminal, which is what gives the failure correct precedence among the other authorization terminals,
    /// so a request reaching here with a planned check is known to be under the limit. This exists so a
    /// planner change that dropped the terminal still fails closed rather than emitting an over-limit
    /// parameter list at the SQL boundary.
    /// </remarks>
    private static bool TryPlanStoredOwnershipAuthorization(
        MappingSet mappingSet,
        OwnershipAuthorizationCheckSpec? ownershipCheck,
        RelationalAuthorizationContext authorizationContext,
        out RelationalOwnershipAuthorization? storedOwnershipAuthorization,
        out string securityConfigurationMessage,
        out SecurityConfigurationFailureDiagnostic[] securityConfigurationDiagnostics
    )
    {
        storedOwnershipAuthorization = null;
        securityConfigurationMessage = string.Empty;
        securityConfigurationDiagnostics = [];

        if (ownershipCheck is null)
        {
            return true;
        }

        if (
            !OwnershipTokenParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.OwnershipTokenIds,
                out var ownershipTokenParameterization,
                out securityConfigurationMessage,
                out securityConfigurationDiagnostics
            )
        )
        {
            return false;
        }

        storedOwnershipAuthorization = new RelationalOwnershipAuthorization(
            ownershipCheck,
            ownershipTokenParameterization
        );
        return true;
    }

    /// <summary>
    /// Plans the stored custom-view checks, or reports the security-configuration failure that stops the read.
    /// </summary>
    private static bool TryPlanGetByIdCustomViewAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy> customViewStrategies,
        out RelationalCustomViewAuthorization? storedCustomViewAuthorization,
        out GetResult? securityConfigurationFailure,
        out IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checksToValidateBeforeFailure
    )
    {
        storedCustomViewAuthorization = null;
        securityConfigurationFailure = null;
        checksToValidateBeforeFailure = [];

        if (customViewStrategies.Count == 0)
        {
            return true;
        }

        var outcome = SingleRecordCustomViewAuthorizationPlanner.Plan(
            mappingSet,
            mappingSet.GetConcreteResourceModelOrThrow(resource),
            customViewStrategies,
            NamespaceAuthorizationOperation.ReadSingle
        );

        if (
            outcome
            is SingleRecordCustomViewAuthorizationPlanOutcome.SecurityConfiguration configurationFailure
        )
        {
            securityConfigurationFailure = BuildGetAuthorizationSecurityConfigurationFailure(
                mappingSet,
                resource,
                configurationFailure.Failures
            );
            // Views configured ahead of the earliest planning failure planned successfully and execute first,
            // so they are still validated before this failure is reported.
            checksToValidateBeforeFailure = SingleRecordChecksBeforeTerminal(
                configurationFailure.PlannedChecks,
                RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                    configurationFailure.Failures
                )
            );
            return false;
        }

        var checks = ((SingleRecordCustomViewAuthorizationPlanOutcome.Plan)outcome).Checks;

        if (checks.Count > 0)
        {
            storedCustomViewAuthorization = new RelationalCustomViewAuthorization(checks);
        }

        return true;
    }

    private GetByIdAuthorizationPreflightResult AuthorizeGetByIdRelationshipPreflight(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalCustomViewAuthorization? storedCustomViewAuthorization,
        RelationalOwnershipAuthorization? storedOwnershipAuthorization,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext,
        IReadOnlyList<SupportedCustomViewAuthorizationStrategy>? customViewStrategiesToValidate = null
    )
    {
        var storedRelationshipAuthorization = _relationshipAuthorizationPlanner.PlanStoredValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext
        );

        // Reached either from a planner terminal, which carries the strategies it never got to plan, or from
        // the Plan path, where the views planned successfully and are carried on the authorization instead.
        var terminalCustomViewStrategies = customViewStrategiesToValidate ?? [];

        return storedRelationshipAuthorization switch
        {
            // OwnershipBased executes last per auth.md regardless of configured position, so every resolved
            // view runs before this 501.
            RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled =>
                storedCustomViewAuthorization is { } plannedForNotImplemented
                    ? new GetByIdAuthorizationPreflightResult.Stop(
                        new GetResult.GetFailureNotImplemented(
                            BuildKnownButNotEnabledGetAuthorizationMessage(
                                resource,
                                knownButNotEnabled.Failures
                            )
                        ),
                        plannedForNotImplemented.Checks
                    )
                    : GetByIdTerminal(
                        mappingSet,
                        resource,
                        new GetResult.GetFailureNotImplemented(
                            BuildKnownButNotEnabledGetAuthorizationMessage(
                                resource,
                                knownButNotEnabled.Failures
                            )
                        ),
                        terminalCustomViewStrategies,
                        int.MaxValue
                    ),

            RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError =>
                storedCustomViewAuthorization is { } plannedForConfigError
                    ? new GetByIdAuthorizationPreflightResult.Stop(
                        BuildGetAuthorizationSecurityConfigurationFailure(
                            mappingSet,
                            resource,
                            securityConfigurationError.Failures
                        ),
                        SingleRecordChecksBeforeTerminal(
                            plannedForConfigError.Checks,
                            RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                                securityConfigurationError.Failures
                            )
                        )
                    )
                    : GetByIdTerminal(
                        mappingSet,
                        resource,
                        BuildGetAuthorizationSecurityConfigurationFailure(
                            mappingSet,
                            resource,
                            securityConfigurationError.Failures
                        ),
                        terminalCustomViewStrategies,
                        RelationalAuthorizationPlanner.EarliestSecurityConfigurationFailureIndex(
                            securityConfigurationError.Failures
                        )
                    ),

            _ => new GetByIdAuthorizationPreflightResult.Proceed(
                storedNamespaceAuthorization,
                storedCustomViewAuthorization,
                storedOwnershipAuthorization,
                storedRelationshipAuthorization
            ),
        };
    }

    private async Task<GetAuthorizationOutcome> AuthorizeGetByIdAgainstTargetAsync(
        IGetRequest relationalGetRequest,
        MappingSet mappingSet,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalCustomViewAuthorization? storedCustomViewAuthorization,
        RelationalOwnershipAuthorization? storedOwnershipAuthorization,
        RelationshipAuthorizationResult storedRelationshipAuthorization,
        long documentId,
        long storedContentVersion,
        CancellationToken cancellationToken = default
    )
    {
        var authorizationContext = relationalGetRequest.AuthorizationContext;
        var andFilterOutcome = await AuthorizeGetAndFiltersAsync(
                mappingSet,
                documentId,
                storedNamespaceAuthorization,
                storedCustomViewAuthorization,
                storedOwnershipAuthorization
            )
            .ConfigureAwait(false);

        if (andFilterOutcome is not null)
        {
            return andFilterOutcome;
        }

        var relationshipOutcome = await AuthorizeGetRelationshipAsync(
                mappingSet,
                storedRelationshipAuthorization,
                authorizationContext,
                documentId,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (
            storedNamespaceAuthorization is null
            && storedCustomViewAuthorization is null
            && storedOwnershipAuthorization is null
        )
        {
            return relationshipOutcome;
        }

        // The AND checks read the stored row but report no content version, so their decisions are only
        // valid for the version that drove this attempt. The served representation, the relationship
        // boundary, and the post-hydration boundary must all agree on that one version; otherwise a
        // mutation interleaving with the authorization sequence could change a namespace or a basis value
        // and serve a representation those checks never validated.
        return AnchorStoredAndFilterReadBoundary(relationshipOutcome, storedContentVersion);
    }

    /// <summary>
    /// Runs the AND-combined stored checks, returning the first failing outcome or <see langword="null"/>
    /// when all of them authorize.
    /// </summary>
    /// <remarks>
    /// Ownership runs after the namespace and custom-view stage and is deliberately not interleaved with it.
    /// auth.md places <c>OwnershipBased</c> last among the AND strategies whatever position it is configured
    /// at, so its configured index attributes a denial to it but does not order its execution. This is the
    /// one place where configured order and execution order diverge on purpose.
    /// </remarks>
    private async Task<GetAuthorizationOutcome?> AuthorizeGetAndFiltersAsync(
        MappingSet mappingSet,
        long documentId,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalCustomViewAuthorization? storedCustomViewAuthorization,
        RelationalOwnershipAuthorization? storedOwnershipAuthorization
    )
    {
        var namespaceAndCustomViewOutcome = await AuthorizeGetNamespaceAndCustomViewFiltersAsync(
                mappingSet,
                documentId,
                storedNamespaceAuthorization,
                storedCustomViewAuthorization
            )
            .ConfigureAwait(false);

        if (namespaceAndCustomViewOutcome is not null)
        {
            return namespaceAndCustomViewOutcome;
        }

        return storedOwnershipAuthorization is null
            ? null
            : await ExecuteGetOwnershipAuthorizationAsync(
                    mappingSet,
                    documentId,
                    storedOwnershipAuthorization
                )
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the namespace and custom view-based stored checks in CMS-configured order, returning the first
    /// failing outcome or <see langword="null"/> when all of them authorize.
    /// </summary>
    /// <remarks>
    /// Custom views and <c>NamespaceBased</c> interleave by configured index, and the first failure is the one
    /// reported, so custom views configured before the namespace check must run before it and those configured
    /// after must run after. A compiled custom-view batch is one command, so honoring that order costs one
    /// command per contiguous run — two only when custom views straddle the namespace index, which is why the
    /// list is partitioned rather than executed check by check.
    /// </remarks>
    private async Task<GetAuthorizationOutcome?> AuthorizeGetNamespaceAndCustomViewFiltersAsync(
        MappingSet mappingSet,
        long documentId,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalCustomViewAuthorization? storedCustomViewAuthorization
    )
    {
        if (storedNamespaceAuthorization is null)
        {
            return storedCustomViewAuthorization is null
                ? null
                : await ExecuteGetCustomViewAuthorizationAsync(
                        mappingSet,
                        documentId,
                        storedCustomViewAuthorization.Checks,
                        storedCustomViewAuthorization.Checks
                    )
                    .ConfigureAwait(false);
        }

        var (customViewsBeforeNamespace, customViewsAfterNamespace) = storedCustomViewAuthorization is null
            ? ((IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec>)[], [])
            : CustomViewAuthorizationCheckSplitter.PartitionByConfiguredIndex(
                storedCustomViewAuthorization.Checks,
                storedNamespaceAuthorization.Checks[0].RawConfiguredIndex
            );

        if (customViewsBeforeNamespace.Count > 0)
        {
            var beforeOutcome = await ExecuteGetCustomViewAuthorizationAsync(
                    mappingSet,
                    documentId,
                    customViewsBeforeNamespace,
                    storedCustomViewAuthorization!.Checks
                )
                .ConfigureAwait(false);

            if (beforeOutcome is not null)
            {
                return beforeOutcome;
            }
        }

        var namespaceOutcome = await ExecuteGetNamespaceAuthorizationAsync(
                mappingSet,
                documentId,
                storedNamespaceAuthorization
            )
            .ConfigureAwait(false);

        if (namespaceOutcome is not null)
        {
            return namespaceOutcome;
        }

        return customViewsAfterNamespace.Count == 0
            ? null
            : await ExecuteGetCustomViewAuthorizationAsync(
                    mappingSet,
                    documentId,
                    customViewsAfterNamespace,
                    storedCustomViewAuthorization!.Checks
                )
                .ConfigureAwait(false);
    }

    private async Task<GetAuthorizationOutcome?> ExecuteGetCustomViewAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> checks,
        IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> plannedChecks
    )
    {
        var executionResult = await _customViewAuthorizationExecutor
            .ExecuteAsync(
                new CustomViewAuthorizationExecutionRequest(mappingSet, documentId, checks, plannedChecks)
            )
            .ConfigureAwait(false);

        return executionResult switch
        {
            CustomViewAuthorizationExecutionResult.Authorized => null,
            CustomViewAuthorizationExecutionResult.NotAuthorized notAuthorized => new GetAuthorizationOutcome(
                new GetResult.GetFailureCustomViewNotAuthorized(notAuthorized.Failure),
                null,
                false
            ),
            CustomViewAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                new GetAuthorizationOutcome(
                    new GetResult.GetFailureSecurityConfiguration(
                        [invalidFailure.FailureMessage],
                        invalidFailure.Diagnostics
                    ),
                    null,
                    false
                ),
            // The stored target row was deleted between the unlocked target lookup and this check. Retry so
            // the read boundary re-resolves the target; a target still gone on the next attempt is a 404.
            CustomViewAuthorizationExecutionResult.StaleTarget => new GetAuthorizationOutcome(
                null,
                null,
                RetryTargetResolution: true
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported custom view authorization execution result '{executionResult.GetType().Name}'."
            ),
        };
    }

    private async Task<GetAuthorizationOutcome> AuthorizeGetRelationshipAsync(
        MappingSet mappingSet,
        RelationshipAuthorizationResult storedRelationshipAuthorization,
        RelationalAuthorizationContext authorizationContext,
        long documentId,
        CancellationToken cancellationToken = default
    )
    {
        switch (storedRelationshipAuthorization)
        {
            case RelationshipAuthorizationResult.NoAuthorizationRequired:
            case RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                return GetAuthorizationOutcome.NotRequired;

            case RelationshipAuthorizationResult.NoClaims noClaims:
                if (
                    !TryCreateNoClaimsRelationshipAuthorizationFailure(
                        noClaims,
                        authorizationContext.ClaimEducationOrganizationIds,
                        GetByIdRelationshipAuthorizationAuth1Index,
                        out var noClaimsFailure
                    ) || noClaimsFailure is null
                )
                {
                    return new GetAuthorizationOutcome(
                        new GetResult.UnknownFailure(
                            "Relationship authorization required caller EducationOrganizationIds, but denial metadata could not be built."
                        ),
                        null,
                        false
                    );
                }

                return new GetAuthorizationOutcome(
                    CreateGetRelationshipNotAuthorized(noClaimsFailure),
                    null,
                    false
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                return await ExecuteGetRelationshipAuthorizationAsync(
                        mappingSet,
                        documentId,
                        authorized,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            default:
                throw new InvalidOperationException(
                    $"Unsupported relationship authorization result '{storedRelationshipAuthorization.GetType().Name}' after GET-by-id authorization preflight."
                );
        }
    }

    private static GetAuthorizationOutcome AnchorStoredAndFilterReadBoundary(
        GetAuthorizationOutcome relationshipOutcome,
        long storedContentVersion
    )
    {
        // A failure or an already-requested retry from the relationship boundary takes precedence and
        // is returned unchanged.
        if (relationshipOutcome is not { FailureResult: null, RetryTargetResolution: false } authorized)
        {
            return relationshipOutcome;
        }

        // The AND checks ran against the stored row but report no version, so their decisions are only
        // valid for the stored content version. When the relationship boundary either reported no version
        // (a no-op OR group) or reported the same stored version, pin the read boundary to that version so
        // hydration and the post-hydration boundary serve exactly the representation those checks saw.
        if (
            authorized.ObservedContentVersion is null
            || authorized.ObservedContentVersion == storedContentVersion
        )
        {
            return authorized with { ObservedContentVersion = storedContentVersion };
        }

        // The relationship boundary observed a different content version than the one the AND checks
        // authorized, so a mutation interleaved with the authorization sequence and those decisions can no
        // longer be trusted for the version that would be served. Force a retry so the entire
        // authorization sequence re-runs against the current row.
        return authorized with
        {
            RetryTargetResolution = true,
        };
    }

    private async Task<GetAuthorizationOutcome?> ExecuteGetNamespaceAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationalWriteNamespaceAuthorization storedNamespaceAuthorization,
        CancellationToken cancellationToken = default
    )
    {
        var executionResult = await _namespaceAuthorizationExecutor
            .ExecuteAsync(
                new NamespaceAuthorizationExecutionRequest(
                    mappingSet,
                    documentId,
                    ProposedNamespace: null,
                    storedNamespaceAuthorization.Checks,
                    storedNamespaceAuthorization.NamespacePrefixParameterization
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return executionResult switch
        {
            NamespaceAuthorizationExecutionResult.Authorized => null,
            NamespaceAuthorizationExecutionResult.NotAuthorized notAuthorized => new GetAuthorizationOutcome(
                new GetResult.GetFailureNamespaceNotAuthorized(notAuthorized.Failure),
                null,
                false
            ),
            NamespaceAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                new GetAuthorizationOutcome(
                    new GetResult.GetFailureSecurityConfiguration(
                        [invalidFailure.FailureMessage],
                        invalidFailure.Diagnostics
                    ),
                    null,
                    false
                ),
            // The stored target row was deleted between the unlocked target lookup and this check.
            // Request a retry so the read boundary re-resolves the target; a target that is still gone on
            // the next attempt surfaces as a 404 rather than a namespace mismatch.
            NamespaceAuthorizationExecutionResult.StaleTarget => new GetAuthorizationOutcome(
                null,
                null,
                RetryTargetResolution: true
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported namespace authorization execution result '{executionResult.GetType().Name}'."
            ),
        };
    }

    private async Task<GetAuthorizationOutcome?> ExecuteGetOwnershipAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationalOwnershipAuthorization storedOwnershipAuthorization,
        CancellationToken cancellationToken = default
    )
    {
        var executionResult = await _ownershipAuthorizationExecutor
            .ExecuteAsync(
                new OwnershipAuthorizationExecutionRequest(
                    mappingSet,
                    documentId,
                    storedOwnershipAuthorization.Check,
                    storedOwnershipAuthorization.OwnershipTokenParameterization
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return executionResult switch
        {
            OwnershipAuthorizationExecutionResult.Authorized => null,
            OwnershipAuthorizationExecutionResult.NotAuthorized notAuthorized => new GetAuthorizationOutcome(
                new GetResult.GetFailureOwnershipNotAuthorized(notAuthorized.Failure),
                null,
                false
            ),
            OwnershipAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                new GetAuthorizationOutcome(
                    new GetResult.GetFailureSecurityConfiguration(
                        [invalidFailure.FailureMessage],
                        invalidFailure.Diagnostics
                    ),
                    null,
                    false
                ),
            // The stored target row was deleted between the unlocked target lookup and this check.
            // Request a retry so the read boundary re-resolves the target; a target that is still gone on
            // the next attempt surfaces as a 404 rather than an ownership denial.
            OwnershipAuthorizationExecutionResult.StaleTarget => new GetAuthorizationOutcome(
                null,
                null,
                RetryTargetResolution: true
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported ownership authorization execution result '{executionResult.GetType().Name}'."
            ),
        };
    }

    private async Task<GetAuthorizationOutcome> ExecuteGetRelationshipAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationshipAuthorizationResult.Authorized authorized,
        CancellationToken cancellationToken = default
    )
    {
        if (authorized.ClaimEducationOrganizationIdParameterization is null)
        {
            return new GetAuthorizationOutcome(
                new GetResult.UnknownFailure(
                    "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
                ),
                null,
                false
            );
        }

        var authorizationExecutionResult = await _singleRecordRelationshipAuthorizationExecutor
            .ExecuteAsync(
                new SingleRecordRelationshipAuthorizationExecutionRequest(
                    mappingSet,
                    documentId,
                    authorized.CheckSpecs,
                    authorized.ClaimEducationOrganizationIdParameterization,
                    GetByIdRelationshipAuthorizationAuth1Index,
                    authorized.ExecutableShape
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        return authorizationExecutionResult switch
        {
            SingleRecordRelationshipAuthorizationExecutionResult.Authorized authorizationSuccess =>
                new GetAuthorizationOutcome(null, authorizationSuccess.ObservedContentVersion, false),
            SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                new GetAuthorizationOutcome(
                    CreateGetRelationshipNotAuthorized(notAuthorized.RelationshipFailure),
                    null,
                    false
                ),
            SingleRecordRelationshipAuthorizationExecutionResult.StaleTarget => new GetAuthorizationOutcome(
                null,
                null,
                true
            ),
            SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                new GetAuthorizationOutcome(
                    new GetResult.GetFailureSecurityConfiguration(
                        [invalidFailure.FailureMessage],
                        invalidFailure.Diagnostics
                    ),
                    null,
                    false
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported single-record authorization execution result '{authorizationExecutionResult.GetType().Name}'."
            ),
        };
    }

    private static bool ShouldBypassSingleRecordAuthorization(IGetRequest relationalGetRequest) =>
        relationalGetRequest.ReadMode switch
        {
            RelationalGetRequestReadMode.StoredDocument => true,
            RelationalGetRequestReadMode.ExternalResponse => false,
            _ => throw new ArgumentOutOfRangeException(
                nameof(relationalGetRequest),
                relationalGetRequest.ReadMode,
                "Unsupported relational GET read mode."
            ),
        };

    private static bool TryCreateNoClaimsRelationshipAuthorizationFailure(
        RelationshipAuthorizationResult.NoClaims noClaims,
        IReadOnlyList<long> claimEducationOrganizationIds,
        int emittedAuth1Index,
        out RelationshipAuthorizationFailure? relationshipFailure
    ) =>
        RelationshipAuthorizationFailureMapper.TryMapNoClaimsFailure(
            noClaims.CheckSpecs,
            noClaims.Failures,
            claimEducationOrganizationIds,
            emittedAuth1Index,
            out relationshipFailure
        );

    private static GetResult.GetFailureRelationshipNotAuthorized CreateGetRelationshipNotAuthorized(
        RelationshipAuthorizationFailure relationshipFailure
    ) => new(relationshipFailure);

    private static UpsertResult.UpsertFailureRelationshipNotAuthorized CreateUpsertRelationshipNotAuthorized(
        RelationshipAuthorizationFailure relationshipFailure
    ) => new(relationshipFailure);

    private sealed record GetAuthorizationOutcome(
        GetResult? FailureResult,
        long? ObservedContentVersion,
        bool RetryTargetResolution
    )
    {
        public static GetAuthorizationOutcome NotRequired { get; } = new(null, null, false);
    }

    private abstract record GetByIdAuthorizationPreflightResult
    {
        private GetByIdAuthorizationPreflightResult() { }

        // A document-independent namespace planner terminal (no usable root column, no prefixes, or
        // prefix cap exceeded) denied or failed the request before any target lookup.
        /// <param name="CustomViewChecksToValidate">
        /// The views configured strictly before this terminal. They are AND filters executing in
        /// CMS-configured order, so they run first and a missing or non-conforming view keeps its own 500
        /// rather than being hidden by the terminal's response.
        /// </param>
        public sealed record Stop(
            GetResult Result,
            IReadOnlyList<SingleRecordCustomViewAuthorizationCheckSpec> CustomViewChecksToValidate
        ) : GetByIdAuthorizationPreflightResult
        {
            public Stop(GetResult result)
                : this(result, []) { }
        }

        // Document-dependent authorization remains and runs per attempt against the resolved target.
        // StoredNamespaceAuthorization is null when only relationship strategies must be evaluated.
        /// <param name="StoredOwnershipAuthorization">
        /// The planned ownership check, or null when the request configured none. Carried only here and
        /// never on a Stop: unlike a custom view, an ownership check has nothing to validate structurally
        /// ahead of a terminal — its table and column are fixed and its token parameterization was already
        /// validated during preflight — so a document-independent terminal simply preempts it, exactly as it
        /// preempts the namespace check.
        /// </param>
        public sealed record Proceed(
            RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization,
            RelationalCustomViewAuthorization? StoredCustomViewAuthorization,
            RelationalOwnershipAuthorization? StoredOwnershipAuthorization,
            RelationshipAuthorizationResult StoredRelationshipAuthorization
        ) : GetByIdAuthorizationPreflightResult;

        // No per-record authorization is required for this read (StoredDocument-mode bypass); the
        // target is still looked up and served.
        public sealed record AuthorizationNotRequired : GetByIdAuthorizationPreflightResult
        {
            public static AuthorizationNotRequired Instance { get; } = new();
        }
    }

    private static bool ShouldApplyReadableProfileProjection(IGetRequest relationalGetRequest) =>
        relationalGetRequest.ReadMode == RelationalGetRequestReadMode.ExternalResponse
        && relationalGetRequest.ReadableProfileProjectionContext is not null;

    private static string BuildKnownButNotEnabledGetAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures
    ) =>
        BuildKnownButNotEnabledAuthorizationMessage(
            resource,
            knownButNotEnabledFailures,
            operationLabel: "GET-by-id",
            effectiveAuthorizationLabel: "GET",
            executionBoundaryName: "single-record relationship execution boundary",
            supportedStrategySetName: "single-record relationship",
            supportedStrategyNames: RelationshipAuthorizationStrategyCatalog.SupportedRelationshipStrategyNames
        );

    private static string BuildKnownButNotEnabledDeleteAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures
    ) =>
        BuildKnownButNotEnabledAuthorizationMessage(
            resource,
            knownButNotEnabledFailures,
            operationLabel: "DELETE",
            effectiveAuthorizationLabel: "DELETE",
            executionBoundaryName: "single-record relationship execution boundary",
            supportedStrategySetName: "single-record relationship",
            supportedStrategyNames: RelationshipAuthorizationStrategyCatalog.SupportedRelationshipStrategyNames
        );

    private static string BuildKnownButNotEnabledPostAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures
    ) =>
        BuildKnownButNotEnabledAuthorizationMessage(
            resource,
            knownButNotEnabledFailures,
            operationLabel: "POST",
            effectiveAuthorizationLabel: "POST",
            executionBoundaryName: "POST create-new relationship execution boundary",
            supportedStrategySetName: "POST create-new relationship",
            supportedStrategyNames: RelationshipAuthorizationStrategyCatalog.SupportedRelationshipStrategyNames
        );

    private static string BuildKnownButNotEnabledPutAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures
    ) =>
        BuildKnownButNotEnabledAuthorizationMessage(
            resource,
            knownButNotEnabledFailures,
            operationLabel: "PUT",
            effectiveAuthorizationLabel: "PUT",
            executionBoundaryName: "PUT relationship execution boundary",
            supportedStrategySetName: "PUT relationship",
            supportedStrategyNames: RelationshipAuthorizationStrategyCatalog.SupportedRelationshipStrategyNames
        );

    private static GetResult.GetFailureSecurityConfiguration BuildGetAuthorizationSecurityConfigurationFailure(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failures);

        if (HasOnlyEdOrgSubjectSelectionFailures(failures))
        {
            string[] errors =
            [
                BuildEdOrgSubjectSelectionFailureMessage(
                    mappingSet,
                    resource,
                    failures,
                    operationLabel: "GET-by-id",
                    effectiveAuthorizationLabel: "GET"
                ),
            ];

            return new GetResult.GetFailureSecurityConfiguration(
                errors,
                BuildSecurityConfigurationFailureDiagnostics(failures)
            );
        }

        string[] securityConfigurationErrors = BuildSecurityConfigurationFailureMessages(
            mappingSet,
            failures,
            operationLabel: "GET-by-id",
            effectiveAuthorizationLabel: "GET",
            executionBoundaryName: "single-record relationship execution boundary"
        );

        return new GetResult.GetFailureSecurityConfiguration(
            securityConfigurationErrors,
            BuildSecurityConfigurationFailureDiagnostics(failures)
        );
    }

    private static DeleteResult.DeleteFailureSecurityConfiguration BuildDeleteAuthorizationSecurityConfigurationFailure(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failures);

        if (HasOnlyEdOrgSubjectSelectionFailures(failures))
        {
            string[] errors =
            [
                BuildEdOrgSubjectSelectionFailureMessage(
                    mappingSet,
                    resource,
                    failures,
                    operationLabel: "DELETE",
                    effectiveAuthorizationLabel: "DELETE"
                ),
            ];

            return new DeleteResult.DeleteFailureSecurityConfiguration(
                errors,
                BuildSecurityConfigurationFailureDiagnostics(failures)
            );
        }

        string[] securityConfigurationErrors = BuildSecurityConfigurationFailureMessages(
            mappingSet,
            failures,
            operationLabel: "DELETE",
            effectiveAuthorizationLabel: "DELETE",
            executionBoundaryName: "single-record relationship execution boundary"
        );

        return new DeleteResult.DeleteFailureSecurityConfiguration(
            securityConfigurationErrors,
            BuildSecurityConfigurationFailureDiagnostics(failures)
        );
    }

    private static UpsertResult.UpsertFailureSecurityConfiguration BuildPostAuthorizationSecurityConfigurationFailure(
        MappingSet mappingSet,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failures);

        string[] securityConfigurationErrors = BuildSecurityConfigurationFailureMessages(
            mappingSet,
            failures,
            operationLabel: "POST",
            effectiveAuthorizationLabel: "POST",
            executionBoundaryName: "POST create-new relationship execution boundary"
        );

        return new UpsertResult.UpsertFailureSecurityConfiguration(
            securityConfigurationErrors,
            BuildSecurityConfigurationFailureDiagnostics(failures)
        );
    }

    private static UpdateResult.UpdateFailureSecurityConfiguration BuildPutAuthorizationSecurityConfigurationFailure(
        MappingSet mappingSet,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failures);

        string[] securityConfigurationErrors = BuildSecurityConfigurationFailureMessages(
            mappingSet,
            failures,
            operationLabel: "PUT",
            effectiveAuthorizationLabel: "PUT",
            executionBoundaryName: "PUT relationship execution boundary"
        );

        return new UpdateResult.UpdateFailureSecurityConfiguration(
            securityConfigurationErrors,
            BuildSecurityConfigurationFailureDiagnostics(failures)
        );
    }

    private static string BuildKnownButNotEnabledQueryAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures
    ) =>
        BuildKnownButNotEnabledAuthorizationMessage(
            resource,
            knownButNotEnabledFailures,
            operationLabel: "query",
            effectiveAuthorizationLabel: "GET-many",
            executionBoundaryName: "GET-many relationship query execution boundary",
            supportedStrategySetName: "GET-many relationship",
            supportedStrategyNames: RelationshipAuthorizationStrategyCatalog.SupportedRelationshipStrategyNames
        );

    private static string BuildKnownButNotEnabledAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures,
        string operationLabel,
        string effectiveAuthorizationLabel,
        string executionBoundaryName,
        string supportedStrategySetName,
        IReadOnlyList<string> supportedStrategyNames
    )
    {
        var unsupportedStrategyNames = knownButNotEnabledFailures
            .Select(static failure => failure.ConfiguredStrategy?.StrategyName)
            .Where(static strategyName => strategyName is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static strategyName => strategyName, StringComparer.Ordinal)
            .Select(static strategyName => $"'{strategyName}'");
        var supportedStrategyNamesText = string.Join(
            ", ",
            supportedStrategyNames.Select(static strategyName => $"'{strategyName}'")
        );

        return $"Relational {operationLabel} authorization is not implemented for resource '{RelationalWriteSupport.FormatResource(resource)}' "
            + $"when effective {effectiveAuthorizationLabel} authorization includes strategies outside the current {executionBoundaryName}. Unsupported strategies: "
            + $"[{string.Join(", ", unsupportedStrategyNames)}]. Supported {supportedStrategySetName} strategies are "
            + $"{supportedStrategyNamesText}, and "
            + $"'{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' as a no-op.";
    }

    private static string BuildEdOrgSubjectSelectionFailureMessage(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures,
        string operationLabel,
        string effectiveAuthorizationLabel
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failures);

        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies =
        [
            .. failures
                .Select(static failure => failure.ConfiguredStrategy)
                .Where(static configuredStrategy => configuredStrategy is not null)
                .Cast<ConfiguredAuthorizationStrategy>(),
        ];

        var unresolvedDetails = failures
            .Where(static failure =>
                failure.FailureKind is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
            )
            .Select(static failure =>
                FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath)
            )
            .Where(static detail => detail is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static detail => detail, StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();

        var nonRootCandidateDetails = failures
            .Where(static failure =>
                failure.FailureKind is RelationshipAuthorizationFailureKind.NoApplicableRootSubject
                && failure.Location?.JsonPath is not null
            )
            .Select(static failure =>
            {
                var location = failure.Location;

                if (location is null || location.Table is null || location.Column is null)
                {
                    return null;
                }

                var detail = FormatSecurableElementDetail(location.ReadableName, location.JsonPath);

                return $"{detail} -> '{location.Table}.{location.Column.Value}'";
            })
            .Where(static detail => detail is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static detail => detail, StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();

        var configuredDetails = failures
            .Select(static failure =>
                FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath)
            )
            .Where(static detail => detail is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static detail => detail, StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();

        var configuredDetailText =
            configuredDetails.Length == 0
                ? "No EducationOrganization securable elements are configured for this resource."
                : $"Configured elements: [{string.Join(", ", configuredDetails)}].";
        var nonRootCandidateText =
            nonRootCandidateDetails.Length == 0
                ? "No EducationOrganization securable elements resolved to relational columns."
                : $"Resolved non-root candidates: [{string.Join(", ", nonRootCandidateDetails)}].";

        List<string> detailSections = [];

        if (unresolvedDetails.Length > 0)
        {
            detailSections.Add(
                "require resolvable EducationOrganization securable elements, but the following elements could not be resolved to relational columns in mapping set "
                    + $"'{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}': "
                    + $"[{string.Join(", ", unresolvedDetails)}]."
            );
        }

        if (
            failures.Any(static failure =>
                failure.FailureKind is RelationshipAuthorizationFailureKind.NoApplicableRootSubject
            )
        )
        {
            detailSections.Add(
                "require at least one applicable concrete root-table EducationOrganization authorization subject, but none were found in mapping set "
                    + $"'{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}'. "
                    + $"{nonRootCandidateText} {configuredDetailText}"
            );
        }

        return $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(resource)}'. "
            + $"Effective {effectiveAuthorizationLabel} strategies [{FormatStrategyNames(configuredAuthorizationStrategies)}] "
            + string.Join(" ", detailSections);
    }

    private static QueryResult.QueryFailureSecurityConfiguration BuildQueryAuthorizationSecurityConfigurationFailure(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failures);

        if (HasOnlyEdOrgSubjectSelectionFailures(failures))
        {
            string[] errors =
            [
                BuildEdOrgSubjectSelectionFailureMessage(
                    mappingSet,
                    resource,
                    failures,
                    operationLabel: "query",
                    effectiveAuthorizationLabel: "GET-many"
                ),
            ];

            return new QueryResult.QueryFailureSecurityConfiguration(
                errors,
                BuildSecurityConfigurationFailureDiagnostics(failures)
            );
        }

        string[] securityConfigurationErrors = BuildSecurityConfigurationFailureMessages(
            mappingSet,
            failures,
            operationLabel: "query",
            effectiveAuthorizationLabel: "GET-many",
            executionBoundaryName: "GET-many relationship query execution boundary"
        );

        return new QueryResult.QueryFailureSecurityConfiguration(
            securityConfigurationErrors,
            BuildSecurityConfigurationFailureDiagnostics(failures)
        );
    }

    private static SecurityConfigurationFailureDiagnostic[] BuildSecurityConfigurationFailureDiagnostics(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    ) => [.. failures.Select(BuildSecurityConfigurationFailureDiagnostic)];

    private static SecurityConfigurationFailureDiagnostic BuildSecurityConfigurationFailureDiagnostic(
        RelationshipAuthorizationFailureMetadata failure
    )
    {
        string resourceFullName = RelationalWriteSupport.FormatResource(failure.Resource);
        string? physicalPath = FormatPhysicalPath(failure.Location);

        return new SecurityConfigurationFailureDiagnostic(
            ProviderOrPlannerFailureKind: $"RelationshipAuthorization.{failure.FailureKind}",
            ResourceFullName: resourceFullName,
            ConfiguredStrategyNames: failure.ConfiguredStrategy is null
                ? null
                : [failure.ConfiguredStrategy.StrategyName],
            ConfiguredStrategyIndexes: failure.ConfiguredStrategy is null
                ? null
                : [failure.ConfiguredStrategy.RawConfiguredIndex],
            TargetResourceFullName: IsCustomViewFailure(failure) ? resourceFullName : null,
            MissingPropertyName: failure.Location?.ReadableName,
            PhysicalPath: physicalPath
        );
    }

    private static bool IsCustomViewFailure(RelationshipAuthorizationFailureMetadata failure) =>
        failure.FailureKind
            is RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource
                or RelationshipAuthorizationFailureKind.NoCustomViewJoinPath;

    private static string? FormatPhysicalPath(RelationshipAuthorizationFailureLocation? location)
    {
        if (location?.Table is null)
        {
            return null;
        }

        return location.Column is null
            ? location.Table.ToString()
            : $"{location.Table}.{location.Column.Value}";
    }

    private static string[] BuildSecurityConfigurationFailureMessages(
        MappingSet mappingSet,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures,
        string operationLabel,
        string effectiveAuthorizationLabel,
        string executionBoundaryName
    )
    {
        string[] unknownStrategyNames =
        [
            .. failures
                .Where(IsUnknownAuthorizationStrategyFailure)
                .Select(static failure => failure.ConfiguredStrategy?.StrategyName)
                .Where(static strategyName => strategyName is not null)
                .Cast<string>(),
        ];

        var canUseCanonicalUnknownStrategyMessage = unknownStrategyNames.Length > 0;
        var canonicalUnknownStrategyMessageAdded = false;
        List<string> messages = [];

        foreach (var failure in failures)
        {
            if (IsUnknownAuthorizationStrategyFailure(failure) && canUseCanonicalUnknownStrategyMessage)
            {
                if (!canonicalUnknownStrategyMessageAdded)
                {
                    messages.Add(
                        SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies(
                            unknownStrategyNames
                        )
                    );
                    canonicalUnknownStrategyMessageAdded = true;
                }

                continue;
            }

            messages.Add(
                BuildSecurityConfigurationFailureMessage(
                    mappingSet,
                    failure,
                    operationLabel,
                    effectiveAuthorizationLabel,
                    executionBoundaryName
                )
            );
        }

        return [.. messages];
    }

    private static bool IsUnknownAuthorizationStrategyFailure(
        RelationshipAuthorizationFailureMetadata failure
    ) =>
        failure.FailureKind
            is RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy
                or RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource;

    private static bool HasOnlyEdOrgSubjectSelectionFailures(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    ) =>
        failures.Count > 0
        && failures.All(static failure =>
            failure.FailureKind
                is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
                    or RelationshipAuthorizationFailureKind.NoApplicableRootSubject
            && failure.PersonMetadata is null
            && failure.Location?.Kind is null or SecurableElementKind.EducationOrganization
            && failure.Contributors.All(static contributor =>
                contributor.Kind is SecurableElementKind.EducationOrganization
            )
            && failure.SkippedContributors.All(static contributor =>
                contributor.Kind is SecurableElementKind.EducationOrganization
            )
            && failure.IneligibleSubjects.All(static ineligibleSubject =>
                ineligibleSubject.Subject.PersonMetadata is null
            )
        );

    private static string BuildSecurityConfigurationFailureMessage(
        MappingSet mappingSet,
        RelationshipAuthorizationFailureMetadata failure,
        string operationLabel,
        string effectiveAuthorizationLabel,
        string executionBoundaryName
    )
    {
        if (
            TryBuildPeopleSecurityConfigurationFailureMessage(
                mappingSet,
                failure,
                operationLabel,
                out var peopleFailureMessage
            )
        )
        {
            return peopleFailureMessage;
        }

        return failure.FailureKind switch
        {
            RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Effective {effectiveAuthorizationLabel} authorization also includes known-but-not-enabled strategy '{failure.ConfiguredStrategy?.StrategyName}', "
                    + $"which is outside the current {executionBoundaryName}.",
            RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' matches the {{BasisResource}}With... custom-view convention, "
                    + $"but basis resource '{failure.Location?.AuthorizationObjectName}' was not found in mapping set "
                    + $"'{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}'.",
            RelationshipAuthorizationFailureKind.NoCustomViewJoinPath =>
                CustomViewAuthorizationFailureMessages.NoJoinPath(failure, operationLabel),
            RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' is not a recognized built-in strategy and does not match the "
                    + "{BasisResource}With... custom-view convention.",
            RelationshipAuthorizationFailureKind.UnresolvedSecurableElement =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' requires resolvable EducationOrganization securable elements, "
                    + $"but element {FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath)} could not be resolved to a relational column.",
            RelationshipAuthorizationFailureKind.NoApplicableRootSubject =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' requires a concrete root-table EducationOrganization authorization subject, "
                    + $"but {FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath) ?? "no configured EducationOrganization securable element"} "
                    + (
                        failure.Location?.Table is not null && failure.Location?.Column is not null
                            ? $"resolved to '{failure.Location.Table}.{failure.Location.Column.Value}' instead of a '{DbTableKind.Root}' table."
                            : failure.Hint ?? "did not produce a concrete root-table binding."
                    ),
            RelationshipAuthorizationFailureKind.NoExecutableSubjects =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' has no executable relationship authorization subjects for this operation. "
                    + failure.Hint,
            RelationshipAuthorizationFailureKind.MissingProposedRootBinding =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' requires proposed-value EducationOrganization subject "
                    + $"{FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath) ?? "from relationship authorization metadata"}, "
                    + $"but root column '{failure.Location?.Table}.{failure.Location?.Column?.Value}' does not have a matching root write binding.",
            RelationshipAuthorizationFailureKind.MissingProposedCustomViewRootBinding =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' uses custom auth view '{failure.Location?.AuthorizationObjectName ?? "<unknown>"}'. "
                    + (
                        string.IsNullOrWhiteSpace(failure.Hint)
                            ? "The custom view basis resource is reached only through a child collection table, so no root-table value can authorize proposed data for a write."
                            : failure.Hint.Trim()
                    ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(failure),
                failure.FailureKind,
                $"Unsupported {operationLabel} authorization security-configuration failure kind."
            ),
        };
    }

    private static bool TryBuildPeopleSecurityConfigurationFailureMessage(
        MappingSet mappingSet,
        RelationshipAuthorizationFailureMetadata failure,
        string operationLabel,
        out string message
    )
    {
        message = string.Empty;

        if (!TryGetPeopleSubjectKindName(failure, out var subjectKindName))
        {
            return false;
        }

        var resourceName = RelationalWriteSupport.FormatResource(failure.Resource);
        var strategyName = failure.ConfiguredStrategy?.StrategyName;
        var authViewPhrase = FormatPeopleAuthViewPhrase(failure);
        var authViewSentence = FormatPeopleAuthViewSentence(failure);
        var locationSentence = FormatPeopleLocationSentence(failure);
        var contributorSentence = FormatPeopleContributorSentence(failure);
        var skippedContributorSentence = FormatSkippedPeopleContributorSentence(failure);
        var ineligibleSubjectSentence = FormatIneligiblePeopleSubjectSentence(failure);
        var hintSentence = FormatHintSentence(failure.Hint);

        message = failure.FailureKind switch
        {
            RelationshipAuthorizationFailureKind.UnresolvedSecurableElement =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{resourceName}'. "
                    + $"Strategy '{strategyName}' requires resolvable {subjectKindName} securable elements{authViewPhrase}, "
                    + $"but element {FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath) ?? "from People relationship authorization metadata"} "
                    + "could not be resolved to a DocumentId-based relational path."
                    + contributorSentence
                    + hintSentence,
            RelationshipAuthorizationFailureKind.NoApplicableRootSubject =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{resourceName}'. "
                    + $"Strategy '{strategyName}' has no applicable {subjectKindName} relationship authorization subject{authViewPhrase}. "
                    + locationSentence
                    + authViewSentence
                    + contributorSentence
                    + skippedContributorSentence
                    + hintSentence,
            RelationshipAuthorizationFailureKind.NoExecutableSubjects =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{resourceName}'. "
                    + $"Strategy '{strategyName}' has no executable {subjectKindName} relationship authorization subjects for this operation. "
                    + authViewSentence
                    + contributorSentence
                    + ineligibleSubjectSentence
                    + hintSentence,
            RelationshipAuthorizationFailureKind.MissingProposedRootBinding =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{resourceName}'. "
                    + $"Strategy '{strategyName}' requires proposed-value {subjectKindName} relationship authorization subject "
                    + $"{FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath) ?? "from People relationship authorization metadata"}{authViewPhrase}, "
                    + $"but anchor column '{failure.Location?.Table}.{failure.Location?.Column?.Value}' does not have a matching root write binding."
                    + contributorSentence
                    + hintSentence,
            RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations =>
                $"Relational {operationLabel} authorization metadata is invalid for resource '{resourceName}'. "
                    + $"Strategy '{strategyName}' selects People relationship subject '{subjectKindName}' "
                    + $"through auth view '{failure.Location?.AuthorizationObjectName}', but the people auth views were not emitted in mapping set "
                    + $"'{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}'. {failure.Hint}",
            _ => string.Empty,
        };

        return message.Length > 0;
    }

    private static bool TryGetPeopleSubjectKindName(
        RelationshipAuthorizationFailureMetadata failure,
        out string subjectKindName
    )
    {
        if (failure.Location?.Kind is { } locationKind && IsPeopleSecurableElementKind(locationKind))
        {
            subjectKindName = locationKind.ToString();
            return true;
        }

        var contributorKind = failure
            .Contributors.Select(static contributor => contributor.Kind)
            .FirstOrDefault(IsPeopleSecurableElementKind);

        if (IsPeopleSecurableElementKind(contributorKind))
        {
            subjectKindName = contributorKind.ToString();
            return true;
        }

        var skippedContributorKind = failure
            .SkippedContributors.Select(static contributor => contributor.Kind)
            .FirstOrDefault(IsPeopleSecurableElementKind);

        if (IsPeopleSecurableElementKind(skippedContributorKind))
        {
            subjectKindName = skippedContributorKind.ToString();
            return true;
        }

        var ineligibleSubjectKind = failure
            .IneligibleSubjects.SelectMany(static ineligibleSubject =>
                ineligibleSubject.Subject.Contributors.Select(static contributor => contributor.Kind)
            )
            .FirstOrDefault(IsPeopleSecurableElementKind);

        if (IsPeopleSecurableElementKind(ineligibleSubjectKind))
        {
            subjectKindName = ineligibleSubjectKind.ToString();
            return true;
        }

        if (failure.PersonMetadata is not null)
        {
            subjectKindName = failure.PersonMetadata.PersonKind.ToString();
            return true;
        }

        subjectKindName = string.Empty;
        return false;
    }

    private static bool IsPeopleSecurableElementKind(SecurableElementKind kind) =>
        kind is SecurableElementKind.Student or SecurableElementKind.Contact or SecurableElementKind.Staff;

    private static string FormatPeopleAuthViewPhrase(RelationshipAuthorizationFailureMetadata failure)
    {
        var authViewName = GetPeopleAuthViewName(failure);

        return authViewName is null ? string.Empty : $" through auth view '{authViewName}'";
    }

    private static string FormatPeopleAuthViewSentence(RelationshipAuthorizationFailureMetadata failure)
    {
        var authViewName = GetPeopleAuthViewName(failure);

        return authViewName is null ? string.Empty : $"Auth view: '{authViewName}'. ";
    }

    private static string? GetPeopleAuthViewName(RelationshipAuthorizationFailureMetadata failure)
    {
        var subjectAuthViewNames = failure
            .IneligibleSubjects.Where(static ineligibleSubject =>
                ineligibleSubject.Subject.PersonMetadata is not null
            )
            .Select(static ineligibleSubject => ineligibleSubject.Subject.AuthObject.Name.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (subjectAuthViewNames.Length == 1)
        {
            return subjectAuthViewNames[0];
        }

        return failure.AuthObject?.Name.ToString() ?? failure.Location?.AuthorizationObjectName;
    }

    private static string FormatPeopleLocationSentence(RelationshipAuthorizationFailureMetadata failure)
    {
        var elementDetail = FormatSecurableElementDetail(
            failure.Location?.ReadableName,
            failure.Location?.JsonPath
        );

        if (elementDetail is null)
        {
            return string.Empty;
        }

        if (failure.Location?.Table is not null && failure.Location.Column is not null)
        {
            return $"Element {elementDetail} resolved to '{failure.Location.Table}.{failure.Location.Column.Value}'. ";
        }

        return $"Element {elementDetail} did not produce an executable People subject. ";
    }

    private static string FormatPeopleContributorSentence(RelationshipAuthorizationFailureMetadata failure) =>
        FormatContributorSentence(
            "Contributors",
            failure.Contributors.Select(static contributor =>
                FormatSecurableElementDetail(contributor.ReadableName, contributor.JsonPath)
            )
        );

    private static string FormatSkippedPeopleContributorSentence(
        RelationshipAuthorizationFailureMetadata failure
    ) =>
        FormatContributorSentence(
            "Skipped People securable elements",
            failure.SkippedContributors.Select(static contributor =>
            {
                var elementDetail =
                    FormatSecurableElementDetail(contributor.ReadableName, contributor.JsonPath)
                    ?? $"'{contributor.Kind}'";
                var columnDetail =
                    contributor.Table is not null && contributor.Column is not null
                        ? $"; column: '{contributor.Table}.{contributor.Column.Value}'"
                        : string.Empty;
                var authViewDetail = contributor.AuthObject is not null
                    ? $"; auth view: '{contributor.AuthObject.Name}'"
                    : string.Empty;

                return $"{elementDetail} (reason: {contributor.Reason}{columnDetail}{authViewDetail})";
            })
        );

    private static string FormatIneligiblePeopleSubjectSentence(
        RelationshipAuthorizationFailureMetadata failure
    ) =>
        FormatContributorSentence(
            "Ineligible People subjects",
            failure.IneligibleSubjects.Select(ineligibleSubject =>
            {
                var contributorDetail =
                    ineligibleSubject
                        .Subject.Contributors.Select(static contributor =>
                            FormatSecurableElementDetail(contributor.ReadableName, contributor.JsonPath)
                        )
                        .FirstOrDefault(static detail => detail is not null)
                    ?? $"'{ineligibleSubject.Subject.Table}.{ineligibleSubject.Subject.Column.Value}'";
                var authViewDetail = ineligibleSubject.Subject.PersonMetadata is not null
                    ? $"; auth view: '{ineligibleSubject.Subject.AuthObject.Name}'"
                    : string.Empty;

                return $"{contributorDetail} (reason: {ineligibleSubject.Reason}{authViewDetail})";
            })
        );

    private static string FormatContributorSentence(string label, IEnumerable<string?> details)
    {
        var distinctDetails = details
            .Where(static detail => detail is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static detail => detail, StringComparer.Ordinal)
            .ToArray();

        return distinctDetails.Length == 0
            ? string.Empty
            : $"{label}: [{string.Join(", ", distinctDetails)}]. ";
    }

    private static string FormatHintSentence(string? hint) =>
        string.IsNullOrWhiteSpace(hint) ? string.Empty : $" {hint}";

    private static string FormatStrategyNames(
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies
    ) =>
        string.Join(
            ", ",
            configuredAuthorizationStrategies
                .Select(static strategy => strategy.StrategyName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static strategyName => strategyName, StringComparer.Ordinal)
                .Select(static strategyName => $"'{strategyName}'")
        );

    private static string? FormatSecurableElementDetail(string? readableName, string? jsonPath)
    {
        if (readableName is null && jsonPath is null)
        {
            return null;
        }

        return $"'{readableName ?? "<unknown>"}' at '{jsonPath ?? "<unknown>"}'";
    }

    private QueryResult BuildQuerySuccess(
        IQueryRequest relationalQueryRequest,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan,
        HydratedPage hydratedPage
    )
    {
        ArgumentNullException.ThrowIfNull(relationalQueryRequest);
        ArgumentNullException.ThrowIfNull(readPlan);
        ArgumentNullException.ThrowIfNull(hydratedPage);

        JsonArray edfiDocs = [];
        var projectionContext = relationalQueryRequest.ReadableProfileProjectionContext;
        var materializedDocuments = _readMaterializer.MaterializePage(
            new RelationalReadPageMaterializationRequest(
                readPlan,
                hydratedPage,
                RelationalReadMaterializationMode.ExternalResponse
            )
            {
                MappingSet = relationalQueryRequest.MappingSet,
                EtagVariant = new EtagVariantInputs(
                    projectionContext?.ProfileName,
                    ResponseFormat.Json,
                    relationalQueryRequest.ResponseContentCoding
                ),
            }
        );

        foreach (
            var edfiDoc in materializedDocuments.Select(static materializedDocument =>
                materializedDocument.Document
            )
        )
        {
            var projectedOrUnchangedDocument = edfiDoc;

            if (projectionContext is not null)
            {
                projectedOrUnchangedDocument = _readableProfileProjector.Project(
                    projectedOrUnchangedDocument,
                    projectionContext.ContentTypeDefinition,
                    projectionContext.IdentityPropertyNames
                );
            }

            _readMaterializer.StripReferenceLinks(projectedOrUnchangedDocument, readPlan);

            edfiDocs.Add(projectedOrUnchangedDocument);
        }

        // The selected-keyset boundary passes through unchanged, including when the body above came back
        // empty: it describes what page selection chose, not what survived to hydration. It is already
        // expressed in the key the page was ordered by, because selection carried that key out with the
        // ids, so there is nothing further to qualify it with here.
        return new QueryResult.QuerySuccess(
            edfiDocs,
            relationalQueryRequest.Paging.IncludesTotalCount
                ? RelationalReadGuardrails.ConvertTotalCountOrThrow(
                    resource,
                    hydratedPage.TotalCount,
                    "query hydration"
                )
                : null,
            hydratedPage.HighestSelectedAnchor
        );
    }

    private static WritePrecondition NormalizeWritePrecondition(WritePrecondition? writePrecondition) =>
        writePrecondition ?? new WritePrecondition.None();
}
