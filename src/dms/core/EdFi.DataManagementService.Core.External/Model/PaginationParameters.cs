// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// The special query parameters used to drive pagination
/// </summary>
public record PaginationParameters(
    /// <summary>
    /// The pagination limit
    /// </summary>
    int? Limit,

    /// <summary>
    /// The pagination offset
    /// </summary>
    int? Offset,

    /// <summary>
    /// The pagination totalCount
    /// </summary>
    bool TotalCount
);
