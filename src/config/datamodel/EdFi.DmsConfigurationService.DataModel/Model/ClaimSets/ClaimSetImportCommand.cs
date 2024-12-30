// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

public class ClaimSetImportCommand : IClaimSetCommand
{
    public required string Name { get; set; }
    public required List<ResourceClaim> ResourceClaims { get; set; }

    public class Validator(IClaimSetDataProvider claimSetDataProvider)
        : ClaimSetCommandValidator<ClaimSetImportCommand>(
            claimSetDataProvider,
            isResourceClaimsOptional: false
        );
}
