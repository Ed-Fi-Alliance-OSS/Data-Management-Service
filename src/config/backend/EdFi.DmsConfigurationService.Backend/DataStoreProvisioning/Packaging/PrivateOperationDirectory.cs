// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

/// <summary>
/// The per-operation private directory of D-5: <c>&lt;root&gt;/dms-provision-&lt;op&gt;</c>, claimed for this operation
/// only, with Unix mode <c>0700</c>, holding files this type creates with <c>CreateNew</c> semantics and mode
/// <c>0600</c>. The claim is the native create call itself (<c>mkdir</c>, <c>CreateDirectoryW</c>), which fails
/// when any entry of that name already exists, so an instance is returned only for a directory this call
/// created: a losing creator never writes into, changes the mode of, or deletes an entry it did not create.
/// <see cref="System.IO.Directory.CreateDirectory(string)"/> is not used because it succeeds over an existing
/// directory. File names are chosen by the callers from fixed constants, never from a package or a request, so no
/// path inside the directory is attacker-chosen. On Windows the native create carries a protected access control
/// list that allows only the current user and is inherited by every file created inside, so the root's
/// permissions never reach the directory or a password file in it; the mode calls are Unix-only. Disposal
/// deletes the directory and everything in it on a best-effort basis;
/// <see cref="TryDelete"/> reports whether the deletion succeeded so bounded cleanup (D-19) can record
/// <c>OperationDirectory</c> residue.
/// </summary>
public sealed class PrivateOperationDirectory : IDisposable
{
    public const string NamePrefix = "dms-provision-";

    /// <summary>
    /// The name of the only credential an operation ever writes into its directory: the replay's
    /// <c>libpq</c> password file (D-10). Cleanup recognizes it by this name and reports it as
    /// <c>CredentialFile</c> residue at Critical (D-19).
    /// </summary>
    public const string CredentialFileName = "pgpass";

    /// <summary>Owner read, write, and traverse only.</summary>
    public const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Owner read and write only.</summary>
    public const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const int Win32ErrorAccessDenied = 5;
    private const int Win32ErrorPathNotFound = 3;
    private const int Win32ErrorAlreadyExists = 183;
    private const int ErrnoPermissionDenied = 1;
    private const int ErrnoNoSuchEntry = 2;
    private const int ErrnoAccessDenied = 13;
    private const int ErrnoExists = 17;

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
    /// Claims <c>&lt;rootDirectory&gt;/dms-provision-&lt;op&gt;</c>. The root must be a fully qualified path to an existing
    /// directory; an entry already present under the generated name, whatever its kind or contents, is refused
    /// with <see cref="IOException"/> and left untouched. On Unix a root that would let another identity replace the
    /// directory after the claim is refused first with <see cref="UnsafeOperationRootException"/>
    /// (<see cref="OperationRootSafety"/>), and nothing is created.
    /// Once the native claim succeeds the directory is this call's own: when securing it then fails, the directory
    /// is deleted on a best-effort basis and <see cref="OperationDirectorySetupException"/> carries the owner, so
    /// the caller can report whatever remains.
    /// </summary>
    public static PrivateOperationDirectory Create(string rootDirectory, DmsOperationId operationId) =>
        Create(rootDirectory, operationId, beforeClaim: null, afterClaim: null);

    /// <summary>
    /// Test seam: <paramref name="beforeClaim"/> runs after the root checks and immediately before the claim,
    /// the point where a competing creator may insert the entry; <paramref name="afterClaim"/> runs after a
    /// successful claim and before the directory is secured, where a test can make securing it fail.
    /// </summary>
    internal static PrivateOperationDirectory Create(
        string rootDirectory,
        DmsOperationId operationId,
        Action? beforeClaim,
        Action<string>? afterClaim = null
    )
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

        if (!OperatingSystem.IsWindows())
        {
            // The lexical form is checked and claimed under, so no "." or ".." segment can reach another directory.
            rootDirectory = System.IO.Path.GetFullPath(rootDirectory);
            OperationRootSafety.RequireSafe(rootDirectory);
        }

        string path = System.IO.Path.Combine(rootDirectory, NamePrefix + operationId.Value);
        beforeClaim?.Invoke();

        if (OperatingSystem.IsWindows())
        {
            ClaimOnWindows(path);
        }
        else
        {
            ClaimOnUnix(path);
        }

        // The claim succeeded, so the directory is this operation's own from here on: a later failure deletes it
        // and never loses it.
        PrivateOperationDirectory owned = new(path, operationId);
        try
        {
            afterClaim?.Invoke(path);
            if (!OperatingSystem.IsWindows())
            {
                // The create mode is subject to the process umask, which can only remove bits; the explicit set
                // makes the mode exact regardless of umask.
                File.SetUnixFileMode(path, PrivateDirectoryMode);
            }
        }
        catch (Exception exception)
        {
            owned.TryDelete();
            if (exception is IOException or UnauthorizedAccessException)
            {
                throw new OperationDirectorySetupException(owned, exception);
            }

            throw;
        }

        return owned;
    }

    /// <summary>
    /// Attaches to the directory this operation already claimed, for a component that writes into it without
    /// owning it (the replay runner's password file): <paramref name="path"/> must be fully qualified, exist,
    /// and be named <c>dms-provision-&lt;op&gt;</c> for <paramref name="operationId"/>. Nothing is created or
    /// changed. The owner alone decides when the directory is deleted; an attached instance is not disposed.
    /// </summary>
    public static PrivateOperationDirectory Attach(string path, DmsOperationId operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "The operation directory path must be fully qualified.",
                nameof(path)
            );
        }

        string expectedName = NamePrefix + operationId.Value;
        if (!string.Equals(System.IO.Path.GetFileName(path), expectedName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The directory is not the operation's private directory.",
                nameof(path)
            );
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("The operation directory does not exist.");
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
    /// When securing a file this call created fails, its handle is disposed before the exception leaves and the
    /// file is deleted on a best-effort basis; what remains stays inside this directory, whose owner reports it.
    /// </summary>
    public FileStream CreateFile(string fileName) => CreateFile(fileName, afterCreate: null);

    /// <summary>
    /// Test seam: <paramref name="afterCreate"/> runs after the file is created and before it is secured, where a
    /// test can make securing it fail.
    /// </summary>
    internal FileStream CreateFile(string fileName, Action<FileStream>? afterCreate)
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

        // CreateNew succeeded, so the file is this call's own: a refused name never reaches here.
        try
        {
            afterCreate?.Invoke(stream);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stream.SafeFileHandle, PrivateFileMode);
            }
        }
        catch
        {
            stream.Dispose();
            TryDeleteFile(path);
            throw;
        }

        return stream;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the directory's owner, whose release deletes or reports it.
        }
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

    /// <summary>
    /// The claim on Windows: the create itself applies <see cref="PrivateDirectorySecurity"/>, so there is no moment
    /// at which the directory, or a file later created in it, carries the root's inheritable permissions. An
    /// existing entry fails the create and is never touched.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void ClaimOnWindows(string path)
    {
        byte[] descriptor = PrivateDirectorySecurity().GetSecurityDescriptorBinaryForm();
        GCHandle pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        int error;
        try
        {
            SecurityAttributes attributes = new()
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            if (CreateDirectoryW(path, ref attributes))
            {
                return;
            }

            error = Marshal.GetLastPInvokeError();
        }
        finally
        {
            pinned.Free();
        }

        throw error switch
        {
            Win32ErrorAlreadyExists => AlreadyExists(),
            Win32ErrorPathNotFound => new DirectoryNotFoundException(
                "The operation directory root does not exist."
            ),
            Win32ErrorAccessDenied => new UnauthorizedAccessException(
                "Access to the operation directory root is denied."
            ),
            _ => new IOException($"Creating the operation directory failed with Win32 error {error}."),
        };
    }

    /// <summary>
    /// A protected list (nothing inherited from the root) with one entry: full control for the current user,
    /// inherited by the files and directories created inside. The Windows counterpart of modes 0700 and 0600.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static DirectorySecurity PrivateDirectorySecurity()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user =
            identity.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(
            new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow
            )
        );
        return security;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    private static void ClaimOnUnix(string path)
    {
        if (MakeDirectory(path, (uint)PrivateDirectoryMode) == 0)
        {
            return;
        }

        int errno = Marshal.GetLastPInvokeError();
        throw errno switch
        {
            ErrnoExists => AlreadyExists(),
            ErrnoNoSuchEntry => new DirectoryNotFoundException(
                "The operation directory root does not exist."
            ),
            ErrnoAccessDenied or ErrnoPermissionDenied => new UnauthorizedAccessException(
                "Access to the operation directory root is denied."
            ),
            _ => new IOException($"Creating the operation directory failed with errno {errno}."),
        };
    }

    private static IOException AlreadyExists() =>
        new("A file system entry already exists under the generated operation directory name.");

#pragma warning disable SYSLIB1054 // Two small calls; do not enable unsafe compilation for the library.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(
        string lpPathName,
        ref SecurityAttributes lpSecurityAttributes
    );

    [DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
    private static extern int MakeDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
#pragma warning restore SYSLIB1054
}

/// <summary>
/// The claim of <see cref="PrivateOperationDirectory.Create(string, DmsOperationId)"/> succeeded but securing the
/// directory failed. Deletion was already attempted; <see cref="Directory"/> is the owner of what may remain, for the
/// caller's cleanup reporting. The inner exception is the original failure.
/// </summary>
public sealed class OperationDirectorySetupException(
    PrivateOperationDirectory directory,
    Exception innerException
) : IOException("Securing the claimed operation directory failed.", innerException)
{
    public PrivateOperationDirectory Directory { get; } = directory;
}
