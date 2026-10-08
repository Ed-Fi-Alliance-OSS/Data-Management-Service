// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

/// <summary>
/// A disposed test host must become unreachable. Every host test in this project boots its own
/// host, so a host that stays reachable after disposal (for example through a still-armed timer in
/// the runtime's global timer queue) accumulates across the run until the test process runs out of
/// memory.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_A_Test_Host_That_Served_Requests_And_Was_Disposed
{
    private WeakReference _hostServices = default!;

    [OneTimeSetUp]
    public async Task Setup()
    {
        _hostServices = await BootServeAndDisposeHost();

#pragma warning disable S1215 // Forcing collection is the point: reachability is only observable after a GC
        for (int attempt = 0; attempt < 5 && _hostServices.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }
#pragma warning restore S1215
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> BootServeAndDisposeHost()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection => TestMockHelper.AddEssentialMocks(collection));
        });

        using (HttpClient client = factory.CreateClient())
        {
            (await client.GetAsync("/health")).Dispose();
            (await client.GetAsync("/")).Dispose();
        }

        var hostServices = new WeakReference(factory.Services);
        await factory.DisposeAsync();
        return hostServices;
    }

    [Test]
    public void It_releases_the_host_service_provider()
    {
        _hostServices.IsAlive.Should().BeFalse();
    }
}
