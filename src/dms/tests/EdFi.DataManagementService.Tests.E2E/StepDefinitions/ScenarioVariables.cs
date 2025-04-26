// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Tests.E2E.StepDefinitions
{
    /// <summary>
    /// Provides means of storing string variables in a Test Scenario.
    /// </summary>
    class ScenarioVariables
    {
        private readonly Dictionary<string, string> _variableByName = [];

        public IReadOnlyDictionary<string, string> VariableByName => _variableByName;

        public void Add(string variableName, string value)
        {
            if (!_variableByName.TryAdd(variableName, value))
            {
                throw new InvalidOperationException(
                    $"The variable '{variableName}' is already defined. If you're attempting to change its value, create a different variable instead."
                );
            }
        }

        public string GetValueByName(string variableName)
        {
            if (!_variableByName.TryGetValue(variableName, out string? value))
            {
                throw new InvalidOperationException(
                    $"Attempted to use the non-existent variable '{variableName}'."
                );
            }

            return value;
        }
    }
}
