// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace EdFi.Api.Plugins.Hosting;

/// <summary>
/// Loads the plugins an operator allowlisted, one isolated load context each.
/// </summary>
/// <remarks>
/// <para>
/// The loader is host-agnostic. It takes an <see cref="IConfiguration"/> and the assembly names of the
/// contracts the caller declares, returns the plugin instances in allowlist order, writes its own
/// outcomes to <see cref="Console.Error"/>, and throws <see cref="PluginLoadException"/> on any fatal.
/// It knows nothing about either host's startup, its logging, or the hooks it will later invoke.
/// </para>
/// <para>
/// An allowlisted plugin that does not load is fatal. Skip-and-continue would degrade a broken plugin
/// to silently absent, and for the plugin types this exists for, absence is invisible: what goes quiet
/// is correctness or security posture rather than a capability an operator can see.
/// </para>
/// </remarks>
public static class PluginLoader
{
    /// <summary>
    /// Loads every plugin named in <c>Plugins:Allowed</c>, in order.
    /// </summary>
    /// <param name="configuration">The host's configuration, which the <c>Plugins</c> section is read from.</param>
    /// <param name="contractAssemblyNames">
    /// The simple <em>assembly</em> names of the contracts this host declares, which the entry
    /// assembly's references are checked against. They are assembly names and never package ids: an
    /// assembly reference carries a simple assembly name, so a package id in this set matches nothing
    /// and silently checks nothing.
    /// </param>
    /// <exception cref="PluginLoadException">Any allowlisted plugin could not be loaded.</exception>
    public static LoadedPlugins Load(
        IConfiguration configuration,
        IReadOnlyCollection<string> contractAssemblyNames
    ) => Load(configuration, contractAssemblyNames, Console.Error);

    /// <summary>
    /// The overload the public one calls with <see cref="Console.Error"/>, so that a test can read the
    /// channel without redirecting the process's own.
    /// </summary>
    internal static LoadedPlugins Load(
        IConfiguration configuration,
        IReadOnlyCollection<string> contractAssemblyNames,
        TextWriter diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contractAssemblyNames);
        ArgumentNullException.ThrowIfNull(diagnostics);

        PluginsConfiguration plugins = Bind(configuration, diagnostics);

        // No name survived the allowlist, so nothing is read and nothing is written. This is the
        // shipped default and the reason a plugin-free deployment boots exactly as it did before.
        if (!plugins.TryGetResolvedRoot(out string? configuredRoot))
        {
            return LoadedPlugins.Empty;
        }

        // Existence before resolution. ResolveLinkTarget throws for a path that does not exist, so
        // resolving first would turn a missing root into a raw DirectoryNotFoundException instead of
        // the fatal that names the path an operator has to create.
        if (!Directory.Exists(configuredRoot))
        {
            throw Report(
                diagnostics,
                pluginName: null,
                new PluginLoadException(
                    PluginLoadFailure.PluginRootMissing,
                    null,
                    $"the plugin root '{PluginDiagnosticText.Quote(configuredRoot)}' does not exist, "
                        + $"and Plugins:Allowed asks for {plugins.AllowedNames.Count} plugin(s). "
                        + "Either create the root and place the plugins in it, or clear Plugins:Allowed."
                )
            );
        }

        string resolvedRoot = ResolveDirectory(configuredRoot, pluginName: null, diagnostics);

        List<LoadedPlugin> loaded = [];

        foreach (string name in plugins.AllowedNames)
        {
            loaded.Add(LoadPlugin(name, resolvedRoot, contractAssemblyNames, diagnostics));
        }

        ReportDirectoriesNobodyAskedFor(resolvedRoot, plugins.AllowedNames, diagnostics);

        return new LoadedPlugins(loaded);
    }

    private static PluginsConfiguration Bind(IConfiguration configuration, TextWriter diagnostics)
    {
        try
        {
            return PluginsConfigurationBinder.Bind(configuration);
        }
        catch (PluginLoadException exception)
        {
            throw Report(diagnostics, pluginName: null, exception);
        }
    }

    private static LoadedPlugin LoadPlugin(
        string name,
        string resolvedRoot,
        IReadOnlyCollection<string> contractAssemblyNames,
        TextWriter diagnostics
    )
    {
        try
        {
            string candidate = Path.Combine(resolvedRoot, name);

            // Existence before resolution again, and the order is the criterion: a misspelled entry has
            // to fail with the missing-path fatal rather than with a raw exception out of resolution.
            if (!Directory.Exists(candidate))
            {
                throw new PluginLoadException(
                    PluginLoadFailure.PluginDirectoryMissing,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' is allowlisted but "
                        + $"'{PluginDiagnosticText.Quote(candidate)}' does not exist."
                );
            }

            string resolvedPlugin = ResolveDirectory(candidate, name, diagnostics);

            // The containment check resolves symbolic links rather than normalizing lexically, because
            // Path.GetFullPath never reads the filesystem: a directory link out of the root passes it
            // while the assembly actually loaded is the one outside. The plugin path is composed
            // against the *resolved* root, because composing against the configured one rejects every
            // plugin under a root that is itself a link.
            string rootPrefix = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? resolvedRoot
                : resolvedRoot + Path.DirectorySeparatorChar;

            if (!resolvedPlugin.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new PluginLoadException(
                    PluginLoadFailure.EscapesPluginRoot,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' resolves to "
                        + $"'{PluginDiagnosticText.Quote(resolvedPlugin)}', which is not under the "
                        + $"plugin root '{PluginDiagnosticText.Quote(resolvedRoot)}'. The check follows "
                        + "symbolic links, and it covers the plugin directory rather than the files in it."
                );
            }

            string entryAssemblyPath = Path.Combine(resolvedPlugin, $"{name}.dll");
            string manifestPath = Path.Combine(resolvedPlugin, $"{name}.deps.json");

            RequireFile(
                entryAssemblyPath,
                name,
                PluginLoadFailure.EntryAssemblyMissing,
                "its entry assembly"
            );
            RequireFile(
                manifestPath,
                name,
                PluginLoadFailure.DepsJsonMissing,
                "its dependency manifest, without which its private closure would not resolve"
            );

            PluginDepsManifest manifest = PluginDepsManifest.Read(name, manifestPath);

            if (manifest.DeclaresRuntimePack)
            {
                throw new PluginLoadException(
                    PluginLoadFailure.SelfContainedPublish,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' was published self-contained, which "
                        + "its manifest shows by declaring a runtime pack. Publish it with "
                        + "--no-self-contained, so that it carries its own closure and not a copy of the "
                        + "shared framework."
                );
            }

            // Both version checks run before any type is loaded, so neither can surface as a raw
            // type-load error from somewhere the operator cannot act on.
            CheckContractReferences(name, entryAssemblyPath, contractAssemblyNames);
            CheckDeclaredDependencies(name, manifest);

            PluginLoadContext context = CreateLoadContext(name, entryAssemblyPath, manifest);
            Assembly entryAssembly = LoadEntryAssembly(name, entryAssemblyPath, context);

            if (entryAssembly.GetName().Name is not { } entryAssemblyName)
            {
                throw new PluginLoadException(
                    PluginLoadFailure.AssemblyNameMismatch,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' has an entry assembly with no name."
                );
            }

            // The fourth of the four equalities, and the one the file name does not prove: a csproj can
            // declare an AssemblyName and be published under a different file name, and such a plugin
            // satisfies every other check.
            if (!string.Equals(entryAssemblyName, name, StringComparison.Ordinal))
            {
                throw new PluginLoadException(
                    PluginLoadFailure.AssemblyNameMismatch,
                    name,
                    $"plugin directory '{PluginDiagnosticText.Quote(name)}' holds an entry assembly "
                        + $"whose own name is '{PluginDiagnosticText.Quote(entryAssemblyName)}'. The "
                        + "directory, the file name, the assembly name and the plugin's Name all have to "
                        + "be the same string."
                );
            }

            EdFiApiPlugin instance = Activate(name, FindPluginType(name, entryAssembly));

            // A plugin can return null from Name: it is a non-nullable property on the contract, and a
            // third-party assembly is not constrained by that at runtime. A null is simply a name that
            // is not the directory name, so it takes the same refusal and renders as <null>.
            string? declaredName = ReadName(name, instance);

            if (!string.Equals(declaredName, name, StringComparison.Ordinal))
            {
                throw new PluginLoadException(
                    PluginLoadFailure.PluginNameMismatch,
                    name,
                    $"plugin directory '{PluginDiagnosticText.Quote(name)}' holds a plugin whose Name is "
                        + $"'{PluginDiagnosticText.Quote(declaredName)}'. They are compared ordinally, so "
                        + "a plugin's identity does not depend on which filesystem the image was built on."
                );
            }

            LoadedPlugin plugin = new(
                name,
                instance,
                resolvedPlugin,
                entryAssembly.GetName().Version ?? new Version(0, 0, 0, 0),
                $"{name}.dll",
                PluginFileInventory.Build(name, resolvedPlugin, manifest),
                context
            );

            diagnostics.WriteLine(
                $"plugin '{PluginDiagnosticText.Quote(name)}' loaded from "
                    + $"'{PluginDiagnosticText.Quote(resolvedPlugin)}'"
            );

            return plugin;
        }
        catch (PluginLoadException exception)
        {
            throw Report(diagnostics, name, exception);
        }
    }

    /// <summary>
    /// Resolves a directory's final component through symbolic links, falling back to the path itself
    /// when it is not a link.
    /// </summary>
    private static string ResolveDirectory(string path, string? pluginName, TextWriter diagnostics)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            PluginLoadException failure = new(
                PluginLoadFailure.PluginPathUnresolvable,
                pluginName,
                $"'{PluginDiagnosticText.Quote(path)}' could not be resolved. The containment check "
                    + "follows symbolic links, so the path has to be readable to be checked.",
                exception
            );

            throw pluginName is null ? Report(diagnostics, null, failure) : failure;
        }
    }

    private static void RequireFile(
        string path,
        string pluginName,
        PluginLoadFailure reason,
        string description
    )
    {
        if (!File.Exists(path))
        {
            throw new PluginLoadException(
                reason,
                pluginName,
                $"plugin '{PluginDiagnosticText.Quote(pluginName)}' is missing {description}: "
                    + $"'{PluginDiagnosticText.Quote(path)}' does not exist."
            );
        }
    }

    /// <summary>
    /// Compares the entry assembly's references against the contracts the caller declares, reading
    /// metadata rather than loading a type, so the refusal never arrives as a type-load error.
    /// </summary>
    private static void CheckContractReferences(
        string name,
        string entryAssemblyPath,
        IReadOnlyCollection<string> contractAssemblyNames
    )
    {
        if (contractAssemblyNames.Count == 0)
        {
            return;
        }

        HashSet<string> contracts = new(contractAssemblyNames, StringComparer.Ordinal);

        foreach (
            (string referenceName, Version referenceVersion) in EntryAssemblyReferences.Read(
                name,
                entryAssemblyPath
            )
        )
        {
            if (!contracts.Contains(referenceName))
            {
                continue;
            }

            if (!HostAssemblies.TryGetVersion(referenceName, out Version hostVersion))
            {
                throw new PluginLoadException(
                    PluginLoadFailure.ContractAssemblyMissing,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' references the declared contract "
                        + $"'{PluginDiagnosticText.Quote(referenceName)}', which this host cannot "
                        + "resolve. The host has to carry every contract it declares."
                );
            }

            if (hostVersion < referenceVersion)
            {
                throw new PluginLoadException(
                    PluginLoadFailure.ContractVersionSkew,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' requires contract "
                        + $"'{PluginDiagnosticText.Quote(referenceName)}' {referenceVersion}, and this "
                        + $"host carries {hostVersion}. A plugin built against a newer contract than the "
                        + "host is refused at load rather than failing later inside a hook."
                );
            }
        }
    }

    /// <summary>
    /// Applies the host-first comparison to every assembly the manifest declares a version for, before
    /// the plugin is constructed.
    /// </summary>
    /// <remarks>
    /// The <c>Load</c> override alone would discover a skewed dependency only when a code path first
    /// needed it, so a dependency no hook happens to touch would be found on a request rather than at
    /// startup, and "fatal at load" would be a promise this design does not keep.
    /// </remarks>
    private static void CheckDeclaredDependencies(string name, PluginDepsManifest manifest)
    {
        foreach (PluginDeclaredAssembly declared in manifest.DeclaredAssemblies)
        {
            if (
                HostAssemblies.TryGetVersion(declared.SimpleName, out Version hostVersion)
                && hostVersion < declared.DeclaredVersion
            )
            {
                throw new PluginLoadException(
                    PluginLoadFailure.DependencyVersionSkew,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' declares "
                        + $"'{PluginDiagnosticText.Quote(declared.SimpleName)}' "
                        + $"{declared.DeclaredVersion}, and this host carries {hostVersion}. Serving the "
                        + "plugin's own copy would split the type identity that host-first resolution "
                        + "exists to keep."
                );
            }
        }
    }

    private static Assembly LoadEntryAssembly(
        string name,
        string entryAssemblyPath,
        PluginLoadContext context
    )
    {
        try
        {
            return context.LoadFromAssemblyPath(entryAssemblyPath);
        }
        catch (BadImageFormatException exception)
        {
            throw new PluginLoadException(
                PluginLoadFailure.EntryAssemblyUnreadable,
                name,
                $"plugin '{PluginDiagnosticText.Quote(name)}' has an entry assembly at "
                    + $"'{PluginDiagnosticText.Quote(entryAssemblyPath)}' that is not a managed assembly.",
                exception
            );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Unwrap(name, exception)
                ?? new PluginLoadException(
                    PluginLoadFailure.EntryAssemblyLoadFailed,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' could not be loaded from "
                        + $"'{PluginDiagnosticText.Quote(entryAssemblyPath)}'.",
                    exception
                );
        }
    }

    /// <summary>
    /// Finds the one type the entry assembly exposes that the host can construct and call.
    /// </summary>
    private static Type FindPluginType(string name, Assembly entryAssembly)
    {
        try
        {
            // Every step here is reflection over a third party's assembly, and every step of it can
            // bind another assembly. Enumerating the exported types is the obvious one; resolving a
            // constructor is the one that is easy to miss, because asking for the parameterless
            // constructor makes the runtime resolve the parameter types of the type's *other*
            // constructors, and an overload naming a type from a skewed or absent assembly fails
            // there. The whole discovery therefore sits inside one classified handler, so the
            // first-use backstop applies to all of it rather than only to enumeration.
            Type[] exported = entryAssembly.GetExportedTypes();

            // A candidate has to be something the host can actually construct and call. An abstract
            // type, an open generic and a type with no public parameterless constructor are each
            // impossible to construct, so counting them would turn "your plugin type is not
            // constructible" into "your assembly exposes two plugins".
            List<Type> candidates = exported
                .Where(type =>
                    type.IsClass
                    && !type.IsAbstract
                    && !type.ContainsGenericParameters
                    && typeof(EdFiApiPlugin).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null
                )
                .ToList();

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            IEnumerable<Type> nearMisses = exported.Where(type =>
                typeof(EdFiApiPlugin).IsAssignableFrom(type) && !candidates.Contains(type)
            );

            string found =
                candidates.Count == 0 ? "none" : string.Join(", ", candidates.Select(type => type.FullName));
            string excluded = string.Join(", ", nearMisses.Select(type => type.FullName));

            // Thrown from inside the try deliberately: it is a PluginLoadException, and neither handler
            // below catches one, so the zero and multiple classifications survive intact.
            throw new PluginLoadException(
                candidates.Count == 0
                    ? PluginLoadFailure.NoPluginType
                    : PluginLoadFailure.MultiplePluginTypes,
                name,
                $"plugin '{PluginDiagnosticText.Quote(name)}' has to expose exactly one public, "
                    + "non-abstract, non-generic EdFiApiPlugin subclass with a public parameterless "
                    + $"constructor. Constructible: {PluginDiagnosticText.Quote(found)}."
                    + (
                        excluded.Length == 0
                            ? string.Empty
                            : $" Exposed but not constructible: {PluginDiagnosticText.Quote(excluded)}."
                    )
            );
        }
        catch (ReflectionTypeLoadException exception)
        {
            throw Unwrap(name, exception)
                ?? new PluginLoadException(
                    PluginLoadFailure.PluginTypesUnloadable,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' has exported types that could not be "
                        + "loaded: "
                        + string.Join(
                            "; ",
                            exception
                                .LoaderExceptions.OfType<Exception>()
                                .Select(loaderException =>
                                    PluginDiagnosticText.Quote(loaderException.Message)
                                )
                        ),
                    exception
                );
        }
        catch (Exception exception)
            when (exception
                    is FileNotFoundException
                        or FileLoadException
                        or TypeLoadException
                        or BadImageFormatException
            )
        {
            throw Unwrap(name, exception)
                ?? new PluginLoadException(
                    PluginLoadFailure.PluginTypesUnloadable,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' exposes types the runtime could not "
                        + $"resolve: {PluginDiagnosticText.Quote(exception.Message)}",
                    exception
                );
        }
    }

    private static EdFiApiPlugin Activate(string name, Type pluginType)
    {
        try
        {
            return (EdFiApiPlugin)Activator.CreateInstance(pluginType)!;
        }
        catch (Exception exception)
        {
            Exception cause = exception is TargetInvocationException { InnerException: { } inner }
                ? inner
                : exception;

            throw Unwrap(name, cause)
                ?? new PluginLoadException(
                    PluginLoadFailure.PluginActivationFailed,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' exposes "
                        + $"'{PluginDiagnosticText.Quote(pluginType.FullName ?? pluginType.Name)}', which "
                        + $"could not be constructed: {PluginDiagnosticText.Quote(cause.Message)}",
                    cause
                );
        }
    }

    private static PluginLoadContext CreateLoadContext(
        string name,
        string entryAssemblyPath,
        PluginDepsManifest manifest
    )
    {
        try
        {
            // The declared versions go to the context so that a substitution it records can say what the
            // plugin's manifest declared, rather than only what the reference asked for. The two differ,
            // and the declaration is the one the plugin shipped and its author tested against.
            return new PluginLoadContext(name, entryAssemblyPath, DeclaredVersionsOf(manifest));
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            // AssemblyDependencyResolver reads the manifest itself, and it is stricter about parts of
            // it than the loader's own reader needs to be. Its refusal is still a refusal of the
            // plugin's manifest, so it arrives as one rather than as a raw argument exception.
            throw new PluginLoadException(
                PluginLoadFailure.DepsJsonUnreadable,
                name,
                $"plugin '{PluginDiagnosticText.Quote(name)}' could not be given a dependency resolver "
                    + $"over '{PluginDiagnosticText.Quote(entryAssemblyPath)}': "
                    + PluginDiagnosticText.Quote(exception.Message),
                exception
            );
        }
    }

    /// <summary>
    /// The version the manifest declares for each simple assembly name, for the substitution record.
    /// </summary>
    /// <remarks>
    /// A manifest can declare one simple name more than once, as a top-level runtime entry and again as
    /// a RID-specific row. Those agree in every publish measured here; where they did not, the first
    /// declaration wins and the record still says what a declaration said rather than inventing one.
    /// </remarks>
    private static IReadOnlyDictionary<string, Version> DeclaredVersionsOf(PluginDepsManifest manifest)
    {
        Dictionary<string, Version> declared = new(StringComparer.Ordinal);

        foreach (PluginDeclaredAssembly assembly in manifest.DeclaredAssemblies)
        {
            declared.TryAdd(assembly.SimpleName, assembly.DeclaredVersion);
        }

        return declared;
    }

    private static string? ReadName(string name, EdFiApiPlugin instance)
    {
        try
        {
            return instance.Name;
        }
        catch (Exception exception)
        {
            throw Unwrap(name, exception)
                ?? new PluginLoadException(
                    PluginLoadFailure.PluginNameUnavailable,
                    name,
                    $"plugin '{PluginDiagnosticText.Quote(name)}' threw when its Name was read: "
                        + PluginDiagnosticText.Quote(exception.Message),
                    exception
                );
        }
    }

    /// <summary>
    /// Finds a version skew the runtime wrapped, and turns it back into the message an operator can act
    /// on.
    /// </summary>
    /// <remarks>
    /// The runtime wraps anything thrown from inside a load context's resolution callback in a
    /// <see cref="FileLoadException"/> whose own message names only the version the host carries and
    /// says nothing about the version the plugin asked for. Reflection wraps failures again in
    /// <see cref="ReflectionTypeLoadException.LoaderExceptions"/>, which is not on the inner-exception
    /// chain at all, so both are searched.
    /// </remarks>
    private static PluginLoadException? Unwrap(string name, Exception exception)
    {
        if (FindSkew(exception) is not { } skew)
        {
            return null;
        }

        return new PluginLoadException(
            PluginLoadFailure.DependencyVersionSkew,
            name,
            skew.Message,
            exception
        );
    }

    private static PluginVersionSkewException? FindSkew(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is PluginVersionSkewException skew)
            {
                return skew;
            }

            if (exception is ReflectionTypeLoadException reflection)
            {
                foreach (Exception? loaderException in reflection.LoaderExceptions)
                {
                    if (FindSkew(loaderException) is { } nested)
                    {
                        return nested;
                    }
                }
            }

            exception = exception.InnerException;
        }

        return null;
    }

    /// <summary>
    /// Names the directories under the plugin root that nobody allowlisted.
    /// </summary>
    /// <remarks>
    /// Only names are read. Nothing inside such a directory is opened, probed, or has its metadata
    /// read, which is what keeps discovery allowlist-driven: a directory nobody asked for is never
    /// touched, and the warning exists so that an operator who expected it to load finds out why it did
    /// not.
    /// </remarks>
    private static void ReportDirectoriesNobodyAskedFor(
        string resolvedRoot,
        IReadOnlyList<string> allowedNames,
        TextWriter diagnostics
    )
    {
        string[] ignored;

        try
        {
            HashSet<string> allowed = new(allowedNames, StringComparer.Ordinal);

            ignored = Directory
                .EnumerateDirectories(resolvedRoot)
                .Select(directory => Path.GetFileName(directory))
                .Where(directory => directory.Length > 0 && !allowed.Contains(directory))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A warning is not worth failing a boot that has otherwise succeeded, and every plugin the
            // operator asked for has already loaded by this point.
            diagnostics.WriteLine(
                "plugins: the plugin root could not be listed for unallowlisted directories: "
                    + PluginDiagnosticText.Quote(exception.Message)
            );
            return;
        }

        if (ignored.Length > 0)
        {
            diagnostics.WriteLine(
                "plugins: ignoring directories not in the allowlist: "
                    + string.Join(
                        ", ",
                        ignored.Select(directory => $"'{PluginDiagnosticText.Quote(directory)}'")
                    )
            );
        }
    }

    /// <summary>
    /// Writes the outcome line for an allowlist entry, or for the configuration when no entry owns the
    /// failure, and hands the exception back so the caller throws it.
    /// </summary>
    private static PluginLoadException Report(
        TextWriter diagnostics,
        string? pluginName,
        PluginLoadException exception
    )
    {
        diagnostics.WriteLine(
            pluginName is null
                ? $"plugins: {exception.Message}"
                : $"plugin '{PluginDiagnosticText.Quote(pluginName)}' failed: {exception.Message}"
        );

        return exception;
    }
}
