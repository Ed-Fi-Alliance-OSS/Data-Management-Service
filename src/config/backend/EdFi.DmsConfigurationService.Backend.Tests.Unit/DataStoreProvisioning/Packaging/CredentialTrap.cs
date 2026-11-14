// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// Makes the password file in an operation directory undeletable, the way each platform enforces it: on
/// Windows a handle open without delete sharing on the file itself; on Unix the directory's write permission
/// removed, so no entry in it can be unlinked. The file is written first when it does not exist yet.
/// Disposal lifts the trap and removes the directory, so nothing outlives the test.
/// </summary>
internal sealed class CredentialTrap : IDisposable
{
    private readonly string _directoryPath;
    private readonly FileStream? _heldHandle;

    public CredentialTrap(string directoryPath)
    {
        _directoryPath = directoryPath;
        FilePath = Path.Combine(directoryPath, PrivateOperationDirectory.CredentialFileName);
        if (!File.Exists(FilePath))
        {
            File.WriteAllText(FilePath, "*:*:*:*:not-a-real-password\n");
        }

        if (OperatingSystem.IsWindows())
        {
            _heldHandle = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        else
        {
            File.SetUnixFileMode(_directoryPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    public string FilePath { get; }

    public void Dispose()
    {
        if (_heldHandle is not null)
        {
            _heldHandle.Dispose();
        }
        else if (!OperatingSystem.IsWindows() && Directory.Exists(_directoryPath))
        {
            File.SetUnixFileMode(_directoryPath, PrivateOperationDirectory.PrivateDirectoryMode);
        }

        try
        {
            if (Directory.Exists(_directoryPath))
            {
                Directory.Delete(_directoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // The temp path is reclaimed later; the assertions were made before disposal.
        }
    }
}
