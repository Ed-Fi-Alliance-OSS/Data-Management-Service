// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

internal static class CdcRunbookArguments
{
    // Marked examples use one command, literal/quoted arguments, and explicitly bound named variables.
    // Reject shell expressions instead of attempting to interpret arbitrary PowerShell.
    internal static string[] Parse(
        string code,
        IReadOnlyDictionary<string, string> inputs,
        string executable = "api-schema-tools"
    )
    {
        code = Regex.Replace(code, @"`\r?\n", " ");
        var tokens = Regex.Matches(
            code,
            @"\G\s*(?:'(?<quoted>[^'\r\n]*)'|(?<literal>\$[a-zA-Z][a-zA-Z0-9]*|[a-zA-Z0-9_./:-]+))(?=\s|$)"
        );
        tokens
            .Sum(t => t.Length)
            .Should()
            .Be(code.TrimEnd().Length, "only literal CLI arguments are supported");
        var values = tokens
            .Select(t => t.Groups["quoted"].Success ? t.Groups["quoted"].Value : t.Groups["literal"].Value)
            .ToArray();
        values[0].Should().Be(executable);
        return values
            .Skip(1)
            .Select(value =>
            {
                if (!value.StartsWith('<') && !value.StartsWith('$'))
                {
                    return value;
                }
                inputs
                    .ContainsKey(value)
                    .Should()
                    .BeTrue($"fixture input {value} must be explicitly declared");
                return inputs[value];
            })
            .ToArray();
    }
}
