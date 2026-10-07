// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace Acme.IdentityFixture;

/// <summary>
/// Permits the first activation of the identity provider, which is the host's startup probe, and
/// throws on every later one, so a request-time activation failure can be provoked while startup
/// still succeeds. It counts with <see cref="Interlocked.Increment(ref int)"/>, so concurrent
/// activations cannot both be the first.
/// </summary>
public sealed class FixtureActivationGate
{
    private const int PermittedActivations = 1;

    private int _activations;

    /// <summary>Counts one activation and throws when it is past the permitted ones.</summary>
    public void Enter(string stage)
    {
        if (Interlocked.Increment(ref _activations) > PermittedActivations)
        {
            throw FixtureFailures.Nested($"The {stage} activation of the identity provider failed.");
        }
    }
}
