// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

internal sealed class StaticClaimSetProvider(IList<ClaimSet> claimSets) : IClaimSetProvider
{
    public Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null) => Task.FromResult(claimSets);
}
