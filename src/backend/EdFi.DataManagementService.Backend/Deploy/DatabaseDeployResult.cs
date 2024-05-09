// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Deploy;

/// <summary>
/// A database deploy result
/// </summary>
public record DatabaseDeployResult
{
    /// <summary>
    /// A successful database deploy result
    /// </summary>
    public record DatabaseDeploySuccess() : DatabaseDeployResult;

    /// <summary>
    /// A failed database deploy result
    /// </summary>
    /// <param name="Error">The exception caught during database deployment</param>
    public record DatabaseDeployFailure(Exception Error) : DatabaseDeployResult;
}
