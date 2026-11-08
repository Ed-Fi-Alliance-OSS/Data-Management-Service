// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Mssql.DataStoreProvisioning;

/// <summary>
/// Paths as the database server sees them, which need not follow the CMS host's convention: SQL Server on Linux
/// answers <c>/var/opt/mssql/data/</c>, on Windows <c>C:\…\DATA\</c> or a UNC share. A path that starts with
/// <c>/</c> is joined with <c>/</c>; any other absolute path with <c>\</c>. The absolute rule is the options
/// validator's <c>IsAbsoluteOnEitherConvention</c>.
/// </summary>
internal static class MssqlServerPaths
{
    public static bool IsAbsolute(string path) =>
        path.StartsWith('/')
        || path.StartsWith(@"\\", StringComparison.Ordinal)
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/');

    /// <summary><paramref name="directory"/> without its trailing separators, the convention's separator, then <paramref name="fileName"/>.</summary>
    public static string Join(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        char separator = directory.StartsWith('/') ? '/' : '\\';
        return directory.TrimEnd('/', '\\') + separator + fileName;
    }
}
