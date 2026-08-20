// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Xml;
using System.Xml.Linq;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// One load's deadlock evidence, read from a capture session that belongs to that load alone.
///
/// <para><see cref="Signatures"/> is only a comparable description of what deadlocked when
/// <see cref="InconclusiveReason"/> is null. An incomplete capture yields an empty list rather than
/// a short one, because a differential comparison reads a shorter signature list as an improvement:
/// under-reporting on the baseline side would present itself as a fix.</para>
///
/// <para><see cref="Graphs"/> holds every raw graph the session recorded, including graphs that
/// could not be attributed to the leased database, so the evidence survives independently of both
/// the attribution filter and the normalizer.</para>
/// </summary>
public sealed record DeadlockCapture(
    IReadOnlyList<string> Signatures,
    IReadOnlyList<string> Graphs,
    int AttributedGraphCount,
    string? InconclusiveReason = null
)
{
    public bool IsInconclusive => InconclusiveReason is not null;
}

/// <summary>
/// Turns the XML an Extended Events deadlock capture produces into comparable signatures. Pure
/// string-to-object work: it owns no session, touches no database, and is kept out of
/// <see cref="MssqlConcurrentWriteLoadTestBase"/> so that base stays about driving loads and
/// managing capture sessions — and so this logic can be unit-tested against hand-authored graphs
/// without a lease, a host, or a server.
/// </summary>
public static class DeadlockGraphReader
{
    /// <summary>
    /// Returned in place of a signature list for any graph this reader could not reduce to tuples -
    /// XML that did not parse, and XML that parsed but described no locked resource with a
    /// participant. Neither must reduce to "no signatures": that is the same silent under-reporting
    /// the truncation check exists to prevent, and it biases a differential comparison toward a false
    /// pass.
    /// </summary>
    public const string UnparsableGraphSignature = "(unparsable deadlock graph)";

    private const string NoFrame = "(no frame)";
    private const string NoObjectName = "(no objectname)";
    private const string NoIndexName = "(no indexname)";
    private const string NoLockMode = "(no mode)";

    /// <summary>
    /// Turns a ring buffer target payload into this run's signature multiset.
    ///
    /// <para>Session isolation is the primary attribution; the leased-database filter here is the
    /// second line of defense, and it keeps unattributed graphs in <see cref="DeadlockCapture.Graphs"/>
    /// so filtering can never lose evidence silently.</para>
    ///
    /// <para>An incomplete payload is inconclusive rather than short. The target reports rendering
    /// truncation through <c>truncated</c>, and eviction shows up as <c>totalEventsProcessed</c>
    /// exceeding <c>eventCount</c> or as a non-zero <c>droppedCount</c>; all three mean graphs are
    /// missing from the payload, which is exactly what a differential comparison must not absorb. A
    /// payload with no <c>RingBufferTarget</c> element is inconclusive for the same reason: those
    /// counters live on it, so without it the payload cannot be shown to be complete.</para>
    /// </summary>
    public static DeadlockCapture CaptureFromRingBufferTarget(string ringBufferTargetXml, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(ringBufferTargetXml);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        XElement payload;
        try
        {
            payload = XElement.Parse(ringBufferTargetXml);
        }
        catch (XmlException exception)
        {
            return new DeadlockCapture(
                [],
                [],
                0,
                $"the ring buffer target payload did not parse: {exception.Message}"
            );
        }

        // Every completeness counter is an attribute of RingBufferTarget, and ReadCounter reports a
        // missing attribute as zero - so reading them off some other element would report a complete
        // payload no matter what was missing from it, which is the one answer this type must never
        // give. Absence of that element is therefore inconclusive, not a reason to fall back to the
        // payload root. The graph walk still runs, because an unreadable header does not make the
        // graphs the payload does carry any less useful as evidence.
        XElement? ringBuffer =
            payload.Name.LocalName == "RingBufferTarget"
                ? payload
                : payload.Descendants("RingBufferTarget").FirstOrDefault();
        XElement graphSource = ringBuffer ?? payload;

        List<string> graphs = [];
        List<string> signatures = [];
        int attributedGraphCount = 0;

        foreach (XElement graph in DeadlockGraphsIn(graphSource))
        {
            graphs.Add(graph.ToString());
            if (!TargetsDatabase(graph, databaseName))
            {
                continue;
            }

            attributedGraphCount++;
            signatures.AddRange(SignaturesOf(graph.ToString()));
        }

        foreach (string unparsed in UnparsedGraphsIn(graphSource))
        {
            graphs.Add(unparsed);
            attributedGraphCount++;
            signatures.AddRange(SignaturesOf(unparsed));
        }

        string? inconclusiveReason = ringBuffer is null
            ? "the payload carried no RingBufferTarget element, so its completeness counters could not be read"
            : IncompletePayloadReason(ringBuffer);

        return new DeadlockCapture(
            inconclusiveReason is null ? signatures : [],
            graphs,
            attributedGraphCount,
            inconclusiveReason
        );
    }

    /// <summary>
    /// Normalizes one deadlock graph to its <c>(procname, line, objectname, indexname, lockmode)</c>
    /// tuple set: for every participant in every locked resource, the statement that participant was
    /// running and the mode it held or wanted. Distinct and ordered, so two graphs of the same cycle
    /// compare equal regardless of which process the engine chose as victim.
    ///
    /// <para>The database qualifier is stripped from <c>procname</c> and <c>objectname</c> because
    /// leased database names are per-run; without that, no signature would ever match across runs.</para>
    ///
    /// <para><c>line</c> is a line number inside the emitted trigger body, so a change to the emitted
    /// SQL renumbers it. When comparing a baseline against a candidate, a pair of signatures that
    /// differ only in <c>line</c> is renumbering, not a different cycle.</para>
    /// </summary>
    public static IReadOnlyList<string> SignaturesOf(string deadlockXml)
    {
        ArgumentNullException.ThrowIfNull(deadlockXml);

        XElement deadlock;
        try
        {
            deadlock = XElement.Parse(deadlockXml);
        }
        catch (XmlException)
        {
            return [UnparsableGraphSignature];
        }

        // <stackFrames> carries raw call-stack <frame> elements that are not statement frames.
        // Removing them keeps the statement walk below from reading one as an execution-stack frame.
        foreach (XElement stackFrames in deadlock.Descendants("stackFrames").ToArray())
        {
            stackFrames.Remove();
        }

        Dictionary<string, string> statementByProcessId = new(StringComparer.Ordinal);
        foreach (XElement process in deadlock.Descendants("process"))
        {
            if (process.Attribute("id")?.Value is string processId)
            {
                statementByProcessId[processId] = TopStatementOf(process);
            }
        }

        List<string> signatures = [];
        foreach (XElement resource in deadlock.Element("resource-list")?.Elements() ?? [])
        {
            string objectName =
                StripDatabaseQualifier(resource.Attribute("objectname")?.Value) ?? NoObjectName;
            string indexName = resource.Attribute("indexname")?.Value ?? NoIndexName;

            IEnumerable<XElement> participants = resource
                .Elements("owner-list")
                .Elements("owner")
                .Concat(resource.Elements("waiter-list").Elements("waiter"));

            foreach (XElement participant in participants)
            {
                string statement =
                    participant.Attribute("id")?.Value is string processId
                    && statementByProcessId.TryGetValue(processId, out string? found)
                        ? found
                        : $"{NoFrame}|0";
                string lockMode = participant.Attribute("mode")?.Value ?? NoLockMode;
                signatures.Add($"{statement}|{objectName}|{indexName}|{lockMode}");
            }
        }

        // Reached when the XML parsed but carried nothing to describe: no resource-list, an empty
        // one, or resources with no owner or waiter. Every real graph names at least one locked
        // resource with at least one participant, so returning an empty list here would only ever
        // report a graph the reader failed to understand as a run that deadlocked less - the same
        // false pass the parse failure above refuses.
        return signatures.Count == 0
            ? [UnparsableGraphSignature]
            : [.. signatures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The statement at the top of a process's execution stack as <c>procname|line</c>. That is the
    /// statement holding or waiting for the lock; deeper frames are its callers.
    /// </summary>
    private static string TopStatementOf(XElement process)
    {
        XElement? frame = process.Element("executionStack")?.Element("frame");
        if (frame is null)
        {
            return $"{NoFrame}|0";
        }

        string procName = StripDatabaseQualifier(frame.Attribute("procname")?.Value) ?? "(adhoc)";
        string line = frame.Attribute("line")?.Value ?? "0";
        return $"{procName}|{line}";
    }

    /// <summary>
    /// Drops the leading database qualifier from a three-part name, so <c>&lt;leased db&gt;.dms.Document</c>
    /// normalizes to <c>dms.Document</c> and compares across runs.
    /// </summary>
    private static string? StripDatabaseQualifier(string? name)
    {
        if (name is null)
        {
            return null;
        }

        string[] parts = name.Split('.');
        return parts.Length == 3 ? $"{parts[1]}.{parts[2]}" : name;
    }

    /// <summary>
    /// Attributes a graph to the leased database by the two signals a graph carries: a process's
    /// <c>currentdbname</c>, and the database qualifier on a locked resource's <c>objectname</c>.
    /// </summary>
    private static bool TargetsDatabase(XElement deadlock, string databaseName)
    {
        if (
            deadlock
                .Descendants("process")
                .Any(process =>
                    string.Equals(
                        process.Attribute("currentdbname")?.Value,
                        databaseName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
        )
        {
            return true;
        }

        string qualifier = $"{databaseName}.";
        return deadlock
            .Descendants()
            .Any(element =>
                element
                    .Attribute("objectname")
                    ?.Value.StartsWith(qualifier, StringComparison.OrdinalIgnoreCase) == true
            );
    }

    /// <summary>
    /// The <c>&lt;deadlock&gt;</c> element of every deadlock event in the payload. The ring buffer
    /// target nests xml-typed event data as real XML rather than as escaped text.
    /// </summary>
    private static IEnumerable<XElement> DeadlockGraphsIn(XElement ringBuffer) =>
        DeadlockReportValuesIn(ringBuffer).Select(value => value.Element("deadlock")).OfType<XElement>();

    /// <summary>
    /// Event payloads that carried no <c>&lt;deadlock&gt;</c> element but did carry text. Kept as raw
    /// strings so <see cref="SignaturesOf"/> reports them as unparsable rather than dropping them.
    /// </summary>
    private static IEnumerable<string> UnparsedGraphsIn(XElement ringBuffer) =>
        DeadlockReportValuesIn(ringBuffer)
            .Where(value => value.Element("deadlock") is null && !string.IsNullOrWhiteSpace(value.Value))
            .Select(value => value.Value);

    private static IEnumerable<XElement> DeadlockReportValuesIn(XElement ringBuffer) =>
        ringBuffer
            .Descendants("event")
            .Where(@event => @event.Attribute("name")?.Value == "xml_deadlock_report")
            .Elements("data")
            .Where(data => data.Attribute("name")?.Value == "xml_report")
            .Elements("value");

    private static string? IncompletePayloadReason(XElement ringBuffer)
    {
        long truncated = ReadCounter(ringBuffer, "truncated");
        long dropped = ReadCounter(ringBuffer, "droppedCount");
        long buffered = ReadCounter(ringBuffer, "eventCount");
        long processed = ReadCounter(ringBuffer, "totalEventsProcessed");

        if (truncated != 0)
        {
            return $"the ring buffer target reported truncated=\"{truncated}\", so its rendered payload "
                + "omitted graphs it holds";
        }

        if (dropped != 0)
        {
            return $"the ring buffer target reported droppedCount=\"{dropped}\"";
        }

        return processed > buffered
            ? $"the ring buffer evicted graphs: totalEventsProcessed=\"{processed}\" exceeds "
                + $"eventCount=\"{buffered}\""
            : null;
    }

    private static long ReadCounter(XElement ringBuffer, string attributeName) =>
        long.TryParse(ringBuffer.Attribute(attributeName)?.Value, out long value) ? value : 0;
}
