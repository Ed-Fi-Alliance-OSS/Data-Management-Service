// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using SecurityDriven;
using static EdFi.DataManagementService.Core.External.Backend.UpsertResult;
using static EdFi.DataManagementService.Core.Handler.Utility;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Handler;

/// <summary>
/// Handles an upsert request that has made it through the middleware pipeline steps.
/// </summary>
internal class UpsertHandler(
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline,
    IApiSchemaProvider _apiSchemaProvider,
    IAuthorizationServiceFactory authorizationServiceFactory
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug("Entering UpsertHandler - {TraceId}", requestInfo.FrontendRequest.TraceId.Value);

        // Resolve repository from the per-request scoped service provider
        var documentStoreRepository =
            requestInfo.ScopedServiceProvider!.GetRequiredService<IDocumentStoreRepository>();

        var updateCascadeHandler = new UpdateCascadeHandler(_apiSchemaProvider, _logger);

        var upsertResult = await ExecuteWithRetryLogging(
            _resiliencePipeline,
            _logger,
            "upsert",
            requestInfo.FrontendRequest.TraceId,
            r => IsRetryableResult(r),
            r => r is InsertSuccess or UpdateSuccess,
            async ct =>
            {
                // A document uuid that will be assigned if this is a new document
                DocumentUuid candidateDocumentUuid = new(FastGuid.NewPostgreSqlGuid());

                return await documentStoreRepository.UpsertDocument(
                    new UpsertRequest(
                        ResourceInfo: requestInfo.ResourceInfo,
                        DocumentInfo: requestInfo.DocumentInfo,
                        EdfiDoc: requestInfo.ParsedBody,
                        Headers: requestInfo.FrontendRequest.Headers,
                        TraceId: requestInfo.FrontendRequest.TraceId,
                        DocumentUuid: candidateDocumentUuid,
                        DocumentSecurityElements: requestInfo.DocumentSecurityElements,
                        UpdateCascadeHandler: updateCascadeHandler,
                        ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
                            requestInfo.AuthorizationStrategyEvaluators,
                            requestInfo.AuthorizationSecurableInfo,
                            authorizationServiceFactory,
                            requestInfo.ScopedServiceProvider!,
                            _logger
                        ),
                        ResourceAuthorizationPathways: requestInfo.AuthorizationPathways
                    )
                );
            },
            requestInfo
        );
        _logger.LogDebug(
            "Document store UpsertDocument returned {UpsertResult}- {TraceId}",
            upsertResult.GetType().FullName,
            requestInfo.FrontendRequest.TraceId.Value
        );

        requestInfo.FrontendResponse = upsertResult switch
        {
            InsertSuccess insertSuccess => new FrontendResponse(
                StatusCode: 201,
                Body: null,
                Headers: new() { ["etag"] = requestInfo.ParsedBody["_etag"]?.ToString() ?? "" },
                LocationHeaderPath: PathComponents.ToResourcePath(
                    requestInfo.PathComponents,
                    insertSuccess.NewDocumentUuid
                )
            ),
            UpdateSuccess updateSuccess => new(
                StatusCode: 200,
                Body: null,
                Headers: new() { ["etag"] = requestInfo.ParsedBody["_etag"]?.ToString() ?? "" },
                LocationHeaderPath: PathComponents.ToResourcePath(
                    requestInfo.PathComponents,
                    updateSuccess.ExistingDocumentUuid
                )
            ),
            UpsertFailureDescriptorReference failure => new(
                StatusCode: 400,
                Body: ForBadRequest(
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
            ),
            UpsertFailureReference failure => new(
                StatusCode: 409,
                Body: ForInvalidReferences(
                    failure.ResourceNames,
                    traceId: requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            UpsertFailureIdentityConflict failure => new FrontendResponse(
                StatusCode: 409,
                Body: ForIdentityConflict(
                    [
                        $"A natural key conflict occurred when attempting to create a new resource {failure.ResourceName.Value} with a duplicate key. "
                            + $"The duplicate keys and values are {string.Join(',', failure.DuplicateIdentityValues.Select(d => $"({d.Key} = {d.Value})"))}",
                    ],
                    traceId: requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            // Returns 500 to match ODS/API behavior: after retries are exhausted for a deadlock,
            // the client receives a generic system error rather than a retryable status code.
            UpsertFailureWriteConflict => new(
                StatusCode: 500,
                Body: ForSystemError(requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            UpsertFailureNotAuthorized failure => new(
                StatusCode: 403,
                Body: ForForbidden(
                    traceId: requestInfo.FrontendRequest.TraceId,
                    errors: failure.ErrorMessages,
                    hints: failure.Hints
                ),
                Headers: []
            ),
            UnknownFailure failure => new(
                StatusCode: 500,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            _ => new(
                StatusCode: 500,
                Body: ToJsonError("Unknown UpsertResult", requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }
}
