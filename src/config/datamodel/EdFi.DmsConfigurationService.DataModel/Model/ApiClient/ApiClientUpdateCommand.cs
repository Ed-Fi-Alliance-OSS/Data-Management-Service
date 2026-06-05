// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Serialization;
using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

public class ApiClientUpdateCommand
{
    public required long Id { get; set; }
    public required long ApplicationId { get; set; }
    public required string Name { get; set; } = "";
    public required bool IsApproved { get; set; }
    public long[] DataStoreIds { get; set; } = [];

    /// <summary>
    /// Set server-side after the identity provider issues a new UUID on update.
    /// Not part of the HTTP request body.
    /// </summary>
    [JsonIgnore]
    public Guid? ClientUuid { get; set; }

    public class Validator : AbstractValidator<ApiClientUpdateCommand>
    {
        public Validator()
        {
            RuleFor(a => a.Id).NotEmpty().GreaterThan(0);
            RuleFor(a => a.ApplicationId).NotEmpty().GreaterThan(0);
            RuleFor(a => a.Name).NotEmpty().MaximumLength(50);
            RuleFor(a => a.DataStoreIds)
                .NotEmpty()
                .WithMessage("DataStoreIds cannot be empty. At least one Data Store is required.");
        }
    }
}
