// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// Runs an act inside a fixture's <c>Setup</c> and keeps what it threw, so the <c>It_</c> tests can assert on
/// both the exception and the file system state the act left behind.
/// </summary>
internal static class Record
{
    public static Exception? Exception(Action act)
    {
        try
        {
            act();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    public static async Task<Exception?> ExceptionAsync(Func<Task> act)
    {
        try
        {
            await act();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
