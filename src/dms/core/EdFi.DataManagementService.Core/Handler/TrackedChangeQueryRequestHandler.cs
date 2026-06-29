// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Response;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using static EdFi.DataManagementService.Core.Handler.Utility;

namespace EdFi.DataManagementService.Core.Handler;

internal sealed class TrackedChangeQueryRequestHandler(
    ILogger _logger,
    ResiliencePipeline _resiliencePipeline
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        _logger.LogDebug(
            "Entering TrackedChangeQueryRequestHandler - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        var changeQueryRepository = requestInfo.ScopedServiceProvider.GetService<IChangeQueryRepository>();

        if (changeQueryRepository is null)
        {
            _logger.LogWarning(
                "IChangeQueryRepository is not registered; tracked Change Queries require the relational backend - {TraceId}",
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = NotFoundResponse(requestInfo);
            return;
        }

        if (CreateQueryRequestOrNull(requestInfo) is not { } trackedChangeQueryRequest)
        {
            _logger.LogDebug(
                "Tracked Change Query metadata was not found for resource '{ProjectName}:{ResourceName}' - {TraceId}",
                requestInfo.ResourceInfo.ProjectName.Value,
                requestInfo.ResourceInfo.ResourceName.Value,
                requestInfo.FrontendRequest.TraceId.Value
            );
            requestInfo.FrontendResponse = NotFoundResponse(requestInfo);
            return;
        }

        TrackedChangeQueryResult trackedChangeQueryResult = await ExecuteWithRetryLogging(
            _resiliencePipeline,
            _logger,
            "tracked change query",
            requestInfo.FrontendRequest.TraceId,
            static _ => false,
            static _ => true,
            async ct => await changeQueryRepository.QueryTrackedChanges(trackedChangeQueryRequest, ct),
            requestInfo
        );

        if (trackedChangeQueryResult.AuthorizationFailure is { } authorizationFailure)
        {
            requestInfo.FrontendResponse = CreateAuthorizationFailureResponse(
                requestInfo,
                authorizationFailure
            );
            return;
        }

        Dictionary<string, string> headers = trackedChangeQueryResult.TotalCount.HasValue
            ? new()
            {
                {
                    "Total-Count",
                    trackedChangeQueryResult.TotalCount.Value.ToString(CultureInfo.InvariantCulture)
                },
            }
            : [];

        requestInfo.FrontendResponse = new FrontendResponse(
            StatusCode: 200,
            Body: trackedChangeQueryResult.Items,
            Headers: headers
        );
    }

    private static FrontendResponse NotFoundResponse(RequestInfo requestInfo) =>
        new(
            StatusCode: 404,
            Body: FailureResponse.ForNotFound(
                "The specified data could not be found.",
                requestInfo.FrontendRequest.TraceId
            ),
            Headers: [],
            ContentType: "application/problem+json"
        );

    private static FrontendResponse CreateAuthorizationFailureResponse(
        RequestInfo requestInfo,
        ChangeQueryAuthorizationFailure failure
    )
    {
        TraceId traceId = requestInfo.FrontendRequest.TraceId;

        return failure switch
        {
            ChangeQueryAuthorizationFailure.SecurityConfiguration securityConfiguration =>
                new FrontendResponse(
                    StatusCode: 500,
                    Body: FailureResponse.ForSecurityConfiguration(
                        traceId,
                        SecurityConfigurationErrors(securityConfiguration)
                    ),
                    Headers: [],
                    ContentType: "application/problem+json"
                ),
            ChangeQueryAuthorizationFailure.NamespaceNoPrefixesConfigured noPrefixes => new FrontendResponse(
                StatusCode: 403,
                Body: NamespaceAuthorizationFailureResponse.ForFailure(
                    new NamespaceAuthorizationFailure(
                        NamespaceAuthorizationFailureKind.NoPrefixesConfigured,
                        ValueSource: null,
                        EmittedAuth1Index: null,
                        StrategyName: noPrefixes.StrategyName,
                        ConfiguredNamespacePrefixes: []
                    ),
                    traceId
                ),
                Headers: [],
                ContentType: "application/problem+json"
            ),
            _ => throw new InvalidOperationException(
                $"Unsupported change query authorization failure '{failure.GetType().Name}'."
            ),
        };
    }

    private static string[] SecurityConfigurationErrors(
        ChangeQueryAuthorizationFailure.SecurityConfiguration securityConfiguration
    ) =>
        securityConfiguration.Errors.Count > 0
            ? [.. securityConfiguration.Errors]
            :
            [
                SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies(
                    securityConfiguration.UnavailableStrategyNames
                ),
            ];

    private static ITrackedChangeQueryRequest? CreateQueryRequestOrNull(RequestInfo requestInfo)
    {
        ChangeQueryEndpointOperation operation =
            requestInfo.ChangeQueryOperation
            ?? throw new InvalidOperationException(
                "Tracked Change Query operation was not parsed before handler execution."
            );

        MappingSet mappingSet =
            requestInfo.MappingSet
            ?? throw new InvalidOperationException(
                "Tracked Change Queries require a resolved relational mapping set."
            );

        QualifiedResourceName resource = new(
            requestInfo.ResourceInfo.ProjectName.Value,
            requestInfo.ResourceInfo.ResourceName.Value
        );

        ConcreteResourceModel? resourceModel = mappingSet.Model.ConcreteResourcesInNameOrder.SingleOrDefault(
            model => model.RelationalModel.Resource == resource
        );
        if (resourceModel is null)
        {
            return null;
        }

        TrackedChangeTableInfo? trackedChangeTable =
            resourceModel.StorageKind is ResourceStorageKind.SharedDescriptorTable
                ? mappingSet.Model.TrackedChangeTablesInNameOrder.SingleOrDefault(table =>
                    table.Kind is TrackedChangeTableKind.SharedDescriptor
                )
                : mappingSet.Model.TrackedChangeTablesInNameOrder.SingleOrDefault(table =>
                    table.SourceTable == resourceModel.RelationalModel.Root.Table
                );
        if (trackedChangeTable is null)
        {
            return null;
        }

        return new RelationalTrackedChangeQueryRequest(
            ResourceInfo: requestInfo.ResourceInfo,
            Operation: operation,
            PaginationParameters: requestInfo.PaginationParameters,
            ChangeVersionRange: requestInfo.ChangeVersionRange,
            TraceId: requestInfo.FrontendRequest.TraceId,
            AuthorizationContext: RelationalAuthorizationContext.Create(requestInfo.ClientAuthorizations),
            AuthorizationStrategyEvaluators: requestInfo.AuthorizationStrategyEvaluators,
            MappingSet: mappingSet,
            ResourceModel: resourceModel,
            TrackedChangeTable: trackedChangeTable
        );
    }
}
