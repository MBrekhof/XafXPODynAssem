using DevExpress.ExpressApp.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor.ApplicationBuilder;
using DevExpress.ExpressApp.Blazor.Services;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.WebApi.Services;
using DevExpress.ExpressApp.Xpo;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.PermissionPolicy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.SignalR;
using Microsoft.OpenApi.Models;
using XafXPODynAssem.Blazor.Server.Hubs;
using XafXPODynAssem.Blazor.Server.Services;
using XafXPODynAssem.Module.BusinessObjects;
using XafXPODynAssem.Module.Services;

namespace XafXPODynAssem.Blazor.Server
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(typeof(Microsoft.AspNetCore.SignalR.HubConnectionHandler<>), typeof(ProxyHubConnectionHandler<>));

            services.AddScoped<ISchemaFileService, BlazorSchemaFileService>();

            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddHttpContextAccessor();
            services.AddScoped<CircuitHandler, CircuitHandlerProxy>();

            // Set connection string for runtime entity bootstrap (before XAF initializes)
            XafXPODynAssem.Module.XafXPODynAssemModule.RuntimeConnectionString =
                Configuration.GetConnectionString("ConnectionString");

            // Early bootstrap: compile runtime types before XAF init
            XafXPODynAssem.Module.XafXPODynAssemModule.EarlyBootstrap();

            services.AddXaf(Configuration, builder =>
            {
                builder.UseApplication<XafXPODynAssemBlazorApplication>();
                builder.Modules
                    .AddConditionalAppearance()
                    .AddDashboards(options =>
                    {
                        options.DashboardDataType = typeof(DevExpress.Persistent.BaseImpl.DashboardData);
                    })
                    .AddFileAttachments()
                    .AddNotifications()
                    .AddOffice()
                    .AddReports(options =>
                    {
                        options.EnableInplaceReports = true;
                        options.ReportDataType = typeof(DevExpress.Persistent.BaseImpl.ReportDataV2);
                        options.ReportStoreMode = DevExpress.ExpressApp.ReportsV2.ReportStoreModes.XML;
                    })
                    .AddScheduler()
                    .AddValidation(options =>
                    {
                        options.AllowValidationDetailsAccess = false;
                    })
                    .AddViewVariants()
                    .Add<XafXPODynAssem.Module.XafXPODynAssemModule>()
                    .Add<XafXPODynAssemBlazorModule>();
                builder.ObjectSpaceProviders
                    .AddSecuredXpo((serviceProvider, options) =>
                    {
                        string connectionString = null;
                        if (Configuration.GetConnectionString("ConnectionString") != null)
                        {
                            connectionString = Configuration.GetConnectionString("ConnectionString");
                        }
#if EASYTEST
                        if(Configuration.GetConnectionString("EasyTestConnectionString") != null) {
                            connectionString = Configuration.GetConnectionString("EasyTestConnectionString");
                        }
#endif
                        ArgumentNullException.ThrowIfNull(connectionString);
                        options.ConnectionString = connectionString;
                        options.ThreadSafe = true;
                        options.UseSharedDataStoreProvider = true;
                    })
                    .AddNonPersistent();
                builder.Security
                    .UseIntegratedMode(options =>
                    {
                        options.Lockout.Enabled = true;
                        options.RoleType = typeof(PermissionPolicyRole);
                        options.UserType = typeof(XafXPODynAssem.Module.BusinessObjects.ApplicationUser);
                        options.UserLoginInfoType = typeof(XafXPODynAssem.Module.BusinessObjects.ApplicationUserLoginInfo);
                        options.UseXpoPermissionsCaching();
                        options.Events.OnSecurityStrategyCreated += securityStrategy =>
                        {
                            ((SecurityStrategy)securityStrategy).PermissionsReloadMode = PermissionsReloadMode.NoCache;
                        };
                    })
                    .AddPasswordAuthentication(options =>
                    {
                        options.IsSupportChangePassword = true;
                    });
            });
            var authentication = services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });
            authentication.AddCookie(options =>
            {
                options.LoginPath = "/LoginPage";
            });

            services.AddXafWebApi(Configuration, options =>
            {
                // Always expose metadata entities
                options.BusinessObject<CustomClass>();
                options.BusinessObject<CustomField>();
                options.BusinessObject<SchemaHistory>();

                // Expose runtime entities marked with IsApiExposed
                var apiClassNames = XafXPODynAssem.Module.XafXPODynAssemModule.ApiExposedClassNames;
                foreach (var type in XafXPODynAssem.Module.XafXPODynAssemModule.AssemblyManager.RuntimeTypes)
                {
                    if (apiClassNames.Contains(type.Name))
                    {
                        options.BusinessObject(type);
                    }
                }
            });

            services.AddControllers().AddOData((options, serviceProvider) =>
            {
                options
                    .AddRouteComponents("api/odata", new EdmModelBuilder(serviceProvider).GetEdmModel())
                    .EnableQueryFeatures(100);
            });

            services.AddAIServices(Configuration);

            services.AddSwaggerGen(c =>
            {
                c.EnableAnnotations();
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "XafXPODynAssem API",
                    Version = "v1",
                    Description = "OData REST API for runtime and compiled entities"
                });
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c =>
                {
                    c.SwaggerEndpoint("/swagger/v1/swagger.json", "XafXPODynAssem API v1");
                });
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }
            app.UseHttpsRedirection();
            app.UseRequestLocalization();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();
            app.UseXaf();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapXafEndpoints();
                endpoints.MapBlazorHub();
                endpoints.MapHub<SchemaUpdateHub>("/schemaUpdateHub");
                endpoints.MapFallbackToPage("/_Host");
                endpoints.MapControllers();
            });

            // Wire RestartService for graceful shutdown
            var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
            RestartService.Configure(lifetime);

            // Wire schema change orchestrator to SignalR hub + exit code 42 restart
            var hubContext = app.ApplicationServices.GetRequiredService<IHubContext<SchemaUpdateHub>>();
            var orchestrator = XafXPODynAssem.Module.Services.SchemaChangeOrchestrator.Instance;
            orchestrator.SchemaChanged += (version) =>
            {
                var needsRestart = orchestrator.RestartNeeded;

                _ = hubContext.Clients.All.SendAsync("SchemaChanged", version, needsRestart);

                if (needsRestart)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        Console.WriteLine("[RESTART] Force-exiting for restart (exit code 42)...");
                        Environment.Exit(42);
                    });
                }
            };
        }
    }
}
