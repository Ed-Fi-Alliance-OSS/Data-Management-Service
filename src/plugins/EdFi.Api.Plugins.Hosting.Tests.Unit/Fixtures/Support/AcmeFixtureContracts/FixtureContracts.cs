// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;

namespace Acme.FixtureContracts;

/// <summary>A service a fixture plugin registers, owned by the plugin rather than by any host.</summary>
public interface IAcmeFirstService
{
    string Describe();
}

/// <summary>A second, so a case can distinguish two descriptors one plugin contributed.</summary>
public interface IAcmeSecondService
{
    string Describe();
}

/// <summary>
/// A third, registered by the second fixture plugin, so a case can tell two plugins' contributions
/// apart by service type rather than by counting.
/// </summary>
public interface IAcmeThirdService
{
    string Describe();
}

/// <summary>
/// A service nothing ever registers, so an implementation taking it in its constructor cannot be
/// activated.
/// </summary>
public interface IFixtureMissingDependency
{
    string Describe();
}

/// <summary>An implementation, shared so a test can name the type a descriptor carries.</summary>
public sealed class AcmeService : IAcmeFirstService, IAcmeSecondService, IAcmeThirdService
{
    public string Describe() => nameof(AcmeService);
}

/// <summary>A second implementation, for the cases that replace one descriptor with another.</summary>
public sealed class SecondAcmeService : IAcmeFirstService, IAcmeSecondService, IAcmeThirdService
{
    public string Describe() => nameof(SecondAcmeService);
}

/// <summary>
/// What a fixture plugin's hook observed, written by the plugin and read by the test.
/// </summary>
/// <remarks>
/// This assembly is served host-first, so the plugin and the test share one instance of this static
/// and the plugin can report on a collection the test never sees. A test clears it before driving the
/// hook, because the same process runs every case.
/// </remarks>
public static class FixtureObservations
{
    private static readonly ConcurrentDictionary<string, string> Recorded = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> Counted = new(StringComparer.Ordinal);
    private static int _sequence;

    public static void Record(string key, string value) => Recorded[key] = value;

    public static string? Read(string key) => Recorded.TryGetValue(key, out string? value) ? value : null;

    /// <summary>
    /// Counts an event, which is how the activation cases assert how many times something was
    /// constructed, disposed or invoked without the test having to name the type that did it.
    /// </summary>
    public static void Count(string key) => Counted.AddOrUpdate(key, 1, static (_, count) => count + 1);

    public static int CountOf(string key) => Counted.TryGetValue(key, out int count) ? count : 0;

    /// <summary>
    /// The next value of a monotonic counter shared by everything in this process.
    /// </summary>
    /// <remarks>
    /// Two channels that cannot see each other can still be ordered against each other through this:
    /// the host's diagnostic writer takes a number when it announces a plugin, and the plugin takes one
    /// on entry to its hook, so which happened first is a comparison rather than an inference.
    /// </remarks>
    public static int Next() => Interlocked.Increment(ref _sequence);

    public static void Clear()
    {
        Recorded.Clear();
        Counted.Clear();
        Interlocked.Exchange(ref _sequence, 0);
    }
}
