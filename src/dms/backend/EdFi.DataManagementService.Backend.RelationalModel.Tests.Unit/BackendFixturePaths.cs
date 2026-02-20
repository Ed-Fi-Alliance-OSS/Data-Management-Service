// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

internal static class BackendFixturePaths
{
    public static string GetAuthoritativeFixtureRoot(string startDirectory)
    {
        var solutionRoot = GoldenFixtureTestHelpers.FindSolutionRoot(startDirectory);
        return Path.Combine(solutionRoot, "backend", "Fixtures", "authoritative");
    }
}
