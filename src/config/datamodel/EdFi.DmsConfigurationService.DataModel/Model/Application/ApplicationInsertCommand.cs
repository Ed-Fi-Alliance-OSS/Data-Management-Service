// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using FluentValidation;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.DataModel.Model.Application;

public class ApplicationInsertCommand
{
    public string ApplicationName { get; set; } = "";
    public int VendorId { get; set; }
    public string ClaimSetName { get; set; } = "";
    public long[] EducationOrganizationIds { get; set; } = [];

    public int[] DataStoreIds
    {
        get;
        set
        {
            field = value;
            DataStoreIdsSupplied = true;
        }
    } = [];

    public int[] ProfileIds { get; set; } = [];

    [Description(
        "Whether to create the Application's default API client. Optional, default true. When "
            + "false, no API client is created and the response's key and secret are null. The "
            + "request must then omit enabled and dataStoreIds: either one present is rejected "
            + "with 400 whatever its value, including enabled: false and dataStoreIds: []. "
            + "Create API clients afterward with POST /v3/apiClients."
    )]
    [DefaultValue(true)]
    public bool CreateDefaultCredentials { get; set; } = true;

    /// <summary>
    /// Whether the request body contained <c>dataStoreIds</c>, with any value. Set by the
    /// property setter during deserialization; not part of the HTTP request body.
    /// </summary>
    [JsonIgnore]
    public bool DataStoreIdsSupplied { get; private set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; set; } = [];

    public class Validator : AbstractValidator<ApplicationInsertCommand>
    {
        public Validator()
        {
            RuleFor(a => a.ApplicationName).NotEmpty().MaximumLength(256);
            RuleFor(a => a.ClaimSetName).NotEmpty().MaximumLength(256);
            RuleFor(m => m.ClaimSetName)
                .Matches(new Regex(ValidationConstants.ClaimSetNameNoWhiteSpaceRegex))
                .When(m => !string.IsNullOrEmpty(m.ClaimSetName))
                .WithMessage(ValidationConstants.ClaimSetNameNoWhiteSpaceMessage);
            RuleForEach(a => a.EducationOrganizationIds).GreaterThan(0);
            RuleForEach(a => a.DataStoreIds).GreaterThan(0);
            RuleForEach(a => a.ProfileIds).GreaterThan(0);
            RuleFor(a => a).Custom(RejectDefaultClientFieldsWithoutDefaultCredentials);
        }

        /// <summary>
        /// <c>enabled</c> and <c>dataStoreIds</c> describe the default API client, so they are
        /// rejected whenever no default client is created, whatever value they carry.
        /// </summary>
        private static void RejectDefaultClientFieldsWithoutDefaultCredentials(
            ApplicationInsertCommand command,
            ValidationContext<ApplicationInsertCommand> context
        )
        {
            if (command.CreateDefaultCredentials)
            {
                return;
            }

            if (command.AdditionalProperties.Keys.Contains("enabled", StringComparer.OrdinalIgnoreCase))
            {
                context.AddFailure(
                    new ValidationFailure(
                        "Enabled",
                        "Enabled is not accepted when CreateDefaultCredentials is false, because no API client is created."
                    )
                );
            }

            if (command.DataStoreIdsSupplied)
            {
                context.AddFailure(
                    new ValidationFailure(
                        "DataStoreIds",
                        "DataStoreIds is not accepted when CreateDefaultCredentials is false, because no API client is created."
                    )
                );
            }
        }
    }
}
