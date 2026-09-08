// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.Api.Plugins.Hosting.Tests.Unit;

/// <summary>
/// Builds an <see cref="IConfiguration"/> the way a host does, from JSON, so that the binder is
/// exercised through real configuration rather than through a hand-made options instance.
/// </summary>
internal static class PluginsConfigurationFrom
{
    internal static IConfiguration Json(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    internal static IConfiguration Allowed(string allowed) =>
        Json("{\"Plugins\": {\"Allowed\": " + JsonSerializer.Serialize(allowed) + "}}");

    internal static IConfiguration Directory(string directory) =>
        Json("{\"Plugins\": {\"Directory\": " + JsonSerializer.Serialize(directory) + "}}");
}

[TestFixture]
public class Given_a_configuration_with_no_plugins_section
{
    private PluginsConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Json("{}"));
    }

    [Test]
    public void It_allows_no_plugins()
    {
        _configuration.AllowedNames.Should().BeEmpty();
    }

    [Test]
    public void It_uses_the_default_plugin_root()
    {
        // The shipped default is /app/plugins. On a platform where that is rooted but not fully
        // qualified it acquires the base directory's volume, so the assertion is on the tail rather
        // than on the whole string.
        _configuration.ResolvedRoot.Replace('\\', '/').Should().EndWith("/app/plugins");
    }

    [Test]
    public void It_resolves_the_root_to_a_fully_qualified_path()
    {
        Path.IsPathFullyQualified(_configuration.ResolvedRoot).Should().BeTrue();
    }
}

[TestFixture]
public class Given_an_allowlist_written_out_of_order_with_whitespace_and_empty_entries
{
    private PluginsConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = PluginsConfigurationBinder.Bind(
            PluginsConfigurationFrom.Allowed("  Zulu.Plugin , ,Alpha.Plugin,   ,Mike.Plugin  ")
        );
    }

    [Test]
    public void It_keeps_the_order_the_operator_wrote()
    {
        // Order is contractual for the composition phases, so a sorted result would be wrong even
        // though it contains the same names.
        _configuration.AllowedNames.Should().Equal("Zulu.Plugin", "Alpha.Plugin", "Mike.Plugin");
    }

    [Test]
    public void It_drops_empty_entries()
    {
        _configuration.AllowedNames.Should().HaveCount(3);
    }

    [Test]
    public void It_trims_surrounding_whitespace_from_every_entry()
    {
        _configuration.AllowedNames.Should().OnlyContain(name => name == name.Trim());
    }
}

[TestFixture]
public class Given_an_allowlist_of_one_name
{
    private PluginsConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Allowed("Acme.Good"));
    }

    [Test]
    public void It_allows_that_one_name()
    {
        _configuration.AllowedNames.Should().Equal("Acme.Good");
    }
}

[TestFixture]
public class Given_an_allowlist_that_is_only_separators_and_whitespace
{
    private PluginsConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Allowed(" , , "));
    }

    [Test]
    public void It_allows_no_plugins()
    {
        // Indistinguishable from an absent allowlist, which is the continue-silently case rather than
        // a list of empty names.
        _configuration.AllowedNames.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_an_allowlist_naming_one_plugin_twice
{
    private PluginLoadException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(
                PluginsConfigurationFrom.Allowed("Acme.Good,Other.Plugin,Acme.Good")
            )
        );
    }

    [Test]
    public void It_refuses_the_ambiguous_allowlist()
    {
        _exception.Reason.Should().Be(PluginLoadFailure.DuplicateAllowlistEntry);
    }

    [Test]
    public void It_names_the_duplicate()
    {
        _exception.PluginName.Should().Be("Acme.Good");
        _exception.Message.Should().Contain("Acme.Good");
    }
}

[TestFixture]
public class Given_an_allowlist_whose_duplicate_appears_only_after_trimming
{
    private PluginLoadException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Allowed("Acme.Good,  Acme.Good  "))
        );
    }

    [Test]
    public void It_refuses_the_ambiguous_allowlist()
    {
        _exception.Reason.Should().Be(PluginLoadFailure.DuplicateAllowlistEntry);
    }
}

[TestFixture]
public class Given_an_allowlist_whose_entries_differ_only_in_case
{
    private PluginLoadException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Allowed("Acme.Good,acme.good"))
        );
    }

    [Test]
    public void It_refuses_the_pair_as_ambiguous()
    {
        // Two spellings of one name would resolve to one directory on a case-insensitive filesystem
        // and to two on the image's, so the operator's intent cannot be recovered from the list.
        _exception.Reason.Should().Be(PluginLoadFailure.DuplicateAllowlistEntry);
    }

    [Test]
    public void It_names_the_second_spelling()
    {
        _exception.PluginName.Should().Be("acme.good");
    }
}

[TestFixture]
public class Given_an_allowlist_entry_that_is_not_a_single_path_segment
{
    private static readonly string[] Rejected =
    [
        "/etc/passwd",
        @"C:\Windows",
        "..",
        "../sibling",
        @"..\sibling",
        "sub/dir",
        @"sub\dir",
        ".hidden",
        "-leading-dash",
        "_leading_underscore",
        "has space",
        "quote'name",
    ];

    [TestCaseSource(nameof(Rejected))]
    public void It_refuses_the_entry_and_names_the_rule(string entry)
    {
        PluginLoadException exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Allowed(entry))
        );

        exception.Reason.Should().Be(PluginLoadFailure.InvalidAllowlistName);
        exception.PluginName.Should().Be(entry);
        exception.Message.Should().Contain(PluginsConfigurationBinder.AllowedNameRule);
    }
}

[TestFixture]
public class Given_an_allowlist_entry_of_the_permitted_shape
{
    private static readonly string[] Accepted =
    [
        "A",
        "0",
        "Acme.Good",
        "Acme_Plugin",
        "Acme-Plugin",
        "Acme.Dms.Identity",
        "acme.good",
    ];

    [TestCaseSource(nameof(Accepted))]
    public void It_accepts_the_entry(string entry)
    {
        PluginsConfigurationBinder
            .Bind(PluginsConfigurationFrom.Allowed(entry))
            .AllowedNames.Should()
            .Equal(entry);
    }
}

[TestFixture]
public class Given_a_valid_allowlist_entry_followed_by_an_invalid_one
{
    private PluginLoadException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Allowed("Acme.Good,../sibling"))
        );
    }

    [Test]
    public void It_refuses_the_whole_allowlist_rather_than_acting_on_the_valid_entry()
    {
        // The shape rule runs over every entry before anything is done with any of them, so a well
        // formed first entry cannot buy the list a partial pass.
        _exception.Reason.Should().Be(PluginLoadFailure.InvalidAllowlistName);
        _exception.PluginName.Should().Be("../sibling");
    }
}

[TestFixture]
public class Given_a_valid_allowlist_entry_followed_by_a_duplicate
{
    private PluginLoadException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(
                PluginsConfigurationFrom.Allowed("Acme.Good,Other.Plugin,other.plugin")
            )
        );
    }

    [Test]
    public void It_refuses_the_whole_allowlist()
    {
        _exception.Reason.Should().Be(PluginLoadFailure.DuplicateAllowlistEntry);
        _exception.PluginName.Should().Be("other.plugin");
    }
}

[TestFixture]
public class Given_an_allowlist_entry_that_is_both_duplicated_and_malformed
{
    private PluginLoadException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Allowed("sub/dir,sub/dir"))
        );
    }

    [Test]
    public void It_reports_the_ambiguity_first()
    {
        // Both rules apply, so the order they run in is observable and is pinned here: ambiguity is
        // settled before shape, so the message an operator reads does not depend on which entry the
        // implementation happened to examine first.
        _exception.Reason.Should().Be(PluginLoadFailure.DuplicateAllowlistEntry);
    }
}

[TestFixture]
public class Given_an_allowlist_entry_carrying_a_line_break
{
    private PluginLoadException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Allowed("Acme\r\nplugins: forged line"))
        );
    }

    [Test]
    public void It_refuses_the_entry()
    {
        _exception.Reason.Should().Be(PluginLoadFailure.InvalidAllowlistName);
    }

    [Test]
    public void It_escapes_the_line_break_rather_than_reproducing_it()
    {
        // The loader's messages go to a line-oriented channel and are re-emitted through the host's
        // logger, so a configuration value carrying a line break would otherwise forge a line in both.
        _exception.Message.Should().NotContain("\n");
        _exception.Message.Should().NotContain("\r");
        _exception.Message.Should().Contain(@"Acme\r\nplugins: forged line");
    }
}

[TestFixture]
public class Given_a_relative_plugin_directory
{
    private PluginsConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        _configuration = PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Directory("local-plugins"));
    }

    [Test]
    public void It_resolves_the_directory_against_the_application_base_directory()
    {
        _configuration.ResolvedRoot.Should().Be(Path.GetFullPath("local-plugins", AppContext.BaseDirectory));
    }
}

[TestFixture]
public class Given_a_plugin_directory_that_needs_normalizing
{
    private PluginsConfiguration _configuration = null!;

    [SetUp]
    public void Setup()
    {
        string unnormalized = Path.Combine(AppContext.BaseDirectory, "a", "..", "b");

        _configuration = PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Directory(unnormalized));
    }

    [Test]
    public void It_normalizes_the_directory()
    {
        _configuration.ResolvedRoot.Should().Be(Path.Combine(AppContext.BaseDirectory, "b"));
    }
}

[TestFixture]
public class Given_a_plugin_directory_that_is_present_but_empty
{
    private PluginLoadException _exception = null!;

    [SetUp]
    public void Setup()
    {
        _exception = CaptureLoadFailure.From(() =>
            PluginsConfigurationBinder.Bind(PluginsConfigurationFrom.Directory("   "))
        );
    }

    [Test]
    public void It_refuses_rather_than_falling_back_to_the_default_root()
    {
        // An operator who cleared the setting did not ask for /app/plugins to be read, so silently
        // restoring the default would load plugins nobody asked for.
        _exception.Reason.Should().Be(PluginLoadFailure.PluginPathUnresolvable);
    }
}

/// <summary>
/// Captures the loader failure a call is expected to produce, so that every fixture asserts on the
/// same shape rather than each spelling its own try/catch.
/// </summary>
internal static class CaptureLoadFailure
{
    internal static PluginLoadException From(Func<PluginsConfiguration> act)
    {
        try
        {
            PluginsConfiguration configuration = act();

            throw new AssertionException(
                "Expected a PluginLoadException, but the configuration bound successfully to "
                    + $"root '{configuration.ResolvedRoot}' with "
                    + $"[{string.Join(", ", configuration.AllowedNames)}]."
            );
        }
        catch (PluginLoadException exception)
        {
            return exception;
        }
    }
}
