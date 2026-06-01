// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IDataStoreDerivativeRepository
{
    Task<DataStoreDerivativeInsertResult> InsertDataStoreDerivative(DataStoreDerivativeInsertCommand command);
    Task<DataStoreDerivativeQueryResult> QueryDataStoreDerivative(PagingQuery query);
    Task<DataStoreDerivativeGetResult> GetDataStoreDerivative(long id);
    Task<DataStoreDerivativeUpdateResult> UpdateDataStoreDerivative(DataStoreDerivativeUpdateCommand command);
    Task<DataStoreDerivativeDeleteResult> DeleteDataStoreDerivative(long id);
    Task<DataStoreDerivativeQueryByDataStoreResult> GetDataStoreDerivativesByDataStore(long dataStoreId);
    Task<DataStoreDerivativeQueryByDataStoreIdsResult> GetDataStoreDerivativesByDataStoreIds(
        List<long> dataStoreIds
    );
}

public record DataStoreDerivativeInsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(long Id) : DataStoreDerivativeInsertResult();

    /// <summary>
    /// Insert failed due to foreign key violation (invalid DataStoreId)
    /// </summary>
    public record FailureForeignKeyViolation() : DataStoreDerivativeInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreDerivativeInsertResult();
}

public record DataStoreDerivativeQueryResult
{
    /// <summary>
    /// A successful query result with responses
    /// </summary>
    public record Success(IEnumerable<DataStoreDerivativeResponse> DataStoreDerivativeResponses)
        : DataStoreDerivativeQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreDerivativeQueryResult();
}

public record DataStoreDerivativeGetResult
{
    /// <summary>
    /// Successful get data store derivative with the response
    /// </summary>
    public record Success(DataStoreDerivativeResponse DataStoreDerivativeResponse)
        : DataStoreDerivativeGetResult();

    /// <summary>
    /// Data store derivative not found
    /// </summary>
    public record FailureNotFound() : DataStoreDerivativeGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreDerivativeGetResult();
}

public record DataStoreDerivativeUpdateResult
{
    /// <summary>
    /// The data store derivative was updated successfully
    /// </summary>
    public record Success() : DataStoreDerivativeUpdateResult();

    /// <summary>
    /// Data store derivative id not found
    /// </summary>
    public record FailureNotFound() : DataStoreDerivativeUpdateResult();

    /// <summary>
    /// Update failed due to foreign key violation (invalid DataStoreId)
    /// </summary>
    public record FailureForeignKeyViolation() : DataStoreDerivativeUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreDerivativeUpdateResult();
}

public record DataStoreDerivativeDeleteResult
{
    /// <summary>
    /// The data store derivative was deleted successfully
    /// </summary>
    public record Success() : DataStoreDerivativeDeleteResult();

    /// <summary>
    /// Data store derivative id does not exist
    /// </summary>
    public record FailureNotFound() : DataStoreDerivativeDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreDerivativeDeleteResult();
}

public record DataStoreDerivativeQueryByDataStoreResult
{
    /// <summary>
    /// Successful retrieval of data store derivatives for a specific data store
    /// </summary>
    public record Success(IEnumerable<DataStoreDerivativeResponse> DataStoreDerivativeResponses)
        : DataStoreDerivativeQueryByDataStoreResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreDerivativeQueryByDataStoreResult();
}

public record DataStoreDerivativeQueryByDataStoreIdsResult
{
    /// <summary>
    /// Successful retrieval of data store derivatives for multiple data stores
    /// </summary>
    public record Success(IEnumerable<DataStoreDerivativeResponse> DataStoreDerivativeResponses)
        : DataStoreDerivativeQueryByDataStoreIdsResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreDerivativeQueryByDataStoreIdsResult();
}
