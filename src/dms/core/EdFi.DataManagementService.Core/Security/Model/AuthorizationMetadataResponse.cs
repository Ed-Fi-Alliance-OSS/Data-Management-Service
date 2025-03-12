// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Security.Model;

public record AuthorizationMetadataResponse(
    List<AuthorizationMetadataResponse.Claim> Claims,
    List<AuthorizationMetadataResponse.Authorization> Authorizations
)
{
    public record Claim(string Name, int AuthorizationId);

    public record Authorization(int Id, Action[] Actions);

    public record Action(string Name, AuthorizationStrategy[] AuthorizationStrategies);
}
