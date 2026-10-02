// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace Acme.IdentityFixture;

/// <summary>
/// A scoped dependency of the provider, so a test can tell one request's instances from another's
/// and see that the gate and the invocation of one request share them.
/// </summary>
public sealed class FixtureRequestScope
{
    public Guid Id { get; } = Guid.NewGuid();
}
