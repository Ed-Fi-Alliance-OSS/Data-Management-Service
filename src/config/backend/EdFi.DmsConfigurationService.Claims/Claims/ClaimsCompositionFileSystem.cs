// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Claims.Composition;

/// <summary>Internal seam for deterministic discovery and read failures.</summary>
internal interface IClaimsCompositionFileSystem
{
    bool DirectoryExists(string path);
    string[] GetFiles(string path, string pattern);
    string ReadAllText(string path);
}

internal sealed class ClaimsCompositionFileSystem : IClaimsCompositionFileSystem
{
    public bool DirectoryExists(string path)
    {
        try
        {
            // Directory.Exists suppresses access errors, making unreadable input look missing.
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public string[] GetFiles(string path, string pattern) =>
        Directory.GetFiles(path, pattern, SearchOption.AllDirectories);

    public string ReadAllText(string path) => File.ReadAllText(path);
}
