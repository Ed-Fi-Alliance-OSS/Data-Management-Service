// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IApplicationRepository
{
    Task<ApplicationInsertResult> InsertApplication(
        ApplicationInsertCommand command,
        ApiClientCommand clientCommand
    );
    Task<ApplicationQueryResult> QueryApplication(PagingQuery query);
    Task<ApplicationGetResult> GetApplication(long id);
    Task<ApplicationUpdateResult> UpdateApplication(
        ApplicationUpdateCommand command,
        ApiClientCommand clientCommand
    );
    Task<ApplicationDeleteResult> DeleteApplication(long id);
    Task<ApplicationApiClientsResult> GetApplicationApiClients(long id);
}

public record ApplicationInsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(long Id) : ApplicationInsertResult();

    /// <summary>
    /// Referenced vendor not found exception thrown and caught
    /// </summary>
    public record FailureVendorNotFound() : ApplicationInsertResult();

    /// <summary>
    /// Application name already exists for the vendor
    /// </summary>
    public record FailureDuplicateApplication(string ApplicationName) : ApplicationInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationInsertResult();
}

public record ApplicationQueryResult
{
    /// <summary>
    /// A successful query result with responses
    /// </summary>
    public record Success(IEnumerable<ApplicationResponse> ApplicationResponses) : ApplicationQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationQueryResult();
}

public record ApplicationGetResult
{
    /// <summary>
    /// Successful get application with the application response
    /// </summary>
    /// <param name="ApplicationResponse"></param>
    public record Success(ApplicationResponse ApplicationResponse) : ApplicationGetResult();

    /// <summary>
    /// Application not found in data store
    /// </summary>
    public record FailureNotFound() : ApplicationGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationGetResult();
}

public record ApplicationUpdateResult
{
    /// <summary>
    /// The application was updated successfully
    /// </summary>
    public record Success() : ApplicationUpdateResult();

    /// <summary>
    /// Application id not found
    /// </summary>
    public record FailureNotExists() : ApplicationUpdateResult();

    /// <summary>
    /// Referenced vendor not found exception thrown and caught
    /// </summary>
    public record FailureVendorNotFound() : ApplicationUpdateResult();

    /// <summary>
    /// Application name already exists for the vendor
    /// </summary>
    public record FailureDuplicateApplication(string ApplicationName) : ApplicationUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationUpdateResult();
}

public record ApplicationDeleteResult
{
    /// <summary>
    /// The application was deleted successfully
    /// </summary>
    public record Success() : ApplicationDeleteResult();

    /// <summary>
    /// Application id does not exist in the datastore
    /// </summary>
    public record FailureNotExists() : ApplicationDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationDeleteResult();
}

public record ApplicationApiClientsResult
{
    /// <summary>
    /// Successful retrieval of clientUuids
    /// </summary>
    public record Success(ApiClient[] Clients) : ApplicationApiClientsResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationApiClientsResult();
}

/// <summary>
/// Relevant keycloak identifying values for api clients
/// </summary>
public record ApiClient(
    /// <summary>
    /// The identifying string of a client. Must be unique per realm.
    /// </summary>
    string ClientId,
    /// <summary>
    /// The behind the scenes globally unique identifier for the client.
    /// This must be used for deleting the resource and resetting credentials.
    /// </summary>
    Guid ClientUuid
);
