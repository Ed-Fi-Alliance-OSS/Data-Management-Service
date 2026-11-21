// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// The education organization refresh routes. An accepted request stores one <c>Pending</c> refresh job for the
/// current tenant, in its own transaction, and returns its jobQueuedResult. Location is the job's status route,
/// <c>/v3/jobs/{jobId}</c>, relative to the request's path base. The job runs later, on a worker.
/// </summary>
public class EducationOrganizationModule : IEndpointModule
{
    internal const string AllQueuedMessage =
        "Education organizations refresh has been queued for all instances";

    internal const string OneQueuedMessage =
        "Education organizations refresh has been queued for the specified instance";

    internal const string DataStoreNotFound = "Data store not found.";

    internal static readonly EventId RefreshRequestFailed = new(1441_40, nameof(RefreshRequestFailed));

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapSecuredPost("/v3/dataStores/edOrgs/refresh", RefreshAll)
            .WithSummary("Refreshes education organizations for all data stores")
            .WithDescription(
                "Queues a background job that refreshes the education organizations of every data store in the selected tenant. "
                    + "Returns 202 with a jobQueuedResult once the job is stored; it has not run yet. "
                    + "Location is the job's status route, /v3/jobs/{jobId}, relative to the request's path base."
            )
            .Produces<JobQueuedResponse>(202)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(500)
            .AddOpenApiOperationTransformer(DocumentLocation);
        endpoints
            .MapSecuredPost("/v3/dataStores/{dataStoreId}/edOrgs/refresh", RefreshOne)
            .WithSummary("Refreshes education organizations for a specific data store")
            .WithDescription(
                "Queues a background job that refreshes the education organizations of one data store in the selected tenant. "
                    + "A data store that does not exist in the selected tenant returns 404 and queues nothing. "
                    + "Returns 202 with a jobQueuedResult once the job is stored; it has not run yet. "
                    + "Location is the job's status route, /v3/jobs/{jobId}, relative to the request's path base."
            )
            .Produces<JobQueuedResponse>(202)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(500)
            .AddOpenApiOperationTransformer(DocumentLocation)
            .AddOpenApiOperationTransformer(DocumentDataStoreId);
    }

    private static Task<IResult> RefreshAll(
        HttpContext context,
        IJobEnqueuer enqueuer,
        ILogger<EducationOrganizationModule> logger
    ) =>
        EnqueueAsync(
            context,
            enqueuer,
            logger,
            new EducationOrganizationRefreshPayload(EducationOrganizationRefreshScope.All, 0),
            nameof(RefreshAll),
            AllQueuedMessage
        );

    /// <summary>
    /// The existence check reads only the data store's identifier, in the current tenant, so a data store of another
    /// tenant is indistinguishable from an absent one and its connection string is never read.
    /// </summary>
    private static async Task<IResult> RefreshOne(
        int dataStoreId,
        HttpContext context,
        IEducationOrganizationSnapshotRepository snapshots,
        IJobEnqueuer enqueuer,
        ILogger<EducationOrganizationModule> logger
    )
    {
        switch (await snapshots.DataStoreExistsAsync(dataStoreId, context.RequestAborted))
        {
            case SnapshotDataStoreExistsResult.Found:
                return await EnqueueAsync(
                    context,
                    enqueuer,
                    logger,
                    new EducationOrganizationRefreshPayload(
                        EducationOrganizationRefreshScope.One,
                        dataStoreId
                    ),
                    nameof(RefreshOne),
                    OneQueuedMessage
                );
            case SnapshotDataStoreExistsResult.NotFound:
                return FailureResults.NotFound(DataStoreNotFound, context.TraceIdentifier);
            case SnapshotDataStoreExistsResult.DbFailure failure:
                return Failed(context, logger, nameof(RefreshOne), failure.Diagnostic);
            default:
                return FailureResults.Unknown(context.TraceIdentifier);
        }
    }

    /// <summary>Every enqueue failure is a 500 without Location, since no job exists to point to.</summary>
    private static async Task<IResult> EnqueueAsync(
        HttpContext context,
        IJobEnqueuer enqueuer,
        ILogger<EducationOrganizationModule> logger,
        EducationOrganizationRefreshPayload payload,
        string request,
        string message
    )
    {
        if (JobPayloadSerializer.Serialize(payload) is not JobPayloadWriteResult.Success written)
        {
            return Failed(context, logger, request, new(string.Empty, null, "SerializeRefreshPayload"));
        }
        JobEnqueueResult result = await enqueuer.EnqueueAsync(
            new JobEnqueueCommand(
                EducationOrganizationRefreshJobTypes.Refresh,
                EducationOrganizationRefreshJobTypes.PayloadVersion,
                written.Json
            ),
            null,
            context.RequestAborted
        );
        if (result is JobEnqueueResult.Success success)
        {
            string location = new PathString(context.Request.PathBase.Value?.TrimEnd('/'))
                .Add(new PathString("/v3/jobs/" + success.JobId))
                .ToUriComponent();
            return Results.Accepted(
                location,
                new JobQueuedResponse { JobId = success.JobId, Message = message }
            );
        }
        return Failed(
            context,
            logger,
            request,
            result is JobEnqueueResult.FailureUnknown unknown
                ? unknown.Diagnostic
                : new(string.Empty, null, "Enqueue" + result.GetType().Name)
        );
    }

    /// <summary>Logs the diagnostic's fixed fields only, never an exception message or a request value.</summary>
    private static IResult Failed(
        HttpContext context,
        ILogger<EducationOrganizationModule> logger,
        string request,
        JobFailureDiagnostic diagnostic
    )
    {
        logger.LogError(
            RefreshRequestFailed,
            "{Event} Request={Request} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation} TraceId={TraceId}",
            nameof(RefreshRequestFailed),
            request,
            diagnostic.ExceptionTypeChain,
            diagnostic.ProviderErrorCode,
            diagnostic.Operation,
            JobDiagnostics.SafeIdentifier(context.TraceIdentifier)
        );
        return FailureResults.Unknown(context.TraceIdentifier);
    }

    private static Task DocumentLocation(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken token
    )
    {
        if (operation.Responses?["202"] is OpenApiResponse response)
        {
            response.Description =
                "Accepted. The refresh has been queued as a background job; it has not completed when this response is returned.";
            response.Headers = new Dictionary<string, IOpenApiHeader>
            {
                ["Location"] = new OpenApiHeader
                {
                    Required = true,
                    Description =
                        "Path of the job created for this request, in the form /v3/jobs/{jobId}, relative to the request's path base.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uri" },
                },
            };
        }
        return Task.CompletedTask;
    }

    /// <summary>The route binds an int32, which the generator would otherwise publish as an integer or a string.</summary>
    private static Task DocumentDataStoreId(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken token
    )
    {
        if (
            operation
                .Parameters?.OfType<OpenApiParameter>()
                .SingleOrDefault(parameter => parameter.Name == "dataStoreId")
            is OpenApiParameter dataStoreId
        )
        {
            dataStoreId.Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" };
        }
        return Task.CompletedTask;
    }
}
