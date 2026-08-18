// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using FluentAssertions.Execution;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// Coverage for the deadlock-graph normalizer and the incomplete-payload detection on
/// <see cref="MssqlConcurrentWriteLoadTestBase"/>. Every graph here is hand-authored rather than
/// captured, so the expected tuples read next to the XML that produces them and nothing depends on a
/// database or on a run that actually deadlocked.
///
/// <para>The categories are the assembly's CI selectors, not a statement about what this fixture
/// needs: the DMS API integration job runs this assembly with
/// <c>Category=ApiIntegration&amp;Category=MssqlIntegration</c>, so an uncategorized fixture would
/// never execute there - and a guard that never runs is not a guard.</para>
/// </summary>
[Category("ApiIntegration")]
[Category("MssqlIntegration")]
public sealed class Given_Mssql_DeadlockGraphSignatures
{
    /// <summary>Shaped like a real lease name, which is what makes qualifier stripping load-bearing.</summary>
    private const string LeasedDatabase = "dmsfp0d5f4a1b9c2e3f40";

    private const string AnotherLeasedDatabase = "dmsfp7e6d5c4b3a291807";

    [Test]
    public void It_normalizes_a_two_process_cycle_to_one_tuple_per_resource_participant()
    {
        MssqlConcurrentWriteLoadTestBase
            .SignaturesOf(TwoProcessCycle(LeasedDatabase))
            .Should()
            .BeEquivalentTo(
                "edfi.TR_School_Stamp|18|dms.Document|IX_Document_CreatedByOwnershipTokenId|X",
                "edfi.TR_ChartOfAccount_Stamp|31|dms.Document|IX_Document_CreatedByOwnershipTokenId|U",
                "edfi.TR_ChartOfAccount_Stamp|31|edfi.School|PK_School|X",
                "edfi.TR_School_Stamp|18|edfi.School|PK_School|U"
            );
    }

    /// <summary>
    /// Leased database names are generated per run, so a signature that kept the qualifier could never
    /// match across the two runs Gate B compares and the comparison would pass vacuously.
    /// </summary>
    [Test]
    public void It_strips_the_per_run_database_qualifier_so_two_runs_of_one_cycle_compare_equal()
    {
        MssqlConcurrentWriteLoadTestBase
            .SignaturesOf(TwoProcessCycle(LeasedDatabase))
            .Should()
            .BeEquivalentTo(
                MssqlConcurrentWriteLoadTestBase.SignaturesOf(TwoProcessCycle(AnotherLeasedDatabase))
            );
    }

    /// <summary>
    /// The noise graph puts <c>stackFrames</c> ahead of <c>executionStack</c> on one process and gives
    /// the other nothing but <c>stackFrames</c>, so reading a call-stack frame as a statement frame
    /// shows up as a wrong tuple rather than passing by luck.
    /// </summary>
    [Test]
    public void It_ignores_stack_frames_when_reading_the_statement_a_process_was_running()
    {
        MssqlConcurrentWriteLoadTestBase
            .SignaturesOf(CycleCarryingStackFrameNoise(LeasedDatabase))
            .Should()
            .BeEquivalentTo(
                "edfi.TR_School_Stamp|18|dms.Document|IX_Document_CreatedByOwnershipTokenId|X",
                "(no frame)|0|dms.Document|IX_Document_CreatedByOwnershipTokenId|U"
            );
    }

    [Test]
    public void It_records_a_missing_indexname_rather_than_dropping_the_resource()
    {
        MssqlConcurrentWriteLoadTestBase
            .SignaturesOf(HeapCycleWithoutAnIndexName(LeasedDatabase))
            .Should()
            .BeEquivalentTo(
                "dms.TR_Descriptor_Stamp|7|dms.Descriptor|(no indexname)|X",
                "dms.TR_Descriptor_Stamp|7|dms.Descriptor|(no indexname)|U"
            );
    }

    /// <summary>
    /// Returning nothing for a graph that did not parse would read as "this run deadlocked less",
    /// which is the failure mode the capture exists to rule out.
    /// </summary>
    [Test]
    public void It_reports_a_malformed_graph_as_unparsable_rather_than_as_no_signatures()
    {
        MssqlConcurrentWriteLoadTestBase
            .SignaturesOf("<deadlock><process-list>")
            .Should()
            .BeEquivalentTo(MssqlConcurrentWriteLoadTestBase.UnparsableGraphSignature);
    }

    [Test]
    public void It_reports_an_event_payload_that_is_not_a_graph_as_unparsable()
    {
        DeadlockCapture capture = MssqlConcurrentWriteLoadTestBase.CaptureFromRingBufferTarget(
            RingBufferTarget("&lt;deadlock&gt;&lt;process-list&gt;"),
            LeasedDatabase
        );

        using (new AssertionScope())
        {
            capture.IsInconclusive.Should().BeFalse();
            capture
                .Signatures.Should()
                .BeEquivalentTo(MssqlConcurrentWriteLoadTestBase.UnparsableGraphSignature);
            capture.Graphs.Should().HaveCount(1);
        }
    }

    /// <summary>
    /// Session isolation is the primary attribution and this filter is the second line of defense, so
    /// it has to drop a foreign graph from the signatures without dropping it from the evidence.
    /// </summary>
    [Test]
    public void It_keeps_a_graph_from_another_database_as_evidence_but_out_of_the_signatures()
    {
        DeadlockCapture capture = MssqlConcurrentWriteLoadTestBase.CaptureFromRingBufferTarget(
            RingBufferTarget(TwoProcessCycle(LeasedDatabase), CycleInAnotherDatabase()),
            LeasedDatabase
        );

        using (new AssertionScope())
        {
            capture.IsInconclusive.Should().BeFalse();
            capture.AttributedGraphCount.Should().Be(1);
            capture.Graphs.Should().HaveCount(2, "an unattributed graph is still evidence");
            capture
                .Signatures.Should()
                .BeEquivalentTo(
                    "edfi.TR_School_Stamp|18|dms.Document|IX_Document_CreatedByOwnershipTokenId|X",
                    "edfi.TR_ChartOfAccount_Stamp|31|dms.Document|IX_Document_CreatedByOwnershipTokenId|U",
                    "edfi.TR_ChartOfAccount_Stamp|31|edfi.School|PK_School|X",
                    "edfi.TR_School_Stamp|18|edfi.School|PK_School|U"
                );
        }
    }

    /// <summary>
    /// All three ways a rendered payload can be missing graphs: <c>truncated</c> for a payload the
    /// target could not render in full, <c>droppedCount</c> for graphs the target discarded, and
    /// <c>totalEventsProcessed</c> above <c>eventCount</c> for graphs the buffer evicted.
    /// </summary>
    [TestCase(1, 0, 1, 1, "truncated")]
    [TestCase(0, 3, 1, 1, "droppedCount")]
    [TestCase(0, 0, 1, 9, "evicted")]
    public void It_reports_an_incomplete_ring_buffer_payload_as_inconclusive_with_no_signatures(
        int truncated,
        int droppedCount,
        int eventCount,
        int totalEventsProcessed,
        string expectedReasonFragment
    )
    {
        DeadlockCapture capture = MssqlConcurrentWriteLoadTestBase.CaptureFromRingBufferTarget(
            IncompleteRingBufferTarget(
                TwoProcessCycle(LeasedDatabase),
                truncated,
                droppedCount,
                eventCount,
                totalEventsProcessed
            ),
            LeasedDatabase
        );

        using (new AssertionScope())
        {
            capture.IsInconclusive.Should().BeTrue();
            capture.InconclusiveReason.Should().Contain(expectedReasonFragment);

            // The reason this is a test rather than a report line: a payload known to be missing
            // graphs must not hand back a shorter signature list, because a shorter list on the
            // baseline side of a differential comparison reads as a fix.
            capture.Signatures.Should().BeEmpty();

            // Whatever it did render is still evidence.
            capture.Graphs.Should().HaveCount(1);
        }
    }

    [Test]
    public void It_reports_a_ring_buffer_payload_that_does_not_parse_as_inconclusive()
    {
        DeadlockCapture capture = MssqlConcurrentWriteLoadTestBase.CaptureFromRingBufferTarget(
            """<RingBufferTarget truncated="0" """,
            LeasedDatabase
        );

        using (new AssertionScope())
        {
            capture.IsInconclusive.Should().BeTrue();
            capture.InconclusiveReason.Should().Contain("did not parse");
            capture.Signatures.Should().BeEmpty();
        }
    }

    /// <summary>
    /// Each process holds the lock the other waits for, and the two are running different statements,
    /// so all four participant tuples are distinct and none of them can be produced by accident.
    /// </summary>
    private static string TwoProcessCycle(string databaseName) =>
        $"""
            <deadlock>
              <victim-list>
                <victimProcess id="processa" />
              </victim-list>
              <process-list>
                <process id="processa" spid="61" lockMode="U" currentdb="7" currentdbname="{databaseName}" isolationlevel="read committed (2)">
                  <executionStack>
                    <frame procname="{databaseName}.edfi.TR_School_Stamp" line="18" stmtstart="512">UPDATE d SET [ContentVersion] = ...</frame>
                  </executionStack>
                  <inputbuf>POST /data/ed-fi/schools</inputbuf>
                </process>
                <process id="processb" spid="62" lockMode="U" currentdb="7" currentdbname="{databaseName}" isolationlevel="read committed (2)">
                  <executionStack>
                    <frame procname="{databaseName}.edfi.TR_ChartOfAccount_Stamp" line="31" stmtstart="704">UPDATE d SET [ContentVersion] = ...</frame>
                  </executionStack>
                  <inputbuf>POST /data/ed-fi/chartOfAccounts</inputbuf>
                </process>
              </process-list>
              <resource-list>
                <keylock hobtid="72057594046840832" dbid="7" objectname="{databaseName}.dms.Document" indexname="IX_Document_CreatedByOwnershipTokenId" mode="X">
                  <owner-list>
                    <owner id="processa" mode="X" />
                  </owner-list>
                  <waiter-list>
                    <waiter id="processb" mode="U" requestType="wait" />
                  </waiter-list>
                </keylock>
                <keylock hobtid="72057594046840833" dbid="7" objectname="{databaseName}.edfi.School" indexname="PK_School" mode="X">
                  <owner-list>
                    <owner id="processb" mode="X" />
                  </owner-list>
                  <waiter-list>
                    <waiter id="processa" mode="U" requestType="wait" />
                  </waiter-list>
                </keylock>
              </resource-list>
            </deadlock>
            """;

    /// <summary>
    /// Deliberately nothing like <see cref="TwoProcessCycle"/>: if the leased-database filter stopped
    /// working, this graph's tuples would be conspicuous in the signature set rather than blending in.
    /// </summary>
    private static string CycleInAnotherDatabase() =>
        $"""
            <deadlock>
              <process-list>
                <process id="processc" spid="70" currentdb="9" currentdbname="{AnotherLeasedDatabase}">
                  <executionStack>
                    <frame procname="{AnotherLeasedDatabase}.dbo.TR_Unrelated_Stamp" line="4">UPDATE u SET ...</frame>
                  </executionStack>
                </process>
              </process-list>
              <resource-list>
                <keylock objectname="{AnotherLeasedDatabase}.dbo.Unrelated" indexname="PK_Unrelated" mode="X">
                  <owner-list>
                    <owner id="processc" mode="X" />
                  </owner-list>
                  <waiter-list>
                    <waiter id="processc" mode="U" requestType="wait" />
                  </waiter-list>
                </keylock>
              </resource-list>
            </deadlock>
            """;

    private static string CycleCarryingStackFrameNoise(string databaseName) =>
        $"""
            <deadlock>
              <process-list>
                <process id="processa" spid="61" currentdbname="{databaseName}">
                  <stackFrames>
                    <frame id="frame1" level="1" />
                    <frame id="frame2" level="2" />
                  </stackFrames>
                  <executionStack>
                    <frame procname="{databaseName}.edfi.TR_School_Stamp" line="18">UPDATE d SET ...</frame>
                  </executionStack>
                </process>
                <process id="processb" spid="62" currentdbname="{databaseName}">
                  <stackFrames>
                    <frame id="frame3" level="1" />
                  </stackFrames>
                </process>
              </process-list>
              <resource-list>
                <keylock objectname="{databaseName}.dms.Document" indexname="IX_Document_CreatedByOwnershipTokenId" mode="X">
                  <owner-list>
                    <owner id="processa" mode="X" />
                  </owner-list>
                  <waiter-list>
                    <waiter id="processb" mode="U" requestType="wait" />
                  </waiter-list>
                </keylock>
              </resource-list>
            </deadlock>
            """;

    private static string HeapCycleWithoutAnIndexName(string databaseName) =>
        $"""
            <deadlock>
              <process-list>
                <process id="processa" spid="61" currentdbname="{databaseName}">
                  <executionStack>
                    <frame procname="{databaseName}.dms.TR_Descriptor_Stamp" line="7">UPDATE d SET ...</frame>
                  </executionStack>
                </process>
              </process-list>
              <resource-list>
                <ridlock fileid="1" pageid="9812" dbid="7" objectname="{databaseName}.dms.Descriptor" mode="X">
                  <owner-list>
                    <owner id="processa" mode="X" />
                  </owner-list>
                  <waiter-list>
                    <waiter id="processa" mode="U" requestType="wait" />
                  </waiter-list>
                </ridlock>
              </resource-list>
            </deadlock>
            """;

    private static string RingBufferTarget(params string[] graphs) =>
        BuildRingBufferTarget(
            graphs,
            truncated: 0,
            droppedCount: 0,
            eventCount: graphs.Length,
            totalEventsProcessed: graphs.Length
        );

    private static string IncompleteRingBufferTarget(
        string graph,
        int truncated,
        int droppedCount,
        int eventCount,
        int totalEventsProcessed
    ) => BuildRingBufferTarget([graph], truncated, droppedCount, eventCount, totalEventsProcessed);

    private static string BuildRingBufferTarget(
        IReadOnlyCollection<string> graphs,
        int truncated,
        int droppedCount,
        int eventCount,
        int totalEventsProcessed
    ) =>
        $"""
            <RingBufferTarget truncated="{truncated}" processingTime="0" totalEventsProcessed="{totalEventsProcessed}" eventCount="{eventCount}" droppedCount="{droppedCount}" memoryUsed="12">
            {string.Join(Environment.NewLine, graphs.Select(DeadlockEvent))}
            </RingBufferTarget>
            """;

    private static string DeadlockEvent(string graph) =>
        $"""
            <event name="xml_deadlock_report" package="sqlserver" timestamp="2026-08-18T12:00:00.000Z">
              <data name="xml_report">
                <type name="xml" package="package0" />
                <value>
            {graph}
                </value>
              </data>
            </event>
            """;
}
