// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Backend;

public sealed class RelationalDocumentStoreRepository(
    ILogger<RelationalDocumentStoreRepository> logger,
    IRelationalWriteTargetContextResolver targetContextResolver,
    IReferenceResolver referenceResolver,
    IRelationalWriteFlattener writeFlattener,
    IRelationalWriteTerminalStage terminalStage
) : IDocumentStoreRepository, IQueryHandler
{
    private readonly ILogger<RelationalDocumentStoreRepository> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRelationalWriteTargetContextResolver _targetContextResolver =
        targetContextResolver ?? throw new ArgumentNullException(nameof(targetContextResolver));
    private readonly IReferenceResolver _referenceResolver =
        referenceResolver ?? throw new ArgumentNullException(nameof(referenceResolver));
    private readonly IRelationalWriteFlattener _writeFlattener =
        writeFlattener ?? throw new ArgumentNullException(nameof(writeFlattener));
    private readonly IRelationalWriteTerminalStage _terminalStage =
        terminalStage ?? throw new ArgumentNullException(nameof(terminalStage));

    public Task<UpsertResult> UpsertDocument(IUpsertRequest upsertRequest)
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

        return ExecuteWriteGuardRails<UpsertResult>(
            requestBody: relationalUpsertRequest.EdfiDoc,
            traceId: relationalUpsertRequest.TraceId,
            mappingSet,
            relationalUpsertRequest.ResourceInfo,
            RelationalWriteOperationKind.Post,
            relationalUpsertRequest.DocumentInfo.DocumentReferences,
            relationalUpsertRequest.DocumentInfo.DescriptorReferences,
            relationalUpsertRequest.BackendProfileWriteContext,
            static failureMessage => new UpsertResult.UnknownFailure(failureMessage),
            static validationFailures => new UpsertResult.UpsertFailureValidation(validationFailures),
            static policyMessage => new UpsertResult.UpsertFailureNotAuthorized([policyMessage]),
            static (invalidDocumentReferences, invalidDescriptorReferences) =>
                new UpsertResult.UpsertFailureReference(
                    invalidDocumentReferences,
                    invalidDescriptorReferences
                ),
            async (mappingSet, resource) =>
                await _targetContextResolver
                    .ResolveForPostAsync(
                        mappingSet,
                        resource,
                        relationalUpsertRequest.DocumentInfo.ReferentialId,
                        relationalUpsertRequest.DocumentUuid
                    )
                    .ConfigureAwait(false),
            static terminalStageResult =>
                terminalStageResult switch
                {
                    RelationalWriteTerminalStageResult.Upsert(var result) => result,
                    RelationalWriteTerminalStageResult.Update => throw new InvalidOperationException(
                        "Relational terminal stage returned an update result for a POST request."
                    ),
                    _ => throw new InvalidOperationException(
                        $"Relational terminal stage returned unsupported result type '{terminalStageResult.GetType().Name}' for a POST request."
                    ),
                }
        );
    }

    public Task<GetResult> GetDocumentById(IGetRequest getRequest)
    {
        ArgumentNullException.ThrowIfNull(getRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.GetDocumentById - {TraceId}",
            getRequest.TraceId.Value
        );

        return Task.FromResult<GetResult>(
            new GetResult.UnknownFailure(
                $"Relational GET by id is not implemented for resource '{getRequest.ResourceName.Value}'."
            )
        );
    }

    public Task<UpdateResult> UpdateDocumentById(IUpdateRequest updateRequest)
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

        return ExecuteWriteGuardRails<UpdateResult>(
            requestBody: relationalUpdateRequest.EdfiDoc,
            traceId: relationalUpdateRequest.TraceId,
            mappingSet,
            relationalUpdateRequest.ResourceInfo,
            RelationalWriteOperationKind.Put,
            relationalUpdateRequest.DocumentInfo.DocumentReferences,
            relationalUpdateRequest.DocumentInfo.DescriptorReferences,
            relationalUpdateRequest.BackendProfileWriteContext,
            static failureMessage => new UpdateResult.UnknownFailure(failureMessage),
            static validationFailures => new UpdateResult.UpdateFailureValidation(validationFailures),
            static policyMessage => new UpdateResult.UpdateFailureNotAuthorized([policyMessage]),
            static (invalidDocumentReferences, invalidDescriptorReferences) =>
                new UpdateResult.UpdateFailureReference(
                    invalidDocumentReferences,
                    invalidDescriptorReferences
                ),
            async (mappingSet, resource) =>
                await _targetContextResolver
                    .ResolveForPutAsync(mappingSet, resource, relationalUpdateRequest.DocumentUuid)
                    .ConfigureAwait(false),
            static terminalStageResult =>
                terminalStageResult switch
                {
                    RelationalWriteTerminalStageResult.Update(var result) => result,
                    RelationalWriteTerminalStageResult.Upsert => throw new InvalidOperationException(
                        "Relational terminal stage returned an upsert result for a PUT request."
                    ),
                    _ => throw new InvalidOperationException(
                        $"Relational terminal stage returned unsupported result type '{terminalStageResult.GetType().Name}' for a PUT request."
                    ),
                }
        );
    }

    public Task<DeleteResult> DeleteDocumentById(IDeleteRequest deleteRequest)
    {
        ArgumentNullException.ThrowIfNull(deleteRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.DeleteDocumentById - {TraceId}",
            deleteRequest.TraceId.Value
        );

        return Task.FromResult<DeleteResult>(
            new DeleteResult.UnknownFailure(
                $"Relational DELETE is not implemented for resource '{FormatResource(RelationalWriteSupport.ToQualifiedResourceName(deleteRequest.ResourceInfo))}'."
            )
        );
    }

    public Task<QueryResult> QueryDocuments(IQueryRequest queryRequest)
    {
        ArgumentNullException.ThrowIfNull(queryRequest);

        _logger.LogDebug(
            "Entering RelationalDocumentStoreRepository.QueryDocuments - {TraceId}",
            queryRequest.TraceId.Value
        );

        return Task.FromResult<QueryResult>(
            new QueryResult.UnknownFailure(
                $"Relational query handling is not implemented for resource '{FormatResource(RelationalWriteSupport.ToQualifiedResourceName(queryRequest.ResourceInfo))}'."
            )
        );
    }

    private async Task<TResult> ExecuteWriteGuardRails<TResult>(
        System.Text.Json.Nodes.JsonNode requestBody,
        TraceId traceId,
        MappingSet mappingSet,
        ResourceInfo resourceInfo,
        RelationalWriteOperationKind operationKind,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences,
        BackendProfileWriteContext? backendProfileWriteContext,
        Func<string, TResult> failureFactory,
        Func<WriteValidationFailure[], TResult> validationFailureFactory,
        Func<string, TResult> policyFailureFactory,
        Func<DocumentReferenceFailure[], DescriptorReferenceFailure[], TResult> referenceFailureFactory,
        Func<MappingSet, QualifiedResourceName, Task<RelationalWriteTargetContext>> resolveTargetContextAsync,
        Func<RelationalWriteTerminalStageResult, TResult> terminalResultProjector
    )
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        ArgumentNullException.ThrowIfNull(resourceInfo);
        ArgumentNullException.ThrowIfNull(documentReferences);
        ArgumentNullException.ThrowIfNull(descriptorReferences);
        ArgumentNullException.ThrowIfNull(failureFactory);
        ArgumentNullException.ThrowIfNull(validationFailureFactory);
        ArgumentNullException.ThrowIfNull(policyFailureFactory);
        ArgumentNullException.ThrowIfNull(referenceFailureFactory);
        ArgumentNullException.ThrowIfNull(resolveTargetContextAsync);
        ArgumentNullException.ThrowIfNull(terminalResultProjector);

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

        // ── Profile guard rails (defense-in-depth backup) ────────────────────
        if (backendProfileWriteContext != null)
        {
            // Validate request-side contract against compiled scope catalog
            var scopeCatalog = backendProfileWriteContext.CompiledScopeCatalog;
            ProfileFailure[] contractFailures = ProfileWriteContractValidator.ValidateRequestContract(
                backendProfileWriteContext.Request,
                scopeCatalog,
                profileName: backendProfileWriteContext.ProfileName,
                resourceName: resourceInfo.ResourceName.Value,
                method: operationKind == RelationalWriteOperationKind.Post ? "POST" : "PUT",
                operation: operationKind == RelationalWriteOperationKind.Post ? "upsert" : "update"
            );

            if (contractFailures.Length > 0)
            {
                // Category-5 contract mismatches are internal errors (not client validation),
                // so surface through failureFactory (→ UnknownFailure → HTTP 500).
                return failureFactory(string.Join("; ", contractFailures.Select(f => f.Message)));
            }
        }

        try
        {
            var targetContext = await resolveTargetContextAsync(mappingSet, resource).ConfigureAwait(false);

            // Root creatability guard: reject only when the POST would create a new document.
            // RootResourceCreatable applies only to new-document creation, not upsert-to-existing.
            if (
                backendProfileWriteContext != null
                && operationKind == RelationalWriteOperationKind.Post
                && targetContext is RelationalWriteTargetContext.CreateNew
                && !backendProfileWriteContext.Request.RootResourceCreatable
            )
            {
                return policyFailureFactory(
                    "The resource cannot be created because the profile does not allow creation of the root resource."
                );
            }

            var resolvedReferences = await _referenceResolver
                .ResolveAsync(
                    new ReferenceResolverRequest(
                        MappingSet: mappingSet,
                        RequestResource: resource,
                        DocumentReferences: documentReferences,
                        DescriptorReferences: descriptorReferences
                    )
                )
                .ConfigureAwait(false);

            if (resolvedReferences.HasFailures)
            {
                return referenceFailureFactory(
                    [.. resolvedReferences.InvalidDocumentReferences],
                    [.. resolvedReferences.InvalidDescriptorReferences]
                );
            }

            // ── Stored-state projection stub ──────────────────────────────────
            // DMS-1105 will supply the stored document from targetContext for
            // ExistingDocument flows. When available, this is where the repository
            // would call backendProfileWriteContext.StoredStateProjectionInvoker
            // to produce the ProfileAppliedWriteContext for update/upsert-to-existing.
            //
            // TODO(DMS-1105): IStoredStateProjectionInvoker.ProjectStoredState currently
            // returns ProfileAppliedWriteContext directly — typed failures from the second
            // pipeline run are lost and surface as InvalidOperationException. When this
            // stub is activated, change the return type to a result type that can convey
            // ProfileWritePipelineResult failures back to the repository for proper HTTP
            // status mapping (see CapturedStoredStateProjectionInvoker in
            // ProfileWritePipelineMiddleware).
            ProfileAppliedWriteContext? profileWriteContext = null;

            var flatteningInput = new FlatteningInput(
                operationKind,
                targetContext,
                writePlan,
                requestBody,
                resolvedReferences
            );
            var flattenedWriteSet = _writeFlattener.Flatten(flatteningInput);
            var terminalStageResult = await _terminalStage
                .ExecuteAsync(
                    new RelationalWriteTerminalStageRequest(flatteningInput, flattenedWriteSet, traceId)
                    {
                        ProfileWriteContext = profileWriteContext,
                    }
                )
                .ConfigureAwait(false);

            return terminalResultProjector(terminalStageResult);
        }
        catch (RelationalWriteRequestValidationException ex)
        {
            return validationFailureFactory(ex.ValidationFailures);
        }
    }

    private static string FormatResource(QualifiedResourceName resource) =>
        RelationalWriteSupport.FormatResource(resource);

    private static TRelationalRequest RequireRelationalRequest<TRelationalRequest>(
        object request,
        string paramName
    )
        where TRelationalRequest : class, IRelationalWriteRequest
    {
        return request as TRelationalRequest
            ?? throw new ArgumentException(
                $"Relational repository requires requests that implement {typeof(TRelationalRequest).Name}.",
                paramName
            );
    }
}
