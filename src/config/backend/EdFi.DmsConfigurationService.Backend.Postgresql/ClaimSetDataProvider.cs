// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;

namespace EdFi.DmsConfigurationService.Backend.Postgresql;

public class ClaimSetDataProvider(IClaimSetRepository repository) : IClaimSetDataProvider
{
    public List<string> GetActions()
    {
        return repository.GetActions().Select(a => a.Name).ToList();
    }

    public List<string> GetAuthorizationStrategies()
    {
        return repository.GetAuthorizationStrategies().Select(a => a.AuthStrategyName).ToList();
    }
}
