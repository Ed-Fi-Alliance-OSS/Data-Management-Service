// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

public record DeleteResult
{
    /// <summary>
    /// Successful delete
    /// </summary>
    public record DeleteSuccess(int RecordsDeleted) : DeleteResult();

    /// <summary>
    /// The record to delete did not exist in the database
    /// </summary>
    public record DeleteFailureNotExists() : DeleteResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record UnknownFailure(string FailureMessage) : DeleteResult();
}
