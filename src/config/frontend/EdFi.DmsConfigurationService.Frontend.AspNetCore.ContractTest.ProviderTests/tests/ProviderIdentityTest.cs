// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using PactNet.Verifier;
using PactNet;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.Provider.Tests
{
    [TestFixture]
    public class ProviderIdentityTest : IDisposable
    {
        private static readonly JsonSerializerOptions _options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private IHost _host;
        private IPactVerifier verifier;
        private Uri pactURL = new Uri("http://localhost:5126");

        public ProviderIdentityTest()
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseEnvironment("Test");
                    webBuilder.UseUrls(pactURL.ToString()); // Explicitly set the URL and port
                    webBuilder.UseStartup<TestStartup>(); // Use TestStartup class for test configuration
                                                          // Configure logging.
                    webBuilder.ConfigureLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddConsole();
                        logging.SetMinimumLevel(LogLevel.Debug);
                    });

                })
                .Build();

            _host.Start(); // Start the host explicitly
            // Allow some time for the server to start before running the verification
            Thread.Sleep(2000);

            verifier = new PactVerifier(new PactVerifierConfig()
            {
                LogLevel = PactLogLevel.Debug
            });
        }

        [Test]
        public void Verify()
        {
            string pactFile = Path.Combine("..",
                                                   "..",
                                                   "..",
                                                   "..",
                                                   "EdFi.DmsConfigurationService.Frontend.AspNetCore.ContractTest.ConsumerTests",
                                                   "pacts",
                                                   "DMS API Consumer-DMS Configuration Service API.json");

            verifier!.ServiceProvider("DMS Configuration Service API", pactURL)
                .WithFileSource(new FileInfo(pactFile))
                .WithProviderStateUrl(new Uri("http://localhost:5126/provider-states"))
                .Verify();

        }

        #region IDisposable Support

        private bool _disposed = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                //server.Dispose();
            }

            _disposed = true;
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
