// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure.Authorization;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

public class VendorModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapSecuredPost("/v2/vendors/", InsertVendor);
        endpoints.MapSecuredGet("/v2/vendors/", GetAll);
        endpoints.MapSecuredGet($"/v2/vendors/{{id}}", GetById);
        endpoints.MapSecuredPut($"/v2/vendors/{{id}}", Update);
        endpoints.MapSecuredDelete($"/v2/vendors/{{id}}", Delete);
        endpoints.MapSecuredGet($"/v2/vendors/{{id}}/applications", GetApplicationsByVendorId);
    }

    private static async Task<IResult> InsertVendor(
        VendorInsertCommand entity,
        VendorInsertCommand.Validator validator,
        HttpContext httpContext,
        IVendorRepository repository
    )
    {
        await validator.GuardAsync(entity);
        var insertResult = await repository.InsertVendor(entity);

        var request = httpContext.Request;
        return insertResult switch
        {
            VendorInsertResult.Success success when success.IsNewVendor => Results.Created(
                $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path.Value?.TrimEnd('/')}/{success.Id}",
                null
            ),
            VendorInsertResult.Success success when !success.IsNewVendor => Results.Ok(
               null
            ),
            VendorInsertResult.FailureDuplicateCompanyName => Results.Json(
            FailureResponse.ForDataValidation(
                [
                                new ValidationFailure(
                                    "Name",
                                    "A vendor name already exists in the database. Please enter a unique name."
                                ),
                ],
                httpContext.TraceIdentifier
            ),
                statusCode: (int)HttpStatusCode.BadRequest
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetAll(
        IVendorRepository repository,
        [AsParameters] PagingQuery query,
        HttpContext httpContext
    )
    {
        VendorQueryResult getResult = await repository.QueryVendor(query);
        return getResult switch
        {
            VendorQueryResult.Success success => Results.Ok(success.VendorResponses),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetById(
        long id,
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        VendorGetResult getResult = await repository.GetVendor(id);
        return getResult switch
        {
            VendorGetResult.Success success => Results.Ok(success.VendorResponse),
            VendorGetResult.FailureNotFound => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Update(
        long id,
        VendorUpdateCommand command,
        VendorUpdateCommand.Validator validator,
        HttpContext httpContext,
        IVendorRepository repository,
        IApplicationRepository applicationRepository,
        IClientRepository clientRepository,
        ILogger<ApplicationModule> logger
    )
    {
        await validator.GuardAsync(command);

        if (command.Id != id)
        {
            throw new ValidationException(
                new[] { new ValidationFailure("Id", "Request body id must match the id in the url.") }
            );
        }

        var vendorUpdateResult = await repository.UpdateVendor(command);

        if (vendorUpdateResult is VendorUpdateResult.Success result)
        {
            foreach (var clientUuid in result.AffectedClientUuids)
            {
                var clientUpdateResult = await clientRepository.UpdateClientNamespaceClaimAsync(
                    clientUuid.ToString(),
                    command.NamespacePrefixes
                );

                switch (clientUpdateResult)
                {
                    case ClientUpdateResult.Success:
                        continue;
                    case ClientUpdateResult.FailureIdentityProvider failureIdentityProvider:
                        logger.LogError(
                            "Failure updating client: {failureMessage}",
                            failureIdentityProvider.IdentityProviderError.FailureMessage
                        );
                        return FailureResults.BadGateway(
                            failureIdentityProvider.IdentityProviderError.FailureMessage,
                            httpContext.TraceIdentifier
                        );
                    case ClientUpdateResult.FailureNotFound notFound:
                        logger.LogError(notFound.FailureMessage);
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                    case ClientUpdateResult.FailureUnknown unknownFailure:
                        logger.LogError(
                            "Error updating apiClient {clientUuid}: {message}",
                            clientUuid,
                            unknownFailure.FailureMessage
                        );
                        return FailureResults.Unknown(httpContext.TraceIdentifier);
                }
            }
        }

        return vendorUpdateResult switch
        {
            VendorUpdateResult.Success success => Results.NoContent(),
            VendorUpdateResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> Delete(
        long id,
        HttpContext httpContext,
        IVendorRepository repository,
        ILogger<VendorModule> logger
    )
    {
        VendorDeleteResult deleteResult = await repository.DeleteVendor(id);
        return deleteResult switch
        {
            VendorDeleteResult.Success => Results.NoContent(),
            VendorDeleteResult.FailureNotExists => Results.Json(
                FailureResponse.ForNotFound(
                    $"Vendor {id} not found. It may have been recently deleted.",
                    httpContext.TraceIdentifier
                ),
                statusCode: (int)HttpStatusCode.NotFound
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }

    private static async Task<IResult> GetApplicationsByVendorId(
        long id,
        IVendorRepository repository,
        HttpContext httpContext
    )
    {
        var getResult = await repository.GetVendorApplications(id);

        return getResult switch
        {
            VendorApplicationsResult.Success success => Results.Ok(success.ApplicationResponses),
            VendorApplicationsResult.FailureNotExists => FailureResults.NotFound(
                $"Vendor {id} not found. It may have been recently deleted.",
                httpContext.TraceIdentifier
            ),
            _ => FailureResults.Unknown(httpContext.TraceIdentifier),
        };
    }
}
