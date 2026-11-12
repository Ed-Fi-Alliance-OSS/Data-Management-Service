// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;

/// <summary>
/// A lookup command builder for fixtures that compose services or inject their own first phase, and so never
/// build a lookup. Building one is a fixture defect.
/// </summary>
internal sealed class ThrowingNaturalKeyLookupCommandBuilder : INaturalKeyLookupCommandBuilder
{
    public RelationalCommand BuildCommand(NaturalKeyLookupRequest request) =>
        throw new InvalidOperationException("This fixture never builds a natural-key lookup command.");

    public RelationalCommand BuildEmbeddableCommand(NaturalKeyLookupRequest request) =>
        throw new InvalidOperationException("This fixture never builds a natural-key lookup command.");
}
