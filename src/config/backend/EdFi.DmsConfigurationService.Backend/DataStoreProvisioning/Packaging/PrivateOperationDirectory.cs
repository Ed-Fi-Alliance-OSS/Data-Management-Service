// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

/// <summary>
/// The per-operation private directory of D-5: <c>&lt;root&gt;/dms-provision-&lt;op&gt;</c>, created for this operation
/// only (an existing directory of that name is refused, never reused) with Unix mode <c>0700</c>, holding files
/// this type creates with <c>CreateNew</c> semantics and mode <c>0600</c>. File names are chosen by the callers
/// from fixed constants, never from a package or a request, so no path inside the directory is attacker-chosen.
/// On Windows the directory inherits the root's access control; the mode calls are Unix-only. Disposal deletes
/// the directory and everything in it on a best-effort basis; <see cref="TryDelete"/> reports whether the
/// deletion succeeded so bounded cleanup (D-19) can record <c>OperationDirectory</c> residue.
/// </summary>
public sealed class PrivateOperationDirectory : IDisposable
{
    public const string NamePrefix = "dms-provision-";

    /// <summary>Owner read, write, and traverse only.</summary>
    public const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Owner read and write only.</summary>
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private bool _deleted;

    private PrivateOperationDirectory(string path, DmsOperationId operationId)
    {
        Path = path;
        OperationId = operationId;
    }

    /// <summary>The absolute path of the directory.</summary>
    public string Path { get; }

    public DmsOperationId OperationId { get; }

    /// <summary>
    /// Creates <c>&lt;rootDirectory&gt;/dms-provision-&lt;op&gt;</c>. The root must be a fully qualified path to an existing
    /// directory; a directory already present under the generated name is refused with
    /// <see cref="IOException"/> and left untouched.
    /// </summary>
    public static PrivateOperationDirectory Create(string rootDirectory, DmsOperationId operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!System.IO.Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException(
                "The operation directory root must be a fully qualified path.",
                nameof(rootDirectory)
            );
        }

        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException("The operation directory root does not exist.");
        }

        string path = System.IO.Path.Combine(rootDirectory, NamePrefix + operationId.Value);
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new IOException(
                "A file system entry already exists under the generated operation directory name."
            );
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            // The create mode is subject to the process umask, which can only remove bits; the explicit set
            // afterwards makes the mode exact regardless of umask.
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }

        return new PrivateOperationDirectory(path, operationId);
    }

    /// <summary>The absolute path a file named <paramref name="fileName"/> has inside this directory.</summary>
    public string PathOf(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return System.IO.Path.Combine(Path, fileName);
    }

    /// <summary>
    /// Creates a new file for writing with mode <c>0600</c>, refusing an existing entry of that name
    /// (<see cref="System.IO.FileMode.CreateNew"/>) and sharing with no other handle.
    /// </summary>
    public FileStream CreateFile(string fileName)
    {
        ThrowIfDeleted();
        string path = PathOf(fileName);
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        FileStream stream = new(path, options);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stream.SafeFileHandle, PrivateFileMode);
        }

        return stream;
    }

    /// <summary>Deletes the directory and its contents; true when it is gone afterwards (including when it was already absent).</summary>
    public bool TryDelete()
    {
        if (_deleted)
        {
            return true;
        }

        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        _deleted = !Directory.Exists(Path);
        return _deleted;
    }

    public void Dispose() => TryDelete();

    private void ThrowIfDeleted()
    {
        if (_deleted)
        {
            throw new ObjectDisposedException(nameof(PrivateOperationDirectory));
        }
    }
}
