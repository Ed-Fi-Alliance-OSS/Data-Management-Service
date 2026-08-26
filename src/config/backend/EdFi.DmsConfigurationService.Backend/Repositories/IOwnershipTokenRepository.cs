// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IOwnershipTokenRepository
{
    Task<OwnershipTokenInsertResult> InsertOwnershipToken(OwnershipTokenInsertCommand command);
    Task<OwnershipTokenQueryResult> QueryOwnershipTokens(OwnershipTokenQuery query);
    Task<OwnershipTokenGetResult> GetOwnershipToken(int id);
    Task<OwnershipTokenUpdateResult> UpdateOwnershipToken(OwnershipTokenUpdateCommand command);
    Task<ApiClientOwnershipGetResult> GetApiClientOwnership(int apiClientId);
    Task<ApiClientOwnershipUpdateResult> UpdateApiClientOwnership(ApiClientOwnershipUpdateCommand command);
}

public record OwnershipTokenInsertResult
{
    public record Success(int Id) : OwnershipTokenInsertResult;

    public record FailureUnknown(string FailureMessage) : OwnershipTokenInsertResult;
}

public record OwnershipTokenQueryResult
{
    public record Success(List<OwnershipTokenResponse> OwnershipTokens) : OwnershipTokenQueryResult;

    public record FailureUnknown(string FailureMessage) : OwnershipTokenQueryResult;
}

public record OwnershipTokenGetResult
{
    public record Success(OwnershipTokenResponse OwnershipToken) : OwnershipTokenGetResult;

    public record FailureNotFound() : OwnershipTokenGetResult;

    public record FailureUnknown(string FailureMessage) : OwnershipTokenGetResult;
}

public record OwnershipTokenUpdateResult
{
    public record Success() : OwnershipTokenUpdateResult;

    public record FailureNotFound() : OwnershipTokenUpdateResult;

    public record FailureUnknown(string FailureMessage) : OwnershipTokenUpdateResult;
}

public record ApiClientOwnershipGetResult
{
    public record Success(ApiClientOwnershipResponse Ownership) : ApiClientOwnershipGetResult;

    public record FailureApiClientNotFound() : ApiClientOwnershipGetResult;

    public record FailureUnknown(string FailureMessage) : ApiClientOwnershipGetResult;
}

public record ApiClientOwnershipUpdateResult
{
    public record Success() : ApiClientOwnershipUpdateResult;

    public record FailureApiClientNotFound() : ApiClientOwnershipUpdateResult;

    public record FailureOwnershipTokenNotFound() : ApiClientOwnershipUpdateResult;

    public record FailureUnknown(string FailureMessage) : ApiClientOwnershipUpdateResult;
}
