// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// Clears the named process environment variables for the life of the scope, then puts back the
/// values they held before it. A fixture that configures hosts through these variables then starts
/// every test from the shipped defaults, whatever the shell or CI job set, and leaves the process
/// environment as it found it for the tests that run after it.
/// </summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly Dictionary<string, string?> _originalValues;

    public EnvironmentVariableScope(params string[] names)
    {
        _originalValues = names.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        foreach (string name in names)
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach ((string name, string? value) in _originalValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
