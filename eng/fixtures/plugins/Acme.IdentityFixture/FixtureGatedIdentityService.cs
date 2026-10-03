// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Options;

namespace Acme.IdentityFixture;

/// <summary>
/// The provider registered when <c>ThrowAt</c> is <c>Constructor</c>: its constructor goes through the
/// activation gate, so the startup probe's activation succeeds and every later one throws.
/// </summary>
public sealed class FixtureGatedIdentityService : FixtureIdentityService
{
    public FixtureGatedIdentityService(
        IOptions<IdentityFixtureOptions> options,
        FixtureRequestScope requestScope,
        FixtureState state,
        FixturePolicySource policy,
        FixtureControlEvents events,
        FixtureActivationGate gate
    )
        : base(options, requestScope, state, policy, events)
    {
        gate.Enter("constructor");
    }
}
