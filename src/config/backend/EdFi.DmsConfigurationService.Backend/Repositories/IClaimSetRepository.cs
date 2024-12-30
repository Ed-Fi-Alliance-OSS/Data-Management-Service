// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using Action = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IClaimSetRepository
{
    IEnumerable<Action> GetActions();
    IEnumerable<AuthorizationStrategy> GetAuthorizationStrategies();
    Task<ClaimSetInsertResult> InsertClaimSet(ClaimSetInsertCommand command);
    Task<ClaimSetQueryResult> QueryClaimSet(PagingQuery query, bool verbose);
    Task<ClaimSetGetResult> GetClaimSet(long id, bool verbose);
    Task<ClaimSetUpdateResult> UpdateClaimSet(ClaimSetUpdateCommand command);
    Task<ClaimSetDeleteResult> DeleteClaimSet(long id);
    Task<ClaimSetExportResult> Export(long id);
    Task<ClaimSetImportResult> Import(ClaimSetImportCommand command);
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

    /// <summary>
    /// ClaimSetName must be unique
    /// </summary>
    public record FailureDuplicateClaimSetName() : ClaimSetInsertResult();
}

public record ClaimSetQueryResult
{
    /// <summary>
    /// Successfully queried and returning list of claimSets responses
    /// </summary>
    public record Success(IEnumerable<object> ClaimSetResponses) : ClaimSetQueryResult();

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
    public record Success(object ClaimSetResponse) : ClaimSetGetResult();

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
    public record FailureNotFound() : ClaimSetUpdateResult();

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
    public record FailureNotFound() : ClaimSetDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetDeleteResult();
}

public record ClaimSetExportResult
{
    /// <summary>
    /// Successfully retrieved ClaimSet
    /// </summary>
    public record Success(ClaimSetExportResponse ClaimSetExportResponse) : ClaimSetExportResult();

    /// <summary>
    /// ClaimSet does not exist in the datastore
    /// </summary>
    public record FailureNotFound() : ClaimSetExportResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetExportResult();
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
    public record FailureNotFound() : ClaimSetCopyResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetCopyResult();
}

public record ClaimSetImportResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(long Id) : ClaimSetImportResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : ClaimSetImportResult();

    /// <summary>
    /// ClaimSetName must be unique
    /// </summary>
    public record FailureDuplicateClaimSetName() : ClaimSetImportResult();
}
