// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.EducationOrganizationRefresh;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Configuration;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// <para>
/// The education organization refresh routes. An accepted request stores one <c>Pending</c> refresh job for the
/// current tenant, in its own transaction, and returns its jobQueuedResult. Location is the job's status route,
/// <c>/v3/jobs/{jobId}</c>, relative to the request's path base. The job runs later, on a worker.
/// </para>
/// <para>
/// The tenant aggregate, <c>GET /v3/tenants/{tenantName}/dataStores/edOrgs</c>, which the tenant middleware does not
/// resolve: the route checks the tenant itself and reads the aggregate in a nested scope whose tenant it installs
/// before resolving anything tenant-scoped. <c>GET /v3/dataStores/edOrgs</c> is answered as an unknown route.
/// </para>
/// </summary>
public class EducationOrganizationModule : IEndpointModule
{
    internal const string AllQueuedMessage =
        "Education organizations refresh has been queued for all instances";

    internal const string OneQueuedMessage =
        "Education organizations refresh has been queued for the specified instance";

    internal const string DataStoreNotFound = "Data store not found.";

    internal const string TenantNotFound = "Tenant not found.";

    internal const string TenantHeaderMismatch =
        "The 'Tenant' header does not match the tenant name in the route.";

    /// <summary>The only tenant name, in any case, that single-tenant mode accepts; also its canonical name.</summary>
    internal const string SingleTenantName = "default";

    private const string TenantHeaderName = "Tenant";

    internal static readonly EventId RefreshRequestFailed = new(1441_40, nameof(RefreshRequestFailed));

    internal static readonly EventId TenantAggregateReadFailed = new(
        1441_41,
        nameof(TenantAggregateReadFailed)
    );

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
        endpoints
            .MapSecuredGet("/v3/tenants/{tenantName}/dataStores/edOrgs", GetTenantAggregate)
            .WithSummary("Retrieves the data stores and their education organizations for a specific tenant")
            .WithDescription(
                "Returns the tenant with each of its data stores, ordered by id, and the education organizations of each one's last published refresh, ordered by educationOrganizationId; "
                    + "then its management records linked to no data store, ordered by dataStoreManageId. "
                    + "In multi-tenant mode the Tenant header must be supplied and must match tenantName in the route, ignoring case. "
                    + "In single-tenant mode only the default tenant name is accepted, in any case, and a Tenant header is ignored."
            )
            .Produces<TenantDetailsResponse>(200)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(500);
        // Without this route the request would bind "edOrgs" to GET /v3/dataStores/{id} and get its 400.
        endpoints.MapSecuredGet("/v3/dataStores/edOrgs", NotMapped).ExcludeFromDescription();
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
                return Failed(context, logger, RefreshRequestFailed, nameof(RefreshOne), failure.Diagnostic);
            default:
                return FailureResults.Unknown(context.TraceIdentifier);
        }
    }

    /// <summary>
    /// An enqueue that is not confirmed is a 500 without Location: no job identifier was returned to point to, although
    /// a lost acknowledgement can leave the job stored.
    /// </summary>
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
            return Failed(
                context,
                logger,
                RefreshRequestFailed,
                request,
                new(string.Empty, null, "SerializeRefreshPayload")
            );
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
            RefreshRequestFailed,
            request,
            result is JobEnqueueResult.FailureUnknown unknown
                ? unknown.Diagnostic
                : new(string.Empty, null, "Enqueue" + result.GetType().Name)
        );
    }

    /// <summary>
    /// Checks the tenant before any tenant-scoped dependency exists: in multi-tenant mode the header is compared with the
    /// route before the tenant is looked up, so a missing or mismatched header never reaches the database.
    /// </summary>
    private static async Task<IResult> GetTenantAggregate(
        string tenantName,
        HttpContext context,
        IOptions<AppSettings> appSettings,
        ITenantRepository tenants,
        IServiceScopeFactory scopes,
        ILogger<EducationOrganizationModule> logger
    )
    {
        TenantContext tenant;
        string canonicalName;
        if (appSettings.Value.MultiTenancy)
        {
            string header = context.Request.Headers[TenantHeaderName].ToString();
            if (string.IsNullOrWhiteSpace(header))
            {
                // The tenant middleware's own response, which this route bypasses.
                await FailureResponseWriter.WriteAsync(
                    context,
                    FailureResponse.ForBadRequest(
                        TenantResolutionMiddleware.MissingTenantHeaderMessage,
                        context.TraceIdentifier
                    ),
                    context.RequestAborted
                );
                return Results.Empty;
            }
            if (!string.Equals(header, tenantName, StringComparison.OrdinalIgnoreCase))
            {
                return FailureResults.BadRequest(TenantHeaderMismatch, context.TraceIdentifier);
            }
            switch (await tenants.GetTenantByName(tenantName))
            {
                case TenantGetByNameResult.Success found:
                    tenant = new TenantContext.Multitenant(
                        found.TenantResponse.Id,
                        found.TenantResponse.Name
                    );
                    canonicalName = found.TenantResponse.Name;
                    break;
                case TenantGetByNameResult.FailureNotFound:
                    return FailureResults.NotFound(TenantNotFound, context.TraceIdentifier);
                default:
                    // The failure's message may carry exception text, so only the operation is logged.
                    return Failed(
                        context,
                        logger,
                        TenantAggregateReadFailed,
                        nameof(GetTenantAggregate),
                        new(string.Empty, null, "GetTenantByName")
                    );
            }
        }
        else
        {
            if (!string.Equals(tenantName, SingleTenantName, StringComparison.OrdinalIgnoreCase))
            {
                return FailureResults.NotFound(TenantNotFound, context.TraceIdentifier);
            }
            tenant = new TenantContext.NotMultitenant();
            canonicalName = SingleTenantName;
        }

        // The request scope keeps its own tenant context; only the nested scope reads the selected tenant.
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextProvider>().Context = tenant;
        IEducationOrganizationSnapshotRepository snapshots =
            scope.ServiceProvider.GetRequiredService<IEducationOrganizationSnapshotRepository>();
        return await snapshots.GetTenantAggregateAsync(context.RequestAborted) switch
        {
            SnapshotTenantAggregateResult.Success success => Results.Ok(
                new TenantDetailsResponse(
                    canonicalName,
                    canonicalName,
                    [.. success.DataStores.Select(Response)]
                )
            ),
            SnapshotTenantAggregateResult.DbFailure failure => Failed(
                context,
                logger,
                TenantAggregateReadFailed,
                nameof(GetTenantAggregate),
                failure.Diagnostic
            ),
            _ => FailureResults.Unknown(context.TraceIdentifier),
        };
    }

    private static TenantDataStoreResponse Response(TenantAggregateDataStore dataStore) =>
        new(
            dataStore.Id,
            dataStore.DataStoreManageId,
            dataStore.Name,
            dataStore.DataStoreType,
            dataStore.Status,
            dataStore.DatabaseTemplate,
            dataStore.DatabaseName,
            [
                .. dataStore.EducationOrganizations.Select(organization => new EducationOrganizationResponse(
                    organization.EducationOrganizationId,
                    organization.NameOfInstitution,
                    organization.ShortNameOfInstitution,
                    organization.Discriminator,
                    organization.ParentId
                )),
            ]
        );

    /// <summary>
    /// A bodiless 404, which the framework error middleware writes as the unknown-route response, so the route is
    /// indistinguishable from an unmapped one once authorization has passed.
    /// </summary>
    private static IResult NotMapped() => Results.StatusCode(StatusCodes.Status404NotFound);

    /// <summary>Logs the diagnostic's fixed fields only, never an exception message or a request value.</summary>
    private static IResult Failed(
        HttpContext context,
        ILogger<EducationOrganizationModule> logger,
        EventId eventId,
        string request,
        JobFailureDiagnostic diagnostic
    )
    {
        logger.LogError(
            eventId,
            "{Event} Request={Request} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation} TraceId={TraceId}",
            eventId.Name,
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
