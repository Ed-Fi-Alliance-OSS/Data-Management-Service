// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcServiceRegistration")]
public class Given_CdcServiceCollectionExtensions
{
    [Test]
    public void It_registers_the_local_cdc_state_store_surface()
    {
        IServiceCollection services = new ServiceCollection();

        services.AddDmsCdcControlPlane();

        ServiceDescriptor lifecycleDescriptor = services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(ICdcBindingLifecycleService))
            .Subject;
        ServiceDescriptor stateStoreDescriptor = services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(ICdcBindingStateStore))
            .Subject;
        ServiceDescriptor permissionDescriptor = services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(ICdcLocalStateStorePermissions))
            .Subject;

        lifecycleDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        lifecycleDescriptor.ImplementationType.Should().Be(typeof(CdcBindingLifecycleService));
        stateStoreDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        stateStoreDescriptor.ImplementationFactory.Should().NotBeNull();
        stateStoreDescriptor.ImplementationType.Should().BeNull();
        stateStoreDescriptor.ImplementationInstance.Should().BeNull();
        permissionDescriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);
        permissionDescriptor.ImplementationInstance.Should().BeOfType<CdcLocalStateStorePermissions>();
        services
            .Should()
            .ContainSingle(descriptor => descriptor.ServiceType == typeof(TimeProvider))
            .Which.ImplementationInstance.Should()
            .BeSameAs(TimeProvider.System);
    }

    [Test]
    public async Task It_resolves_the_local_cdc_state_store_from_configured_options_without_startup_mutation()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), $"cdc-service-registration-{Guid.NewGuid():N}");

        try
        {
            IServiceCollection services = new ServiceCollection();
            services.Configure<CdcBindingStateStoreOptions>(options => options.RootPath = rootPath);
            services.AddDmsCdcControlPlane();

            using ServiceProvider serviceProvider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
            );

            Directory.Exists(rootPath).Should().BeFalse();

            ICdcBindingLifecycleService lifecycle =
                serviceProvider.GetRequiredService<ICdcBindingLifecycleService>();

            Directory.Exists(rootPath).Should().BeFalse();

            CdcBinding binding = CdcTargetStatusFixture.CreateBinding();
            CdcBindingLifecycleResult result = await lifecycle.CreateBindingIfAbsentAsync(
                binding,
                CancellationToken.None
            );

            result.Status.Should().Be(CdcControlPlaneOperationStatus.Succeeded);
            result.State.Should().NotBeNull();
            result.State!.State.Should().Be(CdcBindingState.BindingPresent);
            File.Exists(
                    Path.Combine(
                        rootPath,
                        "bindings",
                        binding.DeploymentKey,
                        binding.InstanceKey,
                        $"{binding.Generation}.json"
                    )
                )
                .Should()
                .BeTrue();
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, true);
            }
        }
    }

    [Test]
    public void It_keeps_cdc_control_plane_services_out_of_the_default_core_configuration()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        IServiceCollection services = new ServiceCollection();

        services.AddDmsDefaultConfiguration(
            new LoggerConfiguration().CreateLogger(),
            configuration.GetSection("CircuitBreaker"),
            configuration.GetSection("DeadlockRetry"),
            false
        );

        services
            .Should()
            .NotContain(descriptor => descriptor.ServiceType == typeof(ICdcBindingLifecycleService));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(ICdcBindingStateStore));
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IDmsStartupTask))
            .Should()
            .NotContain(descriptor => IsCdcStartupTask(descriptor));
    }

    [Test]
    public void It_exposes_only_public_types_on_the_cdc_controller_boundaries()
    {
        AssertPublicSurface(typeof(ICdcBindingLifecycleService));
        AssertPublicSurface(typeof(ICdcProviderSourcePositionAdapter));
    }

    private static bool IsCdcStartupTask(ServiceDescriptor descriptor)
    {
        string? fullName = descriptor.ImplementationType?.FullName;

        return fullName is not null && fullName.Contains(".DocumentCache.Cdc.", StringComparison.Ordinal);
    }

    private static void AssertPublicSurface(Type serviceType)
    {
        serviceType.IsPublic.Should().BeTrue();

        foreach (var method in serviceType.GetMethods())
        {
            method.ReturnType.IsVisible.Should().BeTrue(method.Name);
            foreach (var parameter in method.GetParameters())
            {
                parameter.ParameterType.IsVisible.Should().BeTrue($"{method.Name}.{parameter.Name}");
            }
        }

        foreach (var property in serviceType.GetProperties())
        {
            property.PropertyType.IsVisible.Should().BeTrue(property.Name);
        }
    }
}
