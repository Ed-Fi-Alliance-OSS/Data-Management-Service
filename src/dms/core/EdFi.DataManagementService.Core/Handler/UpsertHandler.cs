// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Utilities;
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
internal class UpsertHandler(ILogger _logger, ResiliencePipeline _resiliencePipeline) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug("Entering UpsertHandler - {TraceId}", requestInfo.FrontendRequest.TraceId.Value);

        // Resolve repository from the per-request scoped service provider
        var documentStoreRepository =
            requestInfo.ScopedServiceProvider.GetRequiredService<IDocumentStoreRepository>();

        var mappingSet = RequireMappingSet(requestInfo, "upsert");
        var actionAuthorization =
            requestInfo.UpsertActionAuthorization
            ?? throw new InvalidOperationException(
                "A POST reached the upsert handler without its Create and Update authorization policies. "
                    + "Ensure ResourceActionAuthorizationMiddleware and ProvideAuthorizationFiltersMiddleware run first."
            );

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
                        MappingSet: mappingSet,
                        EdfiDoc: requestInfo.ParsedBody,
                        Headers: requestInfo.FrontendRequest.Headers,
                        TraceId: requestInfo.FrontendRequest.TraceId,
                        TenantKey: requestInfo.FrontendRequest.Tenant ?? string.Empty,
                        DocumentUuid: candidateDocumentUuid,
                        BackendProfileWriteContext: requestInfo.BackendProfileWriteContext
                    )
                    {
                        ActionAuthorization = actionAuthorization,
                        AuthorizationContext = RelationalAuthorizationContext.Create(
                            requestInfo.ClientAuthorizations,
                            requestInfo.ApplicationContext?.CreatorOwnershipTokenId,
                            requestInfo.ApplicationContext?.OwnershipTokenIds
                        ),
                    }
                );
            },
            requestInfo,
            // A client disconnect must not abandon a non-idempotent write that would otherwise
            // have been retried and applied, so the resilience context stays uncancellable here.
            CancellationToken.None
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
                Headers: new() { ["etag"] = insertSuccess.ETag },
                LocationHeaderPath: PathComponents.ToResourcePath(
                    requestInfo.PathComponents,
                    insertSuccess.NewDocumentUuid
                )
            ),
            UpdateSuccess updateSuccess => new(
                StatusCode: 200,
                Body: null,
                Headers: new() { ["etag"] = updateSuccess.ETag },
                LocationHeaderPath: PathComponents.ToResourcePath(
                    requestInfo.PathComponents,
                    updateSuccess.ExistingDocumentUuid
                )
            ),
            UpsertFailureReference failure
                when failure.HasDocumentReferenceFailures && !failure.HasDescriptorReferenceFailures => new(
                StatusCode: 409,
                Body: ForInvalidReferences(
                    ValidationErrorFactory.BuildInvalidWriteReferenceValidationErrors(
                        failure.InvalidDocumentReferences,
                        failure.InvalidDescriptorReferences
                    ),
                    traceId: requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            UpsertFailureReference failure => new(
                StatusCode: 400,
                Body: ForBadRequest(
                    "Data validation failed. See 'validationErrors' for details.",
                    traceId: requestInfo.FrontendRequest.TraceId,
                    ValidationErrorFactory.BuildInvalidWriteReferenceValidationErrors(
                        failure.InvalidDocumentReferences,
                        failure.InvalidDescriptorReferences
                    ),
                    []
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
                Headers: [],
                ContentType: "application/problem+json"
            ),
            UpsertFailureETagMisMatch mismatch => new(
                StatusCode: 412,
                Body: ForETagMisMatch(mismatch.Reason, requestInfo.FrontendRequest.TraceId),
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
            UpsertFailureRelationshipNotAuthorized failure => new(
                StatusCode: 403,
                Body: ForRelationshipAuthorization(
                    requestInfo.FrontendRequest.TraceId,
                    failure.RelationshipFailure
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            UpsertFailureNamespaceNotAuthorized notAuthorized => new(
                StatusCode: 403,
                Body: NamespaceAuthorizationFailureResponse.ForFailure(
                    notAuthorized.NamespaceFailure,
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            UpsertFailureCustomViewNotAuthorized notAuthorized => new(
                StatusCode: 403,
                Body: CustomViewAuthorizationFailureResponse.ForFailure(
                    notAuthorized.CustomViewFailure,
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            UpsertFailureOwnershipNotAuthorized notAuthorized => new(
                StatusCode: 403,
                Body: OwnershipAuthorizationFailureResponse.ForFailure(
                    notAuthorized.OwnershipFailure,
                    requestInfo.FrontendRequest.TraceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            UpsertFailureNotImplemented failure => new(
                StatusCode: 501,
                Body: ToJsonError(failure.FailureMessage, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            UpsertFailureTargetActionNotPermitted notPermitted => CreateTargetActionNotPermittedResponse(
                requestInfo,
                notPermitted.Action
            ),
            UpsertFailureSecurityConfiguration failure => CreateTargetActionSecurityConfigurationResponse(
                requestInfo,
                failure
            ),
            UpsertFailureValidation failure => ValidationErrorFactory.CreateValidationErrorResponse(
                ValidationErrorFactory.BuildWriteValidationErrors(failure.ValidationFailures),
                requestInfo.FrontendRequest.TraceId
            ),
            UpsertFailureImmutableIdentity failure => new(
                StatusCode: 400,
                Body: ForImmutableIdentity(
                    failure.FailureMessage,
                    traceId: requestInfo.FrontendRequest.TraceId
                ),
                Headers: []
            ),
            UpsertFailureProfileDataPolicy failure => new(
                StatusCode: 400,
                Body: ForDataPolicyEnforced(failure.ProfileName, requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
            UnknownFailure failure => CreateUnknownFailureResponse(
                _logger,
                requestInfo,
                failure.FailureMessage
            ),
            _ => new(
                StatusCode: 500,
                Body: ToJsonError("Unknown UpsertResult", requestInfo.FrontendRequest.TraceId),
                Headers: []
            ),
        };
    }

    /// <summary>
    /// Renders the action the POST's target selected exactly as the middleware renders an action the request
    /// maps to: a denial, or a grant that configures no strategies.
    /// </summary>
    private FrontendResponse CreateTargetActionNotPermittedResponse(
        RequestInfo requestInfo,
        UpsertTargetAction action
    ) =>
        RequireUpsertActionPolicies(requestInfo).For(action) switch
        {
            UpsertActionPolicyEvidence.Denied denied =>
                ResourceActionAuthorizationResponses.CreateActionDeniedResponse(
                    requestInfo,
                    denied.ActionName,
                    denied.ResourceClaimName,
                    denied.ClaimSetName
                ),
            UpsertActionPolicyEvidence.NoStrategies noStrategies =>
                ResourceActionAuthorizationResponses.CreateNoStrategiesSecurityConfigurationResponse(
                    _logger,
                    requestInfo,
                    noStrategies.ActionName,
                    noStrategies.MatchedResourceClaimUris,
                    noStrategies.MatchedResourceClaimName
                ),
            var evidence => throw new InvalidOperationException(
                $"The backend refused the {evidence.ActionName} action, which the request's claim set permits."
            ),
        };

    /// <summary>
    /// Logs a POST security-configuration failure against the action its target selected and that action's
    /// configured strategies. A failure decided before the target was observed names no action; that is only
    /// valid when Create and Update share one strategy list, so it is logged against both.
    /// </summary>
    private FrontendResponse CreateTargetActionSecurityConfigurationResponse(
        RequestInfo requestInfo,
        UpsertFailureSecurityConfiguration failure
    )
    {
        UpsertActionPolicies policies = RequireUpsertActionPolicies(requestInfo);

        if (failure.TargetAction is not { } action)
        {
            if (
                requestInfo.UpsertActionAuthorization?.TryGetSharedPolicy(out _) is not true
                || policies.Create is not UpsertActionPolicyEvidence.Permitted shared
            )
            {
                throw new InvalidOperationException(
                    "A POST security-configuration failure must name the action its target selected unless "
                        + "Create and Update share one strategy list."
                );
            }

            return CreateSecurityConfigurationFailureResponse(
                _logger,
                requestInfo,
                failure.Errors,
                failure.Diagnostics,
                cmsAction: $"{policies.Create.ActionName}, {policies.Update.ActionName}",
                configuredStrategyNames: shared.StrategyNames
            );
        }

        UpsertActionPolicyEvidence evidence = policies.For(action);

        return CreateSecurityConfigurationFailureResponse(
            _logger,
            requestInfo,
            failure.Errors,
            failure.Diagnostics,
            cmsAction: evidence.ActionName,
            configuredStrategyNames: evidence is UpsertActionPolicyEvidence.Permitted permitted
                ? permitted.StrategyNames
                : []
        );
    }

    private static UpsertActionPolicies RequireUpsertActionPolicies(RequestInfo requestInfo) =>
        requestInfo.UpsertActionPolicies
        ?? throw new InvalidOperationException(
            "A POST reached the upsert handler without its Create and Update authorization evidence."
        );
}
