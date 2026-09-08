// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Management;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.Hooks;

/// <summary>
/// Hydrates every @InstanceFixture scenario's context from the run-scoped suite-owned fixture before any
/// step runs. Runs at a low order so hydration precedes step-level BeforeScenario hooks and the feature
/// Background. Hydration requires no Configuration Service token; scenarios that perform their own CMS
/// operations authenticate later in a Background step.
/// </summary>
[Binding]
public class InstanceFixtureHooks(InstanceManagementContext context)
{
    [BeforeScenario("@InstanceFixture", Order = 10)]
    public void HydrateFixtureState()
    {
        if (!InstanceFixtureState.IsAvailable)
        {
            throw new InvalidOperationException(
                "This scenario is tagged @InstanceFixture but the suite-owned Instance Management E2E fixture "
                    + "environment contract is not present. Run the Instance Management E2E suite through the "
                    + "build orchestration so the pre-registered fixture is available; the suite must never "
                    + "fall back to creating fixtures per scenario."
            );
        }

        // InstanceFixtureState.Current fails fast with a specific message if the contract is malformed.
        InstanceFixtureHydrator.HydrateAll(context, InstanceFixtureState.Current);
    }
}
