// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DmsInstanceRouteContext;

namespace EdFi.DmsConfigurationService.Backend.Repositories;

public interface IDmsInstanceRouteContextRepository
{
    Task<DmsInstanceRouteContextInsertResult> InsertDmsInstanceRouteContext(
        DmsInstanceRouteContextInsertCommand command
    );
    Task<DmsInstanceRouteContextQueryResult> QueryInstanceRouteContext(PagingQuery query);
    Task<DmsInstanceRouteContextGetResult> GetInstanceRouteContext(long id);
    Task<DmsInstanceRouteContextUpdateResult> UpdateDmsInstanceRouteContext(
        DmsInstanceRouteContextUpdateCommand command
    );
    Task<InstanceRouteContextDeleteResult> DeleteInstanceRouteContext(long id);
    Task<InstanceRouteContextQueryByInstanceResult> GetInstanceRouteContextsByInstance(long instanceId);
    Task<InstanceRouteContextQueryByInstanceIdsResult> GetInstanceRouteContextsByInstanceIds(
        List<long> instanceIds
    );
}

public record DmsInstanceRouteContextInsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record Success(long Id) : DmsInstanceRouteContextInsertResult();

    /// <summary>
    /// Referenced instance not found exception thrown and caught
    /// </summary>
    public record FailureInstanceNotFound() : DmsInstanceRouteContextInsertResult();

    /// <summary>
    /// Instance route context already exists for the given InstanceId and ContextKey
    /// </summary>
    public record FailureDuplicateDmsInstanceRouteContext(long InstanceId, string ContextKey)
        : DmsInstanceRouteContextInsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DmsInstanceRouteContextInsertResult();
}

public record DmsInstanceRouteContextQueryResult
{
    /// <summary>
    /// A successful query result with responses
    /// </summary>
    public record Success(IEnumerable<DmsInstanceRouteContextResponse> DmsInstanceRouteContextResponses)
        : DmsInstanceRouteContextQueryResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DmsInstanceRouteContextQueryResult();
}

public record DmsInstanceRouteContextGetResult
{
    /// <summary>
    /// Successful get instance route context with the response
    /// </summary>
    /// <param name="DmsInstanceRouteContextResponse"></param>
    public record Success(DmsInstanceRouteContextResponse DmsInstanceRouteContextResponse)
        : DmsInstanceRouteContextGetResult();

    /// <summary>
    /// Instance route context not found in data store
    /// </summary>
    public record FailureNotFound() : DmsInstanceRouteContextGetResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DmsInstanceRouteContextGetResult();
}

public record DmsInstanceRouteContextUpdateResult
{
    /// <summary>
    /// The instance route context was updated successfully
    /// </summary>
    public record Success() : DmsInstanceRouteContextUpdateResult();

    /// <summary>
    /// Instance route context id not found
    /// </summary>
    public record FailureNotExists() : DmsInstanceRouteContextUpdateResult();

    /// <summary>
    /// Referenced instance not found exception thrown and caught
    /// </summary>
    public record FailureInstanceNotFound() : DmsInstanceRouteContextUpdateResult();

    /// <summary>
    /// Instance route context already exists for the given InstanceId and ContextKey
    /// </summary>
    public record FailureDuplicateDmsInstanceRouteContext(long InstanceId, string ContextKey)
        : DmsInstanceRouteContextUpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : DmsInstanceRouteContextUpdateResult();
}

public record InstanceRouteContextDeleteResult
{
    /// <summary>
    /// The instance route context was deleted successfully
    /// </summary>
    public record Success() : InstanceRouteContextDeleteResult();

    /// <summary>
    /// Instance route context id does not exist in the datastore
    /// </summary>
    public record FailureNotExists() : InstanceRouteContextDeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : InstanceRouteContextDeleteResult();
}

public record InstanceRouteContextQueryByInstanceResult
{
    /// <summary>
    /// Successful retrieval of instance route contexts for a specific instance
    /// </summary>
    public record Success(IEnumerable<DmsInstanceRouteContextResponse> DmsInstanceRouteContextResponses)
        : InstanceRouteContextQueryByInstanceResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : InstanceRouteContextQueryByInstanceResult();
}

public record InstanceRouteContextQueryByInstanceIdsResult
{
    /// <summary>
    /// Successful retrieval of instance route contexts for multiple instances
    /// </summary>
    public record Success(IEnumerable<DmsInstanceRouteContextResponse> DmsInstanceRouteContextResponses)
        : InstanceRouteContextQueryByInstanceIdsResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record FailureUnknown(string FailureMessage) : InstanceRouteContextQueryByInstanceIdsResult();
}
