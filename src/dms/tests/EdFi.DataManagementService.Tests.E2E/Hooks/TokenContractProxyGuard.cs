// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Reqnroll;

namespace EdFi.DataManagementService.Tests.E2E.Hooks;

[Binding]
public static class TokenContractProxyGuard
{
    [BeforeScenario(Order = 1)]
    public static void RequireDedicatedTokenProxy(ScenarioContext scenarioContext)
    {
        if (
            scenarioContext.ScenarioInfo.Tags.Contains("TokenContractProxy", StringComparer.Ordinal)
            && Environment.GetEnvironmentVariable("DMS_TOKEN_CONTRACT_PROXY_ACTIVE") != "1"
        )
        {
            throw new InvalidOperationException(
                "Token contract proxy scenarios require the dedicated launcher: "
                    + "pwsh ./eng/docker-compose/tests/oauth-token-contract/Invoke-TokenContractE2E.ps1."
            );
        }
    }
}
