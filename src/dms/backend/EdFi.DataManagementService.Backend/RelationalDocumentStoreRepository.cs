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
    ISingleRecordRelationshipAuthorizationExecutor? singleRecordRelationshipAuthorizationExecutor = null,
    IRelationalParameterConfigurator? relationalParameterConfigurator = null
) : IDocumentStoreRepository, IQueryHandler
{
    private const int GetByIdRelationshipAuthorizationAuth1Index = 0;
    private const int DeleteRelationshipAuthorizationAuth1Index = 0;
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
    private readonly ISingleRecordRelationshipAuthorizationExecutor? _singleRecordRelationshipAuthorizationExecutor =
        singleRecordRelationshipAuthorizationExecutor;
    private readonly IRelationalParameterConfigurator _relationalParameterConfigurator =
        relationalParameterConfigurator ?? DefaultRelationalParameterConfigurator.Instance;
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

        // TODO DMS-1057: Restore relational write authorization checks once NamespaceBased
        // CRUD authorization is implemented for relational writes.

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
                        relationalUpsertRequest.TraceId
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
                profileWriteContext
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
                    relationalGetRequest.TraceId
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

        // TODO DMS-1057: Restore relational write authorization checks once NamespaceBased
        // CRUD authorization is implemented for relational writes.

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
                        relationalUpdateRequest.TraceId
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
                profileWriteContext
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

        // TODO DMS-1057: Restore relational write authorization checks once NamespaceBased
        // CRUD authorization is implemented for relational writes.

        if (relationalDeleteRequest.ResourceInfo.IsDescriptor)
        {
            return _descriptorWriteHandler.HandleDeleteAsync(
                new DescriptorDeleteRequest(
                    mappingSet,
                    resource,
                    relationalDeleteRequest.DocumentUuid,
                    relationalDeleteRequest.TraceId
                )
                {
                    WritePrecondition = writePrecondition,
                }
            );
        }

        return DeleteDocumentByIdAsync(relationalDeleteRequest, mappingSet, resource, writePrecondition);
    }

    private async Task<DeleteResult> DeleteDocumentByIdAsync(
        IRelationalDeleteRequest relationalDeleteRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        WritePrecondition writePrecondition
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
                                relationalDeleteRequest,
                                mappingSet,
                                resource,
                                resolved.DocumentId,
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
        IRelationalDeleteRequest relationalDeleteRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        long documentId,
        IRelationalCommandExecutor sessionCommandExecutor
    )
    {
        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            relationalDeleteRequest.AuthorizationStrategyEvaluators
        );
        var relationshipAuthorizationResult = _relationshipAuthorizationPlanner.PlanStoredValues(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            relationalDeleteRequest.AuthorizationContext
        );

        switch (relationshipAuthorizationResult)
        {
            case RelationshipAuthorizationResult.NoAuthorizationRequired:
            case RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                return null;

            case RelationshipAuthorizationResult.NoClaims noClaims:
                if (
                    !TryCreateRelationshipAuthorizationFailure(
                        noClaims.CheckSpecs,
                        relationalDeleteRequest.AuthorizationContext.ClaimEducationOrganizationIds,
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
            _relationalParameterConfigurator
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
                new DeleteResult.UnknownFailure(invalidFailure.FailureMessage),
            _ => throw new InvalidOperationException(
                $"Unsupported single-record authorization execution result '{authorizationExecutionResult.GetType().Name}'."
            ),
        };
    }

    private static DeleteResult.DeleteFailureRelationshipNotAuthorized CreateDeleteRelationshipNotAuthorized(
        RelationshipAuthorizationFailure relationshipFailure
    ) => new(BuildRelationshipAuthorizationErrorMessages(relationshipFailure), relationshipFailure);

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
                        relationalQueryRequest.TraceId
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
        var relationshipAuthorizationResult = _relationshipAuthorizationPlanner.PlanStoredValues(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            relationalQueryRequest.AuthorizationContext
        );
        PageDocumentIdAuthorizationSpec? pageQueryAuthorization = null;

        switch (relationshipAuthorizationResult)
        {
            case RelationshipAuthorizationResult.NoAuthorizationRequired:
            case RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                break;

            case RelationshipAuthorizationResult.Authorized authorized:
                pageQueryAuthorization = PageDocumentIdAuthorizationSpecAdapter.Adapt(authorized);
                break;

            case RelationshipAuthorizationResult.NoClaims:
                return new QueryResult.QuerySuccess(
                    [],
                    relationalQueryRequest.PaginationParameters.TotalCount ? 0 : null
                );

            case RelationshipAuthorizationResult.KnownButNotEnabled knownButNotEnabled:
                return new QueryResult.QueryFailureNotImplemented(
                    BuildKnownButNotEnabledQueryAuthorizationMessage(resource, knownButNotEnabled.Failures)
                );

            case RelationshipAuthorizationResult.SecurityConfigurationError securityConfigurationError:
                return BuildQueryAuthorizationSecurityConfigurationFailure(
                    mappingSet,
                    resource,
                    securityConfigurationError.Failures
                );

            default:
                throw new InvalidOperationException(
                    $"Unsupported relationship authorization result '{relationshipAuthorizationResult.GetType().Name}'."
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

        var hydratedPage = await _documentHydrator
            .HydrateAsync(readPlan, plannedQuery, new HydrationExecutionOptions(), default)
            .ConfigureAwait(false);

        return BuildQuerySuccess(relationalQueryRequest, resource, readPlan, hydratedPage);
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
        BackendProfileWriteContext? profileWriteContext = null
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
                        targetContext: targetResolution.TargetContext!,
                        profileWriteContext: profileWriteContext,
                        writePrecondition: writePrecondition
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

    private async Task<GetResult> GetDocumentByIdAsync(
        IRelationalGetRequest relationalGetRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        ResourceReadPlan readPlan
    )
    {
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

            var authorizationOutcome = await AuthorizeGetByIdIfRequiredAsync(
                    relationalGetRequest,
                    mappingSet,
                    resource,
                    existingDocument.DocumentId
                )
                .ConfigureAwait(false);

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

    private async Task<GetAuthorizationOutcome> AuthorizeGetByIdIfRequiredAsync(
        IRelationalGetRequest relationalGetRequest,
        MappingSet mappingSet,
        QualifiedResourceName resource,
        long documentId
    )
    {
        if (ShouldBypassSingleRecordAuthorization(relationalGetRequest))
        {
            return GetAuthorizationOutcome.NotRequired;
        }

        var configuredAuthorizationStrategies = ConfiguredAuthorizationStrategyAdapter.Adapt(
            relationalGetRequest.AuthorizationStrategyEvaluators
        );
        var relationshipAuthorizationResult = _relationshipAuthorizationPlanner.PlanStoredValues(
            mappingSet,
            resource,
            configuredAuthorizationStrategies,
            relationalGetRequest.AuthorizationContext
        );

        switch (relationshipAuthorizationResult)
        {
            case RelationshipAuthorizationResult.NoAuthorizationRequired:
            case RelationshipAuthorizationResult.NoFurtherAuthorizationRequired:
                return GetAuthorizationOutcome.NotRequired;

            case RelationshipAuthorizationResult.NoClaims noClaims:
                if (
                    !TryCreateRelationshipAuthorizationFailure(
                        noClaims.CheckSpecs,
                        relationalGetRequest.AuthorizationContext.ClaimEducationOrganizationIds,
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

        if (_singleRecordRelationshipAuthorizationExecutor is null)
        {
            return new GetAuthorizationOutcome(
                new GetResult.UnknownFailure(
                    "Relational single-record relationship authorization executor is not configured."
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
                    new GetResult.UnknownFailure(invalidFailure.FailureMessage),
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

    private static bool TryCreateRelationshipAuthorizationFailure(
        IReadOnlyList<RelationshipAuthorizationCheckSpec> checkSpecs,
        IReadOnlyList<long> claimEducationOrganizationIds,
        int emittedAuth1Index,
        out RelationshipAuthorizationFailure? relationshipFailure
    )
    {
        relationshipFailure = null;

        if (checkSpecs.Count == 0)
        {
            return false;
        }

        List<RelationshipAuthorizationAuth1SubjectFailure> subjectFailures = [];

        for (var strategyOrdinal = 0; strategyOrdinal < checkSpecs.Count; strategyOrdinal++)
        {
            var checkSpec = checkSpecs[strategyOrdinal];

            for (var subjectOrdinal = 0; subjectOrdinal < checkSpec.Subjects.Count; subjectOrdinal++)
            {
                subjectFailures.Add(
                    new RelationshipAuthorizationAuth1SubjectFailure(
                        strategyOrdinal,
                        subjectOrdinal,
                        RelationshipAuthorizationAuth1SubjectFailureKind.NoRelationship
                    )
                );
            }
        }

        if (subjectFailures.Count == 0)
        {
            return false;
        }

        return RelationshipAuthorizationFailureMapper.TryMapAuth1Failure(
            new RelationshipAuthorizationAuth1FailurePayload(emittedAuth1Index, subjectFailures),
            checkSpecs,
            claimEducationOrganizationIds,
            out relationshipFailure
        );
    }

    private static GetResult.GetFailureRelationshipNotAuthorized CreateGetRelationshipNotAuthorized(
        RelationshipAuthorizationFailure relationshipFailure
    ) => new(BuildRelationshipAuthorizationErrorMessages(relationshipFailure), relationshipFailure);

    private static string[] BuildRelationshipAuthorizationErrorMessages(
        RelationshipAuthorizationFailure relationshipFailure
    )
    {
        string edOrgIdsFromFilters = string.Join(
            ", ",
            relationshipFailure.ClaimEducationOrganizationIds.Select(static id => $"'{id.Value}'")
        );
        string[] notAuthorizedProperties =
        [
            .. relationshipFailure
                .FailedStrategies.SelectMany(static strategy => strategy.FailedSubjects)
                .SelectMany(static subject => GetRelationshipAuthorizationPropertyNames(subject))
                .Distinct(StringComparer.Ordinal),
        ];

        if (notAuthorizedProperties.Length == 0)
        {
            return
            [
                "No relationships have been established between the caller's education organization id claims "
                    + $"({edOrgIdsFromFilters}) and the requested resource.",
            ];
        }

        if (notAuthorizedProperties.Length == 1)
        {
            return
            [
                "No relationships have been established between the caller's education organization id claims "
                    + $"({edOrgIdsFromFilters}) and the resource item's {notAuthorizedProperties[0]} value.",
            ];
        }

        return
        [
            "No relationships have been established between the caller's education organization id claims "
                + $"({edOrgIdsFromFilters}) and one or more of the following properties of the resource item: "
                + $"{string.Join(", ", notAuthorizedProperties.Select(static property => $"'{property}'"))}.",
        ];
    }

    private static IEnumerable<string> GetRelationshipAuthorizationPropertyNames(
        RelationshipAuthorizationFailedSubject subject
    )
    {
        if (subject.SecurableElements.Length == 0)
        {
            yield return subject.RootBinding.ColumnName;
            yield break;
        }

        foreach (var securableElement in subject.SecurableElements)
        {
            yield return securableElement.ReadableName;
        }
    }

    private sealed record GetAuthorizationOutcome(
        GetResult? FailureResult,
        long? ObservedContentVersion,
        bool RetryTargetResolution
    )
    {
        public static GetAuthorizationOutcome NotRequired { get; } = new(null, null, false);
    }

    private static bool ShouldApplyReadableProfileProjection(IRelationalGetRequest relationalGetRequest) =>
        relationalGetRequest.ReadMode == RelationalGetRequestReadMode.ExternalResponse
        && relationalGetRequest.ReadableProfileProjectionContext is not null;

    private static string BuildKnownButNotEnabledGetAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures
    )
    {
        var unsupportedStrategyNames = knownButNotEnabledFailures
            .Select(static failure => failure.ConfiguredStrategy?.StrategyName)
            .Where(static strategyName => strategyName is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static strategyName => strategyName, StringComparer.Ordinal)
            .Select(static strategyName => $"'{strategyName}'");

        return $"Relational GET-by-id authorization is not implemented for resource '{RelationalWriteSupport.FormatResource(resource)}' "
            + "when effective GET authorization includes strategies outside the current DMS-1056 EdOrg-only scope. Unsupported strategies: "
            + $"[{string.Join(", ", unsupportedStrategyNames)}]. Supported DMS-1056 strategies are "
            + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly}', "
            + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted}', and "
            + $"'{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' as a no-op.";
    }

    private static string BuildKnownButNotEnabledDeleteAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures
    )
    {
        var unsupportedStrategyNames = knownButNotEnabledFailures
            .Select(static failure => failure.ConfiguredStrategy?.StrategyName)
            .Where(static strategyName => strategyName is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static strategyName => strategyName, StringComparer.Ordinal)
            .Select(static strategyName => $"'{strategyName}'");

        return $"Relational DELETE authorization is not implemented for resource '{RelationalWriteSupport.FormatResource(resource)}' "
            + "when effective DELETE authorization includes strategies outside the current DMS-1056 EdOrg-only scope. Unsupported strategies: "
            + $"[{string.Join(", ", unsupportedStrategyNames)}]. Supported DMS-1056 strategies are "
            + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly}', "
            + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted}', and "
            + $"'{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' as a no-op.";
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
                BuildEdOrgSubjectSelectionFailureMessage(mappingSet, resource, failures),
            ]);
        }

        return new GetResult.GetFailureSecurityConfiguration([
            .. failures.Select(failure => BuildSecurityConfigurationFailureMessage(mappingSet, failure)),
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
                BuildEdOrgSubjectSelectionFailureMessage(mappingSet, resource, failures),
            ]);
        }

        return new DeleteResult.DeleteFailureSecurityConfiguration([
            .. failures.Select(failure => BuildSecurityConfigurationFailureMessage(mappingSet, failure)),
        ]);
    }

    private static string BuildKnownButNotEnabledQueryAuthorizationMessage(
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> knownButNotEnabledFailures
    )
    {
        var unsupportedStrategyNames = knownButNotEnabledFailures
            .Select(static failure => failure.ConfiguredStrategy?.StrategyName)
            .Where(static strategyName => strategyName is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static strategyName => strategyName, StringComparer.Ordinal)
            .Select(static strategyName => $"'{strategyName}'");

        return $"Relational query authorization is not implemented for resource '{RelationalWriteSupport.FormatResource(resource)}' "
            + "when effective GET-many authorization includes strategies outside the current DMS-1055 EdOrg-only scope. Unsupported strategies: "
            + $"[{string.Join(", ", unsupportedStrategyNames)}]. Supported DMS-1055 strategies are "
            + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnly}', "
            + $"'{AuthorizationStrategyNameConstants.RelationshipsWithEdOrgsOnlyInverted}', and "
            + $"'{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' as a no-op.";
    }

    private static string BuildEdOrgSubjectSelectionFailureMessage(
        MappingSet mappingSet,
        QualifiedResourceName resource,
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
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

        return $"Relational query authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(resource)}'. "
            + $"Effective GET-many strategies [{FormatStrategyNames(configuredAuthorizationStrategies)}] "
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
                BuildEdOrgSubjectSelectionFailureMessage(mappingSet, resource, failures),
            ]);
        }

        return new QueryResult.QueryFailureSecurityConfiguration([
            .. failures.Select(failure => BuildSecurityConfigurationFailureMessage(mappingSet, failure)),
        ]);
    }

    private static bool HasOnlyEdOrgSubjectSelectionFailures(
        IReadOnlyList<RelationshipAuthorizationFailureMetadata> failures
    ) =>
        failures.All(static failure =>
            failure.FailureKind
                is RelationshipAuthorizationFailureKind.UnresolvedSecurableElement
                    or RelationshipAuthorizationFailureKind.NoApplicableRootSubject
        );

    private static string BuildSecurityConfigurationFailureMessage(
        MappingSet mappingSet,
        RelationshipAuthorizationFailureMetadata failure
    ) =>
        failure.FailureKind switch
        {
            RelationshipAuthorizationFailureKind.KnownButNotEnabledStrategy =>
                $"Relational query authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Effective GET-many authorization also includes known-but-not-enabled strategy '{failure.ConfiguredStrategy?.StrategyName}', "
                    + "which is outside the current DMS-1055 EdOrg-only scope.",
            RelationshipAuthorizationFailureKind.UnknownCustomViewBasisResource =>
                $"Relational query authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' matches the {{BasisResource}}With... custom-view convention, "
                    + $"but basis resource '{failure.Location?.AuthorizationObjectName}' was not found in mapping set "
                    + $"'{MappingSetResourceLookupExtensions.FormatMappingSetKey(mappingSet.Key)}'.",
            RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy =>
                $"Relational query authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' is not a recognized built-in strategy and does not match the "
                    + "{BasisResource}With... custom-view convention.",
            RelationshipAuthorizationFailureKind.UnresolvedSecurableElement =>
                $"Relational query authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' requires resolvable EducationOrganization securable elements, "
                    + $"but element {FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath)} could not be resolved to a relational column.",
            RelationshipAuthorizationFailureKind.NoApplicableRootSubject =>
                $"Relational query authorization metadata is invalid for resource '{RelationalWriteSupport.FormatResource(failure.Resource)}'. "
                    + $"Strategy '{failure.ConfiguredStrategy?.StrategyName}' requires a concrete root-table EducationOrganization authorization subject, "
                    + $"but {FormatSecurableElementDetail(failure.Location?.ReadableName, failure.Location?.JsonPath) ?? "no configured EducationOrganization securable element"} "
                    + (
                        failure.Location?.Table is not null && failure.Location?.Column is not null
                            ? $"resolved to '{failure.Location.Table}.{failure.Location.Column.Value}' instead of a '{DbTableKind.Root}' table."
                            : failure.Hint ?? "did not produce a concrete root-table binding."
                    ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(failure),
                failure.FailureKind,
                "Unsupported query-authorization security-configuration failure kind."
            ),
        };

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
