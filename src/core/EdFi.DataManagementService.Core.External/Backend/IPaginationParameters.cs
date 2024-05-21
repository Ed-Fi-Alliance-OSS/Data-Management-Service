// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// The special query parameters used to drive pagination
/// </summary>
public interface IPaginationParameters
{
    /// <summary>
    /// The pagination limit
    /// </summary>
    string? limit { get; }

    /// <summary>
    /// The pagination offset
    /// </summary>
    string? offset { get; }
}
