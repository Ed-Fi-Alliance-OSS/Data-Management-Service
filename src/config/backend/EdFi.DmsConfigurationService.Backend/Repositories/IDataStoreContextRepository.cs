// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreContext;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IDataStoreContextRepository
{
    Task<DataStoreContextInsertResult> InsertDataStoreContext(DataStoreContextInsertCommand command);
    Task<DataStoreContextQueryResult> QueryDataStoreContext(PagingQuery query);
    Task<DataStoreContextGetResult> GetDataStoreContext(long id);
    Task<DataStoreContextUpdateResult> UpdateDataStoreContext(DataStoreContextUpdateCommand command);
    Task<DataStoreContextDeleteResult> DeleteDataStoreContext(long id);
    Task<DataStoreContextQueryByDataStoreResult> GetDataStoreContextsByDataStore(long dataStoreId);
    Task<DataStoreContextQueryByDataStoreIdsResult> GetDataStoreContextsByDataStoreIds(
        List<long> dataStoreIds
    );
}

public record DataStoreContextInsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(long Id) : DataStoreContextInsertResult();

    /// <summary>
    /// Referenced data store not found exception thrown and caught
    /// </summary>
    public record FailureDataStoreNotFound() : DataStoreContextInsertResult();

    /// <summary>
    /// Data store context already exists for the given DataStoreId and ContextKey
    /// </summary>
    public record FailureDuplicateDataStoreContext(long DataStoreId, string ContextKey)
        : DataStoreContextInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreContextInsertResult();
}

public record DataStoreContextQueryResult
{
    /// <summary>
    /// A successful query result with responses
    /// </summary>
    public record Success(IEnumerable<DataStoreContextResponse> DataStoreContextResponses)
        : DataStoreContextQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreContextQueryResult();
}

public record DataStoreContextGetResult
{
    /// <summary>
    /// Successful get data store context with the response
    /// </summary>
    public record Success(DataStoreContextResponse DataStoreContextResponse) : DataStoreContextGetResult();

    /// <summary>
    /// Data store context not found
    /// </summary>
    public record FailureNotFound() : DataStoreContextGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreContextGetResult();
}

public record DataStoreContextUpdateResult
{
    /// <summary>
    /// The data store context was updated successfully
    /// </summary>
    public record Success() : DataStoreContextUpdateResult();

    /// <summary>
    /// Data store context id not found
    /// </summary>
    public record FailureNotExists() : DataStoreContextUpdateResult();

    /// <summary>
    /// Referenced data store not found exception thrown and caught
    /// </summary>
    public record FailureDataStoreNotFound() : DataStoreContextUpdateResult();

    /// <summary>
    /// Data store context already exists for the given DataStoreId and ContextKey
    /// </summary>
    public record FailureDuplicateDataStoreContext(long DataStoreId, string ContextKey)
        : DataStoreContextUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreContextUpdateResult();
}

public record DataStoreContextDeleteResult
{
    /// <summary>
    /// The data store context was deleted successfully
    /// </summary>
    public record Success() : DataStoreContextDeleteResult();

    /// <summary>
    /// Data store context id does not exist
    /// </summary>
    public record FailureNotExists() : DataStoreContextDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreContextDeleteResult();
}

public record DataStoreContextQueryByDataStoreResult
{
    /// <summary>
    /// Successful retrieval of data store contexts for a specific data store
    /// </summary>
    public record Success(IEnumerable<DataStoreContextResponse> DataStoreContextResponses)
        : DataStoreContextQueryByDataStoreResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreContextQueryByDataStoreResult();
}

public record DataStoreContextQueryByDataStoreIdsResult
{
    /// <summary>
    /// Successful retrieval of data store contexts for multiple data stores
    /// </summary>
    public record Success(IEnumerable<DataStoreContextResponse> DataStoreContextResponses)
        : DataStoreContextQueryByDataStoreIdsResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DataStoreContextQueryByDataStoreIdsResult();
}
