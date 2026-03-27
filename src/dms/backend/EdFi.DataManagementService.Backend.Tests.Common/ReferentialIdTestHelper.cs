// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Be.Vlaanderen.Basisregisters.Generators.Guid;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Test helper for computing expected ReferentialId values, matching the
/// string format used by ReferentialIdCalculator in production code.
/// </summary>
public static class ReferentialIdTestHelper
{
    private static readonly Guid EdFiUuidv5Namespace = new("edf1edf1-3df1-3df1-3df1-3df1edf1edf1");

    /// <summary>
    /// Compute expected ReferentialId matching the trigger's hash format.
    /// </summary>
    public static Guid ComputeReferentialId(
        string projectName,
        string resourceName,
        params (string jsonPath, string value)[] identityElements
    )
    {
        var identityString = string.Join(
            "#",
            identityElements.Select(e => $"${e.jsonPath}={e.value}")
        );
        return Deterministic.Create(EdFiUuidv5Namespace, $"{projectName}{resourceName}{identityString}");
    }
}
