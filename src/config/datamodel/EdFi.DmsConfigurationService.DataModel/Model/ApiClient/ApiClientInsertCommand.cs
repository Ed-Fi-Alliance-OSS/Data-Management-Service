// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;
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

    [Description(
        "Data Store ids to assign to the API client. Optional. An empty array, or omitting the "
            + "property, creates a client with no Data Store assignment: it authenticates and "
            + "receives a token, but reaches no Data Store data. An explicit null is rejected. "
            + "Supplied ids must already exist in the caller's tenant."
    )]
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
                .NotNull()
                .WithMessage(
                    "DataStoreIds cannot be null. Supply an array of Data Store ids, or an empty array for a client with no Data Store assignment."
                );
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
