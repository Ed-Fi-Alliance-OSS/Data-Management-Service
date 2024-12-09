// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation;

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ImportClaimSetRequest
{
    public string Name { get; set; } = "";

    public List<ClaimSetResourceClaim> ResourceClaims { get; set; } = [];

    public class Validator : AbstractValidator<ImportClaimSetRequest>
    {
        public Validator()
        {
            RuleFor(m => m.Name).NotEmpty();
            RuleFor(m => m.ResourceClaims).NotEmpty();
        }
    }
}
public class ImportClaimSetCommand
{
    public string Name { get; set; } = "";

    public List<ClaimSetResourceClaim> ResourceClaims { get; set; } = [];

    public class Validator : AbstractValidator<ImportClaimSetRequest>
    {
        public Validator()
        {
            RuleFor(m => m.Name).NotEmpty();
            RuleFor(m => m.ResourceClaims).NotEmpty();
        }
    }
}
public class ImportClaimSetResponse
{
    public string Name { get; set; } = "";

    public List<ClaimSetResourceClaim> ResourceClaims { get; set; } = [];
}
