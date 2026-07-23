// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Guard for query-parameter validators on list endpoints. Mirrors
/// <c>ValidatorExtensions.GuardAsync</c> but throws <see cref="ParameterValidationException"/> (not a
/// FluentValidation <c>ValidationException</c>) so a query-parameter failure is reported with the Ed-Fi
/// "Parameter Validation Failed" (urn:ed-fi:api:bad-request:parameter) contract instead of the request-body
/// "data" contract. This handles the semantically-invalid-but-bound class (e.g. offset=-1, limit=0, an
/// unknown orderBy). The syntactically-unbindable class (e.g. offset=abc) fails minimal-API binding before
/// this runs and is classified by the global exception handler via endpoint <see
/// cref="ParameterValidationMetadata"/>. Errors are surfaced in FluentValidation rule-declaration order,
/// which is deterministic; every paging-validator message is already value-free.
/// </summary>
public static class ParameterQueryValidatorExtensions
{
    public static async Task GuardQueryAsync<TQuery>(this IValidator<TQuery> validator, TQuery? query)
    {
        query ??= Activator.CreateInstance<TQuery>();
        var validationResult = await validator.ValidateAsync(query);

        if (!validationResult.IsValid)
        {
            throw new ParameterValidationException(
                validationResult.Errors.Select(failure => failure.ErrorMessage)
            );
        }
    }
}
