// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class CdcFixtureStartupCleanup
{
    // Only used while preserving an existing startup failure. Ordinary disposal still reports failure.
    internal static async Task DisposeAsync(IAsyncDisposable fixture, TextWriter diagnostics = null!)
    {
        try
        {
            await fixture.DisposeAsync();
        }
        catch (Exception)
        {
            try
            {
                await (diagnostics ?? TestContext.Error).WriteLineAsync(
                    "CDC fixture cleanup failed after startup failure. Details redacted."
                );
            }
            catch (Exception)
            {
                // A diagnostic destination failure must not replace the original startup failure either.
            }
        }
    }
}
