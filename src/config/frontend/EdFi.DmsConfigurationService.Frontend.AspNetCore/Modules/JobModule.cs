// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// <c>GET /v3/jobs/{jobId}</c> (spec §5): the status of one job of the current tenant, in the five-property
/// Admin API v3 contract (§1.4).
/// </summary>
public class JobModule : IEndpointModule
{
    internal static readonly EventId JobStatusReadFailed = new(1437_40, nameof(JobStatusReadFailed));

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapSecuredGet("/v3/jobs/{jobId}", GetById)
            .WithSummary("Get job status")
            .WithDescription("Get the status of a job by its ID")
            .Produces<JobStatusResponse>(200)
            .ProducesProblem(404)
            .ProducesProblem(500);
    }

    // The identifier is an opaque string with no route constraint (Q4); the repository matches it exactly and
    // only within the current tenant, so another tenant's job is indistinguishable from an absent one.
    private static async Task<IResult> GetById(
        string jobId,
        HttpContext httpContext,
        IJobRepository repository,
        ILogger<JobModule> logger
    )
    {
        JobStatusQueryResult result = await repository.GetJobStatus(jobId, httpContext.RequestAborted);
        switch (result)
        {
            case JobStatusQueryResult.Success success:
                return Results.Ok(success.Job);
            case JobStatusQueryResult.FailureNotFound:
                return FailureResults.NotFound("Job not found.", httpContext.TraceIdentifier);
            case JobStatusQueryResult.FailureUnknown failure:
                // D-16a: the diagnostic's fixed fields only, never an exception message.
                logger.LogError(
                    JobStatusReadFailed,
                    "{Event} JobId={JobId} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation} TraceId={TraceId}",
                    nameof(JobStatusReadFailed),
                    JobDiagnostics.SafeIdentifier(jobId),
                    failure.Diagnostic.ExceptionTypeChain,
                    failure.Diagnostic.ProviderErrorCode,
                    failure.Diagnostic.Operation,
                    JobDiagnostics.SafeIdentifier(httpContext.TraceIdentifier)
                );
                return FailureResults.Unknown(httpContext.TraceIdentifier);
            default:
                return FailureResults.Unknown(httpContext.TraceIdentifier);
        }
    }
}
