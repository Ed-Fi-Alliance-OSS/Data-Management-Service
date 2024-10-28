// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Application;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IApplicationRepository
{
    Task<ApplicationInsertResult> InsertApplication(
        ApplicationInsertCommand command,
        Guid clientUuid,
        string clientSecret
    );

    Task<ApplicationQueryResult> QueryApplication(PagingQuery query);
    Task<ApplicationGetResult> GetApplication(long id);
    Task<ApplicationUpdateResult> UpdateApplication(ApplicationUpdateCommand command);
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
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationInsertResult();
}

public record ApplicationQueryResult
{
    public record Success(IEnumerable<ApplicationResponse> ApplicationResponses) : ApplicationQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationQueryResult();
}

public record ApplicationGetResult
{
    public record Success(ApplicationResponse ApplicationResponse) : ApplicationGetResult();

    public record FailureNotFound() : ApplicationGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationGetResult();
}

public record ApplicationUpdateResult
{
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
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationUpdateResult();
}

public record ApplicationDeleteResult
{
    public record Success() : ApplicationDeleteResult();

    /// <summary>
    /// Application id not found
    /// </summary>
    public record FailureNotExists() : ApplicationDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationDeleteResult();
}

public record ApplicationApiClientsResult
{
    public record Success(Guid[] ClientUuids) : ApplicationApiClientsResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ApplicationApiClientsResult();
}
