// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

public record GetResult<T>
{
    /// <summary>
    /// A successful get selecting multiple entities
    /// </summary>
    /// <param name="Results">The entities returned from the query</param>
    public record GetSuccess(IReadOnlyList<T> Results) : GetResult<T>();

    /// <summary>
    /// A successful get by Id returning one record
    /// </summary>
    /// <param name="Result">The record returned from the query</param>
    public record GetByIdSuccess(T Result) : GetResult<T>();

    /// <summary>
    /// The record does not exist in the database
    /// </summary>
    public record GetByIdFailureNotExists() : GetResult<T>();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record UnknownFailure(string FailureMessage) : GetResult<T>();
}
