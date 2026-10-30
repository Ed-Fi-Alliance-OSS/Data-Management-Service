// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

/// <summary>
/// Calls the diagnostic directly with an in-memory configuration and a fake environment, so the
/// condition and the parsing are pinned without a host or an identity provider.
/// </summary>
public abstract class RequireHttpsMetadataStartupWarningTests
{
    protected List<(LogLevel Level, string Message)> Entries { get; } = [];

    protected void Emit(string environmentName, params (string Key, string Value)[] settings)
    {
        // NUnit reuses one fixture instance for every test in it, and Setup runs before each.
        Entries.Clear();

        IHostEnvironment environment = A.Fake<IHostEnvironment>();
        A.CallTo(() => environment.EnvironmentName).Returns(environmentName);

        RequireHttpsMetadataStartupWarning.Emit(
            new RecordingLogger(Entries),
            environment,
            BuildConfiguration(settings)
        );
    }

    protected static IConfigurationRoot BuildConfiguration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value))
            )
            .Build();

    private sealed class RecordingLogger(List<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => entries.Add((logLevel, formatter(state, exception)));
    }

    [TestFixture]
    public class Given_Https_Metadata_Not_Required_In_Production : RequireHttpsMetadataStartupWarningTests
    {
        [SetUp]
        public void Setup() => Emit("Production", (RequireHttpsMetadataStartupWarning.SettingKey, "false"));

        [Test]
        public void It_logs_one_warning() => Entries.Should().ContainSingle();

        [Test]
        public void It_logs_the_expected_message() =>
            Entries
                .Single()
                .Message.Should()
                .Be(
                    "JwtAuthentication:RequireHttpsMetadata is false and the hosting environment is not Development. "
                        + "OIDC metadata and signing keys may be fetched without requiring https. "
                        + "Set JwtAuthentication:RequireHttpsMetadata to true unless the identity provider is reachable only over a trusted network."
                );

        [Test]
        public void It_logs_at_warning_level() => Entries.Single().Level.Should().Be(LogLevel.Warning);
    }

    [TestFixture]
    public class Given_Https_Metadata_Not_Required_In_Development : RequireHttpsMetadataStartupWarningTests
    {
        [SetUp]
        public void Setup() => Emit("Development", (RequireHttpsMetadataStartupWarning.SettingKey, "false"));

        [Test]
        public void It_logs_nothing() => Entries.Should().BeEmpty();
    }

    [TestFixture("Test")]
    [TestFixture("Staging")]
    public class Given_Https_Metadata_Not_Required_In_Another_Environment(string environmentName)
        : RequireHttpsMetadataStartupWarningTests
    {
        [SetUp]
        public void Setup() =>
            Emit(environmentName, (RequireHttpsMetadataStartupWarning.SettingKey, "false"));

        [Test]
        public void It_logs_one_warning() =>
            Entries
                .Should()
                .ContainSingle()
                .Which.Message.Should()
                .Be(RequireHttpsMetadataStartupWarning.Message);
    }

    [TestFixture]
    public class Given_Https_Metadata_Required_In_Production : RequireHttpsMetadataStartupWarningTests
    {
        [SetUp]
        public void Setup() => Emit("Production", (RequireHttpsMetadataStartupWarning.SettingKey, "true"));

        [Test]
        public void It_logs_nothing() => Entries.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_No_Https_Metadata_Value_In_Production : RequireHttpsMetadataStartupWarningTests
    {
        [SetUp]
        public void Setup() => Emit("Production");

        [Test]
        public void It_logs_nothing() => Entries.Should().BeEmpty();
    }

    [TestFixture("TRUE", true)]
    [TestFixture("False", false)]
    [TestFixture(" true ", true)]
    [TestFixture("\tfalse ", false)]
    public class Given_A_Boolean_Spelling_The_Binder_Accepts(string spelling, bool expected)
        : RequireHttpsMetadataStartupWarningTests
    {
        [SetUp]
        public void Setup() => Emit("Production", (RequireHttpsMetadataStartupWarning.SettingKey, spelling));

        [Test]
        public void It_warns_only_when_the_value_parses_as_false()
        {
            if (expected)
            {
                Entries.Should().BeEmpty();
            }
            else
            {
                Entries
                    .Should()
                    .ContainSingle()
                    .Which.Message.Should()
                    .Be(RequireHttpsMetadataStartupWarning.Message);
            }
        }

        [Test]
        public void It_reads_the_value_the_options_binder_reads()
        {
            JwtAuthenticationOptions options = new();
            BuildConfiguration((RequireHttpsMetadataStartupWarning.SettingKey, spelling))
                .GetSection("JwtAuthentication")
                .Bind(options);

            options.RequireHttpsMetadata.Should().Be(expected);
        }
    }

    [TestFixture("")]
    [TestFixture("yes")]
    [TestFixture("1")]
    [TestFixture("0")]
    [TestFixture("no")]
    [TestFixture("fals e")]
    public class Given_An_Unreadable_Https_Metadata_Value_In_Production(string value)
        : RequireHttpsMetadataStartupWarningTests
    {
        [SetUp]
        public void Setup() => Emit("Production", (RequireHttpsMetadataStartupWarning.SettingKey, value));

        [Test]
        public void It_logs_only_the_unreadable_value_notice() =>
            Entries
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be((LogLevel.Warning, RequireHttpsMetadataStartupWarning.UnreadableMessage));

        [Test]
        public void It_is_rejected_by_the_options_binder()
        {
            IConfigurationRoot configuration = BuildConfiguration(
                (RequireHttpsMetadataStartupWarning.SettingKey, value)
            );

            Action bind = () =>
                configuration.GetSection("JwtAuthentication").Bind(new JwtAuthenticationOptions());

            bind.Should().Throw<InvalidOperationException>();
        }
    }
}
