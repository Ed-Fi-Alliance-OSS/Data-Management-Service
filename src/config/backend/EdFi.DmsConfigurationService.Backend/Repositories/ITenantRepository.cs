// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface ITenantRepository
{
    Task<TenantInsertResult> InsertTenant(TenantInsertCommand command);
    Task<TenantQueryResult> QueryTenant(PagingQuery query);
    Task<TenantGetResult> GetTenant(long id);
    Task<TenantGetByNameResult> GetTenantByName(string name);
}

public record TenantInsertResult
{
    /// <summary>
    /// Successful insert
    /// </summary>
    /// <param name="Id">The Id of the inserted record</param>
    public record Success(long Id) : TenantInsertResult();

    /// <summary>
    /// Tenant name must be unique
    /// </summary>
    public record FailureDuplicateName() : TenantInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : TenantInsertResult();
}

public record TenantQueryResult
{
    /// <summary>
    /// Successfully queried and returning list of tenant responses
    /// </summary>
    public record Success(IEnumerable<TenantResponse> TenantResponses) : TenantQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : TenantQueryResult();
}

public record TenantGetResult
{
    /// <summary>
    /// Successfully retrieved tenant and returning tenant response
    /// </summary>
    public record Success(TenantResponse TenantResponse) : TenantGetResult();

    /// <summary>
    /// Tenant does not exist in the datastore
    /// </summary>
    public record FailureNotFound() : TenantGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : TenantGetResult();
}

public record TenantGetByNameResult
{
    /// <summary>
    /// Successfully retrieved tenant by name and returning tenant response
    /// </summary>
    public record Success(TenantResponse TenantResponse) : TenantGetByNameResult();

    /// <summary>
    /// Tenant does not exist in the datastore
    /// </summary>
    public record FailureNotFound() : TenantGetByNameResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : TenantGetByNameResult();
}
