// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture("postgresql")]
[TestFixture("mssql")]
public class Given_CdcComposeDataStoreProvider(string provider)
{
    private IDataStoreProvider _inner = null!;
    private IDataStoreProvider _subject = null!;
    private DataStore _selected = null!;

    [SetUp]
    public void Setup()
    {
        _inner = A.Fake<IDataStoreProvider>();
        _selected = new(
            42,
            "SharedInstance",
            "selected",
            provider == "postgresql"
                ? "Host=dms-postgresql;Port=5432;Database=original;Username=original-user;Password=original-password"
                : "Server=dms-mssql,1433;Database=original;User Id=original-user;Password=original-password;TrustServerCertificate=true",
            []
        );
        A.CallTo(() => _inner.GetAll(A<string>.Ignored))
            .ReturnsLazily(() => [_selected, _selected with { Id = 43 }]);
        A.CallTo(() => _inner.LoadDataStores(A<string>.Ignored, A<CancellationToken>.Ignored))
            .ReturnsLazily(() => Task.FromResult<IList<DataStore>>([_selected, _selected with { Id = 43 }]));
        _subject = new CdcComposeDataStoreProvider(
            _inner,
            DocumentCacheTargetKey.Create("", 42),
            provider,
            15432
        );
    }

    [Test]
    public void It_changes_only_the_compose_endpoint_and_preserves_the_selected_database_and_credentials()
    {
        var selected = _subject.GetById(42)!;
        if (provider == "postgresql")
        {
            var connection = new NpgsqlConnectionStringBuilder(selected.ConnectionString);
            connection.Host.Should().Be("127.0.0.1");
            connection.Port.Should().Be(15432);
            connection.Database.Should().Be("original");
            connection.Username.Should().Be("original-user");
            connection.Password.Should().Be("original-password");
            connection.Host = "dms-postgresql";
            connection.Port = 5432;
            connection
                .ConnectionString.Should()
                .Be(new NpgsqlConnectionStringBuilder(_selected.ConnectionString).ConnectionString);
        }
        else
        {
            var connection = new SqlConnectionStringBuilder(selected.ConnectionString);
            connection.DataSource.Should().Be("127.0.0.1,15432");
            connection.InitialCatalog.Should().Be("original");
            connection.UserID.Should().Be("original-user");
            connection.Password.Should().Be("original-password");
            connection.DataSource = "dms-mssql,1433";
            connection
                .ConnectionString.Should()
                .Be(new SqlConnectionStringBuilder(_selected.ConnectionString).ConnectionString);
        }
        selected.Should().BeEquivalentTo(_selected, o => o.Excluding(d => d.ConnectionString));
    }

    [Test]
    public async Task It_exposes_only_the_explicit_selected_target_to_runtime_initialization()
    {
        (await _subject.LoadDataStores()).Should().ContainSingle(d => d.Id == 42);
        _subject.GetAll().Should().ContainSingle(d => d.Id == 42);
        _subject.GetById(43).Should().BeNull();
        _subject.GetAll("other-tenant").Should().BeEmpty();
        _subject.GetById(42, "other-tenant").Should().BeNull();
    }

    [Test]
    public void It_does_not_translate_an_unrelated_source_endpoint()
    {
        _selected = _selected with
        {
            ConnectionString = _selected
                .ConnectionString!.Replace("dms-postgresql", "external")
                .Replace("dms-mssql", "external"),
        };
        Action act = () => _subject.GetById(42);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("CDC CMS target is outside the selected Compose endpoint.");
    }

    [Test]
    public async Task It_preserves_refresh_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await _subject.RefreshInstancesIfExpiredAsync("", cancellation.Token);
        A.CallTo(() => _inner.RefreshInstancesIfExpiredAsync("", cancellation.Token))
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public void It_registers_translation_only_when_the_host_side_compose_port_is_explicit()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDataStoreProvider>(_ => _inner);
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AppSettings:Datastore"] = provider })
            .Build();
        var target = DocumentCacheTargetKey.Create("", 42);
        CdcComposeDataStoreProvider.Register(services, settings, target);
        using (var untouched = services.BuildServiceProvider())
        {
            untouched.GetRequiredService<IDataStoreProvider>().Should().BeSameAs(_inner);
        }
        settings["Cdc:Compose:DatabaseHostPort"] = "15432";
        CdcComposeDataStoreProvider.Register(services, settings, target);
        using var translated = services.BuildServiceProvider();
        translated.GetRequiredService<IDataStoreProvider>().Should().BeOfType<CdcComposeDataStoreProvider>();
    }
}
