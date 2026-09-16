// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcSqlServerStartupLogEvidence
{
    [Test]
    public void It_retains_only_fixed_markers_and_numeric_error_codes_from_both_streams()
    {
        var evidence = CdcSqlServerStartupLogClassifier.Parse(
            new(
                0,
                "private-host: Error: 701, Severity: 17, State: 1.\n"
                    + "This program has encountered a fatal error\nReason: 0x00000001\n"
                    + "Signal: SIGABRT - private-details\nLast errno: 12\n",
                "password=private-secret; unable to allocate memory; /private/path"
            )
        );
        evidence.State.Should().Be("Observed");
        evidence.FatalMessage.Should().BeTrue();
        evidence.MemoryMessage.Should().BeTrue();
        evidence.MappingMessage.Should().BeFalse();
        evidence.SqlErrorNumbers.Should().Equal(701);
        evidence.FatalReasonCodes.Should().Equal(1U);
        evidence.LastErrnos.Should().Equal(12);
        evidence.Signals.Should().Equal("SIGABRT");
        JsonSerializer.Serialize(evidence).Should().NotContain("private").And.NotContain("password");
    }

    [TestCase("requires a minimum of 2000 megabytes", true, false)]
    [TestCase("Invalid mapping of address private-address", false, true)]
    [TestCase("private-unrecognized-startup-failure", false, false)]
    public void It_reports_marker_presence_without_assigning_an_exit_cause(
        string text,
        bool memory,
        bool mapping
    )
    {
        var evidence = CdcSqlServerStartupLogClassifier.Parse(new(0, text, string.Empty));
        evidence.MemoryMessage.Should().Be(memory);
        evidence.MappingMessage.Should().Be(mapping);
        evidence.SqlErrorNumbers.Should().BeEmpty();
        JsonSerializer.Serialize(evidence).Should().NotContain("private");
    }

    [Test]
    public void It_deduplicates_and_bounds_numeric_codes()
    {
        string text = string.Concat(
            Enumerable
                .Range(1, 20)
                .Select(n => $"Error: {n}, Severity: 16, State: 1.\nError: {n}, Severity: 16, State: 1.\n")
        );
        var evidence = CdcSqlServerStartupLogClassifier.Parse(new(0, text, string.Empty));
        evidence.SqlErrorNumbers.Should().Equal(Enumerable.Range(1, 8));
        evidence.Truncated.Should().BeFalse();
    }

    [Test]
    public void It_bounds_each_input_stream_and_reports_truncation()
    {
        var evidence = CdcSqlServerStartupLogClassifier.Parse(
            new(
                0,
                new string('x', 32767) + "This program has encountered a fatal error private-secret",
                "Error: 17113, Severity: 16, State: 1.\n"
            )
        );
        evidence.Truncated.Should().BeTrue();
        evidence.FatalMessage.Should().BeFalse();
        evidence.SqlErrorNumbers.Should().Equal(17113);
        JsonSerializer.Serialize(evidence).Should().NotContain("private");
    }

    [Test]
    public void It_does_not_classify_output_from_a_failed_log_command()
    {
        var evidence = CdcSqlServerStartupLogClassifier.Parse(
            new(1, "out of memory", "private-daemon-error")
        );
        evidence.Should().BeEquivalentTo(CdcSqlServerStartupLogEvidence.Empty("Unavailable"));
    }

    [Test]
    public void It_discards_unknown_signals_addresses_and_oversized_codes()
    {
        const string text =
            "Reason: 0x123456789abcdef0\nLast errno: 123456\n"
            + "Signal: SIGPRIVATE\nError: 1234567, Severity: 16, State: 1.\n"
            + "private-password private-path private-host";
        var evidence = CdcSqlServerStartupLogClassifier.Parse(new(0, text, string.Empty));
        evidence.FatalReasonCodes.Should().BeEmpty();
        evidence.LastErrnos.Should().BeEmpty();
        evidence.Signals.Should().BeEmpty();
        evidence.SqlErrorNumbers.Should().BeEmpty();
        JsonSerializer.Serialize(evidence).Should().NotContain("private");
    }
}
