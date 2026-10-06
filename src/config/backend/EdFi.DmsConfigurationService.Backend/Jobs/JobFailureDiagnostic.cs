// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// What the job subsystem records about a failure (spec D-16a): the exception type chain, a vetted
/// provider error code when one exists, and the operation that failed. It never carries an exception
/// message, a stack trace, or any value read from a request, payload, or row.
/// </summary>
/// <param name="ExceptionTypeChain">The <c>/</c>-joined full type names of the exception and its inner
/// exceptions, from <see cref="JobDiagnostics.TypeChain(Exception)"/>.</param>
/// <param name="ProviderErrorCode">A provider error code (PostgreSQL <c>SqlState</c>, SQL Server error
/// number) extracted inside the provider project, or null.</param>
/// <param name="Operation">A fixed operation name chosen by the caller, such as <c>ClaimNext</c>.</param>
public sealed record JobFailureDiagnostic(
    string ExceptionTypeChain,
    string? ProviderErrorCode,
    string Operation
);
