// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Model;

/// <summary>
/// Represents collection of authorization filters and filter operator
/// </summary>
public record AuthorizationStrategyEvaluator(
    /// <summary>
    /// Authorization filters to be applied
    /// </summary>
    AuthorizationFilter[] Filters,
    /// <summary>
    /// Defines possible values describing how a filter is combined with other filters
    /// </summary>
    FilterOperator Operator
);
