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
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;
using JsonArray = System.Text.Json.Nodes.JsonArray;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalDocumentStoreRepository(
    ILogger<RelationalDocumentStoreRepository> logger,
    IRelationalWriteExecutor writeExecutor,
    IRelationalWriteTargetLookupService targetLookupService,
    IDescriptorWriteHandler descriptorWriteHandler,
    IDescriptorReadHandler descriptorReadHandler,
    IReferenceResolver referenceResolver,
    IDocumentHydrator documentHydrator,
    IRelationalReadTargetLookupService readTargetLookupService,
    IRelationalReadMaterializer readMaterializer,
    IReadableProfileProjector readableProfileProjector,
    IRelationalWriteExceptionClassifier writeExceptionClassifier,
    IRelationalDeleteConstraintResolver deleteConstraintResolver,
    IRelationalWriteSessionFactory writeSessionFactory
) : IDocumentStoreRepository, IQueryHandler
{
    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRelationalWriteExecutor _writeExecutor =
        writeExecutor ?? throw new ArgumentNullException(nameof(writeExecutor));
    private readonly IRelationalWriteTargetLookupService _targetLookupService =
        targetLookupService ?? throw new ArgumentNullException(nameof(targetLookupService));
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
                )
                .ConfigureAwait(false);
        }

        var profileWriteContext = relationalUpsertRequest.BackendProfileWriteContext;
        var selectedBody =
            profileWriteContext?.Request.WritableRequestBody ?? relationalUpsertRequest.EdfiDoc;

        var result = await ExecuteWriteGuardRails<UpsertResult>(
                requestBody: selectedBody,
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
                )
                .ConfigureAwait(false);
        }

        var profileWriteContext = relationalUpdateRequest.BackendProfileWriteContext;
        var selectedBody =
            profileWriteContext?.Request.WritableRequestBody ?? relationalUpdateRequest.EdfiDoc;

        var result = await ExecuteWriteGuardRails<UpdateResult>(
                requestBody: selectedBody,
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

        if (relationalDeleteRequest.ResourceInfo.IsDescriptor)
        {
            return _descriptorWriteHandler.HandleDeleteAsync(
                mappingSet,
                resource,
                relationalDeleteRequest.DocumentUuid,
                relationalDeleteRequest.TraceId
            );
        }

        return DeleteDocumentByIdAsync(relationalDeleteRequest, mappingSet);
    }

    private async Task<DeleteResult> DeleteDocumentByIdAsync(
        IRelationalDeleteRequest relationalDeleteRequest,
        MappingSet mappingSet
    )
    {
        var resource = RelationalWriteSupport.ToQualifiedResourceName(relationalDeleteRequest.ResourceInfo);
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
                    // Authorization-check statements will join this DELETE in a future DMS-1009 child
                    // ticket; If-Match/ETag validation against the resolved ContentVersion is likewise
                    // deferred. Until then, this path stays gated behind the UseRelationalBackend
                    // setting, while production DELETE traffic continues to flow through the Old
                    // Postgresql DeleteDocumentById handler which already enforces both.
                    // See reference/design/backend-redesign/design-docs/auth.md for the target shape.
                    var deleteCommand = BuildDocumentDeleteByDocumentIdCommand(
                        mappingSet.Key.Dialect,
                        resolved.DocumentId
                    );

                    outcome = await RelationalDeleteExecution
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

        if (!HasNoOpGetManyAuthorization(relationalQueryRequest.AuthorizationStrategyEvaluators))
        {
            return new QueryResult.QueryFailureNotImplemented(
                BuildQueryAuthorizationNotImplementedMessage(
                    resource,
                    relationalQueryRequest.AuthorizationStrategyEvaluators
                )
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
                    out _
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
            .HydrateAsync(readPlan, plannedQuery, default)
            .ConfigureAwait(false);

        return BuildQuerySuccess(relationalQueryRequest, resource, readPlan, hydratedPage);
    }

    private async Task<TResult> ExecuteWriteGuardRails<TResult>(
        System.Text.Json.Nodes.JsonNode requestBody,
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
                        profileWriteContext: profileWriteContext
                    )
                )
                .ConfigureAwait(false);

            if (
                executorResult.AttemptOutcome is RelationalWriteExecutorAttemptOutcome.StaleNoOpCompare
                && attemptIndex == 0
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
        // Relational authorization is pending a later story. Do not route GET-by-id
        // through the legacy authorization handler while the relational auth seam lands.
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

        var hydratedPage = await _documentHydrator
            .HydrateAsync(readPlan, new PageKeysetSpec.Single(existingDocument.DocumentId), default)
            .ConfigureAwait(false);

        if (hydratedPage.DocumentMetadata.Count == 0)
        {
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

        var edfiDoc = _readMaterializer.Materialize(
            new RelationalReadMaterializationRequest(
                readPlan,
                documentMetadata,
                hydratedPage.TableRowsInDependencyOrder,
                hydratedPage.DescriptorRowsInPlanOrder,
                relationalGetRequest.ReadMode
            )
        );

        if (ShouldApplyReadableProfileProjection(relationalGetRequest))
        {
            var projectionContext = relationalGetRequest.ReadableProfileProjectionContext!;
            edfiDoc = _readableProfileProjector.Project(
                edfiDoc,
                projectionContext.ContentTypeDefinition,
                projectionContext.IdentityPropertyNames
            );
            RelationalApiMetadataFormatter.RefreshEtag(edfiDoc);
        }

        return new GetResult.GetSuccess(
            new DocumentUuid(documentMetadata.DocumentUuid),
            edfiDoc,
            documentMetadata.ContentLastModifiedAt.UtcDateTime,
            null
        );
    }

    private static string FormatResource(QualifiedResourceName resource) =>
        RelationalWriteSupport.FormatResource(resource);

    private static bool HasNoOpGetManyAuthorization(
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        return authorizationStrategyEvaluators.All(static evaluator =>
            string.Equals(
                evaluator.AuthorizationStrategyName,
                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired,
                StringComparison.Ordinal
            )
        );
    }

    private static string BuildQueryAuthorizationNotImplementedMessage(
        QualifiedResourceName resource,
        IReadOnlyList<AuthorizationStrategyEvaluator> authorizationStrategyEvaluators
    )
    {
        ArgumentNullException.ThrowIfNull(authorizationStrategyEvaluators);

        var strategyNames = authorizationStrategyEvaluators
            .Select(static evaluator => evaluator.AuthorizationStrategyName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Select(static name => $"'{name}'");

        return $"Relational query authorization is not implemented for resource '{FormatResource(resource)}' "
            + "when effective GET-many authorization requires filtering. Effective strategies: "
            + $"[{string.Join(", ", strategyNames)}]. Only requests with no authorization strategies or only "
            + $"'{AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired}' are currently supported.";
    }

    private static bool ShouldApplyReadableProfileProjection(IRelationalGetRequest relationalGetRequest) =>
        relationalGetRequest.ReadMode == RelationalGetRequestReadMode.ExternalResponse
        && relationalGetRequest.ReadableProfileProjectionContext is not null;

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
                RelationalApiMetadataFormatter.RefreshEtag(projectedOrUnchangedDocument);
            }

            edfiDocs.Add(projectedOrUnchangedDocument);
        }

        return new QueryResult.QuerySuccess(
            edfiDocs,
            relationalQueryRequest.PaginationParameters.TotalCount
                ? ConvertTotalCountOrThrow(resource, hydratedPage.TotalCount)
                : null
        );
    }

    private static int ConvertTotalCountOrThrow(QualifiedResourceName resource, long? hydratedTotalCount)
    {
        if (hydratedTotalCount is null)
        {
            throw new InvalidOperationException(
                $"Relational query hydration for resource '{FormatResource(resource)}' did not return a total count "
                    + "even though the request asked for totalCount=true."
            );
        }

        if (hydratedTotalCount < 0 || hydratedTotalCount > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Relational query hydration returned total count {hydratedTotalCount.Value} for resource "
                    + $"'{FormatResource(resource)}', but only values in the range [0, {int.MaxValue}] are supported."
            );
        }

        return (int)hydratedTotalCount.Value;
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
}
