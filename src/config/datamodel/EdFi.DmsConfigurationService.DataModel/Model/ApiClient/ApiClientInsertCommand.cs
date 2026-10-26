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

    [Description(
        "Education organization ids to narrow the API client to. Optional; when sent, it must be a "
            + "non-empty list of ids on the client's Application, and duplicates are removed. A list "
            + "without inheritsApplicationEducationOrganizations narrows the client, even when it "
            + "equals the Application's list. An explicit null is rejected."
    )]
    public long[] EducationOrganizationIds
    {
        get;
        set
        {
            field = value;
            EducationOrganizationIdsSupplied = true;
        }
    } = [];

    [Description(
        "Whether the API client inherits its Application's education organizations and follows "
            + "later changes to them. Optional, with no fixed default: a list sent without it narrows "
            + "the client. true inherits; a list sent with true must equal the Application's list, in "
            + "any order. false narrows the client and requires educationOrganizationIds. With neither "
            + "field, a create inherits and an update keeps the client's flag and list. An explicit "
            + "null is rejected."
    )]
    public bool? InheritsApplicationEducationOrganizations
    {
        get;
        set
        {
            field = value;
            InheritsApplicationEducationOrganizationsSupplied = true;
        }
    }

    /// <summary>
    /// Whether the request body contained <c>educationOrganizationIds</c>, with any value. Set by
    /// the property setter during deserialization; not part of the HTTP request body.
    /// </summary>
    [JsonIgnore]
    public bool EducationOrganizationIdsSupplied { get; private set; }

    /// <summary>
    /// Whether the request body contained <c>inheritsApplicationEducationOrganizations</c>, with
    /// any value. Set by the property setter during deserialization; not part of the HTTP request
    /// body.
    /// </summary>
    [JsonIgnore]
    public bool InheritsApplicationEducationOrganizationsSupplied { get; private set; }

    /// <summary>
    /// The education organization scope to store with the client; null makes it inherit.
    /// Not part of the HTTP request body.
    /// </summary>
    [JsonIgnore]
    public ApiClientEducationOrganizationScope? EducationOrganizationScope { get; set; }

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
            RuleFor(a => a)
                .Custom(
                    (command, context) =>
                    {
                        foreach (
                            ValidationFailure failure in ApiClientEducationOrganizationRequest.Validate(
                                command.EducationOrganizationIdsSupplied,
                                command.EducationOrganizationIds,
                                command.InheritsApplicationEducationOrganizationsSupplied,
                                command.InheritsApplicationEducationOrganizations
                            )
                        )
                        {
                            context.AddFailure(failure);
                        }
                    }
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
