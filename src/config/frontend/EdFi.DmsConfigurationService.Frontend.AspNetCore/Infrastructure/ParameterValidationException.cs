// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Signals that one or more query parameters failed validation (e.g. an out-of-range 'limit' or an
/// unknown 'orderBy' field). Deliberately NOT a <see cref="FluentValidation.ValidationException"/> so the
/// global exception handler maps it to the Ed-Fi "Parameter Validation Failed"
/// (urn:ed-fi:api:bad-request:parameter) contract rather than the request-body "data" contract.
/// Carries an immutable collection of already-sanitized messages (no rejected value is ever echoed).
/// </summary>
public sealed class ParameterValidationException : Exception
{
    public ImmutableArray<string> Errors { get; }

    public ParameterValidationException(IEnumerable<string> errors)
        : base("One or more query parameters were invalid.")
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors.ToImmutableArray();
    }
}
