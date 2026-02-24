using System.Configuration;
using MudBlazor.Services;
using NetLock_RMM_Web_Console.Components;
using NetLock_RMM_Web_Console;
using System.Net;
using NetLock_RMM_Web_Console.Classes.MySQL;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using NetLock_RMM_Web_Console.Classes.Authentication;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Authentication;
using NetLock_RMM_Web_Console.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http.Features;
using NetLock_RMM_Web_Console.Components.Pages.Devices;
using MudBlazor;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Primitives;
using static MudBlazor.Defaults;
using NetLock_RMM_Web_Console.Classes.Helper;
using Microsoft.Identity.Web;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Cookies;

NetLock_RMM_Web_Console.Classes.Setup.Directories.Check_Directories(); // Check if directories exist and create them if not

var builder = WebApplication.CreateBuilder(args);

// Load configuration from appsettings.json
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

// Get UseHttps from config
var http = builder.Configuration.GetValue<bool>("Kestrel:Endpoint:Http:Enabled", true);
var http_port = builder.Configuration.GetValue<int>("Kestrel:Endpoint:Http:Port", 80);
var https = builder.Configuration.GetValue<bool>("Kestrel:Endpoint:Https:Enabled", true);
var https_port = builder.Configuration.GetValue<int>("Kestrel:Endpoint:Https:Port", 443);
var https_force = builder.Configuration.GetValue<bool>("Kestrel:Endpoint:Https:Force", true);
var hsts = builder.Configuration.GetValue<bool>("Kestrel:Endpoint:Https:Hsts:Enabled", true);
var hsts_max_age = builder.Configuration.GetValue<int>("Kestrel:Endpoint:Https:Hsts:MaxAge");
var cert_path = builder.Configuration.GetValue<string>("Kestrel:Endpoint:Https:Certificate:Path", String.Empty);
var cert_password = builder.Configuration.GetValue<string>("Kestrel:Endpoint:Https:Certificate:Password", String.Empty);
var loggingEnabled = builder.Configuration.GetValue<bool>("Logging:Custom:Enabled", true);
var publicOverrideUrlRaw = builder.Configuration.GetValue<string>("Webinterface:publicOverrideUrl", string.Empty);
var publicOverrideUrl = publicOverrideUrlRaw.TrimEnd('/');

// IP Whitelist will be loaded from database after MySQL connection is established
List<string> allowedIps = new List<string>();

var knownProxies = builder.Configuration.GetSection("Kestrel:KnownProxies").Get<List<string>>() ?? new List<string>();

Web_Console.loggingEnabled = loggingEnabled;

// Add Remote_Server to the services
var remoteServerConfig = builder.Configuration.GetSection("NetLock_Remote_Server").Get<NetLock_RMM_Web_Console.Classes.Remote_Server.Config>();
Remote_Server.Hostname = remoteServerConfig.Server;

if (remoteServerConfig.UseSSL)
{
    Remote_Server.Connection_String = $"https://{remoteServerConfig.Server}:{remoteServerConfig.Port}";
}
else
{
    Remote_Server.Connection_String = $"http://{remoteServerConfig.Server}:{remoteServerConfig.Port}";
}

// Add File Server to the services
var fileServerConfig = builder.Configuration.GetSection("NetLock_File_Server").Get<NetLock_RMM_Web_Console.Classes.File_Server.Config>();
File_Server.Hostname = fileServerConfig.Server;

if (fileServerConfig.UseSSL)
{
    File_Server.Connection_String = $"https://{fileServerConfig.Server}:{fileServerConfig.Port}";
}
else
{
    File_Server.Connection_String = $"http://{fileServerConfig.Server}:{fileServerConfig.Port}";
}

// Public override URL
if (!String.IsNullOrEmpty(publicOverrideUrl))
{
    Web_Console.publicOverrideUrl = publicOverrideUrl;
    
    // Will be written to database (settings -> public_override_url)
    Console.WriteLine("Public Override URL set to: " + Web_Console.publicOverrideUrl);
}
    
var language = builder.Configuration.GetValue<string>("Webinterface:Language", "en-US");

// Check members portal parts
//OSSCH_START 59ca5277-1894-466f-84df-0b1bf0fb7bfb //OSSCH_END
Console.WriteLine("---------Loader_End----------");

// Output OS
Console.WriteLine("OS: " + RuntimeInformation.OSDescription);
Console.WriteLine("Architecture: " + RuntimeInformation.OSArchitecture);
Console.WriteLine("Framework: " + RuntimeInformation.FrameworkDescription);
Console.WriteLine(Environment.NewLine);

// Output version
Console.WriteLine("NetLock RMM Web Console");
Console.WriteLine("Web Console Version: " + Application_Settings.web_console_version);
Console.WriteLine("Database Version: " + Application_Settings.db_version);
Console.WriteLine(Environment.NewLine);
Console.WriteLine("Configuration loaded from appsettings.json");
Console.WriteLine(Environment.NewLine);

// Output http port
Console.WriteLine("[Kestrel Configuration]");
Console.WriteLine($"Http: {http}");
Console.WriteLine($"Http Port: {http_port}");
Console.WriteLine($"Https: {https}");
Console.WriteLine($"Https Port: {https_port}");
Console.WriteLine($"Https (force): {https_force}");
Console.WriteLine($"Hsts: {hsts}");
Console.WriteLine($"Hsts Max Age: {hsts_max_age}");
Console.WriteLine($"Allowed IPs: (loaded from database)");
Console.WriteLine($"Known Proxies: {string.Join(", ", knownProxies)}");

Console.WriteLine($"Custom Certificate Path: {cert_path}");
Console.WriteLine($"Custom Certificate Password: {cert_password}");
Console.WriteLine(Environment.NewLine);

// Output mysql configuration
var mysqlConfig = builder.Configuration.GetSection("MySQL").Get<Config>() ?? new Config();
MySQL.Connection_String = $"Server={mysqlConfig.Server};Port={mysqlConfig.Port};Database={mysqlConfig.Database};User={mysqlConfig.User};Password={mysqlConfig.Password};SslMode={mysqlConfig.SslMode};{mysqlConfig.AdditionalConnectionParameters}";
MySQL.Database = mysqlConfig.Database;

Console.WriteLine("[MySQL]");
Console.WriteLine($"MySQL Server: {mysqlConfig.Server}");
Console.WriteLine($"MySQL Port: {mysqlConfig.Port}");
Console.WriteLine($"MySQL Database: {mysqlConfig.Database}");
Console.WriteLine($"MySQL User: {mysqlConfig.User}");
Console.WriteLine($"MySQL Password: {mysqlConfig.Password}");
Console.WriteLine($"MySQL SSL Mode: {mysqlConfig.SslMode}");
Console.WriteLine($"MySQL Additional Parameters: {mysqlConfig.AdditionalConnectionParameters}");
Console.WriteLine(Environment.NewLine);

// Output remote server configuration
Console.WriteLine("[Remote Server]");
Console.WriteLine($"Remote Server: {remoteServerConfig.Server}");
Console.WriteLine($"Remote Port: {remoteServerConfig.Port}");
Console.WriteLine($"Remote Use SSL: {remoteServerConfig.UseSSL}");
Console.WriteLine($"Remote Connnection String: {Remote_Server.Connection_String}");
Console.WriteLine(Environment.NewLine);

// Output file server configuration
Console.WriteLine("[File Server]");
Console.WriteLine($"File Server: {fileServerConfig.Server}");
Console.WriteLine($"File Port: {fileServerConfig.Port}");
Console.WriteLine($"File Use SSL: {fileServerConfig.UseSSL}");
Console.WriteLine($"File Connnection String: {File_Server.Connection_String}");
Console.WriteLine(Environment.NewLine);

// Webinterface
Console.WriteLine("[Webinterface]");
Console.WriteLine($"Language: {language}");
Console.WriteLine($"Title: {Web_Console.title}");
Console.WriteLine($"Public Override Domain: {Web_Console.publicOverrideUrl}");
Console.WriteLine(Environment.NewLine);

// Logging
Console.WriteLine("[Logging]");
Console.WriteLine($"Logging: {loggingEnabled}");
Console.WriteLine(Environment.NewLine);

builder.WebHost.UseKestrel(k =>
{
    IServiceProvider appServices = k.ApplicationServices;

    // Set the maximum request body size to 10 GB
    k.Limits.MaxRequestBodySize = 10L * 1024 * 1024 * 1024; // 10 GB
    
    if (https)
    {
        k.Listen(IPAddress.Any, https_port, o =>
        {
            if (String.IsNullOrEmpty(cert_password) && File.Exists(cert_path))
            {
                o.UseHttps(cert_path);
            }
            else if (!String.IsNullOrEmpty(cert_password) && File.Exists(cert_path))
            {
                o.UseHttps(cert_path, cert_password);
            }
            else
            {
                Console.WriteLine("Custom certificate path or password is not set or file does not exist. Exiting...");
                Thread.Sleep(5000);
                Environment.Exit(1);
            }
        });
    }

    if (http)
        k.Listen(IPAddress.Any, http_port);
});

builder.Services.Configure<FormOptions>(x =>
{
    x.ValueLengthLimit = int.MaxValue; // In case of form
    x.MultipartBodyLengthLimit = 10L * 1024 * 1024 * 1024; // 10 GB // In case of multipart
});

// Check mysql connection
Console.WriteLine(Environment.NewLine);
Console.WriteLine("Checking MySQL connection...");

if (!await Database.Check_Connection())
{
    Console.WriteLine("MySQL connection failed. Exiting...");
    Thread.Sleep(5000);
    Environment.Exit(1);
}
else
{
    Console.WriteLine("SQL connection successful.");

    if (!await Database.Verify_Supported_SQL_Server())
    {
        Console.WriteLine("SQL Server version is not supported. We only support MySQL! Exiting...");
        Thread.Sleep(5000);
        Environment.Exit(1);
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("SQL Server version is supported.");
        Console.ResetColor();

        // Check if tables exist
        if (!await Database.Check_Table_Existing()) // Table does not exist
        {
            Console.WriteLine("Database tables do not exist. Creating tables...");
            await Database.Execute_Installation_Script(false);
            await Database.Execute_Update_Scripts();
            Console.WriteLine("Database tables created.");
        }
        else // Table exists
        {
            Console.WriteLine("Verifying & updating database structure if required.");

            // Update database
            await Database.Execute_Update_Scripts();

            await Database.Update_DB_Version();

            await Database.Fix_Settings();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Database structure okay.");
            Console.ResetColor();

            // Get api key
            if (String.IsNullOrEmpty(Members_Portal.ApiKey))
            {
                Members_Portal.ApiKey = await NetLock_RMM_Web_Console.Classes.MySQL.Handler.Get_Api_Key(true);

                Console.WriteLine("Members Portal API key loaded from database: " + Members_Portal.ApiKey);
            }
            else
                await NetLock_RMM_Web_Console.Classes.Members_Portal.Handler.SetApiKey(Members_Portal.ApiKey);

            // Add licensse service as hosted service
            builder.Services.AddHostedService<NetLock_RMM_Web_Console.Classes.Members_Portal.Members_Portal_License_Service>();
            
            // Do cloud stuff
            if (Members_Portal.IsCloudEnabled)
            {
                // Enforce cloud settings
                await Database.EnforceCloudSettings();

                Console.WriteLine("Cloud enabled. Checking cloud connection...");
                if (await NetLock_RMM_Web_Console.Classes.Members_Portal.Handler.CheckConnection())
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Cloud connection successful.");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(
                        "Cloud connection failed. Please check your internet connection or firewall settings.");
                    Console.ResetColor();
                }
            }
        }
        
        //OSSCH_START 552982f1-bd42-4f7c-b30f-b12a8d2e91d1 //OSSCH_END

        // Load IP Whitelist from database
        try
        {
            string ipWhitelistJson =
                await NetLock_RMM_Web_Console.Classes.MySQL.Handler.Quick_Reader("SELECT * FROM settings",
                    "ip_whitelist_web_console");

            if (!string.IsNullOrEmpty(ipWhitelistJson))
            {
                var ipList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ipWhitelistJson);
                if (ipList != null && ipList.Count > 0)
                {
                    allowedIps = ipList;
                    Console.WriteLine($"IP Whitelist loaded from database: {string.Join(", ", allowedIps)}");
                }
                else
                {
                    Console.WriteLine("IP Whitelist is empty in database. All IPs will be allowed.");
                }
            }
            else
            {
                Console.WriteLine("No IP Whitelist configured in database. All IPs will be allowed.");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: Could not load IP Whitelist from database: {ex.Message}");
            Console.WriteLine("All IPs will be allowed.");
            Console.ResetColor();
            Logging.Handler.Error("Program.cs", "Load IP Whitelist", ex.ToString());
        }
    }
}

Console.WriteLine(Environment.NewLine);

// Add MudBlazor services
builder.Services.AddMudServices();

// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// SSO Configuration
Console.WriteLine(Environment.NewLine);
//OSSCH_START 064858d4-3e56-405a-a19a-3b8c48f8dd89 //OSSCH_END

// Blazor and core services
builder.Services.AddBlazoredLocalStorage();
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddHttpContextAccessor();

// Register CustomAuthenticationStateProvider
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>(sp =>
{
    var sessionStorage = sp.GetRequiredService<ProtectedSessionStorage>();
    var tokenService = sp.GetRequiredService<TokenService>();
    var httpAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    return new CustomAuthenticationStateProvider(sessionStorage, tokenService, httpAccessor);
});
builder.Services.AddScoped<ProtectedSessionStorage>();

// Additional services
builder.Services.AddOptions();
builder.Services.AddLocalization();
builder.Services.AddSingleton<MudBlazor.MudThemeProvider>();
builder.Services.AddSingleton<TenantUpdateService>();
builder.Services.AddSingleton<NetLock_RMM_Web_Console.Classes.Theme.ThemeUpdateService>();
builder.Services.AddMvc();

// Configure form options to increase the maximum upload file size limit to 150 MB
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 5L * 1024 * 1024 * 1024; // 5 GB
});

try
{
    builder.Services.AddLocalization(options => { options.ResourcesPath = "Resources"; });
}
catch (Exception ex)
{
    Console.WriteLine(ex.ToString());
}

//increase size of textarea accepted value value
builder.Services.AddServerSideBlazor().AddHubOptions(x => x.MaximumReceiveMessageSize = 102400000);

// Add background services
builder.Services.AddHostedService<NetLock_RMM_Web_Console.Classes.MySQL.AutoCleanupService>();
builder.Services.AddHostedService<NetLock_RMM_Web_Console.Classes.ScreenRecorder.AutoCleanupService>(); //disabled until remote screen control release

// Generate tokenservice secretkey
Web_Console.token_service_secret_key = Randomizer.Handler.Token(true, 32);

var app = builder.Build();

// If SSO is enabled, ensure authentication/authorization middleware are added
if (Sso.IsEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Middleware to filter the IP addresses
var knownProxiesStrings = builder.Configuration.GetSection("Kestrel:KnownProxies").Get<List<string>>() ?? new List<string>();

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    RequireHeaderSymmetry = false
};

bool hasValidProxy = false;

foreach (var proxyIpString in knownProxiesStrings)
{
    if (IPAddress.TryParse(proxyIpString, out var proxyIp))
    {
        forwardedHeadersOptions.KnownProxies.Add(proxyIp);
        hasValidProxy = true;
        Logging.Handler.Debug("Middleware", "KnownProxies", $"Added known proxy IP: {proxyIp}");
        Console.WriteLine($"Added known proxy IP: {proxyIp}");
    }
    else
    {
        Logging.Handler.Error("Middleware", "KnownProxies", $"'{proxyIpString}' is not a valid IP address and will be ignored.");
        Console.WriteLine($"Warning: '{proxyIpString}' is not a valid IP address and will be ignored.");
    }
}

// Check if proxy IPs were added
if (hasValidProxy)
{
    app.UseForwardedHeaders(forwardedHeadersOptions);
}
else
{
    Console.WriteLine("No valid KnownProxies found, skipping UseForwardedHeaders middleware.");
}

if (allowedIps == null || allowedIps.Count == 0)
{
    Logging.Handler.Debug("Middleware", "IP Whitelisting", "No IP addresses are whitelisted. All IPs will be allowed.");
    Console.WriteLine("No IP addresses are whitelisted. All IPs will be allowed.");
}
else
{
    Logging.Handler.Debug("Middleware", "IP Whitelisting", "IP whitelisting enabled. Whitelisted IPs: " + string.Join(", ", allowedIps));
    Console.WriteLine("IP whitelisting enabled. Whitelisted IPs: " + string.Join(", ", allowedIps));

    app.Use(async (context, next) =>
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();

        Logging.Handler.Debug("Middleware", "RemoteIpAddress", remoteIp);
        Logging.Handler.Debug("Middleware", "X-Forwarded-For", forwardedFor);
        Logging.Handler.Debug("Middleware", "X-Forwarded-Proto", forwardedProto);

        // If the request is forwarded, use the first IP from the X-Forwarded-For header
        if (!allowedIps.Contains(remoteIp))
        {
            Logging.Handler.Error("Middleware", "IP Whitelisting", $"IP {remoteIp} is not whitelisted.");
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Your IP is unknown. Your ip: " + remoteIp);
            return;
        }

        await next();
    });
}

// temporary static selection
if (language == "en-US")
    app.UseRequestLocalization("en-US");
else if (language == "de-DE")
{
    Web_Console.language = "de-DE";
    app.UseRequestLocalization("de-DE");
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    if (hsts)
    {
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }
}

if (https_force)
{
    app.UseHttpsRedirection();
}

//app.MapBlazorHub(); // 
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

Console.WriteLine(Environment.NewLine);
Console.WriteLine("Server started.");

// SSO Challenge Endpoints
if (Sso.IsEnabled && NetLock_RMM_Web_Console.Classes.Members_Portal.License.CodeSigned)
{
    //OSSCH_START 4cba7443-aa26-4290-a258-0ba2698d9f22 //OSSCH_END

// Test endpoint
app.MapGet("/test", async context =>
{
    context.Response.StatusCode = 200;
    await context.Response.WriteAsync("ok");
});

// Members Portal Api Cloud Version Endpoints
if (Members_Portal.IsCloudEnabled)
{
    //OSSCH_START 369e43eb-3a95-44f1-80a5-ccb804c000ae //OSSCH_END
}

// Start server
app.Run();
