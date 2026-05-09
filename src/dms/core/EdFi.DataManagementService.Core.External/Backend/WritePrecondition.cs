// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.External.Backend;

/// <summary>
/// Typed write precondition contract passed from Core to backend write flows.
/// </summary>
public abstract record WritePrecondition
{
    /// <summary>
    /// No HTTP write precondition is present on the request.
    /// </summary>
    public sealed record None : WritePrecondition;

    /// <summary>
    /// The request carries an opaque <c>If-Match</c> value that must be compared exactly.
    /// </summary>
    /// <param name="Value">The exact frontend-supplied value after frontend header filtering.</param>
    public sealed record IfMatch(string Value) : WritePrecondition;
}
