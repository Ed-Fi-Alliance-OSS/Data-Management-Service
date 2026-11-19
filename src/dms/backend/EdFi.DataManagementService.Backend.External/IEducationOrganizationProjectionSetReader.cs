// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Reads the complete education-organization projection set from the request's selected data store
/// inside one consistent transaction.
/// </summary>
public interface IEducationOrganizationProjectionSetReader
{
    /// <summary>
    /// Reads every row of the four core education organization types, in ascending identifier order.
    /// </summary>
    /// <returns>
    /// The set, or a typed reason it is unavailable. Caller cancellation is thrown as
    /// <see cref="OperationCanceledException"/>; any other exception is a defect.
    /// </returns>
    Task<EducationOrganizationProjectionSetResult> ReadSetAsync(
        EducationOrganizationProjectionSetReadRequest request,
        CancellationToken cancellationToken
    );
}
