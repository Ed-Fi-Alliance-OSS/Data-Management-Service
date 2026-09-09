// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// The containment check resolves symbolic links rather than normalizing lexically, and it composes the
/// plugin path against the resolved root. All three of these cases are required together: an
/// implementation that passes any one of them alone is a plausible wrong answer.
/// </summary>
[TestFixture]
public class Given_a_plugin_directory_that_is_a_link_out_of_the_root
{
    private TemporaryPluginRoot _root = null!;
    private string _outside = null!;
    private string _link = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create();
        _outside = _root.CreateDirectoryOutsideRoot("outside");
        TemporaryPluginRoot.AddTo(_outside, PluginFixtures.Good, asName: "Acme.Escape");

        _link = TemporaryPluginRoot.CreateDirectoryLink(
            Path.Combine(_root.RootPath, "Acme.Escape"),
            Path.Combine(_outside, "Acme.Escape")
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_the_plugin()
    {
        // The name rule cannot see this: "Acme.Escape" is a perfectly good single path segment.
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, "Acme.Escape");

        run.Failure!.Reason.Should().Be(PluginLoadFailure.EscapesPluginRoot);
    }

    [Test]
    public void It_names_both_resolved_paths()
    {
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(_root.RootPath, "Acme.Escape");

        run.Failure!.Message.Should().Contain(Path.Combine(_outside, "Acme.Escape"));
        run.Failure!.Message.Should().Contain(_root.RootPath);
    }

    [Test]
    public void It_is_a_case_a_lexical_check_would_pass()
    {
        // Path.GetFullPath normalizes lexically and never reads the filesystem, so the composed path
        // starts with the root while the assembly that would load is the one outside it. This asserts
        // the fixture really is that case, so the test above cannot pass for the wrong reason.
        string composed = Path.GetFullPath(Path.Combine(_root.RootPath, "Acme.Escape"));
        composed.Should().StartWith(_root.RootPath);

        string resolved = Directory.ResolveLinkTarget(_link, returnFinalTarget: true)!.FullName;
        resolved.Should().NotStartWith(_root.RootPath);
    }
}

[TestFixture]
public class Given_a_well_formed_plugin_under_a_root_that_is_itself_a_link
{
    private TemporaryPluginRoot _root = null!;
    private string _linkedRoot = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create(rootName: "realplugins");
        _root.Add(PluginFixtures.Good);

        _linkedRoot = TemporaryPluginRoot.CreateDirectoryLink(
            Path.Combine(Path.GetDirectoryName(_root.RootPath)!, "app-plugins"),
            _root.RootPath
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_loads_the_plugin()
    {
        // Composing against the configured root rather than the resolved one rejects this: the resolved
        // root is .../realplugins while the composed path is .../app-plugins/Acme.Good, and the ordinary
        // plugin gets reported as escaping. Resolving the root once and composing against it is what
        // makes both this and the escape case come out right.
        PluginLoaderRun run = PluginLoaderProbe.Run(_linkedRoot, PluginFixtures.Good);

        run.Failure.Should().BeNull();
        run.Result!.Plugins.Select(plugin => plugin.Name).Should().Equal(PluginFixtures.Good);
    }
}

[TestFixture]
public class Given_a_plugin_directory_that_is_a_link_out_of_a_linked_root
{
    private TemporaryPluginRoot _root = null!;
    private string _linkedRoot = null!;

    [SetUp]
    public void Setup()
    {
        _root = TemporaryPluginRoot.Create(rootName: "realplugins");

        string outside = _root.CreateDirectoryOutsideRoot("outside");
        TemporaryPluginRoot.AddTo(outside, PluginFixtures.Good, asName: "Acme.Escape");

        TemporaryPluginRoot.CreateDirectoryLink(
            Path.Combine(_root.RootPath, "Acme.Escape"),
            Path.Combine(outside, "Acme.Escape")
        );

        _linkedRoot = TemporaryPluginRoot.CreateDirectoryLink(
            Path.Combine(Path.GetDirectoryName(_root.RootPath)!, "app-plugins"),
            _root.RootPath
        );
    }

    [TearDown]
    public void TearDown() => _root.Dispose();

    [Test]
    public void It_refuses_the_plugin()
    {
        // The combination neither of the other two cases reaches: both the root and the plugin directory
        // are links, so an implementation that resolves only one of them gets this wrong.
        PluginLoaderRun run = PluginLoaderProbe.RunExpectingFailure(_linkedRoot, "Acme.Escape");

        run.Failure!.Reason.Should().Be(PluginLoadFailure.EscapesPluginRoot);
    }
}
