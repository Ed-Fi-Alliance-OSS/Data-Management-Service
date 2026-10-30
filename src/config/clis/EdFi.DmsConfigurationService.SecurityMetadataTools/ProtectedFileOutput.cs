// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.SecurityMetadataTools;

/// <summary>
/// Publishes an already processed, validated, and serialized document without exposing a partial file.
/// The destination's parent must exist; no directories are created.
/// </summary>
internal static class ProtectedFileOutput
{
    internal static void Write(string outputPath, string content, bool force) =>
        Write(outputPath, content, force, new PhysicalFileOperations());

    internal static void Write(string outputPath, string content, bool force, IProtectedFileOperations files)
    {
        string destination = Path.GetFullPath(outputPath);
        if (files.Exists(destination) && !force)
        {
            throw new IOException("Output file already exists. Use --force to replace it.");
        }

        // Same-directory staging keeps replacement on the same filesystem.
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".security-metadata-tools-{Guid.NewGuid():N}.tmp"
        );
        // Only clean up a file we successfully created, never an existing file at a colliding path.
        Stream stream = files.CreateNew(temporaryPath);
        try
        {
            using (stream)
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
            }

            // Enforce no-overwrite again if a destination appeared after the existence check.
            files.Move(temporaryPath, destination, overwrite: force);
        }
        finally
        {
            files.Delete(temporaryPath);
        }
    }

    private sealed class PhysicalFileOperations : IProtectedFileOperations
    {
        public bool Exists(string path) => File.Exists(path);

        public Stream CreateNew(string path) => new FileStream(path, FileMode.CreateNew, FileAccess.Write);

        public void Move(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);

        public void Delete(string path) => File.Delete(path);
    }
}

/// <summary>Internal seam for deterministic staging and replacement failures.</summary>
internal interface IProtectedFileOperations
{
    bool Exists(string path);
    Stream CreateNew(string path);
    void Move(string source, string destination, bool overwrite);
    void Delete(string path);
}
