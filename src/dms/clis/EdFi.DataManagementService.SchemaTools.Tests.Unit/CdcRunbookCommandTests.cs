// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.RegularExpressions;
using EdFi.DataManagementService.SchemaTools.Cdc;
using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class Given_Cdc_runbook_commands
{
    private ICdcCommandRunner _runner = null!;
    private CdcCommandInvocation _invocation = null!;
    private StringWriter _output = null!;
    private StringWriter _error = null!;

    [SetUp]
    public void Setup()
    {
        _invocation = null!;
        _output = new();
        _error = new();
        _runner = A.Fake<ICdcCommandRunner>();
        A.CallTo(() => _runner.RunAsync(A<CdcCommandInvocation>._, A<TextWriter>._, A<CancellationToken>._))
            .ReturnsLazily(
                (CdcCommandInvocation invocation, TextWriter _, CancellationToken _) =>
                {
                    _invocation = invocation;
                    return Task.FromResult(
                        new CdcCommandResult(CdcCommandHost.Name(invocation.Operation), true, 0, [])
                    );
                }
            );
    }

    [TearDown]
    public void Cleanup()
    {
        _output.Dispose();
        _error.Dispose();
    }

    [TestCase("cdc-pg-status", CdcCommandOperation.Status, 1)]
    [TestCase("cdc-pg-watch", CdcCommandOperation.Watch, 20)]
    [TestCase("cdc-sqlserver-status", CdcCommandOperation.Status, 1)]
    [TestCase("cdc-sqlserver-watch", CdcCommandOperation.Watch, 20)]
    [TestCase("cdc-enable-retry", CdcCommandOperation.Enable, 1)]
    [TestCase("cdc-validate", CdcCommandOperation.Validate, 1)]
    [TestCase("cdc-provenance-rejection", CdcCommandOperation.Validate, 1)]
    [TestCase("cdc-intact-restart", CdcCommandOperation.Restart, 1)]
    [TestCase("cdc-intact-resume", CdcCommandOperation.Resume, 1)]
    [TestCase("cdc-incomplete-shutdown-status", CdcCommandOperation.Status, 1)]
    [TestCase("cdc-native-recovery-watch", CdcCommandOperation.Watch, 3)]
    [TestCase("cdc-status", CdcCommandOperation.Status, 1)]
    [TestCase("cdc-watch", CdcCommandOperation.Watch, 3)]
    [TestCase("cdc-topic-policy-inspect", CdcCommandOperation.Status, 1)]
    [TestCase("cdc-size-increase", CdcCommandOperation.IncreaseRecordSize, 1)]
    [TestCase("cdc-size-retry", CdcCommandOperation.IncreaseRecordSize, 1)]
    [TestCase("cdc-retire", CdcCommandOperation.Retire, 1)]
    [TestCase("cdc-restamp-handoff-status", CdcCommandOperation.Status, 1)]
    [TestCase("cdc-disclosure-containment-result", CdcCommandOperation.Stop, 1)]
    public async Task It_dispatches_the_marked_command_and_exposes_its_options_in_help(
        string id,
        CdcCommandOperation operation,
        int passes
    )
    {
        var args = CdcRunbookSnippets.Arguments(
            CdcRunbookSnippets.Read(id),
            CdcRunbookSnippets.CommandInputs("settings with spaces.json", "/original state")
        );
        (await CdcCommandHost.InvokeAsync(args, _runner, _output, _error)).Should().Be(0, id);
        _invocation
            .Should()
            .Be(
                new CdcCommandInvocation(
                    operation,
                    "settings with spaces.json",
                    "/original state",
                    passes,
                    operation == CdcCommandOperation.Retire ? 7 : 0,
                    operation == CdcCommandOperation.Retire,
                    operation == CdcCommandOperation.IncreaseRecordSize ? "acknowledgement.json" : "",
                    operation == CdcCommandOperation.IncreaseRecordSize
                )
            );
        using var help = new StringWriter();
        (await CdcCommandHost.InvokeAsync([args[0], args[1], "--help"], _runner, help, _error))
            .Should()
            .Be(0);
        foreach (string option in args.Where(a => a.StartsWith("--", StringComparison.Ordinal)))
        {
            help.ToString().Should().Contain(option, $"{id} must use a shipped option");
        }
    }

    [TestCase("cdc-status", "--state-path '<original-state-root>'", "--state-paht '<original-state-root>'")]
    [TestCase("cdc-status", "--settings '<retained-settings-path>'", "")]
    [TestCase("cdc-watch", "--maximum-passes 3", "--maximum-passes 10001")]
    [TestCase("cdc-retire", "--destructive-cleanup", "")]
    [TestCase("cdc-retire", "'<binding-generation>'", "0")]
    [TestCase("cdc-size-increase", "--confirm-consumer-capacity", "")]
    [TestCase("cdc-size-retry", "--acknowledgement '<acknowledgement-path>'", "")]
    [TestCase("cdc-validate", "cdc validate", "cdc adopt")]
    [TestCase("cdc-validate", "cdc validate", "cdc replace-source")]
    public async Task It_rejects_a_broken_copy_before_dispatch(string id, string original, string mutation)
    {
        string copy = CdcRunbookSnippets.Read(id);
        copy.Should().Contain(original);
        var args = CdcRunbookSnippets.Arguments(
            copy.Replace(original, mutation, StringComparison.Ordinal),
            CdcRunbookSnippets.CommandInputs("settings.json", "/state")
        );
        (await CdcCommandHost.InvokeAsync(args, _runner, _output, _error)).Should().Be(2);
        _invocation.Should().BeNull();
    }

    [Test]
    public void It_binds_declared_history_variables_without_interpreting_their_values()
    {
        CdcRunbookArguments
            .Parse(
                "dms-document-cache $HistoryCommand `\n --tenant-key $HistoryTenant --settings $HistorySettings",
                new Dictionary<string, string>
                {
                    ["$HistoryCommand"] = "activate-offline",
                    ["$HistoryTenant"] = "",
                    ["$HistorySettings"] = "/private settings/$(literal).json",
                },
                "dms-document-cache"
            )
            .Should()
            .Equal("activate-offline", "--tenant-key", "", "--settings", "/private settings/$(literal).json");
    }

    [TestCase("dms-document-cache $Undeclared")]
    [TestCase("dms-document-cache $(Get-Content private)")]
    [TestCase("dms-document-cache status; exit")]
    [TestCase("dms-document-cache status | command")]
    [TestCase("dms-document-cache $HistoryCommand.Length")]
    public void It_rejects_undeclared_or_executable_history_expressions(string code)
    {
        Action act = () =>
            CdcRunbookArguments.Parse(
                code,
                new Dictionary<string, string> { ["$HistoryCommand"] = "status" },
                "dms-document-cache"
            );
        act.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_requires_unique_paired_ids_across_the_operator_reference_set()
    {
        string documents = string.Join(
            '\n',
            Directory
                .GetFiles(
                    Path.Combine(CdcRunbookSnippets.RepositoryRoot, "reference/cdc-documentation"),
                    "*.md"
                )
                .Select(File.ReadAllText)
        );
        var starts = Regex
            .Matches(documents, @"<!-- cdc-snippet: ([\w-]+) -->")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        var ends = Regex
            .Matches(documents, @"<!-- /cdc-snippet: ([\w-]+) -->")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        starts.Should().OnlyHaveUniqueItems();
        ends.Should().BeEquivalentTo(starts);
    }

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("missing-end")]
    public void It_rejects_missing_or_duplicate_selected_markers(string mutation)
    {
        string copy = CdcRunbookSnippets.Markdown;
        const string marker = "<!-- cdc-snippet: cdc-status -->";
        copy = mutation switch
        {
            "missing" => copy.Replace(marker, ""),
            "duplicate" => copy + marker,
            _ => copy.Replace("<!-- /cdc-snippet: cdc-status -->", ""),
        };
        Action act = () => CdcRunbookSnippets.Extract(copy, "cdc-status", "powershell");
        act.Should().Throw<AssertionException>();
    }
}
