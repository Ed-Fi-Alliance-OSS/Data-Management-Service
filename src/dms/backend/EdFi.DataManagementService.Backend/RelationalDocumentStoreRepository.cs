// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
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
    IRelationalWriteTargetLookupService targetLookupService,
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
    IRelationalParameterConfigurator? relationalParameterConfigurator = null,
    IRelationshipAuthorizationProviderFailureExtractor? relationshipAuthorizationProviderFailureExtractor =
        null
) : IDocumentStoreRepository, IQueryHandler
{
    private const int GetByIdRelationshipAuthorizationAuth1Index = 0;
    private const int DeleteRelationshipAuthorizationAuth1Index = 0;
    internal const int PostRelationshipAuthorizationAuth1Index = 0;
    internal const int PutRelationshipAuthorizationAuth1Index = 0;
    private const int GetByIdReadBoundaryAttemptCount = 2;

    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRelationalWriteExecutor _writeExecutor =
        writeExecutor ?? throw new ArgumentNullException(nameof(writeExecutor));
    private readonly IRelationalWriteTargetLookupService _targetLookupService =
        targetLookupService ?? throw new ArgumentNullException(nameof(targetLookupService));
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
    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;
    private readonly IRelationshipAuthorizationProviderFailureExtractor _relationshipAuthorizationProviderFailureExtractor =
        relationshipAuthorizationProviderFailureExtractor
        ?? DefaultRelationshipAuthorizationProviderFailureExtractor.Instance;
    private readonly RelationshipAuthorizationPlanner _relationshipAuthorizationPlanner = new(
        edOrgAuthorizationSubjectSelector
    );

    public async Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
    {
        ArgumentNullException.ThrowIfNull(upsertRequest);
        var relationalUpsertRequest = RequireRelationalRequest<IRelationalUpsertRequest>(
            upsertRequest,
            nameof(upsertRequest)
        );
        var mappingSet = relationalUpsertRequest.MappingSet;
        ArgumentNullException.ThrowIfNull(mappingSet);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpsertDocument - {TraceId}",
            relationalUpsertRequest.TraceId.Value
        );

        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalUpsertRequest.ResourceInfo);
        var writePrecondition = NormalizeWritePrecondition(relationalUpsertRequest.WritePrecondition);

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await _descriptorWriteHandler
                .HandlePostAsync(
                    new DescriptorWriteRequest(
                        mappingSet,
                        resource,
                        relationalUpsertRequest.EdfiDoc,
                        relationalUpsertRequest.DocumentUuid,
                        relationalUpsertRequest.DocumentInfo.ReferentialId,
                        relationalUpsertRequest.TraceId,
                        relationalUpsertRequest.AuthorizationStrategyEvaluators,
                        relationalUpsertRequest.AuthorizationContext
                    )
                    {
                        WritePrecondition = writePrecondition,
                    }
                )
                .ConfigureAwait(false);
        }

        var profileWriteContext = relationalUpsertRequest.BackendProfileWriteContext;
        var selectedBody =
            profileWriteContext?.Request.WritableRequestBody ?? relationalUpsertRequest.EdfiDoc;

        var result = await ExecuteWriteGuardRails<UpsertResult>(
                requestBody: selectedBody,
                writePrecondition: writePrecondition,
                traceId: relationalUpsertRequest.TraceId,
                mappingSet,
                relationalUpsertRequest.ResourceInfo,
                RelationalWriteOperationKind.Post,
                new RelationalWriteTargetRequest.Post(
                    relationalUpsertRequest.DocumentInfo.ReferentialId,
                    relationalUpsertRequest.DocumentUuid
                ),
                relationalUpsertRequest.DocumentInfo.DocumentReferences,
                relationalUpsertRequest.DocumentInfo.DescriptorReferences,
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
                    AuthorizePostRelationshipIfRequired(
                        relationalUpsertRequest,
                        mappingSet,
                        resource,
                        writePlan
                    )
            )
            .ConfigureAwait(false);

        return result;
    }

    public Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        ArgumentNullException.ThrowIfNull(getRequest);
        var relationalGetRequest = RequireRelationalRequest<IRelationalGetRequest>(
            getRequest,
            nameof(getRequest)
        );
        var mappingSet = relationalGetRequest.MappingSet;
        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalGetRequest.ResourceInfo);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.GetDocumentById - {TraceId}",
            relationalGetRequest.TraceId.Value
        );

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return _descriptorReadHandler.HandleGetByIdAsync(
                new DescriptorGetByIdRequest(
                    mappingSet,
                    resource,
                    relationalGetRequest.DocumentUuid,
                    relationalGetRequest.ReadMode,
                    relationalGetRequest.AuthorizationStrategyEvaluators,
                    relationalGetRequest.ReadableProfileProjectionContext,
                    relationalGetRequest.TraceId,
                    relationalGetRequest.AuthorizationContext
                )
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

        return GetDocumentByIdAsync(relationalGetRequest, mappingSet, resource, readPlan);
    }

    public async Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
    {
        ArgumentNullException.ThrowIfNull(updateRequest);
        var relationalUpdateRequest = RequireRelationalRequest<IRelationalUpdateRequest>(
            updateRequest,
            nameof(updateRequest)
        );
        var mappingSet = relationalUpdateRequest.MappingSet;
        ArgumentNullException.ThrowIfNull(mappingSet);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.UpdateDocumentById - {TraceId}",
            relationalUpdateRequest.TraceId.Value
        );

        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalUpdateRequest.ResourceInfo);
        var writePrecondition = NormalizeWritePrecondition(relationalUpdateRequest.WritePrecondition);

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await _descriptorWriteHandler
                .HandlePutAsync(
                    new DescriptorWriteRequest(
                        mappingSet,
                        resource,
                        relationalUpdateRequest.EdfiDoc,
                        relationalUpdateRequest.DocumentUuid,
                        referentialId: null,
                        relationalUpdateRequest.TraceId,
                        relationalUpdateRequest.AuthorizationStrategyEvaluators,
                        relationalUpdateRequest.AuthorizationContext
                    )
                    {
                        WritePrecondition = writePrecondition,
                    }
                )
                .ConfigureAwait(false);
        }

        var profileWriteContext = relationalUpdateRequest.BackendProfileWriteContext;
        var selectedBody =
            profileWriteContext?.Request.WritableRequestBody ?? relationalUpdateRequest.EdfiDoc;

        var result = await ExecuteWriteGuardRails<UpdateResult>(
                requestBody: selectedBody,
                writePrecondition: writePrecondition,
                traceId: relationalUpdateRequest.TraceId,
                mappingSet,
                relationalUpdateRequest.ResourceInfo,
                RelationalWriteOperationKind.Put,
                new RelationalWriteTargetRequest.Put(relationalUpdateRequest.DocumentUuid),
                relationalUpdateRequest.DocumentInfo.DocumentReferences,
                relationalUpdateRequest.DocumentInfo.DescriptorReferences,
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
                    AuthorizePutRelationshipIfRequired(
                        relationalUpdateRequest,
                        mappingSet,
                        resource,
                        writePlan
                    )
            )
            .ConfigureAwait(false);

        return result;
    }

    public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        ArgumentNullException.ThrowIfNull(deleteRequest);
        var relationalDeleteRequest = RequireRelationalRequest<IRelationalDeleteRequest>(
            deleteRequest,
            nameof(deleteRequest)
        );

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.DeleteDocumentById - {TraceId}",
            LoggingSanitizer.SanitizeForLogging(relationalDeleteRequest.TraceId.Value)
        );

        var mappingSet = relationalDeleteRequest.MappingSet;
        ArgumentNullException.ThrowIfNull(mappingSet);

        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalDeleteRequest.ResourceInfo);
        var writePrecondition = NormalizeWritePrecondition(relationalDeleteRequest.WritePrecondition);

        if (relationalDeleteRequest.ResourceInfo.IsDescriptor)
        {
            return _descriptorWriteHandler.HandleDeleteAsync(
                new DescriptorDeleteRequest(
                    mappingSet,
                    resource,
                    relationalDeleteRequest.DocumentUuid,
                    relationalDeleteRequest.TraceId,
                    relationalDeleteRequest.AuthorizationStrategyEvaluators,
                    relationalDeleteRequest.AuthorizationContext
                )
                {
                    WritePrecondition = writePrecondition,
                }
            );
        }

        // Namespace planner terminals (no usable root column, no prefixes, MSSQL prefix cap) resolve
        // before the write session opens, so a denial issues no DB roundtrip and never locks the
        // target. The stored namespace check itself runs inside the delete session against the locked
        // target (see AuthorizeDeleteIfRequiredAsync).
        var authorizationPreflight = AuthorizeDeletePreflight(relationalDeleteRequest, mappingSet, resource);

        return authorizationPreflight switch
        {
            DeleteAuthorizationPreflightResult.Stop stop => Task.FromResult(stop.Result),
            DeleteAuthorizationPreflightResult.Proceed proceed => DeleteDocumentByIdAsync(
                relationalDeleteRequest,
                mappingSet,
                resource,
                writePrecondition,
                proceed.StoredNamespaceAuthorization,
                proceed.NonNamespaceConfiguredStrategies
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported relational delete authorization preflight result '{authorizationPreflight.GetType().Name}'."
            ),
        };
    }

    private async Task<DeleteResult> DeleteDocumentByIdAsync(
        IRelationalDeleteRequest relationalDeleteRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        WritePrecondition writePrecondition,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies
    )
    {
        var documentUuid = relationalDeleteRequest.DocumentUuid;
        var traceId = relationalDeleteRequest.TraceId;
        var readPlanPreparation =
            writePrecondition is WritePrecondition.IfMatch
                ? PrepareExistingDocumentReadPlan(mappingSet, resource)
                : new ExistingDocumentReadPlanPreparation(null, null);

        if (writePrecondition is WritePrecondition.IfMatch && readPlanPreparation.ReadPlan is null)
        {
            return new DeleteResult.UnknownFailure(
                readPlanPreparation.FailureMessage
                    ?? RelationalWriteSupport.BuildMissingExistingDocumentReadPlanMessage(resource)
            );
        }

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
            var sessionCommandExecutor = writeSession.CreateCommandExecutor();

            DeleteResult outcome;

            try
            {
                var resolved = await RelationalDocumentUuidLookupSupport
                    .TryResolveDeleteTargetAsync(sessionCommandExecutor, mappingSet, resource, documentUuid)
                    .ConfigureAwait(false);

                if (resolved is null)
                {
                    outcome = new DeleteResult.DeleteFailureNotExists();
                }
                else
                {
                    var lockedContentVersion = await TryLockDeleteTargetAsync(
                            sessionCommandExecutor,
                            mappingSet.Key.Dialect,
                            resolved.DocumentId
                        )
                        .ConfigureAwait(false);

                    if (lockedContentVersion is null)
                    {
                        outcome = new DeleteResult.DeleteFailureNotExists();
                    }
                    else
                    {
                        var lockedTargetContext = new RelationalWriteTargetContext.ExistingDocument(
                            resolved.DocumentId,
                            documentUuid,
                            lockedContentVersion.Value
                        );

                        var authorizationFailure = await AuthorizeDeleteIfRequiredAsync(
                                mappingSet,
                                resource,
                                resolved.DocumentId,
                                storedNamespaceAuthorization,
                                nonNamespaceConfiguredStrategies,
                                relationalDeleteRequest.AuthorizationContext,
                                sessionCommandExecutor
                            )
                            .ConfigureAwait(false);

                        if (authorizationFailure is not null)
                        {
                            outcome = authorizationFailure;
                        }
                        else
                        {
                            outcome = await ExecuteAuthorizedDeleteAsync(
                                    mappingSet,
                                    readPlanPreparation,
                                    documentUuid,
                                    traceId,
                                    writePrecondition,
                                    writeSession,
                                    sessionCommandExecutor,
                                    lockedTargetContext
                                )
                                .ConfigureAwait(false);
                        }
                    }
                }
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

    private async Task<DeleteResult> ExecuteAuthorizedDeleteAsync(
        MappingSet mappingSet,
        ExistingDocumentReadPlanPreparation readPlanPreparation,
        DocumentUuid documentUuid,
        TraceId traceId,
        WritePrecondition writePrecondition,
        IRelationalWriteSession writeSession,
        IRelationalCommandExecutor sessionCommandExecutor,
        RelationalWriteTargetContext.ExistingDocument lockedTargetContext
    )
    {
        if (writePrecondition is WritePrecondition.IfMatch ifMatch)
        {
            var preconditionCheckResult = await _deleteEtagPreconditionChecker
                .CheckAsync(
                    mappingSet,
                    readPlanPreparation.ReadPlan!,
                    lockedTargetContext,
                    ifMatch,
                    writeSession
                )
                .ConfigureAwait(false);

            if (preconditionCheckResult is null)
            {
                return new DeleteResult.DeleteFailureNotExists();
            }

            if (!preconditionCheckResult.IsMatch)
            {
                return new DeleteResult.DeleteFailureETagMisMatch();
            }

            return await ExecuteDeleteByDocumentIdAsync(
                    mappingSet,
                    preconditionCheckResult.TargetContext.DocumentId,
                    documentUuid,
                    traceId,
                    sessionCommandExecutor
                )
                .ConfigureAwait(false);
        }

        return await ExecuteDeleteByDocumentIdAsync(
                mappingSet,
                lockedTargetContext.DocumentId,
                documentUuid,
                traceId,
                sessionCommandExecutor
            )
            .ConfigureAwait(false);
    }

    private async Task<DeleteResult> ExecuteDeleteByDocumentIdAsync(
        MappingSet mappingSet,
        long documentId,
        DocumentUuid documentUuid,
        TraceId traceId,
        IRelationalCommandExecutor sessionCommandExecutor
    )
    {
        var deleteCommand = BuildDocumentDeleteByDocumentIdCommand(mappingSet.Key.Dialect, documentId);

        return await RelationalDeleteExecution
            .TryExecuteAsync(
                sessionCommandExecutor,
                deleteCommand,
                _writeExceptionClassifier,
                _deleteConstraintResolver,
                mappingSet.Model,
                _logger,
                documentUuid,
                traceId,
                DeleteTargetKind.Document
            )
            .ConfigureAwait(false);
    }

    private static Task<long?> TryLockDeleteTargetAsync(
        IRelationalCommandExecutor commandExecutor,
        SqlDialect dialect,
        long documentId,
        CancellationToken cancellationToken = default
    )
    {
        return commandExecutor.ExecuteReaderAsync<long?>(
            RelationalDocumentLockCommandBuilder.BuildContentVersionCommand(dialect, documentId),
            async (reader, ct) =>
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var contentVersion = reader.GetRequiredFieldValue<long>("ContentVersion");

                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Relational DELETE lock returned multiple rows for document id {documentId}."
                    );
                }

                return contentVersion;
            },
            cancellationToken
        );
    }

    private async Task<DeleteResult?> AuthorizeDeleteIfRequiredAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        long documentId,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext,
        IRelationalCommandExecutor sessionCommandExecutor
    )
    {
        // Namespace AND-composes before the relationship OR group; the stored namespace check runs
        // against the locked target and before any precondition result.
        if (storedNamespaceAuthorization is not null)
        {
            var namespaceFailure = await ExecuteDeleteNamespaceAuthorizationAsync(
                    mappingSet,
                    documentId,
                    storedNamespaceAuthorization,
                    sessionCommandExecutor
                )
                .ConfigureAwait(false);

            if (namespaceFailure is not null)
            {
                return namespaceFailure;
            }
        }

        var relationshipAuthorizationResult = _relationshipAuthorizationPlanner.PlanStoredValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext
        );

        if (
            TryCreatePeopleEndpointStagingFailures(
                resource,
                nonNamespaceConfiguredStrategies,
                relationshipAuthorizationResult,
                out var peopleStagingFailures
            )
        )
        {
            return new DeleteResult.DeleteFailureNotImplemented(
                BuildKnownButNotEnabledDeleteAuthorizationMessage(
                    resource,
                    CombinePeopleEndpointStagingFailures(
                        peopleStagingFailures,
                        relationshipAuthorizationResult
                    )
                )
            );
        }

        switch (relationshipAuthorizationResult)
        {
            case RelationshipAuthorizationResult.NoAuthorizationRequired:
            case RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                return null;

            case RelationshipAuthorizationResult.NoClaims noClaims:
                if (
                    !TryCreateNoClaimsRelationshipAuthorizationFailure(
                        noClaims,
                        authorizationContext.ClaimEducationOrganizationIds,
                        DeleteRelationshipAuthorizationAuth1Index,
                        out var noClaimsFailure
                    ) || noClaimsFailure is null
                )
                {
                    return new DeleteResult.UnknownFailure(
                        "Relationship authorization required caller EducationOrganizationIds, but denial metadata could not be built."
                    );
                }

                return CreateDeleteRelationshipNotAuthorized(noClaimsFailure);

            case RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled:
                return new DeleteResult.DeleteFailureNotImplemented(
                    BuildKnownButNotEnabledDeleteAuthorizationMessage(resource, knownButNotEnabled.Failures)
                );

            case RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError:
                return BuildDeleteAuthorizationSecurityConfigurationFailure(
                    mappingSet,
                    resource,
                    securityConfigurationError.Failures
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                return await ExecuteDeleteRelationshipAuthorizationAsync(
                        mappingSet,
                        documentId,
                        authorized,
                        sessionCommandExecutor
                    )
                    .ConfigureAwait(false);

            default:
                throw new InvalidOperationException(
                    $"Unsupported relationship authorization result '{relationshipAuthorizationResult.GetType().Name}'."
                );
        }
    }

    private static DeleteAuthorizationPreflightResult AuthorizeDeletePreflight(
        IRelationalDeleteRequest relationalDeleteRequest,
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
                return new DeleteAuthorizationPreflightResult.Stop(
                    new DeleteResult.DeleteFailureSecurityConfiguration([
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ])
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return new DeleteAuthorizationPreflightResult.Stop(
                    new DeleteResult.DeleteFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    )
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return new DeleteAuthorizationPreflightResult.Proceed(
                    null,
                    securityConfigurationError.NonNamespaceConfiguredStrategies
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return new DeleteAuthorizationPreflightResult.Proceed(
                    null,
                    stillUnsupported.NonNamespaceConfiguredStrategies
                );

            case RelationalAuthorizationPlanOutcome.Plan plan:
                return AuthorizeDeletePlanPreflight(
                    mappingSet,
                    plan,
                    relationalDeleteRequest.AuthorizationContext
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
                );
        }
    }

    private static DeleteAuthorizationPreflightResult AuthorizeDeletePlanPreflight(
        MappingSet mappingSet,
        RelationalAuthorizationPlanOutcome.Plan plan,
        RelationalAuthorizationContext authorizationContext
    )
    {
        if (plan.NamespaceChecks.Count == 0)
        {
            return new DeleteAuthorizationPreflightResult.Proceed(
                null,
                plan.NonNamespaceConfiguredStrategies
            );
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage
            )
        )
        {
            return new DeleteAuthorizationPreflightResult.Stop(
                new DeleteResult.DeleteFailureSecurityConfiguration([securityConfigurationMessage])
            );
        }

        return new DeleteAuthorizationPreflightResult.Proceed(
            new RelationalWriteNamespaceAuthorization(plan.NamespaceChecks, namespacePrefixParameterization),
            plan.NonNamespaceConfiguredStrategies
        );
    }

    private async Task<DeleteResult?> ExecuteDeleteNamespaceAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationalWriteNamespaceAuthorization storedNamespaceAuthorization,
        IRelationalCommandExecutor sessionCommandExecutor
    )
    {
        // Run the stored check against the delete session's command executor so it executes inside the
        // same transaction that already locked the target document.
        return await StoredNamespaceAuthorizationExecution
            .ExecuteAsync<DeleteResult>(
                sessionCommandExecutor,
                _relationshipAuthorizationProviderFailureExtractor,
                mappingSet,
                documentId,
                storedNamespaceAuthorization,
                onNotAuthorized: failure => new DeleteResult.DeleteFailureNamespaceNotAuthorized(failure),
                onInvalidAuthorizationFailure: failureMessage => new DeleteResult.DeleteFailureSecurityConfiguration([
                    failureMessage,
                ]),
                // The target is row-locked before this check, so a stale target is not expected; map it
                // to not-exists defensively for the rare case where the lock did not hold.
                onStaleTarget: () => new DeleteResult.DeleteFailureNotExists()
            )
            .ConfigureAwait(false);
    }

    private abstract record DeleteAuthorizationPreflightResult
    {
        private DeleteAuthorizationPreflightResult() { }

        public sealed record Stop(DeleteResult Result) : DeleteAuthorizationPreflightResult;

        public sealed record Proceed(
            RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization,
            IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespaceConfiguredStrategies
        ) : DeleteAuthorizationPreflightResult;
    }

    private async Task<DeleteResult?> ExecuteDeleteRelationshipAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationshipAuthorizationResult.Authorized authorized,
        IRelationalCommandExecutor sessionCommandExecutor
    )
    {
        if (authorized.ClaimEducationOrganizationIdParameterization is null)
        {
            return new DeleteResult.UnknownFailure(
                "Relationship authorization produced executable checks without claim EducationOrganizationId parameterization."
            );
        }

        var authorizationExecutor = new SingleRecordRelationshipAuthorizationExecutor(
            sessionCommandExecutor,
            _relationalParameterConfigurator,
            _relationshipAuthorizationProviderFailureExtractor,
            _logger
        );
        var authorizationExecutionResult = await authorizationExecutor
            .ExecuteAsync(
                new SingleRecordRelationshipAuthorizationExecutionRequest(
                    mappingSet,
                    documentId,
                    authorized.CheckSpecs,
                    authorized.ClaimEducationOrganizationIdParameterization,
                    DeleteRelationshipAuthorizationAuth1Index
                )
            )
            .ConfigureAwait(false);

        return authorizationExecutionResult switch
        {
            SingleRecordRelationshipAuthorizationExecutionResult.Authorized => null,
            SingleRecordRelationshipAuthorizationExecutionResult.NotAuthorized notAuthorized =>
                CreateDeleteRelationshipNotAuthorized(notAuthorized.RelationshipFailure),
            SingleRecordRelationshipAuthorizationExecutionResult.StaleTarget =>
                new DeleteResult.DeleteFailureNotExists(),
            SingleRecordRelationshipAuthorizationExecutionResult.InvalidAuthorizationFailure invalidFailure =>
                new DeleteResult.DeleteFailureSecurityConfiguration([invalidFailure.FailureMessage]),
            _ => throw new InvalidOperationException(
                $"Unsupported single-record authorization execution result '{authorizationExecutionResult.GetType().Name}'."
            ),
        };
    }

    private static DeleteResult.DeleteFailureRelationshipNotAuthorized CreateDeleteRelationshipNotAuthorized(
        RelationshipAuthorizationFailure relationshipFailure
    ) => new(relationshipFailure);

    private static RelationalCommand BuildDocumentDeleteByDocumentIdCommand(
        SqlDialect dialect,
        long documentId
    )
    {
        return dialect switch
        {
            SqlDialect.Pgsql => new RelationalCommand(
                """
                DELETE FROM dms."Document"
                WHERE "DocumentId" = @documentId
                RETURNING "DocumentId";
                """,
                [new RelationalParameter("@documentId", documentId)]
            ),
            SqlDialect.Mssql => new RelationalCommand(
                """
                DELETE FROM [dms].[Document]
                OUTPUT DELETED.[DocumentId]
                WHERE [DocumentId] = @documentId;
                """,
                [new RelationalParameter("@documentId", documentId)]
            ),
            _ => throw new NotSupportedException(
                $"Relational delete does not support SQL dialect '{dialect}'."
            ),
        };
    }

    public async Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        ArgumentNullException.ThrowIfNull(queryRequest);
        var relationalQueryRequest = RequireRelationalRequest<IRelationalQueryRequest>(
            queryRequest,
            nameof(queryRequest)
        );
        var mappingSet = relationalQueryRequest.MappingSet;
        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalQueryRequest.ResourceInfo);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.QueryDocuments - {TraceId}",
            relationalQueryRequest.TraceId.Value
        );

        if (mappingSet.TryGetDescriptorResourceModel(resource, out _))
        {
            return await _descriptorReadHandler
                .HandleQueryAsync(
                    new DescriptorQueryRequest(
                        mappingSet,
                        resource,
                        relationalQueryRequest.QueryElements,
                        relationalQueryRequest.PaginationParameters,
                        relationalQueryRequest.AuthorizationStrategyEvaluators,
                        relationalQueryRequest.ReadableProfileProjectionContext,
                        relationalQueryRequest.TraceId,
                        relationalQueryRequest.AuthorizationContext
                    )
                )
                .ConfigureAwait(false);
        }

        RelationalQueryCapability queryCapability;

        try
        {
            queryCapability = relationalQueryRequest.MappingSet.GetQueryCapabilityOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return new QueryResult.QueryFailureNotImplemented(ex.Message);
        }
        catch (MissingQueryCapabilityLookupGuardRailException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }

        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            relationalQueryRequest.AuthorizationStrategyEvaluators
        );
        var authorizationResolution = ResolveQueryAuthorization(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            relationalQueryRequest.AuthorizationContext,
            relationalQueryRequest.PaginationParameters.TotalCount
        );

        PageDocumentIdAuthorizationSpec? pageQueryAuthorization;

        switch (authorizationResolution)
        {
            case QueryAuthorizationResolution.Complete complete:
                return complete.Result;

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
                    relationalQueryRequest.QueryElements,
                    queryCapability,
                    _referenceResolver
                )
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }

        if (preprocessingResult.Outcome is RelationalQueryPreprocessingOutcome.EmptyPage)
        {
            return new QueryResult.QuerySuccess(
                [],
                relationalQueryRequest.PaginationParameters.TotalCount ? 0 : null
            );
        }

        ResourceReadPlan readPlan;

        try
        {
            readPlan = mappingSet.GetReadPlanOrThrow(resource);
        }
        catch (NotSupportedException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }

        PageKeysetSpec.Query? plannedQuery;

        try
        {
            var planner = new RelationalQueryPageKeysetPlanner(mappingSet.Key.Dialect);

            if (
                !planner.TryPlan(
                    readPlan.Model.Root,
                    preprocessingResult,
                    relationalQueryRequest.PaginationParameters,
                    out plannedQuery,
                    out _,
                    authorization: pageQueryAuthorization
                ) || plannedQuery is null
            )
            {
                return new QueryResult.QuerySuccess(
                    [],
                    relationalQueryRequest.PaginationParameters.TotalCount ? 0 : null
                );
            }
        }
        catch (NotSupportedException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new QueryResult.UnknownFailure(ex.Message);
        }

        // Fail closed when the planned page query would bind more parameters than SQL Server allows. Keyed
        // off the final planned parameter count so it covers every empty-page short-circuit — both
        // preprocessing (e.g. an invalid-UUID filter) and planning (e.g. an invalid scalar root-column
        // filter), which both return an empty page above before reaching here — and reflects the exact
        // command rather than an estimate. The non-authorization count (filter + paging parameters) is the
        // planned total minus the authorization lists, which the message reports alongside the prefix and
        // education-organization counts.
        var nonAuthorizationParameterCount =
            plannedQuery.ParameterValues.Count
            - AuthorizationParameterBudget.CountAuthorizationParameters(
                pageQueryAuthorization?.NamespacePrefixParameterization,
                pageQueryAuthorization?.ClaimEducationOrganizationIdParameterization
            );
        if (
            BuildQueryParameterBudgetFailure(
                mappingSet.Key.Dialect,
                pageQueryAuthorization?.NamespacePrefixParameterization,
                pageQueryAuthorization?.ClaimEducationOrganizationIdParameterization,
                nonAuthorizationParameterCount
            ) is
            { } parameterBudgetFailure
        )
        {
            return parameterBudgetFailure;
        }

        var hydratedPage = await _documentHydrator
            .HydrateAsync(readPlan, plannedQuery, new HydrationExecutionOptions(), default)
            .ConfigureAwait(false);

        return BuildQuerySuccess(relationalQueryRequest, resource, readPlan, hydratedPage);
    }

    private QueryAuthorizationResolution ResolveQueryAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext,
        bool totalCount
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
                return new QueryAuthorizationResolution.Complete(
                    new QueryResult.QueryFailureSecurityConfiguration([
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ])
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return new QueryAuthorizationResolution.Complete(
                    new QueryResult.QueryFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    )
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return ResolveQueryRelationshipAuthorization(
                    mappingSet,
                    resource,
                    securityConfigurationError.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    totalCount,
                    [],
                    null
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return ResolveQueryRelationshipAuthorization(
                    mappingSet,
                    resource,
                    stillUnsupported.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    totalCount,
                    [],
                    null
                );

            case RelationalAuthorizationPlanOutcome.Plan plan:
                return ResolveQueryPlanAuthorization(
                    mappingSet,
                    resource,
                    plan,
                    authorizationContext,
                    totalCount
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
                );
        }
    }

    private QueryAuthorizationResolution ResolveQueryPlanAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalAuthorizationPlanOutcome.Plan plan,
        RelationalAuthorizationContext authorizationContext,
        bool totalCount
    )
    {
        if (plan.NamespaceChecks.Count == 0)
        {
            return ResolveQueryRelationshipAuthorization(
                mappingSet,
                resource,
                plan.NonNamespaceConfiguredStrategies,
                authorizationContext,
                totalCount,
                [],
                null
            );
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage
            )
        )
        {
            return new QueryAuthorizationResolution.Complete(
                new QueryResult.QueryFailureSecurityConfiguration([securityConfigurationMessage])
            );
        }

        return ResolveQueryRelationshipAuthorization(
            mappingSet,
            resource,
            plan.NonNamespaceConfiguredStrategies,
            authorizationContext,
            totalCount,
            plan.NamespaceChecks,
            namespacePrefixParameterization
        );
    }

    private QueryAuthorizationResolution ResolveQueryRelationshipAuthorization(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext,
        bool totalCount,
        IReadOnlyList<NamespaceAuthorizationCheckSpec> namespaceChecks,
        NamespacePrefixParameterization? namespacePrefixParameterization
    )
    {
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
                    ComposePageQueryAuthorization(null, namespaceChecks, namespacePrefixParameterization)
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                return new QueryAuthorizationResolution.Proceed(
                    ComposePageQueryAuthorization(
                        PageDocumentIdAuthorizationSpecAdapter.Adapt(authorized),
                        namespaceChecks,
                        namespacePrefixParameterization
                    )
                );

            case RelationshipAuthorizationResult.NoClaims:
                return new QueryAuthorizationResolution.Complete(
                    new QueryResult.QuerySuccess([], totalCount ? 0 : null)
                );

            case RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled:
                return new QueryAuthorizationResolution.Complete(
                    new QueryResult.QueryFailureNotImplemented(
                        BuildKnownButNotEnabledQueryAuthorizationMessage(
                            resource,
                            knownButNotEnabled.Failures
                        )
                    )
                );

            case RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError:
                return new QueryAuthorizationResolution.Complete(
                    BuildQueryAuthorizationSecurityConfigurationFailure(
                        mappingSet,
                        resource,
                        securityConfigurationError.Failures
                    )
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported relationship authorization result '{relationshipAuthorizationResult.GetType().Name}'."
                );
        }
    }

    /// <summary>
    /// Returns a security-configuration failure when the authorization parameters this query binds, plus
    /// its filter and paging parameters, exceed SQL Server's per-command parameter ceiling; otherwise
    /// <see langword="null"/>. Either authorization parameterization may be <see langword="null"/>, so this
    /// covers the namespace-only, relationship-only, and composed query shapes uniformly.
    /// </summary>
    private static QueryResult? BuildQueryParameterBudgetFailure(
        SqlDialect dialect,
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

        return new QueryResult.QueryFailureSecurityConfiguration([
            NamespaceAuthorizationSecurityConfigurationMessages.CommandParameterCapExceeded(
                namespacePrefixParameterization?.ConfiguredPrefixesInOrder.Count ?? 0,
                claimEducationOrganizationIdParameterization?.ClaimEducationOrganizationIds.Count ?? 0,
                nonAuthorizationParameterCount
            ),
        ]);
    }

    private static PageDocumentIdAuthorizationSpec? ComposePageQueryAuthorization(
        PageDocumentIdAuthorizationSpec? relationshipAuthorization,
        IReadOnlyList<NamespaceAuthorizationCheckSpec> namespaceChecks,
        NamespacePrefixParameterization? namespacePrefixParameterization
    )
    {
        if (namespaceChecks.Count == 0)
        {
            return relationshipAuthorization;
        }

        return (relationshipAuthorization ?? new PageDocumentIdAuthorizationSpec([])) with
        {
            NamespaceChecks = namespaceChecks,
            NamespacePrefixParameterization = namespacePrefixParameterization,
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
        IRelationalUpsertRequest relationalUpsertRequest,
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
                return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                    new UpsertResult.UpsertFailureSecurityConfiguration([
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ])
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                    new UpsertResult.UpsertFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    )
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return AuthorizePostRelationshipBucket(
                    mappingSet,
                    resource,
                    writePlan,
                    securityConfigurationError.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    storedNamespaceAuthorization: null,
                    proposedNamespaceAuthorization: null
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return AuthorizePostRelationshipBucket(
                    mappingSet,
                    resource,
                    writePlan,
                    stillUnsupported.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    storedNamespaceAuthorization: null,
                    proposedNamespaceAuthorization: null
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
        if (plan.NamespaceChecks.Count == 0)
        {
            return AuthorizePostRelationshipBucket(
                mappingSet,
                resource,
                writePlan,
                plan.NonNamespaceConfiguredStrategies,
                authorizationContext,
                storedNamespaceAuthorization: null,
                proposedNamespaceAuthorization: null
            );
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                new UpsertResult.UpsertFailureSecurityConfiguration([securityConfigurationMessage])
            );
        }

        var (storedNamespaceAuthorization, proposedNamespaceAuthorization) = SplitNamespaceAuthorization(
            plan.NamespaceChecks,
            namespacePrefixParameterization
        );

        return AuthorizePostRelationshipBucket(
            mappingSet,
            resource,
            writePlan,
            plan.NonNamespaceConfiguredStrategies,
            authorizationContext,
            storedNamespaceAuthorization,
            proposedNamespaceAuthorization
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
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization
    )
    {
        var relationshipAuthorizationPlan = _relationshipAuthorizationPlanner.PlanUpdateValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext,
            writePlan
        );

        var securityConfigurationFailures = relationshipAuthorizationPlan.SecurityConfigurationFailures;

        if (
            securityConfigurationFailures.Count > 0
            && !HasOnlyPeopleEndpointStagingCompatibleFailures(securityConfigurationFailures)
        )
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                BuildPostAuthorizationSecurityConfigurationFailure(mappingSet, securityConfigurationFailures)
            );
        }

        if (
            TryCreatePeopleEndpointStagingFailures(
                resource,
                nonNamespaceConfiguredStrategies,
                out var peopleStagingFailures
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                new UpsertResult.UpsertFailureNotImplemented(
                    BuildKnownButNotEnabledPostAuthorizationMessage(
                        resource,
                        CombinePeopleEndpointStagingFailures(
                            peopleStagingFailures,
                            relationshipAuthorizationPlan
                        )
                    ),
                    UpsertFailureNotImplementedReason.StrategyNotEnabled
                )
            );
        }

        return relationshipAuthorizationPlan.ProposedValues switch
        {
            RelationshipAuthorizationResult.NoAuthorizationRequired
            or RelationshipAuthorizationResult.NoFurtherAuthorizationRequired =>
                new WriteGuardRailPreflightResult<UpsertResult>.Continue(
                    null,
                    null,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization
                ),

            RelationshipAuthorizationResult.Authorized authorized =>
                new WriteGuardRailPreflightResult<UpsertResult>.Continue(
                    relationshipAuthorizationPlan.StoredValues,
                    authorized,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization
                ),

            // NamespaceBased AND-composes before relationship OR strategies (auth.md). When a
            // proposed namespace check is also planned, defer NoClaims through Continue so the
            // namespace check gets to deny first; ProposedRelationshipAuthorizationOrchestrator
            // emits the NoClaims failure if namespace authorized. With no namespace check planned,
            // short-circuit at preflight to avoid a needless executor roundtrip.
            RelationshipAuthorizationResult.NoClaims noClaims => proposedNamespaceAuthorization is null
            && storedNamespaceAuthorization is null
                ? BuildNoClaimsPostRelationshipAuthorizationFailure(noClaims, authorizationContext)
                : new WriteGuardRailPreflightResult<UpsertResult>.Continue(
                    null,
                    noClaims,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization
                ),

            RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled =>
                new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                    new UpsertResult.UpsertFailureNotImplemented(
                        BuildKnownButNotEnabledPostAuthorizationMessage(
                            resource,
                            knownButNotEnabled.Failures
                        ),
                        UpsertFailureNotImplementedReason.StrategyNotEnabled
                    )
                ),

            RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError =>
                new WriteGuardRailPreflightResult<UpsertResult>.Stop(
                    BuildPostAuthorizationSecurityConfigurationFailure(
                        mappingSet,
                        securityConfigurationError.Failures
                    )
                ),

            _ => throw new InvalidOperationException(
                $"Unsupported relationship authorization result '{relationshipAuthorizationPlan.ProposedValues.GetType().Name}'."
            ),
        };
    }

    private WriteGuardRailPreflightResult<UpdateResult> AuthorizePutRelationshipIfRequired(
        IRelationalUpdateRequest relationalUpdateRequest,
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
                return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                    new UpdateResult.UpdateFailureSecurityConfiguration([
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ])
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                    new UpdateResult.UpdateFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    )
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return AuthorizePutRelationshipBucket(
                    mappingSet,
                    resource,
                    writePlan,
                    securityConfigurationError.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    storedNamespaceAuthorization: null,
                    proposedNamespaceAuthorization: null
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return AuthorizePutRelationshipBucket(
                    mappingSet,
                    resource,
                    writePlan,
                    stillUnsupported.NonNamespaceConfiguredStrategies,
                    authorizationContext,
                    storedNamespaceAuthorization: null,
                    proposedNamespaceAuthorization: null
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
        if (plan.NamespaceChecks.Count == 0)
        {
            return AuthorizePutRelationshipBucket(
                mappingSet,
                resource,
                writePlan,
                plan.NonNamespaceConfiguredStrategies,
                authorizationContext,
                storedNamespaceAuthorization: null,
                proposedNamespaceAuthorization: null
            );
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                new UpdateResult.UpdateFailureSecurityConfiguration([securityConfigurationMessage])
            );
        }

        var (storedNamespaceAuthorization, proposedNamespaceAuthorization) = SplitNamespaceAuthorization(
            plan.NamespaceChecks,
            namespacePrefixParameterization
        );

        return AuthorizePutRelationshipBucket(
            mappingSet,
            resource,
            writePlan,
            plan.NonNamespaceConfiguredStrategies,
            authorizationContext,
            storedNamespaceAuthorization,
            proposedNamespaceAuthorization
        );
    }

    private WriteGuardRailPreflightResult<UpdateResult> AuthorizePutRelationshipBucket(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceWritePlan writePlan,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        RelationalAuthorizationContext authorizationContext,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization
    )
    {
        var relationshipAuthorizationPlan = _relationshipAuthorizationPlanner.PlanUpdateValues(
            mappingSet,
            resource,
            nonNamespaceConfiguredStrategies,
            authorizationContext,
            writePlan
        );

        var securityConfigurationFailures = relationshipAuthorizationPlan.SecurityConfigurationFailures;

        if (
            securityConfigurationFailures.Count > 0
            && !HasOnlyPeopleEndpointStagingCompatibleFailures(securityConfigurationFailures)
        )
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                BuildPutAuthorizationSecurityConfigurationFailure(mappingSet, securityConfigurationFailures)
            );
        }

        if (
            TryCreatePeopleEndpointStagingFailures(
                resource,
                nonNamespaceConfiguredStrategies,
                out var peopleStagingFailures
            )
        )
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                new UpdateResult.UpdateFailureNotImplemented(
                    BuildKnownButNotEnabledPutAuthorizationMessage(
                        resource,
                        CombinePeopleEndpointStagingFailures(
                            peopleStagingFailures,
                            relationshipAuthorizationPlan
                        )
                    ),
                    UpdateFailureNotImplementedReason.StrategyNotEnabled
                )
            );
        }

        var knownButNotEnabledFailures = relationshipAuthorizationPlan.KnownButNotEnabledFailures;

        if (knownButNotEnabledFailures.Count > 0)
        {
            return new WriteGuardRailPreflightResult<UpdateResult>.Stop(
                new UpdateResult.UpdateFailureNotImplemented(
                    BuildKnownButNotEnabledPutAuthorizationMessage(resource, knownButNotEnabledFailures),
                    UpdateFailureNotImplementedReason.StrategyNotEnabled
                )
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
                    proposedNamespaceAuthorization
                ),

            // NamespaceBased AND-composes before the relationship OR group (auth.md,
            // 08-namespace-auth-strategy.md). When a namespace check is planned, defer the stored
            // relationship NoClaims denial into the proposed-relationship slot so the stored-then-proposed
            // namespace checks get to deny first; ProposedRelationshipAuthorizationOrchestrator emits the
            // NoClaims denial only after the proposed namespace check authorizes. With no namespace check
            // planned, keep NoClaims in the stored slot so the stored boundary emits it after the target
            // lock, preserving the existing 404-over-403 ordering for a missing PUT target.
            RelationshipAuthorizationResult.NoClaims noClaims => storedNamespaceAuthorization is null
            && proposedNamespaceAuthorization is null
                ? new WriteGuardRailPreflightResult<UpdateResult>.Continue(
                    noClaims,
                    null,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization
                )
                : new WriteGuardRailPreflightResult<UpdateResult>.Continue(
                    null,
                    noClaims,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization
                ),

            RelationshipAuthorizationResult.Authorized authorized =>
                new WriteGuardRailPreflightResult<UpdateResult>.Continue(
                    authorized,
                    relationshipAuthorizationPlan.ProposedValues
                        as RelationshipAuthorizationResult.Authorized,
                    storedNamespaceAuthorization,
                    proposedNamespaceAuthorization
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

    private async Task<TResult> ExecuteWriteGuardRails<TResult>(
        System.Text.Json.Nodes.JsonNode requestBody,
        WritePrecondition writePrecondition,
        TraceId traceId,
        MappingSet mappingSet,
        ResourceInfo resourceInfo,
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetRequest targetRequest,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        Func<string, TResult> failureFactory,
        Func<RelationalWriteExecutorResult, TResult> executorResultProjector,
        BackendProfileWriteContext? profileWriteContext = null,
        Func<ResourceWritePlan, WriteGuardRailPreflightResult<TResult>>? preflight = null
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
                    break;

                case WriteGuardRailPreflightResult<TResult>.Stop stopResult:
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
            var targetResolution = await ResolveTargetContextAsync(
                    mappingSet,
                    resource,
                    operationKind,
                    targetRequest
                )
                .ConfigureAwait(false);

            if (targetResolution.ImmediateResult is not null)
            {
                return executorResultProjector(targetResolution.ImmediateResult);
            }

            var targetContext = targetResolution.TargetContext!;
            if (readPlanPreparation.ReadPlan is null)
            {
                return failureFactory(
                    readPlanPreparation.FailureMessage
                        ?? RelationalWriteSupport.BuildMissingExistingDocumentReadPlanMessage(resource)
                );
            }

            var executorResult = await _writeExecutor
                .ExecuteAsync(
                    new RelationalWriteExecutorRequest(
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
                        targetContext: targetContext,
                        profileWriteContext: profileWriteContext,
                        writePrecondition: writePrecondition,
                        storedRelationshipAuthorization: storedRelationshipAuthorization,
                        proposedRelationshipAuthorization: proposedRelationshipAuthorization,
                        storedNamespaceAuthorization: storedNamespaceAuthorization,
                        proposedNamespaceAuthorization: proposedNamespaceAuthorization
                    )
                )
                .ConfigureAwait(false);

            if (
                executorResult.AttemptOutcome is RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare
                && attemptIndex == 0
                && writePrecondition is not WritePrecondition.IfMatch
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

    private async Task<TargetContextResolution> ResolveTargetContextAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalWriteOperationKind operationKind,
        RelationalWriteTargetRequest targetRequest
    )
    {
        var targetLookupResult = targetRequest switch
        {
            RelationalWriteTargetRequest.Post(var referentialId, var candidateDocumentUuid) =>
                await _targetLookupService
                    .ResolveForPostAsync(mappingSet, resource, referentialId, candidateDocumentUuid)
                    .ConfigureAwait(false),
            RelationalWriteTargetRequest.Put(var documentUuid) => await _targetLookupService
                .ResolveForPutAsync(mappingSet, resource, documentUuid)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Relational repository target lookup does not support target request type '{targetRequest.GetType().Name}'."
            ),
        };

        var targetContext = RelationalWriteSupport.TryTranslateTargetContext(targetLookupResult);

        if (targetContext is not null)
        {
            return new TargetContextResolution(targetContext, null);
        }

        if (
            operationKind == RelationalWriteOperationKind.Put
            && targetLookupResult is RelationalWriteTargetLookupResult.NotFound
        )
        {
            return new TargetContextResolution(
                null,
                new RelationalWriteExecutorResult.Update(new UpdateResult.UpdateFailureNotExists())
            );
        }

        throw new InvalidOperationException(
            $"Relational {operationKind} repository target lookup returned unsupported result type '{targetLookupResult.GetType().Name}'."
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

    private sealed record TargetContextResolution(
        RelationalWriteTargetContext? TargetContext,
        RelationalWriteExecutorResult? ImmediateResult
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
                RelationalWriteNamespaceAuthorization? proposedNamespaceAuthorization = null
            )
            {
                ValidateStoredRelationshipAuthorization(storedRelationshipAuthorization);
                ValidateProposedRelationshipAuthorization(proposedRelationshipAuthorization);
                StoredRelationshipAuthorization = storedRelationshipAuthorization;
                ProposedRelationshipAuthorization = proposedRelationshipAuthorization;
                StoredNamespaceAuthorization = storedNamespaceAuthorization;
                ProposedNamespaceAuthorization = proposedNamespaceAuthorization;
            }

            public RelationshipAuthorizationResult? StoredRelationshipAuthorization { get; }

            public RelationshipAuthorizationResult? ProposedRelationshipAuthorization { get; }

            public RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization { get; }

            public RelationalWriteNamespaceAuthorization? ProposedNamespaceAuthorization { get; }

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

        public sealed record Stop(TResult Result) : WriteGuardRailPreflightResult<TResult>;
    }

    private async Task<GetResult> GetDocumentByIdAsync(
        IRelationalGetRequest relationalGetRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan
    )
    {
        // Namespace planner terminals (no usable root column, no prefixes, MSSQL prefix cap) resolve
        // before the target lookup, so a denial issues no read roundtrip and never depends on document
        // existence. The stored namespace check itself runs per attempt against the resolved target
        // (see AuthorizeGetByIdAgainstTargetAsync).
        var authorizationPreflight = AuthorizeGetByIdPreflight(relationalGetRequest, mappingSet, resource);

        if (authorizationPreflight is GetByIdAuthorizationPreflightResult.Stop preflightStop)
        {
            return preflightStop.Result;
        }

        for (var attemptIndex = 0; attemptIndex < GetByIdReadBoundaryAttemptCount; attemptIndex++)
        {
            var targetLookupResult = await _readTargetLookupService
                .ResolveForGetByIdAsync(mappingSet, resource, relationalGetRequest.DocumentUuid)
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
                            resource,
                            proceed.StoredNamespaceAuthorization,
                            proceed.NonNamespaceConfiguredStrategies,
                            existingDocument.DocumentId,
                            existingDocument.ContentVersion
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

            // StoredDocument-mode reads do not emit `link`, so the auxiliary document-reference
            // lookup is wasted work — opt out via IncludeDocumentReferenceLookup: false. Descriptor
            // URIs are still needed for both read modes.
            var hydrationExecutionOptions = new HydrationExecutionOptions(
                IncludeDocumentReferenceLookup: relationalGetRequest.ReadMode
                    == RelationalGetRequestReadMode.ExternalResponse
            );
            var hydratedPage = await _documentHydrator
                .HydrateAsync(
                    readPlan,
                    new PageKeysetSpec.Single(existingDocument.DocumentId),
                    hydrationExecutionOptions,
                    default
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
                    authorizationOutcome.ObservedContentVersion
                )
                .ConfigureAwait(false);

            if (shouldRetryPostHydrationReadBoundary)
            {
                continue;
            }

            var edfiDoc = _readMaterializer.Materialize(
                new RelationalReadMaterializationRequest(
                    readPlan,
                    documentMetadata,
                    hydratedPage.TableRowsInDependencyOrder,
                    hydratedPage.DescriptorRowsInPlanOrder,
                    relationalGetRequest.ReadMode
                )
                {
                    MappingSet = mappingSet,
                    DocumentReferenceLookup = hydratedPage.DocumentReferenceLookup,
                }
            );

            if (ShouldApplyReadableProfileProjection(relationalGetRequest))
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

        return new GetResult.UnknownFailure(
            "Relational GET could not read a stable authorized representation for the requested document."
        );
    }

    private async Task<bool> ShouldRetryPostHydrationReadBoundaryAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalReadTargetLookupResult.ExistingDocument expectedDocument,
        long? observedContentVersion
    )
    {
        if (observedContentVersion is null)
        {
            return false;
        }

        var targetLookupResult = await _readTargetLookupService
            .ResolveForGetByIdAsync(mappingSet, resource, expectedDocument.DocumentUuid)
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

    private static GetByIdAuthorizationPreflightResult AuthorizeGetByIdPreflight(
        IRelationalGetRequest relationalGetRequest,
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
                return new GetByIdAuthorizationPreflightResult.Stop(
                    new GetResult.GetFailureSecurityConfiguration([
                        NamespaceAuthorizationSecurityConfigurationMessages.NoUsableRootColumn(
                            RelationalWriteSupport.FormatResource(noUsableRoot.Resource)
                        ),
                    ])
                );

            case RelationalAuthorizationPlanOutcome.NoPrefixesConfigured noPrefixes:
                return new GetByIdAuthorizationPreflightResult.Stop(
                    new GetResult.GetFailureNamespaceNotAuthorized(
                        NamespaceAuthorizationFactory.NoPrefixesConfiguredFailure(noPrefixes.StrategyName)
                    )
                );

            case RelationalAuthorizationPlanOutcome.SecurityConfigurationError securityConfigurationError:
                return new GetByIdAuthorizationPreflightResult.Proceed(
                    null,
                    securityConfigurationError.NonNamespaceConfiguredStrategies
                );

            case RelationalAuthorizationPlanOutcome.StillUnsupported stillUnsupported:
                return new GetByIdAuthorizationPreflightResult.Proceed(
                    null,
                    stillUnsupported.NonNamespaceConfiguredStrategies
                );

            case RelationalAuthorizationPlanOutcome.Plan plan:
                return AuthorizeGetByIdPlanPreflight(mappingSet, plan, authorizationContext);

            default:
                throw new InvalidOperationException(
                    $"Unsupported relational authorization plan outcome '{orchestratorOutcome.GetType().Name}'."
                );
        }
    }

    private static GetByIdAuthorizationPreflightResult AuthorizeGetByIdPlanPreflight(
        MappingSet mappingSet,
        RelationalAuthorizationPlanOutcome.Plan plan,
        RelationalAuthorizationContext authorizationContext
    )
    {
        if (plan.NamespaceChecks.Count == 0)
        {
            return new GetByIdAuthorizationPreflightResult.Proceed(
                null,
                plan.NonNamespaceConfiguredStrategies
            );
        }

        if (
            !NamespacePrefixParameterizationPreflight.TryCreate(
                mappingSet.Key.Dialect,
                authorizationContext.NamespacePrefixes,
                out var namespacePrefixParameterization,
                out var securityConfigurationMessage
            )
        )
        {
            return new GetByIdAuthorizationPreflightResult.Stop(
                new GetResult.GetFailureSecurityConfiguration([securityConfigurationMessage])
            );
        }

        return new GetByIdAuthorizationPreflightResult.Proceed(
            new RelationalWriteNamespaceAuthorization(plan.NamespaceChecks, namespacePrefixParameterization),
            plan.NonNamespaceConfiguredStrategies
        );
    }

    private async Task<GetAuthorizationOutcome> AuthorizeGetByIdAgainstTargetAsync(
        IRelationalGetRequest relationalGetRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        RelationalWriteNamespaceAuthorization? storedNamespaceAuthorization,
        IReadOnlyList<ConfiguredAuthorizationStrategy> nonNamespaceConfiguredStrategies,
        long documentId,
        long storedContentVersion
    )
    {
        var authorizationContext = relationalGetRequest.AuthorizationContext;

        if (storedNamespaceAuthorization is not null)
        {
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

            var relationshipOutcome = await AuthorizeGetRelationshipAsync(
                    mappingSet,
                    resource,
                    nonNamespaceConfiguredStrategies,
                    authorizationContext,
                    documentId
                )
                .ConfigureAwait(false);

            // Namespace authorization read the stored row but does not report a content version, so
            // its decision must be pinned to the stored content version that drove this attempt. The
            // served representation, the relationship boundary, and the post-hydration boundary must
            // all agree on that one version; otherwise a mutation that interleaved with the
            // authorization sequence could change the namespace and serve a representation the
            // namespace check never validated.
            return AnchorNamespaceReadBoundary(relationshipOutcome, storedContentVersion);
        }

        return await AuthorizeGetRelationshipAsync(
                mappingSet,
                resource,
                nonNamespaceConfiguredStrategies,
                authorizationContext,
                documentId
            )
            .ConfigureAwait(false);
    }

    private async Task<GetAuthorizationOutcome> AuthorizeGetRelationshipAsync(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationalAuthorizationContext authorizationContext,
        long documentId
    )
    {
        var relationshipAuthorizationResult = _relationshipAuthorizationPlanner.PlanStoredValues(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            authorizationContext
        );

        if (
            TryCreatePeopleEndpointStagingFailures(
                resource,
                configuredAuthorizationStrategies,
                relationshipAuthorizationResult,
                out var peopleStagingFailures
            )
        )
        {
            return new GetAuthorizationOutcome(
                new GetResult.GetFailureNotImplemented(
                    BuildKnownButNotEnabledGetAuthorizationMessage(
                        resource,
                        CombinePeopleEndpointStagingFailures(
                            peopleStagingFailures,
                            relationshipAuthorizationResult
                        )
                    )
                ),
                null,
                false
            );
        }

        switch (relationshipAuthorizationResult)
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

            case RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled:
                return new GetAuthorizationOutcome(
                    new GetResult.GetFailureNotImplemented(
                        BuildKnownButNotEnabledGetAuthorizationMessage(resource, knownButNotEnabled.Failures)
                    ),
                    null,
                    false
                );

            case RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError:
                return new GetAuthorizationOutcome(
                    BuildGetAuthorizationSecurityConfigurationFailure(
                        mappingSet,
                        resource,
                        securityConfigurationError.Failures
                    ),
                    null,
                    false
                );

            case RelationshipAuthorizationResult.Authorized authorized:
                return await ExecuteGetRelationshipAuthorizationAsync(mappingSet, documentId, authorized)
                    .ConfigureAwait(false);

            default:
                throw new InvalidOperationException(
                    $"Unsupported relationship authorization result '{relationshipAuthorizationResult.GetType().Name}'."
                );
        }
    }

    private static GetAuthorizationOutcome AnchorNamespaceReadBoundary(
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

        // The namespace check ran against the stored row but reports no version, so its decision is
        // only valid for the stored content version. When the relationship boundary either reported
        // no version (a no-op OR group) or reported the same stored version, pin the read boundary to
        // that version so hydration and the post-hydration boundary serve exactly the namespace-checked
        // representation.
        if (
            authorized.ObservedContentVersion is null
            || authorized.ObservedContentVersion == storedContentVersion
        )
        {
            return authorized with { ObservedContentVersion = storedContentVersion };
        }

        // The relationship boundary observed a different content version than the one the namespace
        // check authorized, so a mutation interleaved with the authorization sequence and the
        // namespace decision can no longer be trusted for the version that would be served. Force a
        // retry so the entire authorization sequence re-runs against the current row.
        return authorized with
        {
            RetryTargetResolution = true,
        };
    }

    private async Task<GetAuthorizationOutcome?> ExecuteGetNamespaceAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationalWriteNamespaceAuthorization storedNamespaceAuthorization
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
                )
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
                    new GetResult.GetFailureSecurityConfiguration([invalidFailure.FailureMessage]),
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

    private async Task<GetAuthorizationOutcome> ExecuteGetRelationshipAuthorizationAsync(
        MappingSet mappingSet,
        long documentId,
        RelationshipAuthorizationResult.Authorized authorized
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
                    GetByIdRelationshipAuthorizationAuth1Index
                )
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
                    new GetResult.GetFailureSecurityConfiguration([invalidFailure.FailureMessage]),
                    null,
                    false
                ),
            _ => throw new InvalidOperationException(
                $"Unsupported single-record authorization execution result '{authorizationExecutionResult.GetType().Name}'."
            ),
        };
    }

    private static bool ShouldBypassSingleRecordAuthorization(IRelationalGetRequest relationalGetRequest) =>
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
        public sealed record Stop(GetResult Result) : GetByIdAuthorizationPreflightResult;

        // Document-dependent authorization remains and runs per attempt against the resolved target.
        // StoredNamespaceAuthorization is null when only relationship strategies must be evaluated.
        public sealed record Proceed(
            RelationalWriteNamespaceAuthorization? StoredNamespaceAuthorization,
            IReadOnlyList<ConfiguredAuthorizationStrategy> NonNamespaceConfiguredStrategies
        ) : GetByIdAuthorizationPreflightResult;

        // No per-record authorization is required for this read (StoredDocument-mode bypass); the
        // target is still looked up and served.
        public sealed record AuthorizationNotRequired : GetByIdAuthorizationPreflightResult
        {
            public static AuthorizationNotRequired Instance { get; } = new();
        }
    }

    private static bool ShouldApplyReadableProfileProjection(IRelationalGetRequest relationalGetRequest) =>
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
            executionBoundaryName: "single-record EdOrg-only relationship execution boundary",
            supportedStrategySetName: "single-record EdOrg-only relationship",
            supportedStrategyNames:
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            ]
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
            executionBoundaryName: "single-record EdOrg-only relationship execution boundary",
            supportedStrategySetName: "single-record EdOrg-only relationship",
            supportedStrategyNames:
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            ]
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
            executionBoundaryName: "POST create-new EdOrg-only relationship execution boundary",
            supportedStrategySetName: "POST create-new EdOrg-only relationship",
            supportedStrategyNames:
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            ]
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
            executionBoundaryName: "PUT EdOrg-only relationship execution boundary",
            supportedStrategySetName: "PUT EdOrg-only relationship",
            supportedStrategyNames:
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
            ]
        );

    private static bool TryCreatePeopleEndpointStagingFailures(
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        RelationshipAuthorizationResult relationshipAuthorizationResult,
        out IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        if (
            relationshipAuthorizationResult
                is RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError
            && !HasOnlyPeopleEndpointStagingCompatibleFailures(securityConfigurationError.Failures)
        )
        {
            failures = [];
            return false;
        }

        return TryCreatePeopleEndpointStagingFailures(
            resource,
            configuredAuthorizationStrategies,
            out failures
        );
    }

    internal static bool TryCreatePeopleEndpointStagingFailures(
        QualifiedResourceName resource,
        IReadOnlyList<ConfiguredAuthorizationStrategy> configuredAuthorizationStrategies,
        out IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(configuredAuthorizationStrategies);

        failures =
        [
            .. RelationshipAuthorizationStrategyClassifier
                .SelectPeopleRelationshipStrategies(configuredAuthorizationStrategies)
                .Select(strategy => new RelationshipAuthorizationFailureMetadata(
                    RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy,
                    resource,
                    strategy.ConfiguredStrategy,
                    strategy.RelationshipLocalOrder,
                    Hint: "People relationship single-record endpoint execution is staged until CRUD People relationship authorization execution is enabled."
                )),
        ];

        return failures.Count > 0;
    }

    private static bool HasOnlyPeopleEndpointStagingCompatibleFailures(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    ) =>
        failures.Count > 0
        && failures.All(static failure =>
            failure.FailureKind is RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy
            || IsPeopleCorePlanningFailure(failure)
        );

    private static bool IsPeopleCorePlanningFailure(RelationshipAuthorizationFailureMetadata failure) =>
        failure.FailureKind
            is RelationshipAuthorizationFailureKind.MissingPeopleAuthViewAssociations
                or RelationshipAuthorizationFailureKind.NoExecutableSubjects
                or RelationshipAuthorizationFailureKind.NoApplicableRootSubject
                or RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
                or RelationshipAuthorizationFailureKind.MissingProposedRootBinding
        && (
            failure.PersonMetadata is not null
            || failure.Location?.Kind
                is SecurableElementKind.Student
                    or SecurableElementKind.Contact
                    or SecurableElementKind.Staff
            || failure.Contributors.Any(static contributor =>
                contributor.Kind
                    is SecurableElementKind.Student
                        or SecurableElementKind.Contact
                        or SecurableElementKind.Staff
            )
            || failure.IneligibleSubjects.Any(static ineligibleSubject =>
                ineligibleSubject.Subject.PersonMetadata is not null
            )
        );

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CombinePeopleEndpointStagingFailures(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> peopleEndpointStagingFailures,
        RelationshipAuthorizationResult relationshipAuthorizationResult
    ) =>
        relationshipAuthorizationResult switch
        {
            RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled =>
            [
                .. peopleEndpointStagingFailures,
                .. knownButNotEnabled.Failures,
            ],
            RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError =>
            [
                .. peopleEndpointStagingFailures,
                .. securityConfigurationError.Failures.Where(static failure =>
                    failure.FailureKind is RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy
                ),
            ],
            _ => peopleEndpointStagingFailures,
        };

    private static IReadOnlyList<RelationshipAuthorizationFailureMetadata> CombinePeopleEndpointStagingFailures(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> peopleEndpointStagingFailures,
        RelationshipAuthorizationUpdatePlan relationshipAuthorizationPlan
    )
    {
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures =
        [
            .. relationshipAuthorizationPlan.KnownButNotEnabledFailures,
            .. relationshipAuthorizationPlan.SecurityConfigurationFailures.Where(static failure =>
                failure.FailureKind is RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy
            ),
        ];

        return knownButNotEnabledFailures.Count > 0
            ? [.. peopleEndpointStagingFailures, .. knownButNotEnabledFailures]
            : peopleEndpointStagingFailures;
    }

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
            return new GetResult.GetFailureSecurityConfiguration([
                BuildEdOrgSubjectSelectionFailureMessage(
                    mappingSet,
                    resource,
                    failures,
                    operationLabel: "GET-by-id",
                    effectiveAuthorizationLabel: "GET"
                ),
            ]);
        }

        return new GetResult.GetFailureSecurityConfiguration([
            .. failures.Select(failure =>
                BuildSecurityConfigurationFailureMessage(
                    mappingSet,
                    failure,
                    operationLabel: "GET-by-id",
                    effectiveAuthorizationLabel: "GET",
                    executionBoundaryName: "single-record EdOrg-only relationship execution boundary"
                )
            ),
        ]);
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
            return new DeleteResult.DeleteFailureSecurityConfiguration([
                BuildEdOrgSubjectSelectionFailureMessage(
                    mappingSet,
                    resource,
                    failures,
                    operationLabel: "DELETE",
                    effectiveAuthorizationLabel: "DELETE"
                ),
            ]);
        }

        return new DeleteResult.DeleteFailureSecurityConfiguration([
            .. failures.Select(failure =>
                BuildSecurityConfigurationFailureMessage(
                    mappingSet,
                    failure,
                    operationLabel: "DELETE",
                    effectiveAuthorizationLabel: "DELETE",
                    executionBoundaryName: "single-record EdOrg-only relationship execution boundary"
                )
            ),
        ]);
    }

    private static UpsertResult.UpsertFailureSecurityConfiguration BuildPostAuthorizationSecurityConfigurationFailure(
        MappingSet mappingSet,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failures);

        return new UpsertResult.UpsertFailureSecurityConfiguration([
            .. failures.Select(failure =>
                BuildSecurityConfigurationFailureMessage(
                    mappingSet,
                    failure,
                    operationLabel: "POST",
                    effectiveAuthorizationLabel: "POST",
                    executionBoundaryName: "POST create-new EdOrg-only relationship execution boundary"
                )
            ),
        ]);
    }

    private static UpdateResult.UpdateFailureSecurityConfiguration BuildPutAuthorizationSecurityConfigurationFailure(
        MappingSet mappingSet,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);
        ArgumentNullException.ThrowIfNull(failures);

        return new UpdateResult.UpdateFailureSecurityConfiguration([
            .. failures.Select(failure =>
                BuildSecurityConfigurationFailureMessage(
                    mappingSet,
                    failure,
                    operationLabel: "PUT",
                    effectiveAuthorizationLabel: "PUT",
                    executionBoundaryName: "PUT EdOrg-only relationship execution boundary"
                )
            ),
        ]);
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
            supportedStrategyNames:
            [
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeople,
                AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsAndPeopleInverted,
                AuthorizationStrategyNameConstants.RelationshipsWithPeopleOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly,
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnlyThroughResponsibility,
            ]
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
            return new QueryResult.QueryFailureSecurityConfiguration([
                BuildEdOrgSubjectSelectionFailureMessage(
                    mappingSet,
                    resource,
                    failures,
                    operationLabel: "query",
                    effectiveAuthorizationLabel: "GET-many"
                ),
            ]);
        }

        return new QueryResult.QueryFailureSecurityConfiguration([
            .. failures.Select(failure =>
                BuildSecurityConfigurationFailureMessage(
                    mappingSet,
                    failure,
                    operationLabel: "query",
                    effectiveAuthorizationLabel: "GET-many",
                    executionBoundaryName: "GET-many EdOrg-only relationship query execution boundary"
                )
            ),
        ]);
    }

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
        IRelationalQueryRequest relationalQueryRequest,
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
                RelationalGetRequestReadMode.ExternalResponse
            )
            {
                MappingSet = relationalQueryRequest.MappingSet,
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

        return new QueryResult.QuerySuccess(
            edfiDocs,
            relationalQueryRequest.PaginationParameters.TotalCount
                ? RelationalReadGuardrails.ConvertTotalCountOrThrow(
                    resource,
                    hydratedPage.TotalCount,
                    "query hydration"
                )
                : null
        );
    }

    private static TRelationalRequest RequireRelationalRequest<TRelationalRequest>(
        object request,
        string paramName
    )
        where TRelationalRequest : class
    {
        return request as TRelationalRequest
            ?? throw new ArgumentException(
                $"Relational repository requires requests that implement {typeof(TRelationalRequest).Name}.",
                paramName
            );
    }

    private static WritePrecondition NormalizeWritePrecondition(WritePrecondition? writePrecondition) =>
        writePrecondition ?? new WritePrecondition.None();
}
