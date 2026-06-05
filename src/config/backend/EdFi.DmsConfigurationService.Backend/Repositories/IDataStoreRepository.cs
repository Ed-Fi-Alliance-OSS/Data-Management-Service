// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IDataStoreRepository
{
    Task<DataStoreInsertResult> InsertDataStore(DataStoreInsertCommand command);
    Task<DataStoreQueryResult> QueryDataStore(DataStoreQuery query);
    Task<DataStoreGetResult> GetDataStore(long id);
    Task<DataStoreUpdateResult> UpdateDataStore(DataStoreUpdateCommand command);
    Task<DataStoreDeleteResult> DeleteDataStore(long id);
    Task<DataStoreIdsExistResult> GetExistingDataStoreIds(long[] ids);
    Task<ApplicationByDataStoreQueryResult> QueryApplicationByDataStore(long dataStoreId, PagingQuery query);
}

public record DataStoreInsertResult
{
    public record Success(long Id) : DataStoreInsertResult();

    public record FailureUnknown(string FailureMessage) : DataStoreInsertResult();
}

public record DataStoreQueryResult
{
    public record Success(IEnumerable<DataStoreResponse> DataStoreResponses) : DataStoreQueryResult();

    public record FailureUnknown(string FailureMessage) : DataStoreQueryResult();
}

public record DataStoreGetResult
{
    public record Success(DataStoreResponse DataStoreResponse) : DataStoreGetResult();

    public record FailureNotFound() : DataStoreGetResult();

    public record FailureUnknown(string FailureMessage) : DataStoreGetResult();
}

public record DataStoreUpdateResult
{
    public record Success() : DataStoreUpdateResult();

    public record FailureNotExists() : DataStoreUpdateResult();

    public record FailureUnknown(string FailureMessage) : DataStoreUpdateResult();
}

public record DataStoreDeleteResult
{
    public record Success() : DataStoreDeleteResult();

    public record FailureNotExists() : DataStoreDeleteResult();

    public record FailureUnknown(string FailureMessage) : DataStoreDeleteResult();
}

public record DataStoreIdsExistResult
{
    /// <summary>
    /// Successfully retrieved existing DataStoreIds
    /// </summary>
    /// <param name="ExistingIds">The set of DataStoreIds that exist in the database</param>
    public record Success(HashSet<long> ExistingIds) : DataStoreIdsExistResult();

    public record FailureUnknown(string FailureMessage) : DataStoreIdsExistResult();
}

public record ApplicationByDataStoreQueryResult
{
    public record Success(IEnumerable<ApplicationResponse> ApplicationResponse)
        : ApplicationByDataStoreQueryResult();

    public record FailureNotExists() : ApplicationByDataStoreQueryResult();

    public record FailureUnknown(string FailureMessage) : ApplicationByDataStoreQueryResult();
}
