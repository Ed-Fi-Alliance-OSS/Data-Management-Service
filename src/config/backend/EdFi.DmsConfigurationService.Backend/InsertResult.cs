// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend;

public record InsertResult
{
    /// <summary>
    /// Successful insert.
    /// </summary>
    /// <param name="Id">The Id of the inserted record.</param>
    public record InsertSuccess(long Id) : InsertResult();

    /// <summary>
    /// Unexpected exception thrown and caught
    /// </summary>
    public record UnknownFailure(string FailureMessage) : InsertResult();
}
