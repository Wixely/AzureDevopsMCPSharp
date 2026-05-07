using AzureDevopsMCPSharp.Configuration;
using AzureDevopsMCPSharp.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;

namespace AzureDevopsMCPSharp;

public static class Program
{
    public static int Main(string[] args)
    {
        // When running as a Windows Service the working directory is
        // C:\Windows\System32, so resolve config and logs relative to the exe.
        var contentRoot = AppContext.BaseDirectory;
        var isService = WindowsServiceHelpers.IsWindowsService();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "azdomcp-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "AZDOMCP_")
                .AddCommandLine(args);

            if (isService)
            {
                builder.Host.UseWindowsService(o => o.ServiceName = "AzureDevopsMCPSharp");
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.Configure<AzureDevOpsOptions>(
                builder.Configuration.GetSection(AzureDevOpsOptions.SectionName));
            builder.Services.Configure<ServerOptions>(
                builder.Configuration.GetSection(ServerOptions.SectionName));

            builder.Services.AddSingleton<AzureDevOpsService>();

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(server.Port));

            var app = builder.Build();

            app.UseSerilogRequestLogging();

            var azdo = app.Services.GetRequiredService<AzureDevOpsService>();
            Log.Information("Azure DevOps MCP server starting on http://{Host}:{Port}{Path} (read-only={ReadOnly}, mode={Mode}, contentRoot={ContentRoot})",
                server.Host, server.Port, server.Path, azdo.IsReadOnly, isService ? "WindowsService" : "Console", contentRoot);

            app.MapMcp(server.Path);

            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
