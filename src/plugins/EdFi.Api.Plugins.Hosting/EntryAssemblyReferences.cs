// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Reads an entry assembly's assembly references from metadata, without loading it.
/// </summary>
/// <remarks>
/// The contract-skew check has to run before any type is loaded, so that a plugin built against a newer
/// contract than the host is refused with a message naming both versions rather than surfacing later as
/// a type-load error from somewhere an operator cannot act on. Reading metadata is what makes that
/// ordering possible.
/// </remarks>
internal static class EntryAssemblyReferences
{
    /// <summary>Reads the simple name and version of every assembly the entry assembly references.</summary>
    /// <exception cref="PluginLoadException">The file is not a readable managed assembly.</exception>
    internal static IReadOnlyList<(string Name, Version Version)> Read(
        string pluginName,
        string entryAssemblyPath
    )
    {
        try
        {
            using FileStream stream = File.OpenRead(entryAssemblyPath);
            using PEReader peReader = new(stream);

            if (!peReader.HasMetadata)
            {
                throw Unreadable(pluginName, entryAssemblyPath, innerException: null);
            }

            MetadataReader metadata = peReader.GetMetadataReader();
            List<(string, Version)> references = [];

            foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            {
                AssemblyReference reference = metadata.GetAssemblyReference(handle);
                references.Add((metadata.GetString(reference.Name), reference.Version));
            }

            return references;
        }
        catch (Exception exception)
            when (exception
                    is BadImageFormatException
                        or InvalidOperationException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            throw Unreadable(pluginName, entryAssemblyPath, exception);
        }
    }

    private static PluginLoadException Unreadable(
        string pluginName,
        string entryAssemblyPath,
        Exception? innerException
    ) =>
        new(
            PluginLoadFailure.EntryAssemblyUnreadable,
            pluginName,
            $"plugin '{PluginDiagnosticText.Quote(pluginName)}' has an entry assembly at "
                + $"'{PluginDiagnosticText.Quote(entryAssemblyPath)}' whose metadata could not be read. "
                + "It has to be the managed assembly a dotnet publish of the plugin produced.",
            innerException
        );
}
