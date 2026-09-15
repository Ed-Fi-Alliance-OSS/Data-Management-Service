// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace EdFi.DataManagementService.Tests.Integration.Plugins;

/// <summary>
/// Boots the production host with a plugin root of the test's own and captures what it logged.
/// </summary>
/// <remarks>
/// <para>
/// No database. The host reaches <c>Ready</c> against the same external doubles the manifest-backed
/// fixture case already uses, with a connection string nothing dials, so every plugin case here runs
/// on a machine with no PostgreSQL and none of them is skipped. What stays real is everything the
/// criteria are about: the bootstrap phases, the loader, the contribution phase, the inventory event,
/// and both post-container startup guards.
/// </para>
/// <para>
/// The plugin root is a temporary tree per boot rather than the staging directory itself, because two
/// cases need a directory the staging does not produce: a name that resolves to nothing, and a
/// deliberately corrupt one.
/// </para>
/// </remarks>
internal static class PluginHostProbe
{
    /// <summary>Where the build staged the fixture plugin directories.</summary>
    public static string StagedFixtureRoot => Path.Combine(AppContext.BaseDirectory, "PluginFixtures");

    /// <summary>A temporary plugin root holding copies of the named staged fixtures.</summary>
    public static string CreatePluginRoot(params string[] fixtureNames)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dms-plugin-integration",
            Guid.NewGuid().ToString("N")
        );

        foreach (string fixtureName in fixtureNames)
        {
            CopyDirectory(Path.Combine(StagedFixtureRoot, fixtureName), Path.Combine(root, fixtureName));
        }

        Directory.CreateDirectory(root);

        return root;
    }

    /// <summary>
    /// Writes a plugin directory whose entry assembly is not an assembly at all.
    /// </summary>
    /// <remarks>
    /// The point of the unallowlisted case is that the loader never opens it. Bytes that cannot be
    /// loaded are what makes that observable: a boot that reached them would fail.
    /// </remarks>
    public static void WriteCorruptPluginDirectory(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{name}.dll"), "this is not a managed assembly");
        File.WriteAllText(Path.Combine(directory, $"{name}.deps.json"), "{ not even json ");
    }

    /// <summary>
    /// The production host, configured for a plugin root and an allowlist, with a Serilog sink
    /// capturing everything it writes.
    /// </summary>
    /// <remarks>
    /// The sink is added through <c>ConfigureServices</c> rather than through a logging builder,
    /// because <c>AddServices</c> calls <c>ClearProviders</c> while configuring Serilog from
    /// configuration. This callback runs after that, so the provider it registers survives, which is
    /// the same ordering the external doubles rely on to replace production registrations.
    /// </remarks>
    public static WebApplicationFactory<Program> CreateHost(
        FixtureContext fixture,
        string pluginRoot,
        string allowed,
        string startupStatusFilePath,
        PluginLogCapture capture,
        IReadOnlyDictionary<string, string>? additionalSettings = null
    )
    {
        Logger captureLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(capture)
            .CreateLogger();

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");

            builder.UseSetting("AppSettings:UseApiSchemaPath", "true");
            builder.UseSetting("AppSettings:ApiSchemaPath", fixture.ApiSchemaDirectory);
            builder.UseSetting("AppSettings:StartupStatusFilePath", startupStatusFilePath);
            builder.UseSetting("AppSettings:Datastore", "postgresql");
            builder.UseSetting("AppSettings:BypassAuthorization", "true");
            builder.UseSetting("ConfigurationServiceSettings:BaseUrl", "http://localhost/test-cms");
            builder.UseSetting("ConfigurationServiceSettings:ClientId", "test-cms-client");
            builder.UseSetting("ConfigurationServiceSettings:ClientSecret", "test-cms-secret");
            builder.UseSetting("ConfigurationServiceSettings:Scope", "edfi_admin_api/full_access");

            builder.UseSetting("Plugins:Directory", pluginRoot);
            builder.UseSetting("Plugins:Allowed", allowed);

            foreach ((string key, string value) in additionalSettings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(
                    new SerilogLoggerProvider(captureLogger, dispose: true)
                );

                ExternalDoublesRegistration.RegisterAll(
                    services,
                    fixture,
                    leasedConnectionString: "Host=localhost;Database=unused;Username=unused;Password=unused",
                    new AllowAllClaimSetProvider(fixture),
                    clientEducationOrganizationIds: []
                );
            });
        });
    }

    /// <summary>The startup status document, as it stands on disk.</summary>
    public static JsonObject ReadStartupStatus(string path) =>
        (
            JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Startup status at '{path}' parsed to null.")
        ).AsObject();

    /// <summary>
    /// Removes a temporary file or tree, best effort.
    /// </summary>
    /// <remarks>
    /// Best effort rather than strict, because a plugin loads into a non-collectible load context that
    /// is never unloaded, so on Windows its assemblies stay mapped for the life of the test process and
    /// the directory holding them cannot be removed. Failing a teardown over that would turn a passing
    /// case into a red one for a reason that has nothing to do with what it asserted. Process exit
    /// releases the lock but deletes nothing, so these trees do accumulate under the temporary
    /// directory until something else clears it.
    /// </remarks>
    public static void DeleteIfPresent(string? directoryOrFile)
    {
        if (directoryOrFile is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(directoryOrFile))
            {
                Directory.Delete(directoryOrFile, recursive: true);
            }
            else if (File.Exists(directoryOrFile))
            {
                File.Delete(directoryOrFile);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TestContext.Out.WriteLine(
                $"Left '{directoryOrFile}' in place: {exception.GetType().Name}. A loaded plugin's "
                    + "assemblies stay mapped for the life of this process."
            );
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: true);
        }
    }
}

/// <summary>
/// Every event the host logged, kept as Serilog events rather than as rendered lines.
/// </summary>
/// <remarks>
/// The inventory criterion is that named properties arrive <em>structured</em>, so the assertions have
/// to be able to see a sequence of structures where a rendered string would show only text. Keeping
/// the events also preserves the order the host wrote them in, which one criterion is entirely about.
/// </remarks>
internal sealed class PluginLogCapture : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>Everything logged, in the order it was written.</summary>
    public IReadOnlyList<LogEvent> Events => [.. _events];

    /// <summary>The inventory events, identified by this message template and no other.</summary>
    public IReadOnlyList<LogEvent> InventoryEvents =>
        [
            .. Events.Where(logEvent =>
                logEvent.MessageTemplate.Text.StartsWith(InventoryTemplatePrefix, StringComparison.Ordinal)
            ),
        ];

    /// <summary>
    /// The prefix that tells the structured inventory event apart from the plugin registration
    /// guard's own per-plugin summary, which is built from the same records later in startup.
    /// </summary>
    public const string InventoryTemplatePrefix = "Plugin inventory for ";

    /// <summary>The index of the first event whose message or exception contains <paramref name="text"/>.</summary>
    public int IndexOfMessageContaining(string text)
    {
        IReadOnlyList<LogEvent> events = Events;

        for (int index = 0; index < events.Count; index++)
        {
            if (TextOf(events[index]).Contains(text, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The first event whose message or exception contains <paramref name="text"/>, or null.</summary>
    /// <remarks>
    /// Returning the event rather than its index is what lets a case assert against that one event
    /// instead of against everything logged. Several events can carry the same type name for
    /// unrelated reasons - the inventory renders every implementation type it reports - so an
    /// assertion that only asks whether some event mentions a name is satisfied by the wrong one.
    /// </remarks>
    public LogEvent? FirstEventContaining(string text) =>
        Events.FirstOrDefault(logEvent => TextOf(logEvent).Contains(text, StringComparison.Ordinal));

    /// <summary>An event's rendered message and its exception, when it has one, as one string.</summary>
    /// <remarks>
    /// Both halves, because the startup orchestrator logs a failed task's exception rather than
    /// flattening it into the message: the offense text a startup guard built lives on the exception
    /// and nowhere else.
    /// </remarks>
    public static string TextOf(LogEvent logEvent) =>
        logEvent.Exception is null
            ? logEvent.RenderMessage()
            : $"{logEvent.RenderMessage()}{Environment.NewLine}{logEvent.Exception}";

    public int IndexOfInventoryEventFor(string pluginName)
    {
        IReadOnlyList<LogEvent> events = Events;

        for (int index = 0; index < events.Count; index++)
        {
            if (
                events[index]
                    .MessageTemplate.Text.StartsWith(InventoryTemplatePrefix, StringComparison.Ordinal)
                && ScalarText(events[index], "PluginName") == pluginName
            )
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The value of a scalar property, or null when it is absent or not a scalar.</summary>
    public static string? ScalarText(LogEvent logEvent, string propertyName) =>
        logEvent.Properties.TryGetValue(propertyName, out LogEventPropertyValue? value)
        && value is ScalarValue scalar
            ? scalar.Value as string
            : null;

    /// <summary>The rows of a destructured sequence property.</summary>
    public static IReadOnlyList<LogEventPropertyValue> Sequence(LogEvent logEvent, string propertyName) =>
        logEvent.Properties.TryGetValue(propertyName, out LogEventPropertyValue? value)
        && value is SequenceValue sequence
            ? sequence.Elements
            : [];

    /// <summary>The member names a destructured structure carries.</summary>
    /// <remarks>
    /// Kept apart from <see cref="Member"/> so that a member present with a null value can be told
    /// from one that is absent. The entry assembly's declared version is the case that needs it.
    /// </remarks>
    public static IReadOnlyList<string> MemberNames(LogEventPropertyValue row) =>
        row is StructureValue structure ? [.. structure.Properties.Select(property => property.Name)] : [];

    /// <summary>A member of a destructured structure, as text.</summary>
    public static string? Member(LogEventPropertyValue row, string memberName)
    {
        if (row is not StructureValue structure)
        {
            return null;
        }

        LogEventPropertyValue? member = structure
            .Properties.FirstOrDefault(property =>
                string.Equals(property.Name, memberName, StringComparison.Ordinal)
            )
            ?.Value;

        return member is ScalarValue scalar ? scalar.Value?.ToString() : null;
    }
}
