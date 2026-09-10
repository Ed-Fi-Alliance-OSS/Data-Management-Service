// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.FixtureHost;

/// <summary>
/// A service the fixture host owns and declares no plugin contract for. Removing or overwriting a
/// pre-existing descriptor for it is what the wrapper refuses.
/// </summary>
public interface IFixtureHostService
{
    string Describe();
}

/// <summary>
/// A host-owned service type the host registers no default for, so a plugin's own descriptor for it is
/// the only one on the collection. That is what separates the wrapper's rule, which is about
/// pre-existing descriptors, from the service type itself.
/// </summary>
public interface IFixtureHostUnclaimedService
{
    string Describe();
}

/// <summary>
/// A fixture plugin contract with replace cardinality, declared in a host-prefixed assembly like the
/// real one it stands in for.
/// </summary>
public interface IFixtureReplaceContract
{
    string Describe();
}

/// <summary>
/// A fixture plugin contract with fan-in cardinality, so the activation cases have a contract more
/// than one implementation may legitimately claim.
/// </summary>
public interface IFixtureFanInContract
{
    string Describe();
}
