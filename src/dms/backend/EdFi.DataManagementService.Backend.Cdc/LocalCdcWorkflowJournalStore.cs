// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using static EdFi.DataManagementService.Backend.Cdc.CdcWorkflowJournalValidation;

namespace EdFi.DataManagementService.Backend.Cdc;

/// <summary>
/// Single-controller local storage alongside Core's binding/incident files. Hold the returned session
/// across intent, external effects and live reconciliation, including binding mutations. Watch releases
/// it between passes. The persistent lock file must never be deleted (including on session disposal).
/// </summary>
public sealed partial class LocalCdcWorkflowJournalStore
{
    private readonly CdcStateStorePathResolver _paths;
    private readonly TimeProvider _time;
    private readonly Action<CdcWorkflowWriteBoundary> _onWrite;
    private static readonly ICdcLocalStateStorePermissions _permissions =
        CdcLocalStateStorePermissions.Current;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new SafeNameConverter() },
    };

    public LocalCdcWorkflowJournalStore(string rootPath = LocalCdcBindingStateStore.DefaultRootPath)
        : this(rootPath, TimeProvider.System, _ => { }) { }

    internal LocalCdcWorkflowJournalStore(
        string rootPath,
        TimeProvider time,
        Action<CdcWorkflowWriteBoundary> onWrite
    )
    {
        try
        {
            _paths = new(rootPath);
        }
        catch (Exception exception) when (IsStorageException(exception) || exception is ArgumentException)
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Invalid);
        }
        _time = time;
        _onWrite = onWrite;
    }

    public async Task<Session> AcquireAsync(
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken
    )
    {
        Require(
            timeout > TimeSpan.Zero
                && timeout <= TimeSpan.FromMinutes(10)
                && pollInterval > TimeSpan.Zero
                && pollInterval <= timeout
        );
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                EnsureDirectory(_paths.RootPath);
                string path = Path.Combine(_paths.RootPath, "controller.lock");
                ValidateEntry(path, file: true);
                FileStream stream = new(path, FileOptions(FileMode.OpenOrCreate, FileShare.None));
                try
                {
                    Require(
                        _permissions.ValidateOwnerOnlyFile(path).Succeeded,
                        CdcWorkflowStateFailure.Unavailable
                    );
                    return new(this, stream);
                }
                catch
                {
                    await stream.DisposeAsync();
                    throw;
                }
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 11 or 32 or 33)
            {
                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    throw new CdcWorkflowStateException(CdcWorkflowStateFailure.LockTimeout);
                }
                await Task.Delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken);
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Unavailable);
            }
        }
    }

    public sealed partial class Session : IAsyncDisposable
    {
        private readonly LocalCdcWorkflowJournalStore _store;
        private readonly FileStream _lock;
        private readonly SemaphoreSlim _gate = new(1);
        private bool _disposed;

        internal Session(LocalCdcWorkflowJournalStore store, FileStream controllerLock)
        {
            _store = store;
            _lock = controllerLock;
        }

        public Task<CdcWorkflowJournal> ReadAsync(
            CdcTargetIdentity target,
            CancellationToken cancellationToken
        ) => RunAsync(() => _store.ReadAsync(target, cancellationToken), cancellationToken);

        /// <summary>Only the original managed workflow may create a journal, before CREATE DATABASE.</summary>
        public Task<CdcWorkflowJournal> CreateAsync(
            Guid workflowId,
            CdcTargetIdentity target,
            CancellationToken cancellationToken,
            CdcWorkflowPurpose purpose = CdcWorkflowPurpose.SourceHistoryOnly
        ) =>
            RunAsync(
                async () =>
                {
                    CdcWorkflowJournal journal = new(
                        CdcWorkflowJournal.CurrentVersion,
                        workflowId,
                        target,
                        _store.Now(),
                        [],
                        purpose
                    );
                    await _store.WriteAsync(journal, create: true, cancellationToken);
                    return journal;
                },
                cancellationToken
            );

        public Task<CdcWorkflowJournal> RecordIntentAsync(
            CdcTargetIdentity target,
            Guid workflowId,
            Guid operationId,
            CdcWorkflowEffect effect,
            ImmutableArray<CdcRecordSizeIncreaseJournal> recordSizeIncrease,
            CancellationToken cancellationToken
        ) =>
            RecordIntentCoreAsync(
                target,
                workflowId,
                operationId,
                effect,
                recordSizeIncrease,
                [],
                cancellationToken
            );

        public Task<CdcWorkflowJournal> RecordConnectorRegistrationIntentAsync(
            CdcTargetIdentity target,
            Guid workflowId,
            Guid operationId,
            CdcConnectorRegistrationIntent registration,
            CancellationToken cancellationToken
        ) =>
            RecordIntentCoreAsync(
                target,
                workflowId,
                operationId,
                CdcWorkflowEffect.RegisterConnector,
                [],
                [registration],
                cancellationToken
            );

        private Task<CdcWorkflowJournal> RecordIntentCoreAsync(
            CdcTargetIdentity target,
            Guid workflowId,
            Guid operationId,
            CdcWorkflowEffect effect,
            ImmutableArray<CdcRecordSizeIncreaseJournal> recordSizeIncrease,
            ImmutableArray<CdcConnectorRegistrationIntent> registration,
            CancellationToken cancellationToken
        ) =>
            RunAsync(
                async () =>
                {
                    CdcWorkflowJournal journal = await ReadOwnedAsync(target, workflowId, cancellationToken);
                    CdcWorkflowOperation operation = new(
                        operationId,
                        effect,
                        _store.Now(),
                        recordSizeIncrease,
                        []
                    )
                    {
                        ConnectorRegistration = registration,
                    };
                    CdcWorkflowJournal next = journal with { Operations = journal.Operations.Add(operation) };
                    Validate(next, _store.Now());
                    if (CanExposeSource(effect))
                    {
                        // Exposure is durable before intent can authorize any downstream side effect.
                        await _store.AdvanceSourceHistoryAsync(
                            journal,
                            DocumentCacheDownstreamPublicationStatus.Possible,
                            cancellationToken
                        );
                    }
                    await _store.WriteAsync(next, create: false, cancellationToken);
                    return next;
                },
                cancellationToken
            );

        /// <summary>
        /// Calls the authoritative inspector even when completion was already journaled. Absent/unavailable
        /// evidence does not complete an operation. This receipt is historical, never current readiness.
        /// CREATE DATABASE inspectors must retain the actual creation outcome, not infer it from existence.
        /// </summary>
        public Task<CdcWorkflowJournal> ReconcileCompletionAsync(
            CdcTargetIdentity target,
            Guid workflowId,
            Guid operationId,
            Func<
                CdcWorkflowOperation,
                CancellationToken,
                Task<CdcTransportResult<CdcWorkflowCompletion>>
            > inspect,
            CancellationToken cancellationToken
        ) =>
            RunAsync(
                async () =>
                {
                    CdcWorkflowJournal journal = await ReadOwnedAsync(target, workflowId, cancellationToken);
                    int index = FindOperation(journal, operationId);
                    CdcWorkflowOperation operation = journal.Operations[index];
                    CdcTransportResult<CdcWorkflowCompletion> result;
                    try
                    {
                        result = await inspect(operation, cancellationToken);
                    }
                    catch (Exception exception)
                        when (exception is not (OperationCanceledException or CdcWorkflowStateException))
                    {
                        throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Unavailable);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result is not CdcTransportResult<CdcWorkflowCompletion>.Observed observed)
                    {
                        throw new CdcWorkflowStateException(
                            result is CdcTransportResult<CdcWorkflowCompletion>.Absent
                                ? CdcWorkflowStateFailure.Contradictory
                                : CdcWorkflowStateFailure.Unavailable
                        );
                    }
                    if (!operation.Completions.IsEmpty)
                    {
                        Require(
                            JsonSerializer.Serialize(operation.Completions[0].Evidence, _json)
                                == JsonSerializer.Serialize(observed.Value, _json),
                            CdcWorkflowStateFailure.Contradictory
                        );
                        return journal;
                    }
                    CdcWorkflowJournal next = journal with
                    {
                        Operations = journal.Operations.SetItem(
                            index,
                            operation with
                            {
                                Completions = [new(_store.Now(), observed.Value)],
                            }
                        ),
                    };
                    Validate(next, _store.Now());
                    if (
                        operation.Effect == CdcWorkflowEffect.AssociateSource
                        && CreationReceipt(next).Outcome == CdcDatabaseCreationOutcome.Created
                    )
                    {
                        // Create-only attestation belongs to the original source association, never a retry
                        // of completed provisioning. If either write is lost, the pair fails closed.
                        await _store.CreateSourceHistoryAsync(next, cancellationToken);
                    }
                    // Governed never-reserved cleanup verifies absence without changing source exposure.
                    // Legacy generic retirement keeps its existing conservative historical transition.
                    else if (
                        operation.Effect == CdcWorkflowEffect.EstablishConnector
                        || operation.Effect == CdcWorkflowEffect.Retire
                            && (
                                operation.Retirement.IsEmpty
                                || journal.Operations.Any(o => CanExposeSource(o.Effect))
                            )
                    )
                    {
                        await _store.AdvanceSourceHistoryAsync(
                            journal,
                            operation.Effect == CdcWorkflowEffect.Retire
                                ? DocumentCacheDownstreamPublicationStatus.Historical
                                : DocumentCacheDownstreamPublicationStatus.Active,
                            cancellationToken
                        );
                    }
                    await _store.WriteAsync(next, create: false, cancellationToken);
                    return next;
                },
                cancellationToken
            );

        public Task<CdcWorkflowJournal> AppendAcknowledgementAsync(
            CdcTargetIdentity target,
            Guid workflowId,
            Guid operationId,
            CdcRecordSizeAcknowledgement acknowledgement,
            CancellationToken cancellationToken
        ) =>
            RunAsync(
                async () =>
                {
                    CdcWorkflowJournal journal = await ReadOwnedAsync(target, workflowId, cancellationToken);
                    int index = FindOperation(journal, operationId);
                    CdcWorkflowOperation operation = journal.Operations[index];
                    Require(
                        operation.Effect == CdcWorkflowEffect.IncreaseRecordSize
                            && operation.Completions.IsEmpty
                    );
                    CdcRecordSizeIncreaseJournal increase = operation.RecordSizeIncrease.Single();
                    CdcWorkflowJournal next = journal with
                    {
                        Operations = journal.Operations.SetItem(
                            index,
                            operation with
                            {
                                RecordSizeIncrease =
                                [
                                    increase with
                                    {
                                        Acknowledgements = increase.Acknowledgements.Add(acknowledgement),
                                    },
                                ],
                            }
                        ),
                    };
                    await _store.WriteAsync(next, create: false, cancellationToken);
                    return next;
                },
                cancellationToken
            );

        private async Task<CdcWorkflowJournal> ReadOwnedAsync(
            CdcTargetIdentity target,
            Guid workflowId,
            CancellationToken cancellationToken
        )
        {
            CdcWorkflowJournal journal = await _store.ReadAsync(target, cancellationToken);
            Require(journal.WorkflowId == workflowId, CdcWorkflowStateFailure.Contradictory);
            return journal;
        }

        private static int FindOperation(CdcWorkflowJournal journal, Guid operationId)
        {
            int index = Array.FindIndex(
                journal.Operations.ToArray(),
                operation => operation.OperationId == operationId
            );
            Require(index >= 0, CdcWorkflowStateFailure.Contradictory);
            return index;
        }

        private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return await action();
            }
            catch (Exception exception) when (IsStorageException(exception))
            {
                throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Unavailable);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (!_disposed)
                {
                    _disposed = true;
                    await _lock.DisposeAsync();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task<CdcWorkflowJournal> ReadAsync(
        CdcTargetIdentity target,
        CancellationToken cancellationToken
    )
    {
        string path = JournalPath(target, createDirectories: false);
        try
        {
            string payload = await File.ReadAllTextAsync(path, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(payload);
            ValidateUniqueProperties(document.RootElement);
            Require(
                document.RootElement.GetProperty("version").GetInt32() == CdcWorkflowJournal.CurrentVersion,
                CdcWorkflowStateFailure.UnsupportedVersion
            );
            CdcWorkflowJournal journal =
                JsonSerializer.Deserialize<CdcWorkflowJournal>(payload, _json)
                ?? throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Invalid);
            Validate(journal, Now());
            Require(journal.Target == target, CdcWorkflowStateFailure.Contradictory);
            return journal;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Missing);
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or ArgumentException
                        or InvalidOperationException
                        or KeyNotFoundException
            )
        {
            throw new CdcWorkflowStateException(CdcWorkflowStateFailure.Invalid);
        }
    }

    private async Task WriteAsync(
        CdcWorkflowJournal journal,
        bool create,
        CancellationToken cancellationToken
    )
    {
        Validate(journal, Now());
        string path = JournalPath(journal.Target, createDirectories: true);
        await WritePayloadAsync(path, JsonSerializer.Serialize(journal, _json), create, cancellationToken);
    }

    private async Task WritePayloadAsync(
        string path,
        string payload,
        bool create,
        CancellationToken cancellationToken
    )
    {
        Require(create ? !File.Exists(path) : File.Exists(path), CdcWorkflowStateFailure.Contradictory);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            _onWrite(CdcWorkflowWriteBoundary.BeforeTemporaryWrite);
            await CdcLocalStateStoreFileSystem.Current.WriteAllTextCreateNewFlushAsync(
                temporary,
                payload,
                FileOptions(FileMode.CreateNew, FileShare.None),
                cancellationToken
            );
            _onWrite(CdcWorkflowWriteBoundary.AfterTemporaryFlush);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEntry(path, file: true);
            File.Move(temporary, path, overwrite: !create);
            FlushDirectory(Path.GetDirectoryName(path)!);
            _onWrite(CdcWorkflowWriteBoundary.AfterAtomicReplacement);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string JournalPath(CdcTargetIdentity target, bool createDirectories)
    {
        Require(target is not null && Enum.IsDefined(target.Provider));
        CdcStateStorePathResolution resolved = _paths.ResolveBindingPath(
            CdcBindingIdentity.FromTargetIdentity(target!)
        );
        Require(resolved.Succeeded);
        string path = _paths.RootPath;
        foreach (string segment in new[] { "workflows", resolved.DeploymentKey!, resolved.InstanceKey! })
        {
            if (createDirectories)
            {
                EnsureDirectory(path);
            }
            else
            {
                ValidateEntry(path, file: false);
            }
            path = Path.Combine(path, segment);
        }
        if (createDirectories)
        {
            EnsureDirectory(path);
        }
        else
        {
            ValidateEntry(path, file: false);
        }
        path = Path.Combine(path, $"{resolved.Generation}.json");
        ValidateEntry(path, file: true);
        return path;
    }

    private static void EnsureDirectory(string path)
    {
        // Reject symlink ancestors too: different spellings must not create different controller locks.
        for (DirectoryInfo directory = new(path); directory is not null; directory = directory.Parent!)
        {
            Require(
                directory.LinkTarget is null
                    && (!directory.Exists || !directory.Attributes.HasFlag(FileAttributes.ReparsePoint)),
                CdcWorkflowStateFailure.Unavailable
            );
        }
        ValidateEntry(path, file: false);
        if (!Directory.Exists(path))
        {
            Require(!OperatingSystem.IsWindows(), CdcWorkflowStateFailure.Unavailable);
            string parent = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent))
            {
                // Persist every newly created ancestor, not only the final directory entry.
                EnsureDirectory(parent);
            }
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path, CdcLocalStateStoreUnixModes.OwnerOnlyDirectory);
            }
            FlushDirectory(Path.GetDirectoryName(path)!);
        }
        Require(
            _permissions.ValidateDirectoryNotSharedWritable(path).Succeeded,
            CdcWorkflowStateFailure.Unavailable
        );
    }

    private static void ValidateEntry(string path, bool file)
    {
        string parent = Path.GetDirectoryName(path)!;
        if (Directory.Exists(parent))
        {
            Require(
                !Directory
                    .EnumerateFileSystemEntries(parent)
                    .Any(entry =>
                        string.Equals(
                            Path.GetFileName(entry),
                            Path.GetFileName(path),
                            StringComparison.OrdinalIgnoreCase
                        )
                        && !string.Equals(
                            Path.GetFileName(entry),
                            Path.GetFileName(path),
                            StringComparison.Ordinal
                        )
                    ),
                CdcWorkflowStateFailure.Unavailable
            );
        }
        FileSystemInfo info = file ? new FileInfo(path) : new DirectoryInfo(path);
        Require(
            info.LinkTarget is null
                && (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint)),
            CdcWorkflowStateFailure.Unavailable
        );
        Require(file ? !Directory.Exists(path) : !File.Exists(path), CdcWorkflowStateFailure.Unavailable);
        if (info.Exists)
        {
            Require(
                (
                    file
                        ? _permissions.ValidateOwnerOnlyFile(path)
                        : _permissions.ValidateDirectoryNotSharedWritable(path)
                ).Succeeded,
                CdcWorkflowStateFailure.Unavailable
            );
        }
    }

    private static FileStreamOptions FileOptions(FileMode mode, FileShare share)
    {
        FileStreamOptions options = new()
        {
            Mode = mode,
            Access = FileAccess.ReadWrite,
            Share = share,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = CdcLocalStateStoreUnixModes.OwnerOnlyFile;
        }
        return options;
    }

    private DateTimeOffset Now() => _time.GetUtcNow().ToUniversalTime();

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Require(names.Add(property.Name));
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateUniqueProperties(item);
            }
        }
    }

    // A flushed temporary file plus rename is atomic; syncing the parent makes the renamed entry durable.
    internal static void FlushDirectory(string path)
    {
        Require(!OperatingSystem.IsWindows(), CdcWorkflowStateFailure.Unavailable);
        int descriptor = OpenDirectory(path, 0);
        Require(descriptor >= 0, CdcWorkflowStateFailure.Unavailable);
        try
        {
            Require(SyncDirectory(descriptor) == 0, CdcWorkflowStateFailure.Unavailable);
        }
        finally
        {
            CloseDirectory(descriptor);
        }
    }

#pragma warning disable SYSLIB1054 // Three small Unix-only calls; do not enable unsafe compilation for the library.
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi)]
    private static extern int OpenDirectory(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync")]
    private static extern int SyncDirectory(int descriptor);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseDirectory(int descriptor);
#pragma warning restore SYSLIB1054

    private sealed class SafeNameConverter : JsonConverter<CdcSafeName>
    {
        public override CdcSafeName Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        ) => new(reader.GetString() ?? throw new JsonException());

        public override void Write(Utf8JsonWriter writer, CdcSafeName value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}

internal enum CdcWorkflowWriteBoundary
{
    BeforeTemporaryWrite,
    AfterTemporaryFlush,
    AfterAtomicReplacement,
}
