// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IApiClientRepository
{
    Task<ApiClientQueryResult> QueryApiClient(PagingQuery query);
    Task<ApiClientGetResult> GetApiClientByClientId(string clientId);
}

public record ApiClientQueryResult
{
    /// <summary>
    /// Successful query.
    /// </summary>
    /// <param name="ApiClientResponses">The ApiClient responses.</param>
    public record Success(List<ApiClientResponse> ApiClientResponses) : ApiClientQueryResult();

    /// <summary>
    /// Unknown failure.
    /// </summary>
    /// <param name="FailureMessage">The failure message.</param>
    public record FailureUnknown(string FailureMessage) : ApiClientQueryResult();
}

public record ApiClientGetResult
{
    /// <summary>
    /// Successful get.
    /// </summary>
    /// <param name="ApiClientResponse">The ApiClient response.</param>
    public record Success(ApiClientResponse ApiClientResponse) : ApiClientGetResult();

    /// <summary>
    /// ApiClient not found.
    /// </summary>
    public record FailureNotFound() : ApiClientGetResult();

    /// <summary>
    /// Unknown failure.
    /// </summary>
    /// <param name="FailureMessage">The failure message.</param>
    public record FailureUnknown(string FailureMessage) : ApiClientGetResult();
}
