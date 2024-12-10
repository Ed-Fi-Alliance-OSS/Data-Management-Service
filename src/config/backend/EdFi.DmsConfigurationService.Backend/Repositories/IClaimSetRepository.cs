// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IClaimSetRepository
{
    IEnumerable<AuthorizationStrategy> GetAuthorizationStrategies();
    Task<ClaimSetInsertResult> InsertClaimSet(ClaimSetInsertCommand command);
    Task<ClaimSetQueryResult> QueryClaimSet(PagingQuery query);
    Task<ClaimSetGetResult> GetClaimSet(long id);
    Task<ClaimSetUpdateResult> UpdateClaimSet(ClaimSetUpdateCommand command);
    Task<ClaimSetDeleteResult> DeleteClaimSet(long id);
    Task<ClaimSetGetResult> Export(long id);
    Task<ClaimSetInsertResult> Import(ClaimSetInsertCommand command);
    Task<ClaimSetCopyResult> Copy(ClaimSetCopyCommand command);
}

public record ClaimSetInsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(long Id) : ClaimSetInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetInsertResult();
}

public record ClaimSetQueryResult
{
    /// <summary>
    /// Successfully queried and returning list of claimSets responses
    /// </summary>
    public record Success(IEnumerable<ClaimSetResponse> ClaimSetResponses) : ClaimSetQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetQueryResult();
}

public record ClaimSetGetResult
{
    /// <summary>
    /// Successfully retrieved ClaimSet
    /// </summary>
    public record Success(ClaimSetResponse ClaimSetResponse) : ClaimSetGetResult();

    /// <summary>
    /// ClaimSet does not exist in the datastore
    /// </summary>
    public record FailureNotFound() : ClaimSetGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetGetResult();
}

public record ClaimSetUpdateResult
{
    /// <summary>
    /// Successfully updated ClaimSet
    /// </summary>
    public record Success() : ClaimSetUpdateResult();

    /// <summary>
    /// ClaimSet id not found
    /// </summary>
    public record FailureNotExists() : ClaimSetUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetUpdateResult();
}

public record ClaimSetDeleteResult
{
    public record Success() : ClaimSetDeleteResult();

    /// <summary>
    /// ClaimSet id not found
    /// </summary>
    public record FailureNotExists() : ClaimSetDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetDeleteResult();
}

public record ClaimSetCopyResult
{
    /// <summary>
    /// Successfully updated ClaimSet
    /// </summary>
    public record Success(long Id) : ClaimSetCopyResult();

    /// <summary>
    /// ClaimSet id not found
    /// </summary>
    public record FailureNotExists() : ClaimSetCopyResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetCopyResult();
}
