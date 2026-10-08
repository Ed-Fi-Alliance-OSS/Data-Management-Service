// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

/// <summary>One invocation, one admitted binding, no Reqnroll reset hooks or ordered NUnit tests.</summary>
[TestFixture]
[Explicit("Requires the CDC ApiE2E qualification runner and its private handoff.")]
[NonParallelizable]
[Category("CdcApiE2E")]
public sealed class Given_CdcApiE2E
{
    [Test]
    public async Task It_completes_all_eight_scenarios_on_the_admitted_binding() =>
        await Assert.ThatAsync(
            () =>
                new CdcScenarioRunner().RunAsync(
                    Environment.GetEnvironmentVariable("CDC_API_E2E_REPORT_PATH") ?? "",
                    Environment.GetEnvironmentVariable("CDC_API_E2E_INVOCATION_ID") ?? "",
                    new CdcApiScenarios(),
                    TestContext.CurrentContext.CancellationToken
                ),
            Throws.Nothing
        );
}
