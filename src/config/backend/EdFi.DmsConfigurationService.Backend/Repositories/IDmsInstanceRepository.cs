// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstance;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IDmsInstanceRepository
{
    Task<DmsInstanceInsertResult> InsertDmsInstance(DmsInstanceInsertCommand command);
    Task<DmsInstanceQueryResult> QueryDmsInstance(PagingQuery query);
    Task<DmsInstanceGetResult> GetDmsInstance(long id);
    Task<DmsInstanceUpdateResult> UpdateDmsInstance(DmsInstanceUpdateCommand command);
    Task<DmsInstanceDeleteResult> DeleteDmsInstance(long id);
    Task<DmsInstanceIdsExistResult> GetExistingDmsInstanceIds(long[] ids);
}

public record DmsInstanceInsertResult
{
    public record Success(long Id) : DmsInstanceInsertResult();

    public record FailureUnknown(string FailureMessage) : DmsInstanceInsertResult();
}

public record DmsInstanceQueryResult
{
    public record Success(IEnumerable<DmsInstanceResponse> DmsInstanceResponses) : DmsInstanceQueryResult();

    public record FailureUnknown(string FailureMessage) : DmsInstanceQueryResult();
}

public record DmsInstanceGetResult
{
    public record Success(DmsInstanceResponse DmsInstanceResponse) : DmsInstanceGetResult();

    public record FailureNotFound() : DmsInstanceGetResult();

    public record FailureUnknown(string FailureMessage) : DmsInstanceGetResult();
}

public record DmsInstanceUpdateResult
{
    public record Success() : DmsInstanceUpdateResult();

    public record FailureNotExists() : DmsInstanceUpdateResult();

    public record FailureUnknown(string FailureMessage) : DmsInstanceUpdateResult();
}

public record DmsInstanceDeleteResult
{
    public record Success() : DmsInstanceDeleteResult();

    public record FailureNotExists() : DmsInstanceDeleteResult();

    public record FailureUnknown(string FailureMessage) : DmsInstanceDeleteResult();
}

public record DmsInstanceIdsExistResult
{
    /// <summary>
    /// Successfully retrieved existing DmsInstanceIds
    /// </summary>
    /// <param name="ExistingIds">The set of DmsInstanceIds that exist in the database</param>
    public record Success(HashSet<long> ExistingIds) : DmsInstanceIdsExistResult();

    public record FailureUnknown(string FailureMessage) : DmsInstanceIdsExistResult();
}
