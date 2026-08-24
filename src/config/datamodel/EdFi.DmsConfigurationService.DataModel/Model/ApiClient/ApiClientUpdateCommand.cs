// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

public class ApiClientUpdateCommand
{
    public int Id { get; set; }
    public required int ApplicationId { get; set; }
    public required string Name { get; set; } = "";
    public required bool IsApproved { get; set; }
    public int[] DataStoreIds { get; set; } = [];

    /// <summary>
    /// Set server-side after the identity provider issues a new UUID on update.
    /// Not part of the HTTP request body.
    /// </summary>
    [JsonIgnore]
    public Guid? ClientUuid { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public class Validator : AbstractValidator<ApiClientUpdateCommand>
    {
        public Validator()
        {
            RuleFor(a => a.Id).GreaterThan(0).WithMessage("Id must be greater than 0.");
            RuleFor(a => a.ApplicationId).NotEmpty().GreaterThan(0);
            RuleFor(a => a.Name).NotEmpty().MaximumLength(50);
            RuleFor(a => a.DataStoreIds)
                .NotEmpty()
                .WithMessage("DataStoreIds cannot be empty. At least one Data Store is required.");
            RuleFor(a => a.AdditionalProperties).Custom(RejectOwnershipFields);
        }

        private static void RejectOwnershipFields(
            Dictionary<string, JsonElement>? additionalProperties,
            ValidationContext<ApiClientUpdateCommand> context
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
