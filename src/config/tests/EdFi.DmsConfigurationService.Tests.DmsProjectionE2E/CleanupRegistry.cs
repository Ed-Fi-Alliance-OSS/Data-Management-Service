// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Tests.DmsProjectionE2E;

/// <summary>
/// Everything the run created in the Configuration Service or DMS, each registered the moment its id is known, before
/// any further request or check, so a setup that fails part-way still removes what it made.
/// </summary>
public sealed class CleanupRegistry
{
    private readonly Lock _gate = new();
    private readonly List<(string Description, Func<Task> Step)> _steps = [];

    public IReadOnlyList<string> Descriptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps.Select(step => step.Description)];
            }
        }
    }

    public void Add(string description, Func<Task> step)
    {
        lock (_gate)
        {
            _steps.Add((description, step));
        }
    }

    /// <summary>
    /// Runs every step, newest first, and returns the failures. A failed step never stops the others, and none of
    /// them replaces a failure the run already reported.
    /// </summary>
    public async Task<IReadOnlyList<string>> RunAsync()
    {
        (string Description, Func<Task> Step)[] steps;
        lock (_gate)
        {
            steps = [.. Enumerable.Reverse(_steps)];
            _steps.Clear();
        }

        List<string> failures = [];
        foreach ((string description, Func<Task> step) in steps)
        {
            try
            {
                await step();
            }
            catch (Exception exception)
            {
                failures.Add($"{description}: {exception.Message}");
            }
        }

        return failures;
    }
}
