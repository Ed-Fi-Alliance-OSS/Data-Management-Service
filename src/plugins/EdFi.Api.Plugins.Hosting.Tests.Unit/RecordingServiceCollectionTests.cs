// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using EdFi.DataManagementService.FixtureHost;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Service types the fixture plugin owns. None of them is host-owned, which is what makes removing one
/// ordinary work rather than a refusal.
/// </summary>
public interface IPluginOwnedService
{
    string Describe();
}

public interface IPluginOwnedSecondService
{
    string Describe();
}

public interface IPluginOwnedThirdService
{
    string Describe();
}

internal sealed class PluginOwnedService
    : IPluginOwnedService,
        IPluginOwnedSecondService,
        IPluginOwnedThirdService
{
    public string Describe() => nameof(PluginOwnedService);
}

internal sealed class SecondPluginOwnedService
    : IPluginOwnedService,
        IPluginOwnedSecondService,
        IPluginOwnedThirdService
{
    public string Describe() => nameof(SecondPluginOwnedService);
}

internal sealed class FixtureHostService : IFixtureHostService, IFixtureHostUnclaimedService
{
    public string Describe() => nameof(FixtureHostService);
}

internal sealed class SecondFixtureHostService : IFixtureHostService, IFixtureHostUnclaimedService
{
    public string Describe() => nameof(SecondFixtureHostService);
}

internal sealed class FixtureReplaceContract : IFixtureReplaceContract
{
    public string Describe() => nameof(FixtureReplaceContract);
}

/// <summary>A sink of a plugin's own, which the carve-out permits a plugin to add.</summary>
internal sealed class FixtureLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

    public void Dispose()
    {
        // Nothing to release: the loggers this hands out are the framework's null loggers.
    }
}

/// <summary>
/// Records every <see cref="IServiceCollection"/> member the wrapper forwards to it, then delegates.
/// </summary>
/// <remarks>
/// Sits <em>behind</em> the wrapper rather than in front of it, so what it records is what the wrapper
/// passed on. That is what makes it evidence for two different things: that a read is delegated rather
/// than answered from a snapshot, and that a declining TryAdd never hands the candidate descriptor to
/// anything.
/// </remarks>
internal sealed class ObservingServiceCollection(IServiceCollection inner) : IServiceCollection
{
    internal List<string> Calls { get; } = [];

    public ServiceDescriptor this[int index]
    {
        get
        {
            Calls.Add($"get_Item({index})");
            return inner[index];
        }
        set
        {
            Calls.Add($"set_Item({index},{Describe(value)})");
            inner[index] = value;
        }
    }

    public int Count
    {
        get
        {
            Calls.Add("get_Count");
            return inner.Count;
        }
    }

    public bool IsReadOnly => inner.IsReadOnly;

    public void Add(ServiceDescriptor item)
    {
        Calls.Add($"Add({Describe(item)})");
        inner.Add(item);
    }

    public void Clear()
    {
        Calls.Add("Clear()");
        inner.Clear();
    }

    public bool Contains(ServiceDescriptor item)
    {
        Calls.Add($"Contains({Describe(item)})");
        return inner.Contains(item);
    }

    public void CopyTo(ServiceDescriptor[] array, int arrayIndex)
    {
        Calls.Add("CopyTo");
        inner.CopyTo(array, arrayIndex);
    }

    public IEnumerator<ServiceDescriptor> GetEnumerator()
    {
        Calls.Add("GetEnumerator");
        return inner.GetEnumerator();
    }

    public int IndexOf(ServiceDescriptor item)
    {
        Calls.Add($"IndexOf({Describe(item)})");
        return inner.IndexOf(item);
    }

    public void Insert(int index, ServiceDescriptor item)
    {
        Calls.Add($"Insert({index},{Describe(item)})");
        inner.Insert(index, item);
    }

    public bool Remove(ServiceDescriptor item)
    {
        Calls.Add($"Remove({Describe(item)})");
        return inner.Remove(item);
    }

    public void RemoveAt(int index)
    {
        Calls.Add($"RemoveAt({index})");
        inner.RemoveAt(index);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        Calls.Add("GetEnumerator");
        return ((IEnumerable)inner).GetEnumerator();
    }

    private static string Describe(ServiceDescriptor descriptor) =>
        $"{descriptor.ServiceType.Name}->"
        + (
            descriptor.ImplementationType?.Name
            ?? descriptor.ImplementationInstance?.GetType().Name
            ?? "factory"
        );
}

/// <summary>
/// Shared arrangement: a real collection the host has populated, and a wrapper over it named for a
/// plugin.
/// </summary>
internal static class WrapperFixture
{
    internal const string PluginName = "Acme.Fixture";

    internal static RecordingServiceCollection Wrap(IServiceCollection inner, out StringWriter diagnostics)
    {
        diagnostics = new StringWriter();
        return new RecordingServiceCollection(inner, PluginName, diagnostics);
    }

    /// <summary>
    /// A collection carrying what the cases below need to be pre-existing: a host-owned default, a
    /// replace-contract descriptor the host registered, and two descriptors of the plugin's own kind.
    /// </summary>
    internal static ServiceCollection HostPopulated()
    {
        ServiceCollection services = new();
        services.AddSingleton<IFixtureReplaceContract, FixtureReplaceContract>();
        services.AddSingleton<IPluginOwnedSecondService, PluginOwnedService>();
        services.AddSingleton<IPluginOwnedThirdService, PluginOwnedService>();
        services.AddScoped<IFixtureHostService, FixtureHostService>();
        return services;
    }
}

[TestFixture]
public class Given_a_wrapper_over_a_collection_the_host_has_populated
{
    private ServiceCollection _inner = null!;
    private RecordingServiceCollection _wrapper = null!;
    private ObservingServiceCollection _observed = null!;
    private RecordingServiceCollection _wrapperOverObserved = null!;

    [SetUp]
    public void Setup()
    {
        _inner = WrapperFixture.HostPopulated();
        _wrapper = WrapperFixture.Wrap(_inner, out _);

        _observed = new ObservingServiceCollection(WrapperFixture.HostPopulated());
        _wrapperOverObserved = WrapperFixture.Wrap(_observed, out _);
        _observed.Calls.Clear();
    }

    [Test]
    public void It_enumerates_exactly_what_the_real_collection_holds_in_the_same_order()
    {
        _wrapper.Should().Equal(_inner);
    }

    [Test]
    public void It_agrees_with_the_real_collection_on_count_and_the_indexer()
    {
        _wrapper.Count.Should().Be(_inner.Count);

        for (int index = 0; index < _inner.Count; index++)
        {
            _wrapper[index].Should().BeSameAs(_inner[index]);
        }
    }

    [Test]
    public void It_agrees_with_the_real_collection_on_index_of_and_contains()
    {
        foreach (ServiceDescriptor descriptor in _inner)
        {
            _wrapper.IndexOf(descriptor).Should().Be(_inner.IndexOf(descriptor));
            _wrapper.Contains(descriptor).Should().BeTrue();
        }
    }

    [Test]
    public void It_copies_out_exactly_what_the_real_collection_holds()
    {
        ServiceDescriptor[] copied = new ServiceDescriptor[_inner.Count];

        _wrapper.CopyTo(copied, 0);

        copied.Should().Equal(_inner);
    }

    /// <summary>
    /// A replace-cardinality descriptor the host registered before the hook is visible like any other.
    /// An earlier revision of the design masked these from a plugin's view, and
    /// <see cref="Given_a_hook_that_removes_all_descriptors_for_one_of_its_own_service_types" /> is the
    /// measured reason that is withdrawn.
    /// </summary>
    [Test]
    public void It_shows_the_replace_contract_descriptor_the_host_registered()
    {
        _wrapper
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(IFixtureReplaceContract));
    }

    [Test]
    public void It_delegates_reads_rather_than_answering_from_a_snapshot()
    {
        _ = _wrapperOverObserved.Count;
        _ = _wrapperOverObserved[0];

        _observed.Calls.Should().Equal("get_Count", "get_Item(0)");
    }

    [Test]
    public void It_reports_the_real_collection_as_writable()
    {
        _wrapper.IsReadOnly.Should().Be(_inner.IsReadOnly);
    }
}

[TestFixture]
public class Given_a_hook_that_removes_all_descriptors_for_one_of_its_own_service_types
{
    private ServiceCollection _inner = null!;

    [SetUp]
    public void Setup()
    {
        // The measured shape: [replace contract, B, C]. Reading indices through a masked view and
        // writing them to the real collection removed the first two and kept the third.
        _inner = new ServiceCollection();
        _inner.AddSingleton<IFixtureReplaceContract, FixtureReplaceContract>();
        _inner.AddSingleton<IPluginOwnedSecondService, PluginOwnedService>();
        _inner.AddSingleton<IPluginOwnedThirdService, PluginOwnedService>();

        RecordingServiceCollection wrapper = WrapperFixture.Wrap(_inner, out _);

        wrapper.RemoveAll<IPluginOwnedThirdService>();
    }

    [Test]
    public void It_removes_the_descriptors_for_that_type_and_nothing_else()
    {
        _inner
            .Select(descriptor => descriptor.ServiceType)
            .Should()
            .Equal(typeof(IFixtureReplaceContract), typeof(IPluginOwnedSecondService));
    }
}

[TestFixture]
public class Given_a_hook_whose_try_add_declines
{
    private ObservingServiceCollection _observed = null!;
    private ServiceCollection _inner = null!;

    [SetUp]
    public void Setup()
    {
        _inner = new ServiceCollection();
        _inner.AddSingleton<IPluginOwnedService, PluginOwnedService>();

        _observed = new ObservingServiceCollection(_inner);
        RecordingServiceCollection wrapper = WrapperFixture.Wrap(_observed, out _);
        _observed.Calls.Clear();

        wrapper.TryAddSingleton<IPluginOwnedService, SecondPluginOwnedService>();
    }

    /// <summary>
    /// This asserts a limit rather than a capability. A declining TryAdd reads Count and the indexer,
    /// compares, and returns without handing the candidate to the collection, so no wrapper over
    /// IServiceCollection can record it. The test exists so that nobody later writes an acceptance
    /// criterion this seam cannot satisfy.
    /// </summary>
    [Test]
    public void It_is_seen_only_as_a_count_and_indexer_scan()
    {
        _observed.Calls.Should().OnlyContain(call => call == "get_Count" || call.StartsWith("get_Item"));
    }

    [Test]
    public void It_never_shows_the_candidate_implementation_to_anything()
    {
        _observed
            .Calls.Should()
            .NotContain(call => call.Contains(nameof(SecondPluginOwnedService), StringComparison.Ordinal));
    }

    [Test]
    public void It_leaves_the_real_collection_alone()
    {
        _inner.Should().ContainSingle();
        _inner[0].ImplementationType.Should().Be(typeof(PluginOwnedService));
    }
}

[TestFixture]
public class Given_a_hook_that_displaces_a_pre_existing_host_owned_descriptor
{
    private ServiceCollection _inner = null!;
    private RecordingServiceCollection _wrapper = null!;
    private StringWriter _diagnostics = null!;
    private ServiceDescriptor _hostDescriptor = null!;
    private int _hostDescriptorIndex;

    [SetUp]
    public void Setup()
    {
        _inner = WrapperFixture.HostPopulated();
        _wrapper = WrapperFixture.Wrap(_inner, out _diagnostics);
        _hostDescriptorIndex = _inner
            .Select((descriptor, index) => (descriptor, index))
            .First(pair => pair.descriptor.ServiceType == typeof(IFixtureHostService))
            .index;
        _hostDescriptor = _inner[_hostDescriptorIndex];
    }

    private void AssertRefused(Action displacement)
    {
        int countBefore = _inner.Count;

        displacement
            .Should()
            .Throw<PluginCompositionException>()
            .Where(exception =>
                exception.Reason == PluginCompositionFailure.HostOwnedDescriptorDisplaced
                && exception.PluginName == WrapperFixture.PluginName
            )
            .WithMessage($"*{WrapperFixture.PluginName}*")
            .WithMessage($"*{nameof(IFixtureHostService)}*");

        // Refused before the call reached the real collection, which is the property that makes the
        // refusal reportable at all: the host's registrations are still there to report about.
        _inner.Count.Should().Be(countBefore);
        _inner[_hostDescriptorIndex].Should().BeSameAs(_hostDescriptor);
        _diagnostics.ToString().Should().Contain(nameof(IFixtureHostService));
    }

    [Test]
    public void It_refuses_a_replace_over_that_descriptor()
    {
        AssertRefused(() =>
            _wrapper.Replace(ServiceDescriptor.Scoped<IFixtureHostService, SecondFixtureHostService>())
        );
    }

    [Test]
    public void It_refuses_a_remove_all_over_that_service_type()
    {
        AssertRefused(() => _wrapper.RemoveAll<IFixtureHostService>());
    }

    [Test]
    public void It_refuses_an_assignment_through_the_indexer_over_that_slot()
    {
        AssertRefused(() =>
            _wrapper[_hostDescriptorIndex] = ServiceDescriptor.Singleton<
                IPluginOwnedService,
                PluginOwnedService
            >()
        );
    }

    [Test]
    public void It_refuses_a_direct_remove_of_that_descriptor()
    {
        AssertRefused(() => _wrapper.Remove(_hostDescriptor));
    }

    [Test]
    public void It_refuses_a_remove_at_over_that_slot()
    {
        AssertRefused(() => _wrapper.RemoveAt(_hostDescriptorIndex));
    }
}

[TestFixture]
public class Given_a_hook_that_clears_the_collection
{
    [Test]
    public void It_is_refused_even_when_the_collection_holds_nothing_host_owned()
    {
        ServiceCollection inner = new();
        inner.AddSingleton<IPluginOwnedService, PluginOwnedService>();
        RecordingServiceCollection wrapper = WrapperFixture.Wrap(inner, out StringWriter diagnostics);

        Action clear = wrapper.Clear;

        clear
            .Should()
            .Throw<PluginCompositionException>()
            .Where(exception => exception.Reason == PluginCompositionFailure.ServiceCollectionCleared)
            .WithMessage($"*{WrapperFixture.PluginName}*");
        inner.Should().ContainSingle();
        diagnostics.ToString().Should().Contain(WrapperFixture.PluginName);
    }

    [Test]
    public void It_is_refused_over_a_collection_the_host_populated()
    {
        ServiceCollection inner = WrapperFixture.HostPopulated();
        int countBefore = inner.Count;
        RecordingServiceCollection wrapper = WrapperFixture.Wrap(inner, out _);

        Action clear = wrapper.Clear;

        clear.Should().Throw<PluginCompositionException>();
        inner.Count.Should().Be(countBefore);
    }
}

[TestFixture]
public class Given_a_hook_that_clears_the_logging_providers
{
    private ServiceCollection _inner = null!;
    private RecordingServiceCollection _wrapper = null!;
    private StringWriter _diagnostics = null!;
    private List<ServiceDescriptor> _providerDescriptorsBefore = null!;

    [SetUp]
    public void Setup()
    {
        // Real logging, added the way a host adds it, so the descriptors the plugin's call reaches are
        // the ones a real host would have registered before the hook ran.
        _inner = new ServiceCollection();
        _inner.AddLogging(builder => builder.AddConsole());

        _providerDescriptorsBefore =
        [
            .. _inner.Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider)),
        ];

        _wrapper = WrapperFixture.Wrap(_inner, out _diagnostics);
    }

    [Test]
    public void It_registered_a_provider_to_clear()
    {
        _providerDescriptorsBefore.Should().NotBeEmpty();
    }

    [Test]
    public void It_is_refused_naming_the_plugin_and_the_service_type()
    {
        Action clearProviders = () => _wrapper.AddLogging(builder => builder.ClearProviders());

        clearProviders
            .Should()
            .Throw<PluginCompositionException>()
            .Where(exception =>
                exception.Reason == PluginCompositionFailure.LoggingPipelineDescriptorDisplaced
                && exception.PluginName == WrapperFixture.PluginName
            )
            .WithMessage($"*{WrapperFixture.PluginName}*")
            .WithMessage($"*{nameof(ILoggerProvider)}*");
    }

    /// <summary>
    /// The refusal happens before the call reaches the real collection, so the host's providers are
    /// still registered when it is reported, and the report goes to a channel that does not depend on
    /// the logging pipeline it just protected.
    /// </summary>
    [Test]
    public void It_leaves_the_hosts_provider_descriptors_in_place_and_reports_the_refusal()
    {
        try
        {
            _wrapper.AddLogging(builder => builder.ClearProviders());
        }
        catch (PluginCompositionException)
        {
            // Expected, and asserted by the case above. What this case is about is the state the
            // refusal left behind, so the exception is swallowed here rather than reasserted.
        }

        _inner
            .Where(descriptor => descriptor.ServiceType == typeof(ILoggerProvider))
            .Should()
            .Equal(_providerDescriptorsBefore);
        _diagnostics.ToString().Should().Contain(nameof(ILoggerProvider));
    }
}

[TestFixture]
public class Given_a_hook_that_displaces_another_logging_pipeline_service_type
{
    private ServiceCollection _inner = null!;
    private RecordingServiceCollection _wrapper = null!;

    [SetUp]
    public void Setup()
    {
        _inner = new ServiceCollection();
        _inner.AddLogging(builder => builder.AddConsole());
        _wrapper = WrapperFixture.Wrap(_inner, out _);
    }

    [Test]
    public void It_refuses_removing_the_logger_factory()
    {
        Action removal = () => _wrapper.RemoveAll<ILoggerFactory>();

        removal
            .Should()
            .Throw<PluginCompositionException>()
            .Where(exception =>
                exception.Reason == PluginCompositionFailure.LoggingPipelineDescriptorDisplaced
            )
            .WithMessage($"*{nameof(ILoggerFactory)}*");
        _inner.Should().Contain(descriptor => descriptor.ServiceType == typeof(ILoggerFactory));
    }

    [Test]
    public void It_refuses_removing_the_generic_logger()
    {
        Action removal = () => _wrapper.RemoveAll(typeof(ILogger<>));

        removal
            .Should()
            .Throw<PluginCompositionException>()
            .Where(exception =>
                exception.Reason == PluginCompositionFailure.LoggingPipelineDescriptorDisplaced
            );
        _inner.Should().Contain(descriptor => descriptor.ServiceType == typeof(ILogger<>));
    }
}

[TestFixture]
public class Given_a_hook_that_only_adds_a_logging_provider
{
    private ServiceCollection _inner = null!;
    private RecordingServiceCollection _wrapper = null!;

    [SetUp]
    public void Setup()
    {
        _inner = new ServiceCollection();
        _inner.AddLogging(builder => builder.AddConsole());
        _wrapper = WrapperFixture.Wrap(_inner, out _);
    }

    /// <summary>
    /// The carve-out is about what a plugin may remove, not a ban on a plugin shipping its own sink.
    /// </summary>
    [Test]
    public void It_is_permitted()
    {
        Action addition = () =>
            _wrapper.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, FixtureLoggerProvider>());

        addition.Should().NotThrow();
        _inner
            .Should()
            .Contain(descriptor =>
                descriptor.ServiceType == typeof(ILoggerProvider)
                && descriptor.ImplementationType == typeof(FixtureLoggerProvider)
            );
    }
}

[TestFixture]
public class Given_a_hook_that_removes_a_pre_existing_framework_descriptor_outside_the_logging_set
{
    private ServiceCollection _inner = null!;
    private RecordingServiceCollection _wrapper = null!;

    [SetUp]
    public void Setup()
    {
        _inner = new ServiceCollection();
        _inner.AddLogging(builder => builder.AddConsole());
        _wrapper = WrapperFixture.Wrap(_inner, out _);
    }

    /// <summary>
    /// Permitted, because the host cannot enumerate in advance every framework descriptor a legitimate
    /// plugin might replace and the cost of being wrong is refusing a legitimate plugin. The record of
    /// what was displaced comes from the per-hook diff, which arrives with the invoker.
    /// </summary>
    [Test]
    public void It_is_permitted()
    {
        _inner.Should().Contain(descriptor => descriptor.ServiceType == typeof(IOptions<>));

        Action removal = () => _wrapper.RemoveAll(typeof(IOptions<>));

        removal.Should().NotThrow();
        _inner.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IOptions<>));
    }
}

[TestFixture]
public class Given_a_hook_that_replaces_a_descriptor_it_registered_itself
{
    private ServiceCollection _inner = null!;
    private RecordingServiceCollection _wrapper = null!;

    [SetUp]
    public void Setup()
    {
        _inner = WrapperFixture.HostPopulated();
        _wrapper = WrapperFixture.Wrap(_inner, out _);
    }

    [Test]
    public void It_is_permitted_for_a_service_type_the_plugin_owns()
    {
        _wrapper.AddSingleton<IPluginOwnedService, PluginOwnedService>();

        Action replacement = () =>
            _wrapper.Replace(ServiceDescriptor.Singleton<IPluginOwnedService, SecondPluginOwnedService>());

        replacement.Should().NotThrow();
        _inner
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(IPluginOwnedService))
            .Which.ImplementationType.Should()
            .Be(typeof(SecondPluginOwnedService));
    }

    /// <summary>
    /// The sharper case: the service type is host-owned and the host registered no descriptor for it,
    /// so the only thing making this legal is that the descriptor being displaced is the plugin's own.
    /// That is what pins the rule to pre-existence rather than to the service type.
    /// </summary>
    [Test]
    public void It_is_permitted_even_for_a_host_owned_service_type_the_host_did_not_register()
    {
        _wrapper.AddSingleton<IFixtureHostUnclaimedService, FixtureHostService>();

        Action replacement = () =>
            _wrapper.Replace(
                ServiceDescriptor.Singleton<IFixtureHostUnclaimedService, SecondFixtureHostService>()
            );

        replacement.Should().NotThrow();
        _inner
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(IFixtureHostUnclaimedService))
            .Which.ImplementationType.Should()
            .Be(typeof(SecondFixtureHostService));
    }

    [Test]
    public void It_is_permitted_to_insert_beside_a_pre_existing_host_owned_descriptor()
    {
        Action insertion = () =>
            _wrapper.Insert(0, ServiceDescriptor.Singleton<IPluginOwnedService, PluginOwnedService>());

        insertion.Should().NotThrow();
        _inner[0].ServiceType.Should().Be(typeof(IPluginOwnedService));
    }
}
