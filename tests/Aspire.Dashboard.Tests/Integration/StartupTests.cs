// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Http;
using Aspire.Hosting;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OpenTelemetry.Proto.Collector.Logs.V1;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class StartupTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task EndPointAccessors_AppStarted_EndPointPortsAssigned()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "https://127.0.0.1:0;http://127.0.0.1:0";
                data[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "http://127.0.0.1:0";
                data[DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey] = "http://127.0.0.1:0";
            });

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert
        Assert.Collection(app.FrontendEndPointsAccessor,
            a =>
            {
                Assert.True(a().IsHttps);
                AssertDynamicIPEndpoint(a);
            },
            a =>
            {
                Assert.False(a().IsHttps);
                AssertDynamicIPEndpoint(a);
            });

        AssertDynamicIPEndpoint(app.OtlpServiceGrpcEndPointAccessor);
        AssertDynamicIPEndpoint(app.OtlpServiceHttpEndPointAccessor);
    }

    [Fact]
    public async Task EndPointAccessors_AppStarted_IPv4OrIPv6()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "https://+:0;http://+:0";
                data[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "http://+:0";
                data[DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey] = "http://+:0";
            });

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert,
        Assert.Collection(app.FrontendEndPointsAccessor,
            a =>
            {
                Assert.True(a().IsHttps);
                AssertDynamicIPEndpoint(a);
                AssertIPv4OrIPv6Endpoint(a);
            },
            a =>
            {
                Assert.False(a().IsHttps);
                AssertDynamicIPEndpoint(a);
                AssertIPv4OrIPv6Endpoint(a);
            });

        AssertDynamicIPEndpoint(app.OtlpServiceGrpcEndPointAccessor);
        AssertIPv4OrIPv6Endpoint(app.OtlpServiceGrpcEndPointAccessor);

        AssertDynamicIPEndpoint(app.OtlpServiceHttpEndPointAccessor);
        AssertIPv4OrIPv6Endpoint(app.OtlpServiceGrpcEndPointAccessor);
    }

    [Fact]
    public async Task Configuration_NoExtraConfig_Error()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data.Clear();
            });

        // Assert
        Assert.Collection(app.ValidationFailures,
            s => Assert.Contains(KnownConfigNames.AspNetCoreUrls, s),
            s => Assert.Contains(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, s));
    }

    [Fact]
    public async Task Configuration_EmptyAllowedCertificateRule_Error()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data["Dashboard:Otlp:AuthMode"] = nameof(OtlpAuthMode.ClientCertificate);
                data["Dashboard:Otlp:AllowedCertificates:0"] = string.Empty;
            });

        // Assert
        Assert.Collection(app.ValidationFailures,
            s => Assert.Contains("Dashboard:Otlp:AllowedCertificates:0:Thumbprint", s));
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardConfigFilePath)]
    [InlineData(KnownConfigNames.Legacy.DashboardConfigFilePath)]
    public async Task Configuration_ConfigFilePathDoesntExist_Error(string dashboardConfigFilePathNameKey)
    {
        // Arrange & Act
        var configFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var ex = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                additionalConfiguration: data =>
                {
                    data[dashboardConfigFilePathNameKey] = configFilePath;
                });
        }).DefaultTimeout();

        // Assert
        Assert.Contains(configFilePath, ex.Message);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardFileConfigDirectory)]
    [InlineData(KnownConfigNames.Legacy.DashboardFileConfigDirectory)]
    public async Task Configuration_FileConfigDirectoryDoesExist_Success(string dashboardFileConfigDirectoryNameKey)
    {
        // Arrange
        const string frontendBrowserToken = "SomeSecretContent";
        var fileConfigDirectory = Directory.CreateTempSubdirectory();
        var browserTokenConfigFile = await CreateBrowserTokenConfigFileAsync(fileConfigDirectory, frontendBrowserToken);
        try
        {
            var config = new ConfigurationManager()
                .AddInMemoryCollection(new Dictionary<string, string?> { [dashboardFileConfigDirectoryNameKey] = fileConfigDirectory.FullName })
                .Build();
            WebApplicationBuilder? localBuilder = null;

            // Act
            await using var dashboardWebApplication = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                preConfigureBuilder: builder =>
                {
                    builder.Configuration.AddConfiguration(config);
                    localBuilder = builder;
                });

            // Assert
            Assert.NotNull(localBuilder);
            Assert.Equal(frontendBrowserToken, localBuilder.Configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey]);
        }
        finally
        {
            File.Delete(browserTokenConfigFile);
        }
    }

    [Fact]
    public async Task Configuration_FileConfigDirectoryReloadsChanges_Success()
    {
        // Arrange
        const string initialFrontendBrowserToken = "InitialSecretContent";
        const string changedFrontendBrowserToken = "NewSecretContent";
        var fileConfigDirectory = Directory.CreateTempSubdirectory();
        var browserTokenConfigFile = await CreateBrowserTokenConfigFileAsync(fileConfigDirectory, initialFrontendBrowserToken).DefaultTimeout();
        try
        {
            var loggerFactory = IntegrationTestHelpers.CreateLoggerFactory(testOutputHelper);
            var logger = loggerFactory.CreateLogger<StartupTests>();
            var config = new ConfigurationManager()
                .AddInMemoryCollection(new Dictionary<string, string?> { [DashboardConfigNames.DashboardFileConfigDirectoryName.ConfigKey] = fileConfigDirectory.FullName })
                .Build();
            WebApplicationBuilder? localBuilder = null;
            await using var dashboardWebApplication = IntegrationTestHelpers.CreateDashboardWebApplication(loggerFactory,
                preConfigureBuilder: builder =>
                {
                    builder.Configuration.AddConfiguration(config);
                    localBuilder = builder;
                });

            // Act
            // get the initial browser token to make sure nothing went wrong until here
            var initialBrowserTokenProvidedByConfiguration = localBuilder?.Configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey];

            // update the browser token's config file and get the new value
            await File.WriteAllTextAsync(browserTokenConfigFile, changedFrontendBrowserToken).DefaultTimeout();

            // Assert
            Assert.Equal(initialFrontendBrowserToken, initialBrowserTokenProvidedByConfiguration);

            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () =>
                {
                    var updatedBrowserTokenProvidedByConfiguration = localBuilder?.Configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey];
                    return changedFrontendBrowserToken == updatedBrowserTokenProvidedByConfiguration;
                },
                "Wait for file change to be detected and propagated.", logger);
        }
        finally
        {
            File.Delete(browserTokenConfigFile);
        }
    }

    [Fact]
    public async Task Configuration_FileConfigDirectoryDoesntExist_Error()
    {
        // Arrange & Act
        var fileConfigDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                data =>
                {
                    data[DashboardConfigNames.DashboardFileConfigDirectoryName.ConfigKey] = fileConfigDirectory;
                });
        }).DefaultTimeout();

        // Assert
        Assert.Contains(fileConfigDirectory, ex.Message);
    }

    [Fact]
    public async Task Configuration_OptionsMonitor_CanReadConfiguration()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: initialData =>
            {
                initialData["Dashboard:Otlp:AuthMode"] = nameof(OtlpAuthMode.ApiKey);
                initialData["Dashboard:Otlp:PrimaryApiKey"] = "TestKey123!";
            });

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert
        Assert.Equal(OtlpAuthMode.ApiKey, app.DashboardOptionsMonitor.CurrentValue.Otlp.AuthMode);
        Assert.Equal("TestKey123!", app.DashboardOptionsMonitor.CurrentValue.Otlp.PrimaryApiKey);
    }

    [Fact]
    public async Task Configuration_OptionsMonitor_DebugSession()
    {
        // Arrange
        var testCert = TelemetryTestHelpers.GenerateDummyCertificate();

        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: initialData =>
            {
                initialData[DashboardConfigNames.DebugSessionPortName.ConfigKey] = "8080";
                initialData[DashboardConfigNames.DebugSessionServerCertificateName.ConfigKey] = Convert.ToBase64String(testCert.Export(X509ContentType.Cert));
                initialData[DashboardConfigNames.DebugSessionTokenName.ConfigKey] = "token!";
                initialData[DashboardConfigNames.DebugSessionTelemetryOptOutName.ConfigKey] = "true";
            });

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert
        Assert.Equal(8080, app.DashboardOptionsMonitor.CurrentValue.DebugSession.Port);

        var cert = app.DashboardOptionsMonitor.CurrentValue.DebugSession.GetServerCertificate();
        Assert.NotNull(cert);
        Assert.Equal(testCert.Thumbprint, cert.Thumbprint);

        Assert.Equal("token!", app.DashboardOptionsMonitor.CurrentValue.DebugSession.Token);
        Assert.Equal(true, app.DashboardOptionsMonitor.CurrentValue.DebugSession.TelemetryOptOut);
    }

    [Fact]
    public async Task Configuration_BrowserAndOtlpGrpcEndpointSame_Https_EndPointPortsAssigned()
    {
        // Arrange
        DashboardWebApplication? app = null;
        try
        {
            await ServerRetryHelper.BindPortWithRetry(async port =>
            {
                app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                    additionalConfiguration: initialData =>
                    {
                        initialData[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = $"https://127.0.0.1:{port}";
                        initialData[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = $"https://127.0.0.1:{port}";
                        initialData[DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey] = $"https://127.0.0.1:{port}";
                    });

                // Act
                await app.StartAsync().DefaultTimeout();
            }, NullLogger.Instance);

            // Assert
            Assert.NotNull(app);
            Assert.Equal(app.FrontendSingleEndPointAccessor().EndPoint.Port, app.OtlpServiceGrpcEndPointAccessor().EndPoint.Port);

            // Check browser access
            using var httpClient = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    return true;
                }
            })
            {
                BaseAddress = new Uri($"https://{app.FrontendSingleEndPointAccessor().EndPoint}")
            };
            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            var response = await httpClient.SendAsync(request).DefaultTimeout();
            response.EnsureSuccessStatusCode();

            // Check OTLP service
            using var channel = IntegrationTestHelpers.CreateGrpcChannel($"https://{app.FrontendSingleEndPointAccessor().EndPoint}", testOutputHelper);
            var client = new LogsService.LogsServiceClient(channel);
            var serviceResponse = await client.ExportAsync(new ExportLogsServiceRequest()).ResponseAsync.DefaultTimeout();
            Assert.Equal(0, serviceResponse.PartialSuccess.RejectedLogRecords);
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task Configuration_BrowserAndOtlpGrpcEndpointSame_NoHttps_Error()
    {
        // Arrange
        DashboardWebApplication? app = null;
        var testSink = new TestSink();
        try
        {
            await ServerRetryHelper.BindPortWithRetry(async port =>
            {
                app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                    additionalConfiguration: initialData =>
                    {
                        initialData[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = $"http://127.0.0.1:{port}";
                        initialData[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = $"http://127.0.0.1:{port}";
                        initialData[DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey] = $"http://127.0.0.1:{port}";
                    },
                    testSink: testSink);

                // Act
                await app.StartAsync().DefaultTimeout();
            }, NullLogger.Instance);

            // Assert
            Assert.Contains(testSink.Writes, w =>
            {
                if (w.LoggerName != typeof(DashboardWebApplication).FullName)
                {
                    return false;
                }
                if (w.LogLevel != LogLevel.Warning)
                {
                    return false;
                }
                if (!w.Message?.Contains("The dashboard is configured with a shared endpoint for browser access and the OTLP service. The endpoint doesn't use TLS so browser access is only possible via a TLS terminating proxy.") ?? false)
                {
                    return false;
                }
                return true;
            });
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task Configuration_BrowserAndOtlpHttpEndpointSame_NoHttps_EndPointPortsAssigned()
    {
        // Arrange
        DashboardWebApplication? app = null;
        var testSink = new TestSink();
        try
        {
            await ServerRetryHelper.BindPortWithRetry(async port =>
            {
                app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                    additionalConfiguration: initialData =>
                    {
                        initialData[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = $"http://127.0.0.1:{port}";
                        initialData[DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey] = $"http://127.0.0.1:{port}";
                        initialData.Remove(DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey);
                    },
                    testSink: testSink);

                // Act
                await app.StartAsync().DefaultTimeout();
            }, NullLogger.Instance);

            // Assert
            Assert.NotNull(app);
            Assert.Equal(app.FrontendSingleEndPointAccessor().EndPoint.Port, app.OtlpServiceGrpcEndPointAccessor().EndPoint.Port);

            // Check browser access
            using var httpClient = new HttpClient()
            {
                BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}")
            };
            var request = new HttpRequestMessage(HttpMethod.Get, "/");
            var responseMessage = await httpClient.SendAsync(request).DefaultTimeout();
            responseMessage.EnsureSuccessStatusCode();

            // Check OTLP service
            using var content = new ByteArrayContent(new ExportLogsServiceRequest().ToByteArray());
            content.Headers.TryAddWithoutValidation("content-type", OtlpHttpEndpointsBuilder.ProtobufContentType);

            responseMessage = await httpClient.PostAsync("/v1/logs", content);
            responseMessage.EnsureSuccessStatusCode();

            var response = ExportLogsServiceResponse.Parser.ParseFrom(await responseMessage.Content.ReadAsByteArrayAsync().DefaultTimeout());

            Assert.Equal(OtlpHttpEndpointsBuilder.ProtobufContentType, responseMessage.Content.Headers.GetValues("content-type").Single());
            Assert.False(responseMessage.Headers.Contains("content-security-policy"));
            Assert.Equal(0, response.PartialSuccess.RejectedLogRecords);
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task Configuration_NoAuthMode_DefaultAuthModes()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data.Remove(DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey);
                data.Remove(DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey);
            });

        // Assert
        Assert.Equal(FrontendAuthMode.BrowserToken, app.DashboardOptionsMonitor.CurrentValue.Frontend.AuthMode);
        Assert.Equal(16, Convert.FromHexString(app.DashboardOptionsMonitor.CurrentValue.Frontend.BrowserToken!).Length);
        Assert.Equal(OtlpAuthMode.Unsecured, app.DashboardOptionsMonitor.CurrentValue.Otlp.AuthMode);
        Assert.Empty(app.ValidationFailures);
    }

    [Fact]
    public async Task Configuration_AllowAnonymous_NoError()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data[DashboardConfigNames.DashboardUnsecuredAllowAnonymousName.ConfigKey] = bool.TrueString;
            });

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert
        Assert.Equal(FrontendAuthMode.Unsecured, app.DashboardOptionsMonitor.CurrentValue.Frontend.AuthMode);
        Assert.Equal(OtlpAuthMode.Unsecured, app.DashboardOptionsMonitor.CurrentValue.Otlp.AuthMode);
        Assert.Empty(app.ValidationFailures);
    }

    [Fact]
    public async Task Configuration_ResourceClientCertificates()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data[DashboardConfigNames.ResourceServiceClientAuthModeName.ConfigKey] = nameof(ResourceClientAuthMode.Certificate);
                data[DashboardConfigNames.ResourceServiceClientCertificateSourceName.ConfigKey] = nameof(DashboardClientCertificateSource.KeyStore);
                data[DashboardConfigNames.ResourceServiceClientCertificateSubjectName.ConfigKey] = "MySubject";
            });

        // Assert
        Assert.Equal(ResourceClientAuthMode.Certificate, app.DashboardOptionsMonitor.CurrentValue.ResourceServiceClient.AuthMode);
        Assert.Equal(DashboardClientCertificateSource.KeyStore, app.DashboardOptionsMonitor.CurrentValue.ResourceServiceClient.ClientCertificate.Source);
        Assert.Equal("MySubject", app.DashboardOptionsMonitor.CurrentValue.ResourceServiceClient.ClientCertificate.Subject);
        Assert.Empty(app.ValidationFailures);
    }

    [Fact]
    public async Task Configuration_Logging_OverrideDefaults()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data["Logging:LogLevel:Default"] = "Trace";
                data["Logging:LogLevel:Grpc"] = "Trace";
                data["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Trace";
            },
            clearLogFilterRules: false);

        // Assert
        await app.StartAsync().DefaultTimeout();

        var options = app.Services.GetRequiredService<IOptions<LoggerFilterOptions>>();

        Assert.Single(options.Value.Rules, r => r.CategoryName is null && r.LogLevel == LogLevel.Trace);
        Assert.Single(options.Value.Rules, r => r.CategoryName == "Grpc" && r.LogLevel == LogLevel.Trace);
        Assert.Single(options.Value.Rules, r => r.CategoryName == "Microsoft.Hosting.Lifetime" && r.LogLevel == LogLevel.Trace);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardConfigFilePath)]
    [InlineData(KnownConfigNames.Legacy.DashboardConfigFilePath)]
    public async Task Configuration_Logging_FileConfig_OverrideDefaults(string dashboardConfigFilePathNameKey)
    {
        // Arrange
        var configFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var configJson = new JsonObject
        {
            ["Logging"] = new JsonObject
            {
                ["LogLevel"] = new JsonObject
                {
                    ["Default"] = nameof(LogLevel.Trace),
                    ["Grpc"] = nameof(LogLevel.Trace),
                    ["Microsoft.Hosting.Lifetime"] = nameof(LogLevel.Trace)
                }
            }
        };
        await File.WriteAllTextAsync(configFilePath, configJson.ToString()).DefaultTimeout();

        try
        {
            // Arrange & Act
            await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                additionalConfiguration: data =>
                {
                    data[dashboardConfigFilePathNameKey] = configFilePath;
                },
                clearLogFilterRules: false);

            // Assert
            await app.StartAsync().DefaultTimeout();

            var options = app.Services.GetRequiredService<IOptions<LoggerFilterOptions>>();

            Assert.Single(options.Value.Rules, r => r.CategoryName is null && r.LogLevel == LogLevel.Trace);
            Assert.Single(options.Value.Rules, r => r.CategoryName == "Grpc" && r.LogLevel == LogLevel.Trace);
            Assert.Single(options.Value.Rules, r => r.CategoryName == "Microsoft.Hosting.Lifetime" && r.LogLevel == LogLevel.Trace);
        }
        finally
        {
            File.Delete(configFilePath);
        }
    }

    [Fact]
    public async Task LogOutput_DynamicPort_PortResolvedInLogs()
    {
        // Arrange
        var testSink = new TestSink();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper, testSink: testSink);

        // Act
        await app.StartAsync().DefaultTimeout();

        // Assert
        var l = testSink.Writes.Where(w => w.LoggerName == typeof(DashboardWebApplication).FullName).ToList();
        Assert.Collection(l,
            w =>
            {
                Assert.Equal("Aspire version: {Version}", GetValue(w.State, "{OriginalFormat}"));
            },
            w =>
            {
                Assert.Equal("Now listening on: {DashboardUri}", GetValue(w.State, "{OriginalFormat}"));

                var uri = new Uri((string)GetValue(w.State, "DashboardUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP/gRPC listening on: {OtlpEndpointUri}", GetValue(w.State, "{OriginalFormat}"));

                var uri = new Uri((string)GetValue(w.State, "OtlpEndpointUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP/HTTP listening on: {OtlpEndpointUri}", GetValue(w.State, "{OriginalFormat}"));

                var uri = new Uri((string)GetValue(w.State, "OtlpEndpointUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP server is unsecured. Untrusted apps can send telemetry to the dashboard. For more information, visit https://go.microsoft.com/fwlink/?linkid=2267030", GetValue(w.State, "{OriginalFormat}"));
                Assert.Equal(LogLevel.Warning, w.LogLevel);
            });

        object? GetValue(object? values, string key)
        {
            var list = values as IReadOnlyList<KeyValuePair<string, object>>;
            return list?.SingleOrDefault(kvp => kvp.Key == key).Value;
        }
    }

    [Fact]
    public async Task LogOutput_LocalhostAddress_LocalhostInLogOutput()
    {
        // Arrange
        TestSink? testSink = null;
        DashboardWebApplication? app = null;

        int? frontendPort1 = null;
        int? frontendPort2 = null;
        int? otlpPort = null;
        try
        {
            await ServerRetryHelper.BindPortsWithRetry(async ports =>
            {
                frontendPort1 = ports[0];
                frontendPort2 = ports[1];
                otlpPort = ports[2];

                testSink = new TestSink();
                app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
                    additionalConfiguration: data =>
                    {
                        data[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = $"https://localhost:{frontendPort1};http://localhost:{frontendPort2}";
                        data[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = $"http://localhost:{otlpPort}";
                    }, testSink: testSink);

                // Act
                await app.StartAsync().DefaultTimeout();
            }, NullLogger.Instance, portCount: 3);
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync().DefaultTimeout();
            }
        }

        // Assert
        Assert.NotNull(testSink);
        var l = testSink.Writes.Where(w => w.LoggerName == typeof(DashboardWebApplication).FullName).ToList();
        Assert.Collection(l,
            w =>
            {
                Assert.Equal("Aspire version: {Version}", GetValue(w.State, "{OriginalFormat}"));
            },
            w =>
            {
                Assert.Equal("Now listening on: {DashboardUri}", GetValue(w.State, "{OriginalFormat}"));

                var uri = new Uri((string)GetValue(w.State, "DashboardUri")!);
                Assert.Equal("https", uri.Scheme);
                Assert.Equal("localhost", uri.Host);
                Assert.Equal(frontendPort1, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP/gRPC listening on: {OtlpEndpointUri}", GetValue(w.State, "{OriginalFormat}"));

                var uri = new Uri((string)GetValue(w.State, "OtlpEndpointUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP/HTTP listening on: {OtlpEndpointUri}", GetValue(w.State, "{OriginalFormat}"));

                var uri = new Uri((string)GetValue(w.State, "OtlpEndpointUri")!);
                Assert.NotEqual(0, uri.Port);
            },
            w =>
            {
                Assert.Equal("OTLP server is unsecured. Untrusted apps can send telemetry to the dashboard. For more information, visit https://go.microsoft.com/fwlink/?linkid=2267030", GetValue(w.State, "{OriginalFormat}"));
                Assert.Equal(LogLevel.Warning, w.LogLevel);
            });

        object? GetValue(object? values, string key)
        {
            var list = values as IReadOnlyList<KeyValuePair<string, object>>;
            return list?.SingleOrDefault(kvp => kvp.Key == key).Value;
        }
    }

    [Fact]
    public async Task EndPointAccessors_AppStarted_BrowserGet_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);

        // Act
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        // Act
        var response = await client.GetAsync("/").DefaultTimeout();

        // Assert
        response.EnsureSuccessStatusCode();
        Assert.NotEmpty(response.Headers.GetValues(HeaderNames.ContentSecurityPolicy).Single());
    }

    [Fact]
    public async Task Configuration_CorsNoOtlpHttpEndpoint_Error()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data.Remove(DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey);
                data[DashboardConfigNames.DashboardOtlpCorsAllowedOriginsKeyName.ConfigKey] = "https://localhost:666";
            });

        // Assert
        Assert.Collection(app.ValidationFailures,
            s => Assert.Contains(DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey, s));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Configuration_DisableResourceGraph_EnsureValueSetOnOptions(bool? value)
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper,
            additionalConfiguration: data =>
            {
                data[DashboardConfigNames.UIDisableResourceGraphName.ConfigKey] = value?.ToString().ToLower();
            });

        // Assert
        Assert.Equal(value, app.DashboardOptionsMonitor.CurrentValue.UI.DisableResourceGraph);
    }

    private static void AssertIPv4OrIPv6Endpoint(Func<EndpointInfo> endPointAccessor)
    {
        // Check that the address is IPv4 or IPv6 any.
        var ipEndPoint = endPointAccessor().EndPoint;
        Assert.True(ipEndPoint.Address.Equals(IPAddress.Any) || ipEndPoint.Address.Equals(IPAddress.IPv6Any), "Endpoint address should be IPv4 or IPv6.");
    }

    private static void AssertDynamicIPEndpoint(Func<EndpointInfo> endPointAccessor)
    {
        // Check that the specified dynamic port of 0 is overridden with the actual port number.
        var ipEndPoint = endPointAccessor().EndPoint;
        Assert.NotEqual(0, ipEndPoint.Port);
    }

    private static async Task<string> CreateBrowserTokenConfigFileAsync(DirectoryInfo fileConfigDirectory, string browserToken)
    {
        var browserTokenConfigFile = Path.Combine(fileConfigDirectory.FullName, DashboardConfigNames.DashboardFrontendBrowserTokenName.EnvVarName);
        await File.WriteAllTextAsync(browserTokenConfigFile, browserToken);

        return browserTokenConfigFile;
    }
}
