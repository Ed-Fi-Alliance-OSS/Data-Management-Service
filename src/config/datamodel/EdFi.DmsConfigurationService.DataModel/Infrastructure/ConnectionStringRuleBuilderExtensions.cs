// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Infrastructure;

/// <summary>
/// The connection string rules the data store and data store derivative commands share. Insert and
/// update, for both resources, run the same length limit and the same provider-aware check from one
/// place, so the four of them cannot drift apart.
/// </summary>
public static class ConnectionStringRuleBuilderExtensions
{
    public const int MaximumConnectionStringLength = 1000;

    /// <param name="maximumLengthMessage">
    /// Replaces the default length message for a command that already publishes its own wording.
    /// </param>
    public static void ApplyDataStoreConnectionStringRules<T>(
        this IRuleBuilderInitial<T, string?> ruleBuilder,
        IDataStoreConnectionStringValidator connectionStringValidator,
        string? maximumLengthMessage = null
    )
    {
        IRuleBuilderOptions<T, string?> lengthRule = ruleBuilder
            // Stopping at the first failure keeps an over-long value reporting only that it is too
            // long. The check below cannot succeed on a value the length rule already rejected, and
            // a second message would change the response every client sees for that request.
            .Cascade(CascadeMode.Stop)
            .MaximumLength(MaximumConnectionStringLength);

        if (maximumLengthMessage is not null)
        {
            lengthRule.WithMessage(maximumLengthMessage);
        }

        lengthRule.Custom(
            (connectionString, context) =>
            {
                if (
                    connectionStringValidator.Validate(connectionString)
                    is ConnectionStringValidationResult.Invalid invalid
                )
                {
                    context.AddFailure(invalid.ErrorMessage);
                }
            }
        );
    }
}
