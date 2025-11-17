// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceDerivative;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

/// <summary>
/// Repository for DMS instance derivatives (read replicas and snapshots)
/// </summary>
public interface IDmsInstanceDerivativeRepository
{
    /// <summary>
    /// Insert a new DMS instance derivative
    /// </summary>
    Task<DmsInstanceDerivativeInsertResult> InsertDmsInstanceDerivative(
        DmsInstanceDerivativeInsertCommand command
    );

    /// <summary>
    /// Query DMS instance derivatives with paging
    /// </summary>
    Task<DmsInstanceDerivativeQueryResult> QueryDmsInstanceDerivative(PagingQuery query);

    /// <summary>
    /// Get a DMS instance derivative by ID
    /// </summary>
    Task<DmsInstanceDerivativeGetResult> GetDmsInstanceDerivative(long id);

    /// <summary>
    /// Update an existing DMS instance derivative
    /// </summary>
    Task<DmsInstanceDerivativeUpdateResult> UpdateDmsInstanceDerivative(
        DmsInstanceDerivativeUpdateCommand command
    );

    /// <summary>
    /// Delete a DMS instance derivative by ID
    /// </summary>
    Task<DmsInstanceDerivativeDeleteResult> DeleteDmsInstanceDerivative(long id);

    /// <summary>
    /// Get all derivatives for a specific DMS instance
    /// </summary>
    Task<InstanceDerivativeQueryByInstanceResult> GetInstanceDerivativesByInstance(long instanceId);

    /// <summary>
    /// Get all derivatives for multiple DMS instances
    /// </summary>
    Task<InstanceDerivativeQueryByInstanceIdsResult> GetInstanceDerivativesByInstanceIds(
        List<long> instanceIds
    );
}

// Result types using discriminated unions pattern

/// <summary>
/// Result of inserting a DMS instance derivative
/// </summary>
public record DmsInstanceDerivativeInsertResult
{
    /// <summary>
    /// Insert was successful
    /// </summary>
    /// <param name="Id">The ID of the newly inserted derivative</param>
    public record Success(long Id) : DmsInstanceDerivativeInsertResult();

    /// <summary>
    /// Insert failed due to foreign key violation (invalid InstanceId)
    /// </summary>
    public record FailureForeignKeyViolation() : DmsInstanceDerivativeInsertResult();

    /// <summary>
    /// Insert failed due to unknown error
    /// </summary>
    /// <param name="FailureMessage">Error message</param>
    public record FailureUnknown(string FailureMessage) : DmsInstanceDerivativeInsertResult();
}

/// <summary>
/// Result of querying DMS instance derivatives
/// </summary>
public record DmsInstanceDerivativeQueryResult
{
    /// <summary>
    /// Query was successful
    /// </summary>
    /// <param name="DmsInstanceDerivativeResponses">The list of derivatives</param>
    public record Success(IEnumerable<DmsInstanceDerivativeResponse> DmsInstanceDerivativeResponses)
        : DmsInstanceDerivativeQueryResult();

    /// <summary>
    /// Query failed due to unknown error
    /// </summary>
    /// <param name="FailureMessage">Error message</param>
    public record FailureUnknown(string FailureMessage) : DmsInstanceDerivativeQueryResult();
}

/// <summary>
/// Result of getting a DMS instance derivative by ID
/// </summary>
public record DmsInstanceDerivativeGetResult
{
    /// <summary>
    /// Get was successful
    /// </summary>
    /// <param name="DmsInstanceDerivativeResponse">The derivative details</param>
    public record Success(DmsInstanceDerivativeResponse DmsInstanceDerivativeResponse)
        : DmsInstanceDerivativeGetResult();

    /// <summary>
    /// Get failed because derivative was not found
    /// </summary>
    public record FailureNotFound() : DmsInstanceDerivativeGetResult();

    /// <summary>
    /// Get failed due to unknown error
    /// </summary>
    /// <param name="FailureMessage">Error message</param>
    public record FailureUnknown(string FailureMessage) : DmsInstanceDerivativeGetResult();
}

/// <summary>
/// Result of updating a DMS instance derivative
/// </summary>
public record DmsInstanceDerivativeUpdateResult
{
    /// <summary>
    /// Update was successful
    /// </summary>
    public record Success() : DmsInstanceDerivativeUpdateResult();

    /// <summary>
    /// Update failed because derivative was not found
    /// </summary>
    public record FailureNotFound() : DmsInstanceDerivativeUpdateResult();

    /// <summary>
    /// Update failed due to foreign key violation (invalid InstanceId)
    /// </summary>
    public record FailureForeignKeyViolation() : DmsInstanceDerivativeUpdateResult();

    /// <summary>
    /// Update failed due to unknown error
    /// </summary>
    /// <param name="FailureMessage">Error message</param>
    public record FailureUnknown(string FailureMessage) : DmsInstanceDerivativeUpdateResult();
}

/// <summary>
/// Result of deleting a DMS instance derivative
/// </summary>
public record DmsInstanceDerivativeDeleteResult
{
    /// <summary>
    /// Delete was successful
    /// </summary>
    public record Success() : DmsInstanceDerivativeDeleteResult();

    /// <summary>
    /// Delete failed because derivative was not found
    /// </summary>
    public record FailureNotFound() : DmsInstanceDerivativeDeleteResult();

    /// <summary>
    /// Delete failed due to unknown error
    /// </summary>
    /// <param name="FailureMessage">Error message</param>
    public record FailureUnknown(string FailureMessage) : DmsInstanceDerivativeDeleteResult();
}

/// <summary>
/// Result of querying derivatives for a specific instance
/// </summary>
public record InstanceDerivativeQueryByInstanceResult
{
    /// <summary>
    /// Successful retrieval of derivatives for a specific instance
    /// </summary>
    public record Success(IEnumerable<DmsInstanceDerivativeResponse> DmsInstanceDerivativeResponses)
        : InstanceDerivativeQueryByInstanceResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : InstanceDerivativeQueryByInstanceResult();
}

/// <summary>
/// Result of querying derivatives for multiple instances
/// </summary>
public record InstanceDerivativeQueryByInstanceIdsResult
{
    /// <summary>
    /// Successful retrieval of derivatives for multiple instances
    /// </summary>
    public record Success(IEnumerable<DmsInstanceDerivativeResponse> DmsInstanceDerivativeResponses)
        : InstanceDerivativeQueryByInstanceIdsResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : InstanceDerivativeQueryByInstanceIdsResult();
}
