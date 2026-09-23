// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

/// <summary>Only explicitly selected, marked CDC examples are inputs. This is not a Markdown executor.</summary>
internal static class CdcRunbookSnippets
{
    internal static string RepositoryRoot => CdcRunbookExamples.RepositoryRoot;
    internal static string Markdown => CdcRunbookExamples.Markdown;

    internal static string Read(string id, string language = "powershell") =>
        CdcRunbookExamples.Read(id, language);

    internal static string Extract(string markdown, string id, string language) =>
        CdcRunbookExamples.Extract(markdown, id, language);

    // The marked CLI examples deliberately use a single command with literal or single-quoted arguments.
    // Reject shell expressions instead of attempting to interpret arbitrary PowerShell.
    internal static string[] Arguments(string code, IReadOnlyDictionary<string, string> inputs)
    {
        var tokens = Regex.Matches(code, @"\G\s*(?:'(?<quoted>[^'\r\n]*)'|(?<literal>[a-zA-Z0-9_./:-]+))");
        tokens
            .Sum(t => t.Length)
            .Should()
            .Be(code.TrimEnd().Length, "only literal CLI arguments are supported");
        var values = tokens
            .Select(t => t.Groups["quoted"].Success ? t.Groups["quoted"].Value : t.Groups["literal"].Value)
            .ToArray();
        values[0].Should().Be("api-schema-tools");
        return values
            .Skip(1)
            .Select(value =>
            {
                if (!value.StartsWith('<'))
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

    internal static Dictionary<string, string> CommandInputs(
        string settings,
        string state,
        string generation = "7",
        string acknowledgement = "acknowledgement.json"
    ) =>
        new()
        {
            ["<retained-settings-path>"] = settings,
            ["<original-state-root>"] = state,
            ["<binding-generation>"] = generation,
            ["<acknowledgement-path>"] = acknowledgement,
        };

    internal static async Task<CdcCommandResult> InvokeAsync(
        string id,
        ICdcCommandRunner runner,
        string settings,
        string state,
        string generation = "7"
    )
    {
        var capture = new ResultCapture(runner);
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = await CdcCommandHost.InvokeAsync(
            Arguments(Read(id), CommandInputs(settings, state, generation)),
            capture,
            output,
            error
        );
        capture.Result.Should().NotBeNull($"{id} must dispatch through the shipped command host: {error}");
        code.Should().Be(capture.Result.ExitCode);
        return capture.Result;
    }

    private sealed class ResultCapture(ICdcCommandRunner runner) : ICdcCommandRunner
    {
        internal CdcCommandResult Result { get; private set; } = null!;

        public async Task<CdcCommandResult> RunAsync(
            CdcCommandInvocation invocation,
            TextWriter progress,
            CancellationToken token
        ) => Result = await runner.RunAsync(invocation, progress, token);
    }
}

/// <summary>Use only from nonparallel fixtures; production Load must not inherit repairing overrides.</summary>
internal sealed class CdcRunbookEnvironment : IDisposable
{
    private readonly Dictionary<string, string> _saved = Environment
        .GetEnvironmentVariables()
        .Cast<DictionaryEntry>()
        .Where(e => ((string)e.Key).StartsWith("DMS_CDC__", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    internal CdcRunbookEnvironment()
    {
        Clear();
    }

    private static void Clear()
    {
        foreach (
            string key in Environment
                .GetEnvironmentVariables()
                .Keys.Cast<string>()
                .Where(k => k.StartsWith("DMS_CDC__", StringComparison.OrdinalIgnoreCase))
        )
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    public void Dispose()
    {
        Clear();
        foreach (var (key, value) in _saved)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
