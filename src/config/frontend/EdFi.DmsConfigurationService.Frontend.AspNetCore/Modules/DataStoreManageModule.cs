// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.Jobs;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using EdFi.DmsConfigurationService.DataModel.Model.Job;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http.Extensions;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// Managed-only dependencies are resolved after the feature gate, so disabled deployments need none.
/// </summary>
public static class DataStoreManageModule
{
    private const string Route = "/v3/dataStores/manage";

    internal const string CreateQueuedMessage = "The Data Store has been queued to be created.";
    internal const string DeleteQueuedMessage = "The Data Store has been queued to be deleted.";

    internal static readonly EventId ManagedRequestFailed = new(1439_40, nameof(ManagedRequestFailed));

    private static IResult Invalid(HttpContext context, string message, string field = "dataStore") =>
        FailureResults.DataValidation([new ValidationFailure(field, message)], context.TraceIdentifier);

    /// <summary>The disabled-feature problem; the feature gate middleware returns it before body validation.</summary>
    internal static IResult Disabled(HttpContext context) =>
        Invalid(context, "Data store management is disabled by AppSettings:EnableDataStoreManagement.");

    private static IResult Queued(string? location, string jobId, string message) =>
        Results.Accepted(location, new JobQueuedResponse { JobId = jobId, Message = message });

    private static IResult Unavailable(HttpContext context) =>
        FailureResults.ServiceUnavailable(
            "Managed data store provisioning is unavailable.",
            context.TraceIdentifier
        );

    /// <summary>Logs the diagnostic's fixed fields only, never an exception message, request value or connection string.</summary>
    private static IResult Failed(
        HttpContext context,
        IServiceProvider services,
        string request,
        JobFailureDiagnostic diagnostic
    )
    {
        services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DataStoreManageModule))
            .LogError(
                ManagedRequestFailed,
                "{Event} Request={Request} ExceptionTypeChain={ExceptionTypeChain} ProviderErrorCode={ProviderErrorCode} Operation={Operation} TraceId={TraceId}",
                nameof(ManagedRequestFailed),
                request,
                diagnostic.ExceptionTypeChain,
                diagnostic.ProviderErrorCode,
                diagnostic.Operation,
                JobDiagnostics.SafeIdentifier(context.TraceIdentifier)
            );
        return FailureResults.Unknown(context.TraceIdentifier);
    }

    private static JobFailureDiagnostic EnqueueDiagnostic(JobEnqueueResult failure) =>
        failure is JobEnqueueResult.FailureUnknown unknown
            ? unknown.Diagnostic
            : new(string.Empty, null, "Enqueue" + failure.GetType().Name);

    public static void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup(Route)
            .WithTags("DataStoreManagement")
            .WithMetadata(ManagedDataStoreRouteMetadata.Instance, ExceptionTypeOnlyLoggingMetadata.Instance);
        group
            .MapSecuredGet("", GetAll)
            .WithSummary("Query managed data stores")
            .WithDescription(
                "Includes retained tombstones in the selected tenant. Filters combine before paging. Defaults: offset 0, limit 25, id Descending. Nulls sort last in both directions, with id ties in that direction."
            )
            .Produces<List<DataStoreManageResponse>>(200)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(500)
            .AddOpenApiOperationTransformer(DataStoreManageOpenApi.Query);
        group
            .MapSecuredGet("/{id}", GetById)
            .WithSummary("Get a managed data store")
            .Produces<DataStoreManageResponse>(200)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(500)
            .AddOpenApiOperationTransformer(DataStoreManageOpenApi.Identifier);
        group
            .MapSecuredPost("", Create)
            .WithSummary("Accept managed data store creation")
            .WithDescription(
                "Returns 202 with a jobQueuedResult after durable acceptance; its jobId is readable through /v3/jobs/{jobId}. Location is the absolute management resource URL. Disabled management returns 400; closed provisioner readiness returns 503 without acceptance."
            )
            .Accepts<DataStoreManageInsertCommand>("application/json")
            .Produces<JobQueuedResponse>(202)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(503)
            .ProducesProblem(500)
            .AddOpenApiOperationTransformer(DataStoreManageOpenApi.Create);
        group
            .MapSecuredDelete("/{id}", Delete)
            .WithSummary("Accept managed data store deletion")
            .WithDescription(
                "Only Created can be deleted. Returns 202 with a jobQueuedResult after durable acceptance, without Location; its jobId is readable through /v3/jobs/{jobId}. Absent or Deleted returns 404; other states return 400. Closed readiness returns 503 without changing Created."
            )
            .Produces<JobQueuedResponse>(202)
            .ProducesProblem(400)
            .ProducesProblem(401)
            .ProducesProblem(403)
            .ProducesProblem(404)
            .ProducesProblem(503)
            .ProducesProblem(500)
            .AddOpenApiOperationTransformer(DataStoreManageOpenApi.Identifier);
    }

    private static async Task<IResult> GetAll(
        HttpContext context,
        JobClaimPolicy policy,
        IServiceProvider services
    )
    {
        if (policy.ExcludesManagedDataStores)
        {
            return Disabled(context);
        }
        var (query, errors) = FrontendDataStoreManageQuery.Parse(context.Request.Query);
        if (errors.Count != 0)
        {
            return FailureResults.DataValidation(errors, context.TraceIdentifier);
        }
        return await services
            .GetRequiredService<IDataStoreManageRepository>()
            .QueryAsync(query, context.RequestAborted) switch
        {
            DataStoreManageQueryResult.Success success => Results.Ok(success.DataStores),
            DataStoreManageQueryResult.InvalidRequest => Invalid(
                context,
                "Invalid managed data store query."
            ),
            DataStoreManageQueryResult.FailureUnknown failure => Failed(
                context,
                services,
                "QueryManagedDataStores",
                failure.Diagnostic
            ),
            _ => FailureResults.Unknown(context.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        string id,
        HttpContext context,
        JobClaimPolicy policy,
        IServiceProvider services
    )
    {
        if (policy.ExcludesManagedDataStores)
        {
            return Disabled(context);
        }
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int manageId))
        {
            return Invalid(context, "Id must be an int32 value.", "id");
        }
        return await services
            .GetRequiredService<IDataStoreManageRepository>()
            .GetByIdAsync(manageId, context.RequestAborted) switch
        {
            DataStoreManageGetResult.Success success => Results.Ok(success.DataStore),
            DataStoreManageGetResult.NotFound => FailureResults.NotFound(
                "Managed data store not found.",
                context.TraceIdentifier
            ),
            DataStoreManageGetResult.FailureUnknown failure => Failed(
                context,
                services,
                "GetManagedDataStore",
                failure.Diagnostic
            ),
            _ => FailureResults.Unknown(context.TraceIdentifier),
        };
    }

    private static async Task<IResult> Create(
        HttpContext context,
        JobClaimPolicy policy,
        IServiceProvider services
    )
    {
        if (policy.ExcludesManagedDataStores)
        {
            return Disabled(context);
        }
        DataStoreManageInsertCommand? command;
        // Read after the feature check, so malformed input cannot bypass the disabled response.
        // The DTO's unmapped-member rule is local to this request and does not tighten ordinary APIs.
        if (!context.Request.HasJsonContentType())
        {
            return Invalid(context, "A JSON managed data store request is required.");
        }
        try
        {
            command = await context.Request.ReadFromJsonAsync<DataStoreManageInsertCommand>(
                context.RequestAborted
            );
        }
        catch (JsonException)
        {
            return Invalid(context, "Invalid managed data store JSON request.");
        }
        if (command is null)
        {
            return Invalid(context, "A managed data store request is required.");
        }
        var validation = new DataStoreManageInsertCommand.Validator().Validate(command);
        if (!validation.IsValid)
        {
            return FailureResults.DataValidation(validation.Errors, context.TraceIdentifier);
        }
        var result = await services
            .GetRequiredService<ManagedDataStoreCreateAcceptance>()
            .AcceptAsync(command, context.RequestAborted);
        if (result is DataStoreManageCreateResult.Success success)
        {
            string location = UriHelper.BuildAbsolute(
                context.Request.Scheme,
                context.Request.Host,
                new PathString(context.Request.PathBase.Value?.TrimEnd('/')),
                new PathString(Route + "/" + success.Id.ToString(CultureInfo.InvariantCulture))
            );
            return Queued(location, success.JobId, CreateQueuedMessage);
        }
        return result switch
        {
            DataStoreManageCreateResult.ManagementDisabled => Disabled(context),
            DataStoreManageCreateResult.ProvisioningUnavailable => Unavailable(context),
            DataStoreManageCreateResult.NameReserved => Invalid(
                context,
                "A data store with this name already exists.",
                "name"
            ),
            DataStoreManageCreateResult.TargetReserved => Invalid(
                context,
                "The requested target database is already reserved.",
                "name"
            ),
            DataStoreManageCreateResult.InvalidRequest => Invalid(
                context,
                "Invalid managed data store name or template."
            ),
            DataStoreManageCreateResult.ConnectionResolutionFailed failure => Failed(
                context,
                services,
                "CreateManagedDataStore",
                failure.Diagnostic
            ),
            DataStoreManageCreateResult.FailureUnknown failure => Failed(
                context,
                services,
                "CreateManagedDataStore",
                failure.Diagnostic
            ),
            DataStoreManageCreateResult.EnqueueFailed failure => Failed(
                context,
                services,
                "CreateManagedDataStore",
                EnqueueDiagnostic(failure.Failure)
            ),
            _ => FailureResults.Unknown(context.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        string id,
        HttpContext context,
        JobClaimPolicy policy,
        IServiceProvider services
    )
    {
        if (policy.ExcludesManagedDataStores)
        {
            return Disabled(context);
        }
        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int manageId))
        {
            return Invalid(context, "Id must be an int32 value.", "id");
        }
        return await services
            .GetRequiredService<IDataStoreManageRepository>()
            .AcceptDeleteAsync(manageId, context.RequestAborted) switch
        {
            DataStoreManageDeleteResult.Success success => Queued(null, success.JobId, DeleteQueuedMessage),
            DataStoreManageDeleteResult.ManagementDisabled => Disabled(context),
            DataStoreManageDeleteResult.NotFound => FailureResults.NotFound(
                "Managed data store not found.",
                context.TraceIdentifier
            ),
            DataStoreManageDeleteResult.ProvisioningUnavailable => Unavailable(context),
            DataStoreManageDeleteResult.InvalidState invalid => Invalid(
                context,
                // One fixed message per blocked state. Failed work remains eligible for an automatic retry
                // (which waits while provisioning is paused); Error work is terminal.
                invalid.Status switch
                {
                    ManagedDataStoreStatus.PendingCreate =>
                        "The data store is queued to be created (PendingCreate).",
                    ManagedDataStoreStatus.CreateInProgress =>
                        "The data store is being created (CreateInProgress).",
                    ManagedDataStoreStatus.CreateFailed =>
                        "Data store creation failed and is eligible for an automatic retry (CreateFailed).",
                    ManagedDataStoreStatus.CreateError =>
                        "Data store creation failed and will not be retried; operator investigation is required (CreateError).",
                    ManagedDataStoreStatus.PendingDelete =>
                        "The data store is queued to be deleted (PendingDelete).",
                    ManagedDataStoreStatus.DeleteInProgress =>
                        "The data store is being deleted (DeleteInProgress).",
                    ManagedDataStoreStatus.DeleteFailed =>
                        "Data store deletion failed and is eligible for an automatic retry (DeleteFailed).",
                    ManagedDataStoreStatus.DeleteError =>
                        "Data store deletion failed and will not be retried; operator investigation is required (DeleteError).",
                    _ => "The data store cannot be deleted in its current state.",
                }
            ),
            DataStoreManageDeleteResult.FailureUnknown failure => Failed(
                context,
                services,
                "DeleteManagedDataStore",
                failure.Diagnostic
            ),
            DataStoreManageDeleteResult.EnqueueFailed failure => Failed(
                context,
                services,
                "DeleteManagedDataStore",
                EnqueueDiagnostic(failure.Failure)
            ),
            _ => FailureResults.Unknown(context.TraceIdentifier),
        };
    }
}
