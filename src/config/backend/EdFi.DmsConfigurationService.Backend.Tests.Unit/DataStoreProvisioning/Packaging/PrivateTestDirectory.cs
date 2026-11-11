// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning.Packaging;

/// <summary>
/// Creates a test directory that can hold an operation root, or be one, under the production rule whatever the
/// process <c>umask</c>: on Unix every directory this call creates, the intermediate ones included, is made <c>0700</c>,
/// so a <c>umask</c> of <c>0002</c> cannot leave a group-writable directory on the path. A directory that already existed
/// is left as it is; the workspaces create theirs under the system temp directory, whose sticky mode the rule accepts.
/// </summary>
public static class PrivateTestDirectory
{
    private const UnixFileMode Private =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates <paramref name="path"/> and any missing parent, each private to this user; returns the full path.</summary>
    public static string Create(string path)
    {
        string full = Path.GetFullPath(path);
        List<string> missing = [];
        for (
            string? current = full;
            current is not null && !Directory.Exists(current);
            current = Path.GetDirectoryName(current)
        )
        {
            missing.Add(current);
        }

        Directory.CreateDirectory(full);
        if (!OperatingSystem.IsWindows())
        {
            foreach (string created in missing)
            {
                File.SetUnixFileMode(created, Private);
            }
        }

        return full;
    }
}
