// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[NonParallelizable]
[Category("Logging")]
[Category("Telemetry")]
public sealed class Given_DocumentCacheAdminLoggingAndTelemetry
{
    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public async Task It_keeps_json_stdout_to_one_contract_document_when_cli_logs_are_enabled()
    {
        string tenantKey = "TenantSecretValue";
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        RecordingMutatingCommandDispatcher dispatcher = new(_ => CompletedResult(tenantKey));
        using var loggerProvider = new TextWriterLoggerProvider(stderr);
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.SetMinimumLevel(LogLevel.Information);
                loggingBuilder.AddProvider(loggerProvider);
            })
            .AddSingleton<IDocumentCacheProjectionSupervisor>(
                new SuccessfulProjectionSupervisor(DocumentCacheTargetKey.Create(tenantKey, 1))
            )
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .AddSingleton<IDocumentCacheAdminCliTelemetry, DocumentCacheAdminCliTelemetry>()
            .BuildServiceProvider();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                tenantKey,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "onlineCacheRebuild",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            InvocationTarget(tenantKey),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        string json = stdout.ToString();
        json.TrimEnd().Should().NotContain("\n");
        JsonNode.Parse(json).Should().NotBeNull();

        string diagnostics = stderr.ToString();
        diagnostics.Should().Contain("DocumentCacheAdminCommandStarted");
        diagnostics.Should().Contain("DocumentCacheAdminCommandCompleted");
        diagnostics.Should().Contain("target t1_");
        diagnostics.Should().NotContain(tenantKey);
        diagnostics.Should().NotContain(Fingerprint);
    }

    [Test]
    public async Task It_bounds_human_output_without_raw_target_fingerprint_or_sensitive_diagnostics()
    {
        string tenantKey = "Tenant" + new string('A', 180) + "Secret";
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        RecordingMutatingCommandDispatcher dispatcher = new(_ =>
            Result(
                tenantKey,
                "DocumentUuid 2f0bc840-763e-4e73-9ca3-fbbfc6de3ef1 StudentUniqueId 123456 Password=abc"
            )
        );
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheProjectionSupervisor>(
                new SuccessfulProjectionSupervisor(DocumentCacheTargetKey.Create(tenantKey, 1))
            )
            .AddSingleton<IDocumentCacheAdminMutatingCommandDispatcher>(dispatcher)
            .BuildServiceProvider();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                tenantKey,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "onlineCacheRebuild"
            ),
            InvocationTarget(tenantKey),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        string humanOutput = stdout.ToString();
        humanOutput.Should().Contain("target=t1_");
        humanOutput.Should().Contain("physicalSourceFingerprint=present");
        humanOutput.Should().Contain("diagnostic redacted");
        humanOutput.Should().NotContain(tenantKey);
        humanOutput.Should().NotContain(Fingerprint);
        humanOutput.Should().NotContain("DocumentUuid");
        humanOutput.Should().NotContain("StudentUniqueId");
        humanOutput.Should().NotContain("Password");
        stderr.ToString().Should().BeEmpty();
    }

    [Test]
    public void It_sanitizes_verbose_console_and_rolling_file_log_rendering_at_the_serilog_boundary()
    {
        const string exceptionSentinel = "EXCEPTION_PASSWORD_SENTINEL";
        const string connectionStringSentinel =
            "Server=cli-db-host;Database=DATABASE_SENTINEL;User Id=cli-user;Password=CONNECTION_PASSWORD_SENTINEL";
        const string credentialSentinel = "CREDENTIAL_SENTINEL";
        const string cmsUrlSentinel = "https://cms-sentinel.example.local/configuration";
        const string dataStoreNameSentinel = "DATASTORE_NAME_SENTINEL";
        const string tenantInputSentinel = "TENANT_INPUT_SENTINEL";
        const string databaseIdentifierSentinel = "PHYSICAL_DATABASE_SENTINEL";
        const string documentIdentifierSentinel = "DOCUMENT_IDENTIFIER_SENTINEL";

        string logDirectory = Path.Combine(Path.GetTempPath(), $"dms-document-cache-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);
        string logPath = Path.Combine(logDirectory, $"{DocumentCacheAdminCliConstants.ToolCommandName}.log");
        using var stderr = new StringWriter();
        TextWriter originalError = Console.Error;

        try
        {
            Console.SetError(stderr);
            using Serilog.Core.Logger logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    DocumentCacheAdminLogSanitizingTextFormatter.Instance,
                    logPath,
                    rollingInterval: RollingInterval.Day
                )
                .WriteTo.Console(
                    DocumentCacheAdminLogSanitizingTextFormatter.Instance,
                    standardErrorFromLevel: LogEventLevel.Verbose
                )
                .CreateLogger();

            logger
                .ForContext(
                    "SourceContext",
                    "EdFi.DataManagementService.Core.Configuration.ConfigurationServiceDataStoreProvider"
                )
                .ForContext("Command", DocumentCacheAdminCommandSurface.RebuildOnlineCommandName)
                .ForContext("Target", "t1_safeTarget")
                .ForContext("OutputMode", "json")
                .ForContext("Outcome", "RejectedNoMutation")
                .ForContext("Category", "UnexpectedProviderFailure")
                .ForContext("ConnectionString", connectionStringSentinel)
                .ForContext("ClientSecret", credentialSentinel)
                .ForContext("CmsUrl", cmsUrlSentinel)
                .ForContext("DataStoreName", dataStoreNameSentinel)
                .ForContext("TenantInput", tenantInputSentinel)
                .ForContext("DatabaseIdentifier", databaseIdentifierSentinel)
                .ForContext("DocumentIdentifier", documentIdentifierSentinel)
                .Warning(
                    new InvalidOperationException($"provider failed with {exceptionSentinel}"),
                    "DocumentCacheAdminCommandCompleted command {Command} target {Target} outcome {Outcome}."
                );
        }
        finally
        {
            Console.SetError(originalError);
        }

        string fileOutput = File.ReadAllText(
            Directory.GetFiles(logDirectory).Should().ContainSingle().Subject
        );
        string renderedLogs = stderr + fileOutput;

        renderedLogs.Should().Contain("DocumentCacheAdminCommandCompleted");
        renderedLogs.Should().Contain("level Warning");
        renderedLogs.Should().Contain("command rebuild-online");
        renderedLogs.Should().Contain("target t1_safeTarget");
        renderedLogs.Should().Contain("outcome RejectedNoMutation");
        renderedLogs.Should().Contain("category UnexpectedProviderFailure");
        renderedLogs.Should().Contain("exceptionType InvalidOperationException");
        renderedLogs.Should().NotContain(exceptionSentinel);
        renderedLogs.Should().NotContain(connectionStringSentinel);
        renderedLogs.Should().NotContain("CONNECTION_PASSWORD_SENTINEL");
        renderedLogs.Should().NotContain(credentialSentinel);
        renderedLogs.Should().NotContain(cmsUrlSentinel);
        renderedLogs.Should().NotContain(dataStoreNameSentinel);
        renderedLogs.Should().NotContain(tenantInputSentinel);
        renderedLogs.Should().NotContain(databaseIdentifierSentinel);
        renderedLogs.Should().NotContain(documentIdentifierSentinel);

        Directory.Delete(logDirectory, recursive: true);
    }

    [Test]
    public void It_records_cli_metrics_on_the_document_cache_meter_with_bounded_target_tags()
    {
        DocumentCacheAdminCliTelemetry
            .MeterName.Should()
            .Be("EdFi.DataManagementService.DocumentCacheProjection");
        string meterName =
            "EdFi.DataManagementService.DocumentCacheProjection.test." + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        List<MeasurementRecord> records = [];
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
                records.Add(new MeasurementRecord(instrument.Name, measurement, TagsToArray(tags)))
        );
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
                records.Add(new MeasurementRecord(instrument.Name, measurement, TagsToArray(tags)))
        );
        listener.Start();

        string tenantKey = "TenantNameThatMustNotBecomeMetricLabel";
        DocumentCacheTargetKey targetKey = DocumentCacheTargetKey.Create(tenantKey, 9);
        var telemetry = new DocumentCacheAdminCliTelemetry(meter);
        long startedAt = telemetry.RecordCommandAttempt(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            targetKey,
            jsonOutput: true
        );
        telemetry.RecordCommandCompletion(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            targetKey,
            jsonOutput: true,
            DocumentCacheAdminExitCodes.RejectedNoMutation,
            "RejectedNoMutation",
            "ExpectedSourceMismatch",
            startedAt
        );

        records
            .Exists(record =>
                record.Name == DocumentCacheAdminCliTelemetry.CommandAttemptCounterName
                && record.TagValue("target")?.ToString()?.StartsWith("t1_", StringComparison.Ordinal) == true
            )
            .Should()
            .BeTrue();
        records
            .Exists(record => record.Name == DocumentCacheAdminCliTelemetry.CommandCompletionCounterName)
            .Should()
            .BeTrue();
        records
            .Exists(record => record.Name == DocumentCacheAdminCliTelemetry.CommandDurationName)
            .Should()
            .BeTrue();
        records
            .SelectMany(record => record.Tags.Select(tag => tag.Value?.ToString() ?? string.Empty))
            .Any(tag => tag.Contains(tenantKey, StringComparison.Ordinal))
            .Should()
            .BeFalse();
    }

    private static ParseResult ParseCommand(string commandName, params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([commandName, .. args]);

    private static DocumentCacheAdminInvocationTarget InvocationTarget(string tenantKey) =>
        new(DocumentCacheTargetKey.Create(tenantKey, 1));

    private sealed class SuccessfulProjectionSupervisor(DocumentCacheTargetKey targetKey)
        : IDocumentCacheProjectionSupervisor
    {
        public ImmutableArray<DocumentCacheProjectionTargetRuntimeContext> CurrentTargetContexts => [];

        public Task<DocumentCacheTargetRegistrySnapshot> RefreshAsync(
            DocumentCacheTargetRefreshReason reason,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new DocumentCacheTargetRegistrySnapshot(
                    [
                        DocumentCacheTargetObservation.Configured(
                            targetKey,
                            DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
                        ),
                    ],
                    DateTimeOffset.UtcNow
                )
            );
        }
    }

    private static DocumentCacheAdministrativeCommandResult CompletedResult(string tenantKey) =>
        Result(tenantKey, "diagnostic");

    private static DocumentCacheAdministrativeCommandResult Result(string tenantKey, string diagnostic) =>
        new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            new DocumentCacheAdministrativeTargetKey(tenantKey, 1),
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            mutated: true,
            targetGeneration: 7,
            physicalSourceFingerprint: new DocumentCachePhysicalSourceFingerprint(Fingerprint),
            lifecycle: DocumentCacheLifecycleState.Tracking,
            cacheAheadRecoveryRequired: false,
            phaseDiagnostics:
            [
                new DocumentCacheAdministrativePhaseDiagnostic(
                    DocumentCacheAdministrativeCommandPhase.Preflight,
                    DocumentCacheAdministrativeCommandPhase.ResolveTarget,
                    retryable: false,
                    DocumentCacheAdministrativeDiagnosticCategory.CacheAheadLatchSet,
                    ImmutableArray<long>.Empty,
                    diagnostic
                ),
            ],
            elapsedCommandTime: TimeSpan.FromSeconds(1.25)
        );

    private static KeyValuePair<string, object?>[] TagsToArray(
        ReadOnlySpan<KeyValuePair<string, object?>> tags
    )
    {
        var snapshot = new KeyValuePair<string, object?>[tags.Length];
        for (int index = 0; index < tags.Length; index++)
        {
            snapshot[index] = tags[index];
        }

        return snapshot;
    }

    private sealed record MeasurementRecord(
        string Name,
        object Measurement,
        KeyValuePair<string, object?>[] Tags
    )
    {
        public object? TagValue(string name) =>
            Array.Find(Tags, tag => string.Equals(tag.Key, name, StringComparison.Ordinal)).Value;
    }

    private sealed class RecordingMutatingCommandDispatcher(
        Func<DocumentCacheAdminMutatingCommandRequest, DocumentCacheAdministrativeCommandResult> execute
    ) : IDocumentCacheAdminMutatingCommandDispatcher
    {
        public Task<DocumentCacheAdministrativeCommandResult> ExecuteAsync(
            DocumentCacheAdminMutatingCommandRequest commandRequest,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(execute(commandRequest));
        }
    }

    private sealed class TextWriterLoggerProvider(TextWriter writer) : ILoggerProvider
    {
        private readonly object _gate = new();

        public MicrosoftLogger CreateLogger(string categoryName) => new TextWriterLogger(writer, _gate);

        public void Dispose() { }
    }

    private sealed class TextWriterLogger(TextWriter writer, object gate) : MicrosoftLogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            lock (gate)
            {
                writer.WriteLine(formatter(state, exception));
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose() { }
    }
}
