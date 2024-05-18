// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Core.Backend;

/// <summary>
/// The special query parameters used to drive pagination
/// </summary>
internal record PaginationParameters(
    /// <summary>
    /// The pagination limit
    /// </summary>
    string? limit,

    /// <summary>
    /// The pagination offset
    /// </summary>
    string? offset
) : IPaginationParameters;
