// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.DocumentCacheAdmin;

internal interface IDocumentCacheAdminCliTelemetry
{
    long RecordCommandAttempt(string commandName, DocumentCacheTargetKey targetKey, bool jsonOutput);

    void RecordCommandCompletion(
        string commandName,
        DocumentCacheTargetKey targetKey,
        bool jsonOutput,
        int exitCode,
        string outcome,
        string category,
        long startTimestamp
    );
}

internal sealed class DocumentCacheAdminCliTelemetry : IDocumentCacheAdminCliTelemetry
{
    internal const string MeterName = DocumentCacheProjectionTelemetry.MeterName;
    internal const string CommandAttemptCounterName =
        "edfi.dms.document_cache.administration.cli.command.attempts";
    internal const string CommandCompletionCounterName =
        "edfi.dms.document_cache.administration.cli.command.completions";
    internal const string CommandDurationName = "edfi.dms.document_cache.administration.cli.command.duration";

    private static readonly Meter SharedMeter = new(MeterName);

    private readonly Counter<long> _commandAttemptCounter;
    private readonly Counter<long> _commandCompletionCounter;
    private readonly Histogram<double> _commandDuration;
    private readonly ILogger<DocumentCacheAdminCliTelemetry> _logger;

    public DocumentCacheAdminCliTelemetry(ILogger<DocumentCacheAdminCliTelemetry>? logger = null)
        : this(SharedMeter, logger) { }

    internal DocumentCacheAdminCliTelemetry(
        Meter meter,
        ILogger<DocumentCacheAdminCliTelemetry>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(meter);

        _logger = logger ?? NullLogger<DocumentCacheAdminCliTelemetry>.Instance;
        _commandAttemptCounter = meter.CreateCounter<long>(
            CommandAttemptCounterName,
            unit: "{attempt}",
            description: "DocumentCache administration CLI command attempts."
        );
        _commandCompletionCounter = meter.CreateCounter<long>(
            CommandCompletionCounterName,
            unit: "{completion}",
            description: "DocumentCache administration CLI command completions."
        );
        _commandDuration = meter.CreateHistogram<double>(
            CommandDurationName,
            unit: "ms",
            description: "DocumentCache administration CLI command elapsed duration."
        );
    }

    public long RecordCommandAttempt(string commandName, DocumentCacheTargetKey targetKey, bool jsonOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(targetKey);

        string targetLabel = DocumentCacheAdminOutput.TargetSurrogate(targetKey);
        string commandLabel = DocumentCacheAdminOutput.BoundedLabel(commandName);
        string outputMode = OutputMode(jsonOutput);
        TagList tags = CommonTags(commandLabel, targetLabel, outputMode);

        _commandAttemptCounter.Add(1, tags);
        _logger.LogInformation(
            "DocumentCacheAdminCommandStarted command {Command} target {Target} outputMode {OutputMode}.",
            commandLabel,
            targetLabel,
            outputMode
        );

        return Stopwatch.GetTimestamp();
    }

    public void RecordCommandCompletion(
        string commandName,
        DocumentCacheTargetKey targetKey,
        bool jsonOutput,
        int exitCode,
        string outcome,
        string category,
        long startTimestamp
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(targetKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        string targetLabel = DocumentCacheAdminOutput.TargetSurrogate(targetKey);
        string commandLabel = DocumentCacheAdminOutput.BoundedLabel(commandName);
        string outputMode = OutputMode(jsonOutput);
        string outcomeLabel = DocumentCacheAdminOutput.BoundedLabel(outcome);
        string categoryLabel = DocumentCacheAdminOutput.BoundedLabel(category);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        TagList tags = CommonTags(commandLabel, targetLabel, outputMode);
        tags.Add("outcome", outcomeLabel);
        tags.Add("category", categoryLabel);
        tags.Add("exit_code", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

        _commandCompletionCounter.Add(1, tags);
        _commandDuration.Record(elapsed < TimeSpan.Zero ? 0 : elapsed.TotalMilliseconds, tags);

        LogLevel level =
            exitCode == DocumentCacheAdminExitCodes.Success ? LogLevel.Information : LogLevel.Warning;
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        _logger.Log(
            level,
            "DocumentCacheAdminCommandCompleted command {Command} target {Target} outputMode {OutputMode} outcome {Outcome} category {Category} exitCode {ExitCode} durationMs {DurationMs}.",
            commandLabel,
            targetLabel,
            outputMode,
            outcomeLabel,
            categoryLabel,
            exitCode,
            elapsed.TotalMilliseconds
        );
    }

    private static TagList CommonTags(string commandLabel, string targetLabel, string outputMode) =>
        [
            new("provider", DocumentCacheProjectionTelemetryLabel.Unknown),
            new("target", targetLabel),
            new("command", commandLabel),
            new("output_mode", outputMode),
        ];

    private static string OutputMode(bool jsonOutput) => jsonOutput ? "json" : "human";
}
