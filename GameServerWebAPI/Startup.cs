﻿using FeatureToggle.Internal;
using GameServerWebAPI.Infrastructure;
using GameServerWebAPI.Proxies;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.AzureAppServices;
using NSwag.AspNetCore;
using NSwag.SwaggerGeneration.Processors;
using Polly;
using Polly.Registry;
using Refit;
using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace GameServerWebAPI
{
    public class Startup
    {
        public readonly IConfigurationRoot Configuration;
        public readonly IPolicyRegistry<string> policyRegistry;

        public Startup(IConfiguration configuration, IHostingEnvironment env)
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true, reloadOnChange: true);

            if (env.IsDevelopment())
            {
                var appAssembly = Assembly.Load(new AssemblyName(env.ApplicationName));
                if (appAssembly != null)
                {
                    builder.AddUserSecrets(appAssembly, optional: true);
                }
            }
            builder.AddEnvironmentVariables();
            Configuration = builder.Build();

            if (env.IsProduction())
            {
                builder.AddAzureKeyVault(Configuration["KeyVaultName"]);
                Configuration = builder.Build();
            }
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            // Add registry
            var policyRegistry = services.AddPolicyRegistry();

            // Centrally stored policies
            var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromMilliseconds(1500));
            policyRegistry.Add("steamtimeout", timeoutPolicy);

            // Options for particular external services
            services.Configure<SteamApiOptions>(Configuration.GetSection("SteamApiOptions"));

            ConfigureVersioning(services);
            ConfigureTypedClients(services);
            ConfigureOpenApi(services);
            ConfigureTelemetry(services);
            ConfigureFeatures(services);
            ConfigureHealth(services);
            ConfigureSecurity(services);
            ConfigureApiOptions(services);

            services.AddMvc()
                .AddXmlSerializerFormatters()
                .SetCompatibilityVersion(CompatibilityVersion.Version_2_1);

            // Demonstration purpose only: Not needed when using IOptions or ISnapshotOptions
            services.AddSingleton<IConfiguration>(Configuration);
        }

        private void ConfigureSecurity(IServiceCollection services)
        {
            services.AddHsts(
                options =>
                {
                    options.MaxAge = TimeSpan.FromDays(100);
                    options.IncludeSubDomains = true;
                    options.Preload = true;
                });
        }

        private void ConfigureTypedClients(IServiceCollection services)
        {
            services.AddHttpClient("Steam", options =>
            {
                options.BaseAddress = new Uri(Configuration["SteamApiOptions:BaseUrl"]);
                options.Timeout = TimeSpan.FromMilliseconds(15000);
                options.DefaultRequestHeaders.Add("ClientFactory", "Check");
            })
            .AddPolicyHandler(Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromMilliseconds(500)))
            .AddPolicyHandlerFromRegistry("steamtimeout")
            // Handle 5xx status code and any responses with a 408 (Request Timeout) status code
            .AddTransientHttpErrorPolicy(p => p.RetryAsync(3))
            .AddTypedClient(client => RestService.For<ISteamClient>(client));
        }

        private void ConfigureTelemetry(IServiceCollection services)
        {
            var env = services.BuildServiceProvider().GetRequiredService<IHostingEnvironment>();
            services.AddApplicationInsightsTelemetry(options =>
            {
                options.DeveloperMode = env.IsDevelopment();
                options.InstrumentationKey = Configuration["ApplicationInsights:InstrumentationKey"];
            });
            TelemetryConfiguration.Active.TelemetryInitializers.Add(new ServiceNameInitializer());
        }

        private void ConfigureFeatures(IServiceCollection services)
        {
            var provider = new AppSettingsProvider { Configuration = this.Configuration };
            services.AddSingleton(new AdvancedHealthFeature { ToggleValueProvider = provider });
        }

        private void ConfigureVersioning(IServiceCollection services)
        {
            services.AddApiVersioning(options => {
                options.DefaultApiVersion = new ApiVersion(2, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                // Includes headers "api-supported-versions" and "api-deprecated-versions"
                options.ReportApiVersions = true;

                // Alternative to attribute based versioning
                //options.Conventions.Controller<GameServerController>()
                //    .HasDeprecatedApiVersion(new ApiVersion(0, 9))
                //    .HasApiVersion(1)
                //    .AdvertisesApiVersion(2)
                //    .Action(a => a.Get(default(int))).MapToApiVersion(1);
            });
        }

        private void ConfigureApiOptions(IServiceCollection services)
        {
            services.Configure<ApiBehaviorOptions>(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
                {
                    var problemDetails = new ValidationProblemDetails(context.ModelState)
                    {
                        Instance = context.HttpContext.Request.Path,
                        Status = StatusCodes.Status400BadRequest,
                        Type = "https://asp.net/core",
                        Detail = "Please refer to the errors property for additional details."
                    };
                    return new BadRequestObjectResult(problemDetails)
                    {
                        ContentTypes = { "application/problem+json", "application/problem+xml" }
                    };
                };
            });
        }

        private void ConfigureOpenApi(IServiceCollection services)
        {
            services.AddSwagger();
        }

        private void ConfigureHealth(IServiceCollection services)
        {
            services.AddHealthChecks(checks =>
            {
                checks
                    .AddUrlCheck(Configuration["SteamApiOptions:BaseUrl"],
                        response =>
                        {
                            var status = response.StatusCode == 
                                System.Net.HttpStatusCode.NotFound 
                                ? CheckStatus.Healthy 
                                : CheckStatus.Unhealthy;
                            return new ValueTask<IHealthCheckResult>(HealthCheckResult.FromStatus(status, "Steam API base URL reachable."));
                        }
                    );

                // Use feature toggle to add this functionality
                var feature = services.BuildServiceProvider().GetRequiredService<AdvancedHealthFeature>();
                if (feature.FeatureEnabled)
                {
                    checks.AddHealthCheckGroup(
                        "memory",
                        group => group
                            .AddPrivateMemorySizeCheck(200000000) // Maximum private memory
                            .AddVirtualMemorySizeCheck(3000000000000)
                            .AddWorkingSetCheck(200000000),
                        CheckStatus.Unhealthy
                    );
                }
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            // Next call not required for .NET Core and Azure App Services
            //loggerFactory.AddAzureWebAppDiagnostics(
            //    new AzureAppServicesDiagnosticsSettings
            //    {
            //        OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss zzz} [{Level}] {RequestId}-{SourceContext}: {Message}{NewLine}{Exception}"
            //    }
            //);

            loggerFactory.AddApplicationInsights(app.ApplicationServices, LogLevel.Information);
            loggerFactory.AddEventSourceLogger(); // ETW on Windows, dev/null on other platforms
            loggerFactory.AddConsole();
            loggerFactory.AddDebug();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();

                // Do not expose Swagger interface in production
                app.UseSwaggerUi(typeof(Startup).GetTypeInfo().Assembly, settings =>
                {
                    settings.SwaggerRoute = "/swagger/v2/swagger.json";
                    settings.ShowRequestHeaders = true;
                    settings.DocExpansion = "list";
                    settings.UseJsonEditor = true;
                    settings.PostProcess = document =>
                        {
                            document.BasePath = "/";
                        };
                    settings.GeneratorSettings.Title = "Steam Server Browser Web API";
                    settings.GeneratorSettings.Description = "An newer example of a real-world-ish Web API";
                    settings.GeneratorSettings.Version = "2.0";
                    settings.GeneratorSettings.OperationProcessors.Add(
                        new ApiVersionProcessor() { IncludedVersions = { "2.0" } }
                    );
                });
                app.UseSwaggerUi(typeof(Startup).GetTypeInfo().Assembly, settings =>
                {
                    settings.SwaggerRoute = "/swagger/v1/swagger.json";
                    settings.ShowRequestHeaders = true;
                    settings.DocExpansion = "list";
                    settings.UseJsonEditor = true;
                    settings.PostProcess = document =>
                    {
                        document.BasePath = "/";
                    };
                    settings.GeneratorSettings.Title = "Steam Server Browser Web API";
                    settings.GeneratorSettings.Description = "An example real-world Web API";
                    settings.GeneratorSettings.Version = "1.0";
                    settings.GeneratorSettings.OperationProcessors.Add(
                        new ApiVersionProcessor() { IncludedVersions = { "1.0" } }
                    );
                });
            }
            else
            {
                // Automatically switch to HTTPS via redirect
                app.UseHttpsRedirection();

                // Avoid HTTP calls at all
                app.UseHsts();
            }

            app.UseMvcWithDefaultRoute();
        }
    }
}