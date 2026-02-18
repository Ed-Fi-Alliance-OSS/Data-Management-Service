// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// Supported value-comparison operators in compiled query predicates.
/// </summary>
public enum QueryComparisonOperator
{
    /// <summary>
    /// Equality comparison (<c>=</c>).
    /// </summary>
    Equal,

    /// <summary>
    /// Inequality comparison (<c>&lt;&gt;</c>).
    /// </summary>
    NotEqual,

    /// <summary>
    /// Strict less-than comparison (<c>&lt;</c>).
    /// </summary>
    LessThan,

    /// <summary>
    /// Less-than-or-equal comparison (<c>&lt;=</c>).
    /// </summary>
    LessThanOrEqual,

    /// <summary>
    /// Strict greater-than comparison (<c>&gt;</c>).
    /// </summary>
    GreaterThan,

    /// <summary>
    /// Greater-than-or-equal comparison (<c>&gt;=</c>).
    /// </summary>
    GreaterThanOrEqual,

    /// <summary>
    /// Pattern-match comparison (<c>LIKE</c>).
    /// </summary>
    Like,

    /// <summary>
    /// Set-membership comparison (<c>IN</c>).
    /// </summary>
    In,
}
