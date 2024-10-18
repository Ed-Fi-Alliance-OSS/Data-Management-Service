// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

public record UpdateResult
{
    /// <summary>
    /// A successful update
    /// </summary>
    /// <param name="RecordsUpdated">The number of top level entities updated by the transaction.</param>
    public record UpdateSuccess(int RecordsUpdated) : UpdateResult();

    /// <summary>
    /// The record to update did not exist in the database
    /// </summary>
    public record UpdateFailureNotExists() : UpdateResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record UnknownFailure(string FailureMessage) : UpdateResult();

    /// <summary>
    /// Reference not found exception thrown and caught
    /// </summary>
    public record FailureReferenceNotFound(string ReferenceName) : UpdateResult();
}
