// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

internal interface IStartupStatusSignal
{
    string FilePath { get; }
    void WriteStarting(string phase, string summary);
    void WriteCompleted(string phase, string summary);
    void WriteFailed(string phase, string summary, Exception exception);
    void WriteReady(string summary);
}

internal sealed class FileStartupStatusSignal(string filePath, ILogger<FileStartupStatusSignal> logger)
    : IStartupStatusSignal
{
    private const string DefaultFileName = "dms-startup-status.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public FileStartupStatusSignal(IConfiguration configuration, ILogger<FileStartupStatusSignal> logger)
        : this(ResolveFilePath(configuration.GetValue<string>("AppSettings:StartupStatusFilePath")), logger)
    { }

    public FileStartupStatusSignal(string? configuredFilePath)
        : this(ResolveFilePath(configuredFilePath), NullLogger<FileStartupStatusSignal>.Instance) { }

    private readonly string _filePath = filePath;
    private readonly ILogger<FileStartupStatusSignal> _logger = logger;

    public string FilePath => _filePath;

    public void WriteStarting(string phase, string summary) =>
        Write(new StartupStatusDocument("Starting", phase, summary, null, null, DateTimeOffset.UtcNow));

    public void WriteCompleted(string phase, string summary) =>
        Write(new StartupStatusDocument("Completed", phase, summary, null, null, DateTimeOffset.UtcNow));

    public void WriteFailed(string phase, string summary, Exception exception) =>
        Write(
            new StartupStatusDocument(
                "Failed",
                phase,
                summary,
                exception.GetType().Name,
                exception.Message,
                DateTimeOffset.UtcNow
            )
        );

    public void WriteReady(string summary) =>
        Write(
            new StartupStatusDocument(
                "Ready",
                DmsStartupPhases.Ready,
                summary,
                null,
                null,
                DateTimeOffset.UtcNow
            )
        );

    internal static string ResolveFilePath(string? configuredFilePath) =>
        string.IsNullOrWhiteSpace(configuredFilePath)
            ? Path.Combine(Path.GetTempPath(), DefaultFileName)
            : configuredFilePath;

    private void Write(StartupStatusDocument startupStatus)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);

            if (directory is not null && directory.Length > 0)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(startupStatus, SerializerOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to write DMS startup status file at {FilePath}", _filePath);
        }
    }
}

internal sealed record StartupStatusDocument(
    string State,
    string Phase,
    string Summary,
    string? ErrorType,
    string? ErrorMessage,
    DateTimeOffset UpdatedAtUtc
);
