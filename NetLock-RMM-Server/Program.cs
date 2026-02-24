using Helper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
//using NetLock_RMM_Server.LLM;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing.Tree;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing.Internal.PatternContexts;
using Microsoft.Extensions.Primitives;
using NetLock_RMM_Server;
using NetLock_RMM_Server.Agent.Windows;
using NetLock_RMM_Server.Background_Services;
using NetLock_RMM_Server.Configuration;
using NetLock_RMM_Server.Events;
using NetLock_RMM_Server.Members_Portal;
using NetLock_RMM_Server.SignalR;
using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using NetLock_RMM_Server.Setup;
using static NetLock_RMM_Server.Agent.Windows.Authentification;
using static NetLock_RMM_Server.SignalR.CommandHub;
using System.Text.RegularExpressions;
using System.Text;
using NetLock_RMM_Server.Endpoints;

NetLock_RMM_Server.Configuration.Server.serverStartTime = DateTime.Now; // Set server start time

// Check directories
NetLock_RMM_Server.Setup.Directories.Check_Directories(); // Check if directories exist and create them if not

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
var isRunningInDocker = builder.Configuration.GetValue<bool>("Environment:Docker", true);
var loggingEnabled = builder.Configuration.GetValue<bool>("Logging:Custom:Enabled", true);
var relayServerPort = builder.Configuration.GetValue<int>("Relay_Server:Port", 7081);

Server.isDocker = isRunningInDocker;
Server.loggingEnabled = loggingEnabled;

// Kestrel-Limits aus appsettings.json laden
var kestrelLimitsConfig = builder.Configuration.GetSection("Kestrel:Limits");
var maxConnections = kestrelLimitsConfig.GetValue<int?>("MaxConcurrentConnections") ?? null;
var maxUpgradedConnections = kestrelLimitsConfig.GetValue<int?>("MaxConcurrentUpgradedConnections") ?? null;
var maxBodySize = kestrelLimitsConfig.GetValue<long?>("MaxRequestBodySize") ?? 10L * 1024 * 1024 * 1024;
var keepAliveTimeout = kestrelLimitsConfig.GetValue<int?>("KeepAliveTimeout") ?? 130;

// SignalR-Konfiguration aus appsettings.json laden
var signalRConfig = builder.Configuration.GetSection("SignalR");
var maxMsgSize = signalRConfig.GetValue<long>("MaximumReceiveMessageSize", 102400000);
var clientTimeout = signalRConfig.GetValue<int>("ClientTimeoutInterval", 30);
var keepAlive = signalRConfig.GetValue<int>("KeepAliveInterval", 15);
var handshakeTimeout = signalRConfig.GetValue<int>("HandshakeTimeout", 30);
var detailedErrors = signalRConfig.GetValue<bool>("EnableDetailedErrors", true);
var streamBuffer = signalRConfig.GetValue<int>("StreamBufferCapacity", 20);
var maxParallelInvocations = signalRConfig.GetValue<int>("MaximumParallelInvocationsPerClient", 5);
var maxConnectionAttempts = signalRConfig.GetValue<int>("MaxConnectionAttempts", 5);
var connectionAttemptDelayMs = signalRConfig.GetValue<int>("ConnectionAttemptDelayMs", 5000);

var role_comm = builder.Configuration.GetValue<bool>("Kestrel:Roles:Comm", true);
var role_update = builder.Configuration.GetValue<bool>("Kestrel:Roles:Update", true);
var role_trust = builder.Configuration.GetValue<bool>("Kestrel:Roles:Trust", true);
var role_remote = builder.Configuration.GetValue<bool>("Kestrel:Roles:Remote", true);
var role_notification = builder.Configuration.GetValue<bool>("Kestrel:Roles:Notification", true);
var role_file = builder.Configuration.GetValue<bool>("Kestrel:Roles:File", true);
var role_llm = builder.Configuration.GetValue<bool>("Kestrel:Roles:LLM", true);
var role_relay = builder.Configuration.GetValue<bool>("Kestrel:Roles:Relay", true);


// IP Whitelist will be loaded from database after MySQL connection is established
List<string> backendAllowedIps = new List<string>();
List<string> relayAllowedIps = new List<string>();

Roles.Comm = role_comm;
Roles.Update = role_update;
Roles.Trust = role_trust;
Roles.Remote = role_remote;
Roles.Notification = role_notification;
Roles.File = role_file;
Roles.LLM = role_llm;
Roles.Relay = role_relay;

// Set relay port
Server.relay_port = relayServerPort;

// Members Portal Api
var membersPortal = builder.Configuration.GetSection("Members_Portal_Api").Get<NetLock_RMM_Server.Members_Portal.Config>() ?? new NetLock_RMM_Server.Members_Portal.Config();

Members_Portal.ApiKey = membersPortal.ApiKeyOverride ?? String.Empty;
Members_Portal.IsCloudEnabled = membersPortal.Cloud;
Members_Portal.ServerGuid = membersPortal.ServerGuid ?? String.Empty;

// Output OS
Console.WriteLine("OS: " + RuntimeInformation.OSDescription);
Console.WriteLine("Architecture: " + RuntimeInformation.OSArchitecture);
Console.WriteLine("Framework: " + RuntimeInformation.FrameworkDescription);
Console.WriteLine("Server started at: " + NetLock_RMM_Server.Configuration.Server.serverStartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
Console.WriteLine(Environment.NewLine);

// Output version
Console.WriteLine("NetLock RMM Server");
Console.WriteLine("Version: " + Application_Settings.server_version);
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
Console.WriteLine($"Relay Port: {Server.relay_port}");
Console.WriteLine($"Hsts: {hsts}");
Console.WriteLine($"Hsts Max Age: {hsts_max_age}");

Console.WriteLine($"Custom Certificate Path: {cert_path}");
Console.WriteLine($"Custom Certificate Password: {cert_password}");

Console.WriteLine(Environment.NewLine);

// Output mysql configuration
var mysqlConfig = builder.Configuration.GetSection("MySQL").Get<NetLock_RMM_Server.MySQL.Config>() ?? new NetLock_RMM_Server.MySQL.Config();
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

// Output kestrel configuration
Console.WriteLine($"Server role (comm): {role_comm}");
Console.WriteLine($"Server role (update): {role_update}");
Console.WriteLine($"Server role (trust): {role_trust}");
Console.WriteLine($"Server role (remote): {role_remote}");
Console.WriteLine($"Server role (notification): {role_notification}");
Console.WriteLine($"Server role (file): {role_file}");
Console.WriteLine($"Server role (llm): {role_llm}");
Console.WriteLine($"Server role (relay): {role_relay}");
Console.WriteLine(Environment.NewLine);

// Members Portal Api
Console.WriteLine("[Members Portal Api]");
Console.WriteLine($"Api Key Override: {membersPortal.ApiKeyOverride}");
Console.WriteLine($"Cloud Enabled: {Members_Portal.IsCloudEnabled}");
Console.WriteLine($"Server Guid: {Members_Portal.ServerGuid}");
Console.WriteLine(Environment.NewLine);

// Logging
Console.WriteLine("[Logging]");
Console.WriteLine($"Logging: {loggingEnabled}");
Console.WriteLine(Environment.NewLine);

// Environment
Console.WriteLine("[Environment]");
Console.WriteLine($"Running under Docker: {isRunningInDocker}");

Console.ResetColor();

// Configure Kestrel server options
builder.WebHost.UseKestrel(k =>
{
    IServiceProvider appServices = k.ApplicationServices;

    // Set Kestrel limits
    k.Limits.MaxRequestBodySize = maxBodySize;
    k.Limits.MaxConcurrentConnections = maxConnections;
    k.Limits.MaxConcurrentUpgradedConnections = maxUpgradedConnections;
    k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(keepAliveTimeout);
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    k.Limits.Http2.MaxStreamsPerConnection = 200;
    
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

    // Check if HTTP is enabled
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

if (!await NetLock_RMM_Server.MySQL.Handler.Check_Connection())
{
    Console.WriteLine("MySQL connection failed. Exiting...");
    Thread.Sleep(5000);
    Environment.Exit(1);
}
else
{
    Console.WriteLine("MySQL connection successful.");

    if (!await NetLock_RMM_Server.MySQL.Handler.Verify_Supported_SQL_Server())
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
    }

    await NetLock_RMM_Server.MySQL.Handler.Update_Server_Information();

    int maxConcurrentAgentUpdates = Convert.ToInt32(await NetLock_RMM_Server.MySQL.Handler.Quick_Reader("SELECT * FROM settings;", "agent_updates_max_concurrent_updates"));
    
    NetLock_RMM_Server.Configuration.Server.MaxConcurrentAgentUpdates = maxConcurrentAgentUpdates;
    NetLock_RMM_Server.Configuration.Server.MaxConcurrentNetLockPackageDownloadsSemaphore = new SemaphoreSlim(maxConcurrentAgentUpdates, maxConcurrentAgentUpdates);
    
    Console.WriteLine("Max Concurrent Agent Updates from Settings: " + maxConcurrentAgentUpdates);
    
    // Get api key
    if (String.IsNullOrEmpty(Members_Portal.ApiKey))
    {
        Members_Portal.ApiKey = await NetLock_RMM_Server.MySQL.Handler.Get_Api_Key(true);

        Console.WriteLine("Members Portal API key loaded from database: " + Members_Portal.ApiKey);
    }
    else
        await Handler.SetApiKey(Members_Portal.ApiKey);
    
    // Add license service as hosted service
    builder.Services.AddHostedService<Members_Portal_License_Service>();
    
    // Load public override url
    if (String.IsNullOrEmpty(Server.public_override_url))
    {
        Server.public_override_url = await NetLock_RMM_Server.MySQL.Handler.Quick_Reader("SELECT * FROM settings;", "public_override_url");

        // Extract only hostname or IP from URL (regex inline)
        if (!string.IsNullOrEmpty(Server.public_override_url))
        {
            var match = Regex.Match(Server.public_override_url, @"^(?:https?://)?([^:/\s]+)", RegexOptions.IgnoreCase);
            
            if (match.Success && match.Groups.Count > 1)
                Server.public_override_url = match.Groups[1].Value;
            
            Console.WriteLine("Public Override URL loaded from database: " + Server.public_override_url);
        }
    }
    
    // Load IP Whitelist from database
    try
    {
        string ipWhitelistJson = await NetLock_RMM_Server.MySQL.Handler.Quick_Reader("SELECT * FROM settings;", "ip_whitelist_backend");
        
        if (!string.IsNullOrEmpty(ipWhitelistJson))
        {
            var ipList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ipWhitelistJson);
            if (ipList != null && ipList.Count > 0)
            {
                backendAllowedIps = ipList;
                Console.WriteLine($"[Agent Backend] IP Whitelist loaded from database: {string.Join(", ", backendAllowedIps)}");
            }
            else
            {
                Console.WriteLine("[Agent Backend] IP Whitelist is empty in database. All IPs will be allowed.");
            }
        }
        else
        {
            Console.WriteLine("[Agent Backend] No IP Whitelist configured in database. All IPs will be allowed.");
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Agent Backend] Warning: Could not load IP Whitelist from database: {ex.Message}");
        Console.WriteLine("[Agent Backend] All IPs will be allowed.");
        Console.ResetColor();
        Logging.Handler.Error("Program.cs", "Load IP Whitelist Agent Backend", ex.ToString());
    }
    
    // Load Relay IP Whitelist from database
    try
    {
        string ipWhitelistJson = await NetLock_RMM_Server.MySQL.Handler.Quick_Reader("SELECT * FROM settings;", "ip_whitelist_relay");
        
        if (!string.IsNullOrEmpty(ipWhitelistJson))
        {
            var ipList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(ipWhitelistJson);
            if (ipList != null && ipList.Count > 0)
            {
                relayAllowedIps = ipList;
                Console.WriteLine($"[Relay Server] IP Whitelist loaded from database: {string.Join(", ", relayAllowedIps)}");
            }
            else
            {
                Console.WriteLine("[Relay Server] IP Whitelist is empty in database. All IPs will be allowed.");
            }
        }
        else
        {
            Console.WriteLine("[Relay Server] No IP Whitelist configured in database. All IPs will be allowed.");
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Relay Server] Warning: Could not load IP Whitelist from database: {ex.Message}");
        Console.WriteLine("[Relay Server] All IPs will be allowed.");
        Console.ResetColor();
        Logging.Handler.Error("Program.cs", "Load IP Whitelist Relay Server", ex.ToString());
    }
}

Console.WriteLine(Environment.NewLine);

// Check Package Status and Request Packages from Members Portal API if roles apply, api enabled and API key is set
if (Roles.Update || Roles.Trust)
{
    //OSSCH_START be1cff0e-f13c-4dfe-9063-f0cf9db366f3 //OSSCH_END
}

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddMvc();
builder.Services.AddControllers();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = maxMsgSize; 
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(clientTimeout);
    options.KeepAliveInterval = TimeSpan.FromSeconds(keepAlive);
    options.HandshakeTimeout = TimeSpan.FromSeconds(handshakeTimeout); 
    options.EnableDetailedErrors = detailedErrors; 
    options.StreamBufferCapacity = streamBuffer;
    options.MaximumParallelInvocationsPerClient = maxParallelInvocations;
}).AddHubOptions<CommandHub>(options =>
{
    options.MaximumReceiveMessageSize = maxMsgSize;
});

// Register background service
builder.Services.AddHostedService<Events_Notification_Service>();
builder.Services.AddHostedService<Server_Information_Service>();
builder.Services.AddHostedService<Members_Portal_License_Service>();
builder.Services.AddHostedService<UpdateStateMonitoringService>();

// Apply Relay IP Whitelist to RelayServer
if (role_remote)
{
    // Initialize RSA encryption for relay
    Console.WriteLine("[Relay Server] Initializing RSA encryption...");
    bool cryptoInitialized = await NetLock_RMM_Server.Relay.RelayEncryption.InitializeServerKeys();
    
    if (cryptoInitialized)
    {
        // Cache public key and fingerprint in Configuration.Server for performance
        Server.relay_public_key_pem = NetLock_RMM_Server.Relay.RelayEncryption.GetPublicKeyPem();
        Server.relay_public_key_fingerprint = NetLock_RMM_Server.Relay.RelayEncryption.GetPublicKeyFingerprint();
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Relay Server] RSA encryption initialized");
        Console.WriteLine($"[Relay Server] Public Key Fingerprint: {Server.relay_public_key_fingerprint}");
        Console.WriteLine($"[Relay Server] Public Key cached in Configuration.Server");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("[Relay Server] Failed to initialize RSA encryption - Relay will not start!");
        Console.ResetColor();
        role_remote = false; // Disable relay role if crypto fails
    }
    
    if (role_remote) // Only start if Crypto is successful
    {
        NetLock_RMM_Server.Relay.RelayServer.Instance.SetAllowedIps(relayAllowedIps);
        Console.WriteLine($"[Relay Server] IP Whitelist configured with {relayAllowedIps.Count} entries.");
        
        // Check if TLS is enabled for relay (optional - only if HTTPS is enabled and a certificate is present)
        bool relayUseTls = false;
        string relayCertPath = null;
        string relayCertPassword = null;
        
        if (https && !string.IsNullOrEmpty(cert_path) && File.Exists(cert_path))
        {
            relayUseTls = true;
            relayCertPath = cert_path;
            relayCertPassword = cert_password;
            Console.WriteLine($"[Relay Server] TLS enabled - using certificate: {cert_path}");
        }
        else
        {
            Console.WriteLine($"[Relay Server] TLS disabled - plaintext mode (reverse proxy expected)");
        }
        
        // Start a relay listener on port 7443 (with or without TLS)
        bool relayStarted = await NetLock_RMM_Server.Relay.RelayServer.Instance.StartRelayListener(
            relayUseTls, 
            relayCertPath, 
            relayCertPassword
        );
        
        if (relayStarted)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[Relay Server] Started successfully on port: " + relayServerPort);
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Relay Server] Failed to start!");
            Console.ResetColor();
        }
    }
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (hsts)
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (https_force)
{
    app.UseHttpsRedirection();
}

app.UseRouting();

// IP Whitelist Middleware for Agent Backend
if (backendAllowedIps != null && backendAllowedIps.Count > 0)
{
    Logging.Handler.Debug("Middleware", "IP Whitelisting Agent Backend", "IP whitelisting enabled. Whitelisted IPs: " + string.Join(", ", backendAllowedIps));
    Console.WriteLine("[Agent Backend] IP whitelisting enabled. Whitelisted IPs: " + string.Join(", ", backendAllowedIps));
    
    app.Use(async (context, next) =>
    {
        try
        {
            var remoteIp = context.Request.Headers.TryGetValue("X-Forwarded-For", out var headerValue) 
                ? headerValue.ToString().Split(',')[0].Trim() 
                : context.Connection.RemoteIpAddress?.ToString();
        
            Logging.Handler.Debug("Middleware Agent Backend", "Checking IP", remoteIp);
        
            if (!backendAllowedIps.Contains(remoteIp))
            {
                Logging.Handler.Error("Middleware Agent Backend", "IP Whitelisting", $"IP {remoteIp} is not whitelisted.");
                context.Response.StatusCode = 403;
                await context.Response.WriteAsync("Access denied. Your IP: " + remoteIp);
                return;
            }
        
            await next();
        }
        catch (Exception e)
        {
            Logging.Handler.Error("Middleware Agent Backend", "IP Whitelisting", e.ToString());
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync("Internal server error.");
        }
    });
}
else
{
    Logging.Handler.Debug("Middleware", "IP Whitelisting Agent Backend", "No IP addresses are whitelisted. All IPs will be allowed.");
    Console.WriteLine("[Agent Backend] No IP addresses are whitelisted. All IPs will be allowed.");
}

// Only use the middleware for the commandHub, to verify the signalR connection
app.UseWhen(context => context.Request.Path.StartsWithSegments("/commandHub"), appBuilder =>
{
    appBuilder.UseMiddleware<JsonAuthMiddleware>();
});

app.MapHub<CommandHub>("/commandHub");

// Initialize CommandHubSingleton with IHubContext
var hubContext = app.Services.GetService<IHubContext<CommandHub>>();
CommandHubSingleton.Instance.Initialize(hubContext);

//API URLs*

// Test endpoint
app.MapGet("/test", async context =>
{
    context.Response.StatusCode = 200;
    await context.Response.WriteAsync("ok");
});

// Members Portal Api Cloud Version Endpoints
if (Members_Portal.IsCloudEnabled)
{
    //OSSCH_START 9eebca51-57a6-4138-8ca5-0191c989eb3f //OSSCH_END
}

if (Members_Portal.IsCloudEnabled)
{
    // Credentials update endpoint
    //OSSCH_START f9a0d8a1-99e4-47be-98ac-de2d97aa471b //OSSCH_END
}

// NetLock files download private - GUID, used for update server & trust server
if (role_update || role_trust)
{
    //OSSCH_START 8d6a3315-baaf-46bb-9ef5-701c75d65ecf //OSSCH_END
}

// Map comm agent endpoints if role_comm is enabled
if (role_comm)
{
    app.MapCommServerEndpoints();
}

// Map file server endpoints if role_file is enabled
if (role_file)
{
    app.MapFileServerEndpoints();
}

// Map remote server endpoints if role_remote is enabled
if (role_remote)
{
    app.MapRemoteServerEndpoints();
}

// Map relay server endpoints if role_remote and role_relay are enabled. They depend on each other.
if (role_remote && role_relay)
{
    app.MapRelayEndpoints(); 
}

// Add a middleware to handle exceptions globally and return a 500 status code with a message to the client in case of an unexpected error
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("An unexpected error occurred.");
    });
});

Console.WriteLine(Environment.NewLine);
Console.WriteLine("Server started.");

//Start server
app.Run();

