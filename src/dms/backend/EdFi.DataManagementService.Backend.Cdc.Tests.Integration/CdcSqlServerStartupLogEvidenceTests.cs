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
    public void It_preserves_crash_identity_without_publishing_arbitrary_messages_or_paths()
    {
        const string log = """
            Status: 0xc0000017
            [0] 0xffffffffc0000017
            [1] 0x123456789abcdef01
            Message: private-secret on private-host
            file://package6/windows/system32/sqlpal.dll+0x0000a123
            file:///Windows/System32/lsasrv.dll+0x1234
            file:///private-secret.dll+0x1234
            Build stamp: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            CDC_SQL_STARTUP_INJECTED_FAILURE
            """;
        var evidence = CdcSqlServerStartupLogClassifier.Parse(new(0, log, ""));
        evidence.StatusCodes.Should().Equal("0XC0000017");
        evidence.Parameters.Should().Equal("0XFFFFFFFFC0000017");
        evidence.StackFrames.Should().Equal("sqlpal.dll+0X0000A123", "lsasrv.dll+0X1234");
        evidence.MessageKind.Should().Be("Other");
        evidence.MessageSha256.Should().HaveLength(64);
        evidence.BuildStamp.Should().Be(new string('a', 64));
        evidence.InjectedFailure.Should().BeTrue();
        JsonSerializer.Serialize(evidence).Should().NotContain("private").And.NotContain("file://");
    }

    [Test]
    public void It_retains_only_numeric_resource_fields_and_known_limit_names()
    {
        Dictionary<string, object> evidence = [];
        CdcSqlServerStartupResourceParser.AddHostMemory(
            evidence,
            "MemTotal: 12345 kB\nMemAvailable: 456 kB\nprivate-secret: 999 kB\n"
        );
        CdcSqlServerStartupResourceParser.AddContainerLimits(
            evidence,
            """
            {"Memory":2147483648,"PidsLimit":null,"NanoCpus":0,"Binds":["private-secret"],
             "Ulimits":[{"Name":"nproc","Soft":4096,"Hard":8192},{"Name":"private-secret","Soft":1,"Hard":1}]}
            """
        );
        evidence["HostMemTotalKiB"].Should().Be(12345L);
        evidence["HostMemAvailableKiB"].Should().Be(456L);
        evidence["ContainerMemory"].Should().Be(2147483648L);
        evidence["ContainernprocSoft"].Should().Be(4096L);
        evidence.Should().NotContainKey("ContainerPidsLimit");
        JsonSerializer.Serialize(evidence).Should().NotContain("private");
    }

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
