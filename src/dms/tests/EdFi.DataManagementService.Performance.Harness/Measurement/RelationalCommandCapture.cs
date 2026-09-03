// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Converts a recorded relational-channel command (a partition boundary selection or a
/// descriptor read) into the same capture shape the hydration-keyset channel produces, so
/// shape gates and plan replay treat both channels identically. Parameter names are
/// normalized to their bare form: the recorded name is how the code constructed it, and the
/// SQL text references it with an '@' prefix.
/// </summary>
public static class RelationalCommandCapture
{
    public static PageSelectionQueryCapture ToPageSelectionCapture(RelationalCommand command) =>
        new(
            command.CommandText,
            ParameterValues(command),
            PageSelectionCapture.Sha256Lowercase(command.CommandText)
        );

    public static IReadOnlyDictionary<string, object?> ParameterValues(RelationalCommand command)
    {
        Dictionary<string, object?> values = [];
        foreach (RelationalParameter parameter in command.Parameters)
        {
            values[parameter.Name.TrimStart('@')] = parameter.Value;
        }

        return values;
    }
}
