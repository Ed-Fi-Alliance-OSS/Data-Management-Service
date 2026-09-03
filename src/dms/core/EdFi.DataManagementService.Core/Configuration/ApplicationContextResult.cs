// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Configuration;

/// <summary>
/// Represents the outcome of resolving an application context.
/// </summary>
public abstract record ApplicationContextResult
{
    private ApplicationContextResult() { }

    /// <summary>
    /// An application context was resolved successfully.
    /// </summary>
    public sealed record Success(ApplicationContext ApplicationContext) : ApplicationContextResult;

    /// <summary>
    /// No application context exists for the requested client.
    /// </summary>
    public sealed record NotFound : ApplicationContextResult;

    /// <summary>
    /// The application context could not be resolved.
    /// </summary>
    public sealed record Unavailable : ApplicationContextResult;
}
