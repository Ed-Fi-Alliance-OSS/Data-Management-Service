// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

public class ApiClientInsertCommand
{
    public required int ApplicationId { get; set; }
    public required string Name { get; set; } = "";
    public required bool IsApproved { get; set; }
    public int[] DataStoreIds { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public class Validator : AbstractValidator<ApiClientInsertCommand>
    {
        public Validator()
        {
            RuleFor(a => a.ApplicationId).NotEmpty().GreaterThan(0);
            RuleFor(a => a.Name).NotEmpty().MaximumLength(50);
            RuleFor(a => a.DataStoreIds)
                .NotEmpty()
                .WithMessage("DataStoreIds cannot be empty. At least one Data Store is required.");
            RuleFor(a => a.AdditionalProperties).Custom(RejectOwnershipFields);
        }

        private static void RejectOwnershipFields(
            Dictionary<string, JsonElement>? additionalProperties,
            ValidationContext<ApiClientInsertCommand> context
        )
        {
            if (additionalProperties is null)
            {
                return;
            }

            if (
                additionalProperties.Keys.Contains(
                    "creatorOwnershipTokenId",
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                context.AddFailure(
                    new ValidationFailure(
                        "CreatorOwnershipTokenId",
                        "Ownership fields are not accepted on API-client create or update requests. Use /v3/apiClients/{id}/ownership."
                    )
                );
            }

            if (additionalProperties.Keys.Contains("ownershipTokenIds", StringComparer.OrdinalIgnoreCase))
            {
                context.AddFailure(
                    new ValidationFailure(
                        "OwnershipTokenIds",
                        "Ownership fields are not accepted on API-client create or update requests. Use /v3/apiClients/{id}/ownership."
                    )
                );
            }
        }
    }
}
