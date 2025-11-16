// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Batch;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Extraction;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using SecurityDriven;
using static EdFi.DataManagementService.Core.External.Backend.DeleteResult;
using static EdFi.DataManagementService.Core.External.Backend.UpdateResult;
using static EdFi.DataManagementService.Core.External.Backend.UpsertResult;
using static EdFi.DataManagementService.Core.Handler.Utility;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Handler;

internal class BatchHandler(
    ILogger<BatchHandler> logger,
    IOptions<AppSettings> appSettings,
    ResiliencePipeline resiliencePipeline,
    IBatchUnitOfWorkFactory? batchUnitOfWorkFactory,
    IApiSchemaProvider apiSchemaProvider,
    IAuthorizationServiceFactory authorizationServiceFactory,
    VersionedLazy<PipelineProvider> upsertValidationPipeline,
    VersionedLazy<PipelineProvider> updateValidationPipeline,
    VersionedLazy<PipelineProvider> deleteValidationPipeline
) : IPipelineStep
{
    private readonly ILogger<BatchHandler> _logger = logger;
    private readonly IOptions<AppSettings> _appSettings = appSettings;
    private readonly ResiliencePipeline _resiliencePipeline = resiliencePipeline;
    private readonly IBatchUnitOfWorkFactory? _batchUnitOfWorkFactory = batchUnitOfWorkFactory;
    private readonly IApiSchemaProvider _apiSchemaProvider = apiSchemaProvider;
    private readonly IAuthorizationServiceFactory _authorizationServiceFactory = authorizationServiceFactory;
    private readonly VersionedLazy<PipelineProvider> _upsertValidationPipeline = upsertValidationPipeline;
    private readonly VersionedLazy<PipelineProvider> _updateValidationPipeline = updateValidationPipeline;
    private readonly VersionedLazy<PipelineProvider> _deleteValidationPipeline = deleteValidationPipeline;
    private readonly HashSet<string> _allowIdentityUpdateOverrides = new(
        appSettings.Value.AllowIdentityUpdateOverrides.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        ),
        StringComparer.OrdinalIgnoreCase
    );

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        IReadOnlyList<BatchOperation> operations;
        try
        {
            operations = await BatchRequestParser.ParseAsync(requestInfo);
        }
        catch (BatchRequestException ex)
        {
            _logger.LogWarning(
                "Batch request parsing failed for TraceId {TraceId}: {Message}",
                requestInfo.FrontendRequest.TraceId.Value,
                ex.Message
            );
            requestInfo.FrontendResponse = ex.Response;
            return;
        }

        if (operations.Count == 0)
        {
            _logger.LogInformation(
                "Batch request {TraceId} contained zero operations.",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = BatchResponseBuilder.CreateSuccessResponse(
                requestInfo,
                Array.Empty<BatchOperationSuccess>()
            );
            return;
        }

        int maxOperations = Math.Max(1, _appSettings.Value.BatchMaxOperations);
        if (operations.Count > maxOperations)
        {
            _logger.LogWarning(
                "Batch request {TraceId} exceeded max operation count ({Count} > {Max}).",
                requestInfo.FrontendRequest.TraceId.Value,
                operations.Count,
                maxOperations
            );
            requestInfo.FrontendResponse = BatchResponseBuilder.CreateTooLargeResponse(
                requestInfo,
                operations.Count,
                maxOperations
            );
            return;
        }

        if (_batchUnitOfWorkFactory == null)
        {
            _logger.LogWarning(
                "Batch request {TraceId} rejected because backend does not support batch operations.",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = BatchResponseBuilder.CreateBackendNotImplementedResponse(
                requestInfo
            );
            return;
        }

        await _resiliencePipeline.ExecuteAsync(async _ =>
        {
            await ExecuteBatchOperationsAsync(requestInfo, operations);
            return true;
        });
    }

    private async Task ExecuteBatchOperationsAsync(
        RequestInfo requestInfo,
        IReadOnlyList<BatchOperation> operations
    )
    {
        List<BatchOperationSuccess> successes = new(capacity: operations.Count);

        await using IBatchUnitOfWork unitOfWork = await _batchUnitOfWorkFactory!.BeginAsync(
            requestInfo.FrontendRequest.TraceId,
            requestInfo.FrontendRequest.Headers
        );

        foreach (BatchOperation operation in operations)
        {
            if (!TryResolveResource(operation, requestInfo, out var resolvedResource, out var errorResponse))
            {
                await HandleFailureAsync(requestInfo, unitOfWork, operation, errorResponse);
                return;
            }

            var method = MapMethod(operation.OperationType);
            DocumentUuid? targetDocumentUuid = null;
            DocumentIdentity? naturalKeyIdentity = null;

            if (operation.OperationType != BatchOperationType.Create)
            {
                if (operation.DocumentId.HasValue)
                {
                    targetDocumentUuid = operation.DocumentId;
                }
                else
                {
                    var naturalKeyResult = TryResolveNaturalKeyIdentity(
                        operation,
                        resolvedResource,
                        requestInfo
                    );
                    if (!naturalKeyResult.Success)
                    {
                        await HandleFailureAsync(
                            requestInfo,
                            unitOfWork,
                            operation,
                            naturalKeyResult.ErrorResponse!
                        );
                        return;
                    }

                    naturalKeyIdentity = naturalKeyResult.Identity;
                    ResourceInfo lookupResourceInfo = CreateResourceInfo(resolvedResource);
                    DocumentUuid? resolvedUuid = await unitOfWork.ResolveDocumentUuidAsync(
                        lookupResourceInfo,
                        naturalKeyIdentity!,
                        requestInfo.FrontendRequest.TraceId
                    );

                    if (resolvedUuid == null)
                    {
                        var notFound = new FrontendResponse(
                            StatusCode: 404,
                            Body: FailureResponse.ForNotFound(
                                "Resource to update was not found",
                                requestInfo.FrontendRequest.TraceId
                            ),
                            Headers: []
                        );
                        await HandleFailureAsync(requestInfo, unitOfWork, operation, notFound);
                        return;
                    }

                    targetDocumentUuid = resolvedUuid.Value;
                    if (operation.Document is JsonObject doc)
                    {
                        doc["id"] = targetDocumentUuid.Value.ToString();
                    }
                }
            }

            string path = BuildOperationPath(resolvedResource, targetDocumentUuid);
            RequestInfo operationRequestInfo = CreateOperationRequestInfo(
                requestInfo,
                resolvedResource,
                method,
                path,
                operation.Document
            );

            var pipeline = GetValidationPipeline(method);
            await pipeline.Run(operationRequestInfo);

            if (operationRequestInfo.FrontendResponse != No.FrontendResponse)
            {
                await HandleFailureAsync(
                    requestInfo,
                    unitOfWork,
                    operation,
                    ToFrontendResponse(operationRequestInfo.FrontendResponse)
                );
                return;
            }

            if (
                !ValidateNaturalKeyConsistency(
                    operation,
                    naturalKeyIdentity,
                    operationRequestInfo,
                    out var naturalKeyError
                )
            )
            {
                await HandleFailureAsync(requestInfo, unitOfWork, operation, naturalKeyError!);
                return;
            }

            var executionResult = await ExecuteBackendOperationAsync(
                operation,
                resolvedResource,
                operationRequestInfo,
                unitOfWork,
                targetDocumentUuid
            );

            if (!executionResult.Success)
            {
                await HandleFailureAsync(requestInfo, unitOfWork, operation, executionResult.ErrorResponse!);
                return;
            }

            successes.Add(
                new BatchOperationSuccess(
                    operation.Index,
                    operation.OperationType,
                    resolvedResource.ResourceSchema.ResourceName,
                    executionResult.DocumentUuid!.Value
                )
            );
        }

        await unitOfWork.CommitAsync();
        requestInfo.FrontendResponse = BatchResponseBuilder.CreateSuccessResponse(requestInfo, successes);
        _logger.LogInformation(
            "Batch request {TraceId} successfully executed {Count} operations.",
            requestInfo.FrontendRequest.TraceId.Value,
            successes.Count
        );
    }

    private static RequestMethod MapMethod(BatchOperationType operationType) =>
        operationType switch
        {
            BatchOperationType.Create => RequestMethod.POST,
            BatchOperationType.Update => RequestMethod.PUT,
            BatchOperationType.Delete => RequestMethod.DELETE,
            _ => throw new ArgumentOutOfRangeException(nameof(operationType), operationType, null),
        };

    private bool TryResolveResource(
        BatchOperation operation,
        RequestInfo requestInfo,
        out ResolvedResource resolvedResource,
        out FrontendResponse errorResponse
    )
    {
        var resourceName = operation.Resource;
        List<ResolvedResource> matches = [];

        foreach (var projectSchema in requestInfo.ApiSchemaDocuments.GetAllProjectSchemas())
        {
            var resourceNode = projectSchema.FindResourceSchemaNodeByResourceName(resourceName);
            if (resourceNode == null)
            {
                continue;
            }

            EndpointName endpointName = projectSchema.GetEndpointNameFromResourceName(resourceName);
            matches.Add(new ResolvedResource(projectSchema, new ResourceSchema(resourceNode), endpointName));
        }

        if (matches.Count == 1)
        {
            resolvedResource = matches[0];
            errorResponse = null!;
            return true;
        }

        resolvedResource = null!;
        string detail =
            matches.Count == 0
                ? $"Resource '{resourceName.Value}' was not found in the loaded API schemas."
                : $"Resource '{resourceName.Value}' is defined in multiple projects. Specify a unique resource.";
        errorResponse = new FrontendResponse(
            StatusCode: 400,
            Body: FailureResponse.ForBadRequest(detail, requestInfo.FrontendRequest.TraceId, [], []),
            Headers: []
        );
        return false;
    }

    private NaturalKeyResolutionResult TryResolveNaturalKeyIdentity(
        BatchOperation operation,
        ResolvedResource resolvedResource,
        RequestInfo requestInfo
    )
    {
        if (operation.NaturalKey is not JsonObject naturalKey)
        {
            return NaturalKeyResolutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForBadRequest(
                        $"Operation at index {operation.Index} must specify 'naturalKey'.",
                        requestInfo.FrontendRequest.TraceId,
                        [],
                        []
                    ),
                    Headers: []
                )
            );
        }

        try
        {
            DocumentIdentity identity = IdentityExtractor.ExtractDocumentIdentity(
                resolvedResource.ResourceSchema,
                naturalKey,
                _logger
            );
            return NaturalKeyResolutionResult.Successful(identity);
        }
        catch (Exception ex)
        {
            return NaturalKeyResolutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForBadRequest(
                        $"Invalid naturalKey for operation at index {operation.Index}: {ex.Message}",
                        requestInfo.FrontendRequest.TraceId,
                        [],
                        []
                    ),
                    Headers: []
                )
            );
        }
    }

    private ResourceInfo CreateResourceInfo(ResolvedResource resolvedResource)
    {
        bool allowIdentityUpdates =
            resolvedResource.ResourceSchema.AllowIdentityUpdates
            || _allowIdentityUpdateOverrides.Contains(resolvedResource.ResourceSchema.ResourceName.Value);

        return new ResourceInfo(
            ProjectName: resolvedResource.ProjectSchema.ProjectName,
            ResourceVersion: resolvedResource.ProjectSchema.ResourceVersion,
            ResourceName: resolvedResource.ResourceSchema.ResourceName,
            IsDescriptor: resolvedResource.ResourceSchema.IsDescriptor,
            AllowIdentityUpdates: allowIdentityUpdates,
            EducationOrganizationHierarchyInfo: new EducationOrganizationHierarchyInfo(false, 0, null),
            AuthorizationSecurableInfo: Array.Empty<AuthorizationSecurableInfo>()
        );
    }

    private static string BuildOperationPath(ResolvedResource resolvedResource, DocumentUuid? documentUuid)
    {
        string basePath =
            $"/{resolvedResource.ProjectSchema.ProjectEndpointName.Value}/{resolvedResource.EndpointName.Value}";
        return documentUuid.HasValue ? $"{basePath}/{documentUuid.Value.Value}" : basePath;
    }

    private RequestInfo CreateOperationRequestInfo(
        RequestInfo batchRequestInfo,
        ResolvedResource resolvedResource,
        RequestMethod method,
        string path,
        JsonObject? document
    )
    {
        string? body = document?.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        FrontendRequest frontendRequest = new(
            Path: path,
            Body: body,
            Headers: batchRequestInfo.FrontendRequest.Headers,
            QueryParameters: new Dictionary<string, string>(),
            TraceId: batchRequestInfo.FrontendRequest.TraceId
        );

        return new RequestInfo(frontendRequest, method)
        {
            ApiSchemaDocuments = batchRequestInfo.ApiSchemaDocuments,
            ApiSchemaReloadId = batchRequestInfo.ApiSchemaReloadId,
            ClientAuthorizations = batchRequestInfo.ClientAuthorizations,
        };
    }

    private PipelineProvider GetValidationPipeline(RequestMethod method) =>
        method switch
        {
            RequestMethod.POST => _upsertValidationPipeline.Value,
            RequestMethod.PUT => _updateValidationPipeline.Value,
            RequestMethod.DELETE => _deleteValidationPipeline.Value,
            _ => throw new InvalidOperationException("Unsupported request method for batch processing."),
        };

    private static bool ValidateNaturalKeyConsistency(
        BatchOperation operation,
        DocumentIdentity? naturalKeyIdentity,
        RequestInfo operationRequestInfo,
        out FrontendResponse? errorResponse
    )
    {
        errorResponse = null;

        if (
            operation.OperationType != BatchOperationType.Update
            || naturalKeyIdentity == null
            || operationRequestInfo.ResourceInfo.AllowIdentityUpdates
        )
        {
            return true;
        }

        if (!operationRequestInfo.DocumentInfo.DocumentIdentity.Equals(naturalKeyIdentity))
        {
            errorResponse = new FrontendResponse(
                StatusCode: 400,
                Body: FailureResponse.ForImmutableIdentity(
                    "The naturalKey does not match the document identity.",
                    operationRequestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            );
            return false;
        }

        return true;
    }

    private async Task<OperationExecutionResult> ExecuteBackendOperationAsync(
        BatchOperation operation,
        ResolvedResource resolvedResource,
        RequestInfo operationRequestInfo,
        IBatchUnitOfWork unitOfWork,
        DocumentUuid? targetDocumentUuid
    )
    {
        return operation.OperationType switch
        {
            BatchOperationType.Create => await ExecuteUpsertAsync(operationRequestInfo, unitOfWork),
            BatchOperationType.Update => await ExecuteUpdateAsync(
                operationRequestInfo,
                unitOfWork,
                targetDocumentUuid ?? throw new InvalidOperationException("Missing documentId for update.")
            ),
            BatchOperationType.Delete => await ExecuteDeleteAsync(
                operationRequestInfo,
                unitOfWork,
                targetDocumentUuid ?? throw new InvalidOperationException("Missing documentId for delete.")
            ),
            _ => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError(
                        "Unsupported batch operation type.",
                        operationRequestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                )
            ),
        };
    }

    private async Task<OperationExecutionResult> ExecuteUpsertAsync(
        RequestInfo operationRequestInfo,
        IBatchUnitOfWork unitOfWork
    )
    {
        DocumentUuid candidateDocumentUuid = new(FastGuid.NewPostgreSqlGuid());
        var updateCascadeHandler = new UpdateCascadeHandler(_apiSchemaProvider, _logger);

        var upsertRequest = new UpsertRequest(
            ResourceInfo: operationRequestInfo.ResourceInfo,
            DocumentInfo: operationRequestInfo.DocumentInfo,
            EdfiDoc: operationRequestInfo.ParsedBody,
            Headers: operationRequestInfo.FrontendRequest.Headers,
            TraceId: operationRequestInfo.FrontendRequest.TraceId,
            DocumentUuid: candidateDocumentUuid,
            DocumentSecurityElements: operationRequestInfo.DocumentSecurityElements,
            UpdateCascadeHandler: updateCascadeHandler,
            ResourceAuthorizationHandler: BuildAuthorizationHandler(operationRequestInfo),
            ResourceAuthorizationPathways: operationRequestInfo.AuthorizationPathways
        );

        var result = await unitOfWork.UpsertDocumentAsync(upsertRequest);
        return InterpretUpsertResult(operationRequestInfo, result);
    }

    private async Task<OperationExecutionResult> ExecuteUpdateAsync(
        RequestInfo operationRequestInfo,
        IBatchUnitOfWork unitOfWork,
        DocumentUuid documentUuid
    )
    {
        var updateCascadeHandler = new UpdateCascadeHandler(_apiSchemaProvider, _logger);

        var updateRequest = new UpdateRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: operationRequestInfo.ResourceInfo,
            DocumentInfo: operationRequestInfo.DocumentInfo,
            EdfiDoc: operationRequestInfo.ParsedBody,
            Headers: operationRequestInfo.FrontendRequest.Headers,
            DocumentSecurityElements: operationRequestInfo.DocumentSecurityElements,
            TraceId: operationRequestInfo.FrontendRequest.TraceId,
            UpdateCascadeHandler: updateCascadeHandler,
            ResourceAuthorizationHandler: BuildAuthorizationHandler(operationRequestInfo),
            ResourceAuthorizationPathways: operationRequestInfo.AuthorizationPathways
        );

        var result = await unitOfWork.UpdateDocumentByIdAsync(updateRequest);
        return InterpretUpdateResult(operationRequestInfo, result);
    }

    private async Task<OperationExecutionResult> ExecuteDeleteAsync(
        RequestInfo operationRequestInfo,
        IBatchUnitOfWork unitOfWork,
        DocumentUuid documentUuid
    )
    {
        var deleteRequest = new DeleteRequest(
            DocumentUuid: documentUuid,
            ResourceInfo: operationRequestInfo.ResourceInfo,
            TraceId: operationRequestInfo.FrontendRequest.TraceId,
            ResourceAuthorizationHandler: BuildAuthorizationHandler(operationRequestInfo),
            ResourceAuthorizationPathways: operationRequestInfo.AuthorizationPathways,
            DeleteInEdOrgHierarchy: operationRequestInfo.ProjectSchema.EducationOrganizationTypes.Contains(
                operationRequestInfo.ResourceSchema.ResourceName
            ),
            Headers: operationRequestInfo.FrontendRequest.Headers
        );

        var result = await unitOfWork.DeleteDocumentByIdAsync(deleteRequest);
        return InterpretDeleteResult(operationRequestInfo, result);
    }

    private OperationExecutionResult InterpretUpsertResult(
        RequestInfo requestInfo,
        UpsertResult upsertResult
    ) =>
        upsertResult switch
        {
            InsertSuccess insertSuccess => OperationExecutionResult.Success(insertSuccess.NewDocumentUuid),
            UpdateSuccess updateSuccess => OperationExecutionResult.Success(
                updateSuccess.ExistingDocumentUuid
            ),
            UpsertFailureDescriptorReference failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForBadRequest(
                        "Data validation failed. See 'validationErrors' for details.",
                        traceId: requestInfo.FrontendRequest.TraceId,
                        failure.InvalidDescriptorReferences.ToDictionary(
                            d => d.Path.Value,
                            d =>
                                d.DocumentIdentity.DocumentIdentityElements.Select(e =>
                                        $"{d.ResourceInfo.ResourceName.Value} value '{e.IdentityValue}' does not exist."
                                    )
                                    .ToArray()
                        ),
                        []
                    ),
                    Headers: []
                )
            ),
            UpsertFailureReference failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 409,
                    Body: FailureResponse.ForInvalidReferences(
                        failure.ResourceNames,
                        traceId: requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                )
            ),
            UpsertFailureIdentityConflict failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 409,
                    Body: FailureResponse.ForIdentityConflict(
                        [
                            $"A natural key conflict occurred when attempting to create a new resource {failure.ResourceName.Value} with a duplicate key. "
                                + $"The duplicate keys and values are {string.Join(',', failure.DuplicateIdentityValues.Select(d => $"({d.Key} = {d.Value})"))}",
                        ],
                        traceId: requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                )
            ),
            UpsertFailureWriteConflict => OperationExecutionResult.Failure(
                new FrontendResponse(StatusCode: 409, Body: null, Headers: [])
            ),
            UpsertFailureNotAuthorized failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 403,
                    Body: FailureResponse.ForForbidden(
                        traceId: requestInfo.FrontendRequest.TraceId,
                        errors: failure.ErrorMessages,
                        hints: failure.Hints
                    ),
                    Headers: []
                )
            ),
            UnknownFailure failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                    Headers: []
                )
            ),
            _ => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError("Unknown UpsertResult", requestInfo.FrontendRequest.TraceId),
                    Headers: []
                )
            ),
        };

    private OperationExecutionResult InterpretUpdateResult(
        RequestInfo requestInfo,
        UpdateResult updateResult
    ) =>
        updateResult switch
        {
            UpdateSuccess updateSuccess => OperationExecutionResult.Success(
                updateSuccess.ExistingDocumentUuid
            ),
            UpdateFailureETagMisMatch => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 412,
                    Body: FailureResponse.ForETagMisMatch(
                        "The item has been modified by another user.",
                        traceId: requestInfo.FrontendRequest.TraceId,
                        errors: new[]
                        {
                            "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved.",
                        }
                    ),
                    Headers: []
                )
            ),
            UpdateFailureNotExists => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 404,
                    Body: FailureResponse.ForNotFound(
                        "Resource to update was not found",
                        traceId: requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                )
            ),
            UpdateFailureDescriptorReference failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForBadRequest(
                        "Data validation failed. See 'validationErrors' for details.",
                        traceId: requestInfo.FrontendRequest.TraceId,
                        failure.InvalidDescriptorReferences.ToDictionary(
                            d => d.Path.Value,
                            d =>
                                d.DocumentIdentity.DocumentIdentityElements.Select(e =>
                                        $"{d.ResourceInfo.ResourceName.Value} value '{e.IdentityValue}' does not exist."
                                    )
                                    .ToArray()
                        ),
                        []
                    ),
                    Headers: []
                )
            ),
            UpdateFailureReference failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 409,
                    Body: FailureResponse.ForInvalidReferences(
                        failure.ReferencingDocumentInfo,
                        traceId: requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                )
            ),
            UpdateFailureIdentityConflict failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 409,
                    Body: FailureResponse.ForIdentityConflict(
                        [
                            $"A natural key conflict occurred when attempting to update a resource {failure.ResourceName.Value} with a duplicate key. "
                                + $"The duplicate keys and values are {string.Join(',', failure.DuplicateIdentityValues.Select(d => $"({d.Key} = {d.Value})"))}",
                        ],
                        traceId: requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                )
            ),
            UpdateFailureWriteConflict => OperationExecutionResult.Failure(
                new FrontendResponse(StatusCode: 409, Body: null, Headers: [])
            ),
            UpdateFailureImmutableIdentity failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 400,
                    Body: FailureResponse.ForImmutableIdentity(
                        failure.FailureMessage,
                        traceId: requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                )
            ),
            UpdateFailureNotAuthorized failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 403,
                    Body: FailureResponse.ForForbidden(
                        traceId: requestInfo.FrontendRequest.TraceId,
                        errors: failure.ErrorMessages
                    ),
                    Headers: []
                )
            ),
            UnknownFailure failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                    Headers: []
                )
            ),
            _ => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError("Unknown UpdateResult", requestInfo.FrontendRequest.TraceId),
                    Headers: []
                )
            ),
        };

    private OperationExecutionResult InterpretDeleteResult(
        RequestInfo requestInfo,
        DeleteResult deleteResult
    ) =>
        deleteResult switch
        {
            DeleteSuccess => OperationExecutionResult.Success(requestInfo.PathComponents.DocumentUuid),
            DeleteFailureNotExists => OperationExecutionResult.Failure(
                new FrontendResponse(StatusCode: 404, Body: null, Headers: [])
            ),
            DeleteFailureNotAuthorized notAuthorized => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 403,
                    Body: FailureResponse.ForForbidden(
                        traceId: requestInfo.FrontendRequest.TraceId,
                        errors: notAuthorized.ErrorMessages
                    ),
                    Headers: []
                )
            ),
            DeleteFailureReference failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 409,
                    Body: FailureResponse.ForDataConflict(
                        failure.ReferencingDocumentResourceNames,
                        traceId: requestInfo.FrontendRequest.TraceId
                    ),
                    Headers: []
                )
            ),
            DeleteFailureWriteConflict => OperationExecutionResult.Failure(
                new FrontendResponse(StatusCode: 409, Body: null, Headers: [])
            ),
            DeleteFailureETagMisMatch => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 412,
                    Body: FailureResponse.ForETagMisMatch(
                        "The item has been modified by another user.",
                        traceId: requestInfo.FrontendRequest.TraceId,
                        errors: new[]
                        {
                            "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved.",
                        }
                    ),
                    Headers: []
                )
            ),
            UnknownFailure failure => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                    Headers: []
                )
            ),
            _ => OperationExecutionResult.Failure(
                new FrontendResponse(
                    StatusCode: 500,
                    Body: ToJsonError("Unknown DeleteResult", requestInfo.FrontendRequest.TraceId),
                    Headers: []
                )
            ),
        };

    private ResourceAuthorizationHandler BuildAuthorizationHandler(RequestInfo requestInfo) =>
        new(
            requestInfo.AuthorizationStrategyEvaluators,
            requestInfo.AuthorizationSecurableInfo,
            _authorizationServiceFactory,
            _logger
        );

    private async Task HandleFailureAsync(
        RequestInfo batchRequestInfo,
        IBatchUnitOfWork unitOfWork,
        BatchOperation operation,
        FrontendResponse errorResponse
    )
    {
        await unitOfWork.RollbackAsync();
        _logger.LogWarning(
            "Batch operation {Index} ({Op} {Resource}) failed with status {StatusCode}.",
            operation.Index,
            operation.OperationType.ToOperationString(),
            operation.Resource.Value,
            errorResponse.StatusCode
        );
        batchRequestInfo.FrontendResponse = BatchResponseBuilder.CreateFailureResponse(
            batchRequestInfo,
            new BatchOperationFailure(operation, errorResponse)
        );
    }

    private static FrontendResponse ToFrontendResponse(IFrontendResponse response) =>
        response is FrontendResponse frontendResponse
            ? frontendResponse
            : new FrontendResponse(
                StatusCode: response.StatusCode,
                Body: response.Body,
                Headers: response.Headers,
                LocationHeaderPath: response.LocationHeaderPath,
                ContentType: response.ContentType
            );

    private sealed record ResolvedResource(
        ProjectSchema ProjectSchema,
        ResourceSchema ResourceSchema,
        EndpointName EndpointName
    );

    private readonly record struct NaturalKeyResolutionResult(
        bool Success,
        DocumentIdentity? Identity,
        FrontendResponse? ErrorResponse
    )
    {
        public static NaturalKeyResolutionResult Successful(DocumentIdentity identity) =>
            new(true, identity, null);

        public static NaturalKeyResolutionResult Failure(FrontendResponse errorResponse) =>
            new(false, null, errorResponse);
    }

    private readonly record struct OperationExecutionResult(
        bool Success,
        DocumentUuid? DocumentUuid,
        FrontendResponse? ErrorResponse
    )
    {
        public static OperationExecutionResult Success(DocumentUuid documentUuid) =>
            new(true, documentUuid, null);

        public static OperationExecutionResult Failure(FrontendResponse response) =>
            new(false, null, response);
    }
}
