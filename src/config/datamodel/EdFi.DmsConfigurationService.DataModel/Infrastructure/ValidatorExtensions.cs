// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

public static class ValidatorExtensions
{
    public static async Task GuardAsync<TRequest>(this IValidator<TRequest> validator, TRequest request)
    {
        request ??= Activator.CreateInstance<TRequest>();
        var validationResult = await validator.ValidateAsync(request);

        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }
    }

    public static async Task GuardAsync<TRequest>(
        this IValidator<TRequest> validator,
        ValidationContext<TRequest>? validationContext
    )
    {
        validationContext ??= Activator.CreateInstance<ValidationContext<TRequest>>();

        var validationResult = await validator.ValidateAsync(validationContext);

        if (!validationResult.IsValid)
        {
            throw new ValidationException(validationResult.Errors);
        }
    }
}
