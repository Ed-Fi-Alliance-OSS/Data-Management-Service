// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.Packaging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Catalog;

/// <summary>
/// Makes the recursive deletion of an operation directory fail deterministically, the way the file system
/// enforces it on each platform: on Windows an open handle with no sharing on a file inside the directory; on
/// Unix the directory's own write permission removed, so no entry in it can be unlinked. Disposal lifts the
/// trap so the workspace can delete the directory afterwards.
/// </summary>
internal sealed class DeletionTrap : IDisposable
{
    private readonly string _directoryPath;
    private readonly FileStream? _heldHandle;

    public DeletionTrap(string directoryPath)
    {
        _directoryPath = directoryPath;
        if (OperatingSystem.IsWindows())
        {
            _heldHandle = new FileStream(
                Path.Combine(_directoryPath, "deletion-trap"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None
            );
        }
        else
        {
            File.SetUnixFileMode(_directoryPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

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
    }
}
