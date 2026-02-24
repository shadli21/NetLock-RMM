using Global.Helper;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net;
using System.Security.AccessControl;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Globalization;
using Windows.Helper.ScreenControl;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Windows.Helper;
using Global.Configuration;
using Global.Encryption;
using NetLock_RMM_Agent_Remote.Relay;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Linux.Helper;
using MacOS.Helper;

namespace NetLock_RMM_Agent_Remote
{
    public class Remote_Worker : BackgroundService
    {
        // Server config
        public static string access_key = string.Empty;
        public static bool authorized = false;

        // Server communication
        public static string remote_server = string.Empty;
        public static string file_server = string.Empty;
        public static string relay_server = string.Empty;
        
        public static bool remote_server_status = false;
        public static bool file_server_status = false;
        public static bool relay_server_status = false;
        
        // Local Server
        private const int Port = 7337;
        private const string ServerIp = "127.0.0.1"; // Localhost
        private TcpClient local_server_client;
        private NetworkStream _stream;
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private Timer local_server_clientCheckTimer;

        // Remote Server Client
        public static string remote_server_url_command = String.Empty;
        private HubConnection remote_server_client;
        private Timer remote_server_clientCheckTimer;
        bool remote_server_client_setup = false;

        // User process monitoring
        private Timer user_process_monitoringCheckTimer;

        // Tray icon process monitoring
        private Timer tray_icon_process_monitoringCheckTimer;
        
        // Get server config timer
        private Timer serverConfigCheckTimer;
        
        // Device Identity
        public string device_identity_json = String.Empty;
        
        // Remote screen control
        private bool _agentSettingsRemoteServiceEnabled = false;
        private bool _agentSettingsRemoteShellEnabled = false;
        private bool _agentSettingsRemoteFileBrowserEnabled = false;
        private bool _agentSettingsRemoteTaskManagerEnabled = false;
        private bool _agentSettingsRemoteServiceManagerEnabled = false;
        private bool _agentSettingsRemoteScreenControlEnabled = false;
        private bool _agentSettingsRemoteScreenControlUnattendedAccess = false;
        private bool _remoteScreenControlAccessGranted = false;
        private List<string> _remoteScreenControlGrantedUsers = new List<string>();
        
        // Process monitoring flags
        private bool _isCheckingUserProcesses = false;
        
        // Relay Connection Management (Type 14)
        private ConcurrentDictionary<string, CancellationTokenSource> _activeRelaySessions = new ConcurrentDictionary<string, CancellationTokenSource>();
        
        // E2EE: Session-based Agent Keypairs (reused per session)
        private ConcurrentDictionary<string, (System.Security.Cryptography.RSA rsa, string publicKeyPem)> _sessionKeypairs = new ConcurrentDictionary<string, (System.Security.Cryptography.RSA, string)>();

        // Response Queue System - for sending responses back to the server in background threads
        private readonly ConcurrentDictionary<string, ResponseQueueItem> _responseQueue = new ConcurrentDictionary<string, ResponseQueueItem>();
        private readonly BlockingCollection<string> _responseReadyQueue = new BlockingCollection<string>();
        private CancellationTokenSource _responseQueueCancellationTokenSource;
        private Task _responseQueueProcessorTask;

        // Input Command Queue System - High-priority channel for mouse/keyboard commands
        // Uses System.Threading.Channels for lock-free, high-performance message passing
        private Channel<InputCommand> _inputCommandChannel;
        private CancellationTokenSource _inputCommandCancellationTokenSource;
        private Task _inputCommandProcessorTask;

        private class InputCommand
        {
            public string TargetUser { get; set; }
            public string JsonPayload { get; set; }
            public string ResponseId { get; set; }
        }

        private class ResponseQueueItem
        {
            public string ResponseId { get; set; }
            public string Result { get; set; }
            public bool IsComplete { get; set; }
            public DateTime CreatedAt { get; set; }
            public bool IsSent { get; set; }
        }

        public class Device_Identity
        {
            public string agent_version { get; set; }
            public string package_guid { get; set; }
            public string device_name { get; set; }
            public string location_guid { get; set; }
            public string tenant_guid { get; set; }
            public string access_key { get; set; }
            public string hwid { get; set; }
            public string platform { get; set; }
            public string ip_address_internal { get; set; }
            public string operating_system { get; set; }
            public string domain { get; set; }
            public string antivirus_solution { get; set; }
            public string firewall_status { get; set; }
            public string architecture { get; set; }
            public string last_boot { get; set; }
            public string timezone { get; set; }
            public string cpu { get; set; }
            public string cpu_usage { get; set; }
            public string mainboard { get; set; }
            public string gpu { get; set; }
            public string ram { get; set; }
            public string ram_usage { get; set; }
            public string tpm { get; set; }
            public string environment_variables { get; set; }
            public string last_active_user { get; set; }
        }

        public class Command_Entity
        {
            public int type { get; set; }
            public bool wait_response { get; set; }
            public string powershell_code { get; set; }
            public string response_id { get; set; }
            public int file_browser_command { get; set; }
            public string file_browser_path { get; set; }
            public string file_browser_path_move { get; set; }
            public string file_browser_file_content { get; set; }
            public string file_browser_file_guid { get; set; }
            public string remote_control_username { get; set; }
            public string remote_control_screen_index { get; set; }
            public string remote_control_mouse_action { get; set; }
            public string remote_control_mouse_xyz { get; set; }
            public string remote_control_keyboard_input { get; set; }
            public string remote_control_keyboard_content { get; set; }
            public string remote_control_elevation_username { get; set; }
            public string remote_control_elevation_password { get; set; }
            public int remote_control_render_mode { get; set; }
            public string command { get; set; } // used for service, task manager, screen capture. A command can either be a quick command like "list" or a json string with parameters, a number or json string. Can also be used to transfer command details for other command types
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            bool firstRun = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (firstRun)
                {
                    firstRun = false;
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Service.ExecuteAsync", "Service is starting...", "Information");
                    
                    try
                    {
                        if (Agent.debug_mode)
                            Logging.Debug("Service.OnStart", "Service started", "Information");
                        
                        // Start the response queue processor for handling SignalR responses
                        StartResponseQueueProcessor();
                        
                        // Start the input command processor for high-priority mouse/keyboard commands
                        StartInputCommandProcessor();
                        
                        await LoadServerConfig();

                        // Start the timer to check the local server connection status every 15 seconds
                        local_server_clientCheckTimer =
                            new Timer(async (e) => await Local_Server_Check_Connection_Status(), null, TimeSpan.Zero,
                                TimeSpan.FromSeconds(15));
                        
                        // Start the timer to check the remote server connection status every 15 seconds
                        remote_server_clientCheckTimer =
                            new Timer(async (e) => await Remote_Server_Check_Connection_Status(), null, TimeSpan.Zero,
                                TimeSpan.FromSeconds(15));
                        
                        // Start the timer to check the user process status every 1 minute
                        user_process_monitoringCheckTimer = new Timer(async (e) => await CheckUserProcessStatus(), null,
                            TimeSpan.Zero, TimeSpan.FromSeconds(5));
                        
                        // Start the timer to check the tray icon process status every 1 minute
                        tray_icon_process_monitoringCheckTimer = new Timer(async (e) => await CheckTrayIconProcessStatus(), null,
                          TimeSpan.Zero, TimeSpan.FromMinutes(1));
                        
                        // Start the timer to reload the server config every 1 minute
                        serverConfigCheckTimer = new Timer(async (e) => await LoadServerConfig(),
                            null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
                        
                        // Starting the local server
                        _ = Task.Run(async () => await Local_Server_Start());

                        // Establishing a connection to the local server
                        _ = Task.Run(async () => await Local_Server_Connect()); // Läuft im Hintergrund
                        
                        // Check user process status with a small delay to allow Windows sessions to stabilize during fast login
                        _ = Task.Run(async () => 
                        {
                            await Task.Delay(15000);
                            await CheckUserProcessStatus();
                        });
                    }
                    catch (Exception ex)
                    {
                        if (Agent.debug_mode)
                            Logging.Error("Service.OnStart", "Error during service startup.", ex.ToString());
                    }
                }

                await Task.Delay(1000, stoppingToken);
            }
            
            // Cleanup when service is stopping
            StopInputCommandProcessor();
            StopResponseQueueProcessor();
        }

        #region Comm Agent Local Server 
        private async Task Local_Server_Connect()
        {
            try
            {
                local_server_client = new TcpClient();
                await local_server_client.ConnectAsync(ServerIp, Port);

                _stream = local_server_client.GetStream();
                _ = Local_Server_Handle_Server_Messages(_cancellationTokenSource.Token);
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.Local_Server_Connect", "Connected to the local server.", "");


                // Previously used for initial device identity request. Removed that, but logic stays in place for future use cases.
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Error("Service.Local_Server_Connect", "Failed to connect to the local server.", ex.ToString());
            }
        }

        private async Task Local_Server_Handle_Server_Messages(CancellationToken cancellationToken)
        {
            try
            {
                byte[] buffer = new byte[2096];

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0) // Server disconnected
                        break;

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Local_Server_Handle_Server_Messages", "Received message", message);

                    // Split the message per $
                    string[] messageParts = message.Split('$');

                    // device_identity
                    if (messageParts[0].ToString() == "device_identity")
                    {
                        Logging.Debug("Service.Local_Server_Handle_Server_Messages", "Device identity received",
                            messageParts[1]);
                        
                        // Preset logic in place for future use cases
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.Local_Server_Handle_Server_Messages", "Failed to handle server messages.",
                    ex.ToString());
            }
        }

        private async Task Local_Server_Send_Message(string message)
        {
            try
            {
                if (_stream != null && local_server_client.Connected)
                {
if (Agent.debug_mode)
    Logging.Debug("Service.Local_Server_Send_Message", "Sent message", message);


                    byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                    await _stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                    await _stream.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.Local_Server_Send_Message", "Failed to send message to the local server.",
                    ex.ToString());
            }
        }

        private async Task Local_Server_Check_Connection_Status()
        {
            try
            {
                if (local_server_client == null)
                {
if (Agent.debug_mode)
    Logging.Error("Service.Check_Connection_Status", "local_server_client is null.", "");

                    return;
                }

                // Check if the local_server_client is connected and if, execute regular tasks
                if (local_server_client.Connected)
                {
if (Agent.debug_mode)
    Logging.Debug("Service.Check_Connection_Status", "Local server connection is active.", "");


                    // Previously used for initial device identity request. Removed that, but logic stays in place for future use cases.
                }
                else
                {
                    Logging.Debug("Service.Check_Connection_Status",
                        "Local server connection lost, attempting to reconnect.", "");
                    await Local_Server_Connect();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.Check_Connection_Status",
                    "Failed to check remote_server_client or local_server_client status.", ex.ToString());
            }
        }
        
        #endregion

        #region Response Queue System

        /// <summary>
        /// Starts the response queue processor that runs in the background and sends completed responses to the server.
        /// </summary>
        private void StartResponseQueueProcessor()
        {
            _responseQueueCancellationTokenSource = new CancellationTokenSource();
            _responseQueueProcessorTask = Task.Run(async () => await ProcessResponseQueue(_responseQueueCancellationTokenSource.Token));
            
            if (Agent.debug_mode)
                Logging.Debug("Service.ResponseQueue", "Response queue processor started", "");
        }

        /// <summary>
        /// Stops the response queue processor.
        /// </summary>
        private void StopResponseQueueProcessor()
        {
            try
            {
                _responseQueueCancellationTokenSource?.Cancel();
                _responseReadyQueue.CompleteAdding();
                _responseQueueProcessorTask?.Wait(TimeSpan.FromSeconds(5));
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ResponseQueue", "Response queue processor stopped", "");
            }
            catch (Exception ex)
            {
                Logging.Error("Service.ResponseQueue", "Error stopping response queue processor", ex.ToString());
            }
        }

        /// <summary>
        /// Registers a response_id in the queue. Call this when you start processing a command.
        /// </summary>
        /// <param name="responseId">The response ID to register</param>
        public void RegisterResponse(string responseId)
        {
            try
            {
                if (string.IsNullOrEmpty(responseId))
                    return;

                var item = new ResponseQueueItem
                {
                    ResponseId = responseId,
                    Result = null,
                    IsComplete = false,
                    CreatedAt = DateTime.UtcNow,
                    IsSent = false
                };

                _responseQueue.TryAdd(responseId, item);
            
                if (Agent.debug_mode)
                    Logging.Debug("Service.ResponseQueue", "Response registered", $"response_id: {responseId}");
            }
            catch (Exception e)
            {
                Logging.Error("Service.ResponseQueue", "Failed to register response", $"response_id: {responseId}, error: {e.Message}");
                throw;
            }
        }

        /// <summary>
        /// Completes a response by adding the result. This will trigger sending the response to the server.
        /// </summary>
        /// <param name="responseId">The response ID</param>
        /// <param name="result">The result to send back to the server</param>
        public void CompleteResponse(string responseId, string result)
        {
            try
            {
                if (string.IsNullOrEmpty(responseId))
                    return;

                // If not registered yet, register it first
                if (!_responseQueue.ContainsKey(responseId))
                {
                    RegisterResponse(responseId);
                }

                if (_responseQueue.TryGetValue(responseId, out var item))
                {
                    item.Result = result;
                    item.IsComplete = true;
                
                    // Signal the processor that this response is ready
                    try
                    {
                        _responseReadyQueue.Add(responseId);
                    
                        if (Agent.debug_mode)
                            Logging.Debug("Service.ResponseQueue", "Response completed and queued for sending", 
                                $"response_id: {responseId}, result_length: {result?.Length ?? 0}");
                    }
                    catch (InvalidOperationException)
                    {
                        // Queue is completed, can't add more items
                        if (Agent.debug_mode)
                            Logging.Debug("Service.ResponseQueue", "Response queue is closed, cannot add response", 
                                $"response_id: {responseId}");
                    }
                }
            }
            catch (Exception e)
            {
                Logging.Error("Service.ResponseQueue", "Failed to complete response", $"response_id: {responseId}, error: {e.Message}");
                throw;
            }
        }

        /// <summary>
        /// Processes the response queue and sends completed responses to the server.
        /// </summary>
        private async Task ProcessResponseQueue(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Wait for a response to be ready (blocking with cancellation support)
                    string responseId;
                    try
                    {
                        responseId = _responseReadyQueue.Take(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        // Collection is completed
                        break;
                    }

                    if (_responseQueue.TryGetValue(responseId, out var item) && item.IsComplete && !item.IsSent)
                    {
                        // Only send if result is not null/empty
                        if (!string.IsNullOrEmpty(item.Result))
                        {
                            try
                            {
                                // Wait for connection to be available
                                int retryCount = 0;
                                while (remote_server_client == null || 
                                       remote_server_client.State != HubConnectionState.Connected)
                                {
                                    if (cancellationToken.IsCancellationRequested)
                                        break;
                                    
                                    retryCount++;
                                    if (retryCount > 30) // Max 30 seconds wait
                                    {
                                        Logging.Error("Service.ResponseQueue", "Timeout waiting for server connection", 
                                            $"response_id: {responseId}");
                                        break;
                                    }
                                    
                                    await Task.Delay(1000, cancellationToken);
                                }

                                if (remote_server_client?.State == HubConnectionState.Connected)
                                {
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.ResponseQueue", "Sending response to server", 
                                            $"response_id: {responseId}, result_length: {item.Result.Length}");

                                    await remote_server_client.InvokeAsync("ReceiveClientResponse", 
                                        responseId, item.Result, false, cancellationToken);

                                    item.IsSent = true;
                                    
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.ResponseQueue", "Response sent successfully", 
                                            $"response_id: {responseId}");

                                    // Remove from queue after successful send
                                    _responseQueue.TryRemove(responseId, out _);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logging.Error("Service.ResponseQueue", "Failed to send response", 
                                    $"response_id: {responseId}, error: {ex.Message}");
                                
                                // Re-queue for retry if not cancelled
                                if (!cancellationToken.IsCancellationRequested)
                                {
                                    try
                                    {
                                        await Task.Delay(2000, cancellationToken);
                                        _responseReadyQueue.Add(responseId);
                                    }
                                    catch { }
                                }
                            }
                        }
                        else
                        {
                            // Result is empty, just remove from queue
                            _responseQueue.TryRemove(responseId, out _);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Logging.Error("Service.ResponseQueue", "Error in response queue processor", ex.ToString());
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }

            // Cleanup remaining items
            _responseQueue.Clear();
            
            if (Agent.debug_mode)
                Logging.Debug("Service.ResponseQueue", "Response queue processor exited", "");
        }

        /// <summary>
        /// Cleans up old responses that haven't been sent (e.g., orphaned entries).
        /// Call this periodically if needed.
        /// </summary>
        public void CleanupOldResponses(TimeSpan maxAge)
        {
            try
            {
                var cutoffTime = DateTime.UtcNow - maxAge;
                var oldKeys = _responseQueue.Where(kvp => kvp.Value.CreatedAt < cutoffTime && !kvp.Value.IsSent)
                    .Select(kvp => kvp.Key)
                    .ToList();
            
                foreach (var key in oldKeys)
                {
                    _responseQueue.TryRemove(key, out _);
                
                    if (Agent.debug_mode)
                        Logging.Debug("Service.ResponseQueue", "Removed old response", $"response_id: {key}");
                }
            }
            catch (Exception e)
            {
                Logging.Error("Service.ResponseQueue", "Failed to cleanup old responses", $"error: {e.Message}");
                throw;
            }
        }

        #endregion

        #region Input Command Queue System (Mouse/Keyboard)

        /// <summary>
        /// Starts the input command processor that handles mouse/keyboard commands with high priority.
        /// Uses System.Threading.Channels for lock-free, high-performance processing.
        /// </summary>
        private void StartInputCommandProcessor()
        {
            // Create an unbounded channel for maximum throughput
            // Single consumer ensures commands are processed in order
            _inputCommandChannel = Channel.CreateUnbounded<InputCommand>(new UnboundedChannelOptions
            {
                SingleReader = true,  // Only one processor reads from the channel
                SingleWriter = false, // Multiple SignalR handlers can write
                AllowSynchronousContinuations = true // Better performance for quick operations
            });

            _inputCommandCancellationTokenSource = new CancellationTokenSource();
            _inputCommandProcessorTask = Task.Run(async () => 
                await ProcessInputCommands(_inputCommandCancellationTokenSource.Token));
            
            if (Agent.debug_mode)
                Logging.Debug("Service.InputCommandQueue", "Input command processor started", "");
        }

        /// <summary>
        /// Stops the input command processor.
        /// </summary>
        private void StopInputCommandProcessor()
        {
            try
            {
                _inputCommandChannel?.Writer.Complete();
                _inputCommandCancellationTokenSource?.Cancel();
                _inputCommandProcessorTask?.Wait(TimeSpan.FromSeconds(2));
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.InputCommandQueue", "Input command processor stopped", "");
            }
            catch (Exception ex)
            {
                Logging.Error("Service.InputCommandQueue", "Error stopping input command processor", ex.ToString());
            }
        }

        /// <summary>
        /// Queues an input command (mouse/keyboard) for high-priority processing.
        /// This method returns immediately - the command is processed asynchronously.
        /// </summary>
        /// <param name="targetUser">The target user to send the command to</param>
        /// <param name="jsonPayload">The JSON payload containing the command</param>
        /// <param name="responseId">Optional response ID for tracking</param>
        public void QueueInputCommand(string targetUser, string jsonPayload, string responseId = null)
        {
            if (_inputCommandChannel == null)
            {
                if (Agent.debug_mode)
                    Logging.Debug("Service.InputCommandQueue", "Channel not initialized, executing directly", "");
                
                // Fallback: execute directly if channel not ready
                _ = Task.Run(async () => await SendToClient(targetUser, jsonPayload));
                return;
            }

            var command = new InputCommand
            {
                TargetUser = targetUser,
                JsonPayload = jsonPayload,
                ResponseId = responseId
            };

            // TryWrite is non-blocking and returns immediately
            if (!_inputCommandChannel.Writer.TryWrite(command))
            {
                // Channel is full or completed - fallback to direct execution
                if (Agent.debug_mode)
                    Logging.Debug("Service.InputCommandQueue", "Channel full, executing directly", "");
                
                _ = Task.Run(async () => await SendToClient(targetUser, jsonPayload));
            }
        }

        /// <summary>
        /// Processes input commands from the channel.
        /// Runs on a dedicated thread for maximum responsiveness.
        /// </summary>
        private async Task ProcessInputCommands(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var command in _inputCommandChannel.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        // Process the command immediately
                        await SendToClient(command.TargetUser, command.JsonPayload);
                        
                        // If there's a response ID and we need to confirm, we could do it here
                        // For now, input commands don't typically need responses
                    }
                    catch (Exception ex)
                    {
                        if (Agent.debug_mode)
                            Logging.Error("Service.InputCommandQueue", "Error processing input command", 
                                $"target: {command.TargetUser}, error: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                Logging.Error("Service.InputCommandQueue", "Input command processor crashed", ex.ToString());
            }
            
            if (Agent.debug_mode)
                Logging.Debug("Service.InputCommandQueue", "Input command processor exited", "");
        }

        #endregion

        #region SignalR Remote Server
        
        private readonly SemaphoreSlim _signalRConnectionLock = new SemaphoreSlim(1, 1);
        private bool _signalRConnecting = false;

        private async Task Remote_Server_Check_Connection_Status()
        {
            try
            {
                if (!_agentSettingsRemoteServiceEnabled)
                {
                    // Close the connection if open and return
                    if (remote_server_client != null &&
                        (remote_server_client.State == HubConnectionState.Connected ||
                         remote_server_client.State == HubConnectionState.Connecting ||
                         remote_server_client.State == HubConnectionState.Reconnecting))
                    {
                        Logging.Debug("Service.Check_Connection_Status",
                            "Remote service disabled in agent settings, closing connection.", "");

                        await remote_server_client.StopAsync();
                        await remote_server_client.DisposeAsync();
                        remote_server_client = null;
                        remote_server_client_setup = false;
                    }
                }
                
                if (_agentSettingsRemoteServiceEnabled && !string.IsNullOrEmpty(device_identity_json))
                {
                    // Prevents multiple simultaneous connection attempts (in place to test remote screen control keyboard ghosting) https://github.com/0x101-Cyber-Security/NetLock-RMM/issues/89
                    await _signalRConnectionLock.WaitAsync();
                    
                    try
                    {
                        if (_signalRConnecting)
                            return;

                        if (!remote_server_client_setup || remote_server_client == null ||
                            remote_server_client.State == HubConnectionState.Disconnected)
                        {
                            _signalRConnecting = true;
                            await Setup_SignalR();
                        }
                        else if (remote_server_client.State == HubConnectionState.Connected)
                        {
                            Logging.Debug("Service.Check_Connection_Status",
                                "Remote server connection is already active.", "");
                        }
                    }
                    finally
                    {
                        _signalRConnecting = false;
                        _signalRConnectionLock.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.Check_Connection_Status", "Failed to check remote_server_client status.",
                    ex.ToString());
            }
        }

        public async Task Setup_SignalR()
        {
            try
            {
                // Check if the device_identity is empty, if so, return
                if (String.IsNullOrEmpty(device_identity_json))
                {
                    if (Agent.debug_mode)
                        Logging.Error("Service.Setup_SignalR", "Device identity is empty.", "");

                    return;
                }
                else
                    Logging.Debug("Service.Setup_SignalR", "Device identity is not empty. Preparing remote connection.",
                        "");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.Setup_SignalR", "Device identity JSON", device_identity_json);

                // Deserialise device identity
                var jsonDocument = JsonDocument.Parse(device_identity_json);
                var deviceIdentityElement = jsonDocument.RootElement.GetProperty("device_identity");

                Device_Identity device_identity_object =
                    JsonSerializer.Deserialize<Device_Identity>(deviceIdentityElement.ToString());

                if (remote_server_client != null)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Setup_SignalR", "Disposing existing remote server client.", "");

                    await remote_server_client.StopAsync();
                    await remote_server_client.DisposeAsync();
                    remote_server_client = null;
                }

                remote_server_client = new HubConnectionBuilder()
                    .WithUrl(Global.Configuration.Agent.http_https + remote_server, options =>
                    {
                        options.Headers.Add("Device-Identity", Uri.EscapeDataString(device_identity_json));
                        options.UseStatefulReconnect = true;
                        options.WebSocketConfiguration = socket =>
                        {
                            socket.KeepAliveInterval = TimeSpan.FromSeconds(30);
                        };
                    }).ConfigureLogging(logging =>
                    {
                        if (Agent.debug_mode) 
                            logging.AddConsole();

                        if (Agent.debug_mode) 
                            logging.SetMinimumLevel(LogLevel.Warning);

                    })
                    .WithAutomaticReconnect()
                    .Build();

                // Handle ConnectionEstablished event from server
                remote_server_client.On<string>("ConnectionEstablished", (message) =>
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Setup_SignalR", "ConnectionEstablished with message", message);

                    // Connection established, no further action needed
                });

                // Handle ConnectionEstablished event from server - without parameter
                remote_server_client.On("ConnectionEstablished", () =>
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Setup_SignalR", "ConnectionEstablished without message", "");

                    // Connection established, no further action needed
                });

                remote_server_client.On<string>("SendMessageToClient", async (command) =>
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Setup_SignalR", "SendMessageToClient", command);

                    // Deserialisation of the entire JSON string
                    Command_Entity command_object = JsonSerializer.Deserialize<Command_Entity>(command);

                    try
                    {
                        // Insert the logic here to execute the command
                        if (command_object.type == 61 &&
                            _agentSettingsRemoteScreenControlEnabled) // Tray Icon - Hide chat window
                        {
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Tray icon command", command_object.command);

                            //  Create the JSON object
                            var jsonObject = new
                            {
                                response_id = command_object.response_id,
                                type = "hide_chat_window",
                            };

                            // Convert the object into a JSON string
                            string json = JsonSerializer.Serialize(jsonObject,
                                new JsonSerializerOptions { WriteIndented = true });

                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Remote Control json", json);
                            
                            // Send through local server to tray icon user process
                            await SendToClient(command_object.remote_control_username + "tray", json);
                        }
                        else if (command_object.type == 62) // Tray Icon - Play sound
                        {
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Tray icon command", command_object.command);

                            //  Create the JSON object
                            var jsonObject = new
                            {
                                response_id = command_object.response_id,
                                type = "play_sound",
                            };

                            // Convert the object into a JSON string
                            string json = JsonSerializer.Serialize(jsonObject,
                                new JsonSerializerOptions { WriteIndented = true });

                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Remote Control json", json);

                            // Send through local server to tray icon user process
                            await SendToClient(command_object.remote_control_username + "tray", json);
                        }
                        else if (command_object.type == 63 &&
                                 _agentSettingsRemoteScreenControlEnabled) // Tray Icon - Exit chat window
                        {
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Tray icon command", command_object.command);

                            //  Create the JSON object
                            var jsonObject = new
                            {
                                response_id = command_object.response_id,
                                type = "exit_chat_window",
                            };

                            // Convert the object into a JSON string
                            string json = JsonSerializer.Serialize(jsonObject,
                                new JsonSerializerOptions { WriteIndented = true });

                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Remote Control json", json);

                            // Send through local server to tray icon user process
                            await SendToClient(command_object.remote_control_username + "tray", json);
                        }
                        else if (command_object.type == 64 &&
                                 _agentSettingsRemoteScreenControlEnabled) // Tray Icon - Send message
                        {
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Tray icon command", command_object.command);


                            //  Create the JSON object
                            var jsonObject = new
                            {
                                response_id = command_object.response_id,
                                type = "new_chat_message",
                                command = command_object.command,
                            };

                            // Convert the object into a JSON string
                            string json = JsonSerializer.Serialize(jsonObject,
                                new JsonSerializerOptions { WriteIndented = true });

                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Remote Control json", json);
                            
                            // Send through local server to tray icon user process
                            await SendToClient(command_object.remote_control_username + "tray", json);
                        }
                        else if (command_object.type == 8 &&
                                 _agentSettingsRemoteScreenControlEnabled) // End remote screen control access
                        {

                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Tray icon command", command_object.command);


                            //  Create the JSON object
                            var jsonObject = new
                            {
                                response_id = command_object.response_id,
                                type = "end_remote_session",
                                command = command_object.command,
                            };

                            // Convert the object into a JSON string
                            string json = JsonSerializer.Serialize(jsonObject,
                                new JsonSerializerOptions { WriteIndented = true });

                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Remote Control json", json);

                            // Send through local server to tray icon user process
                            await SendToClient(command_object.remote_control_username + "tray", json);

                            // Remove user from allowed remote screen control users
                            _remoteScreenControlGrantedUsers.Remove(command_object.remote_control_username);

                            Logging.Debug("Service.Setup_SignalR", "Remote Control access ended for user",
                                command_object.remote_control_username);
                        }
                        else if (command_object.type == 14) // Relay Connection Request
                        {
                            try
                            {
                                //Console.WriteLine($"[RELAY] Received Relay Connection Request");
                                // Parse relay details from command field
                                var relayDetails =
                                    JsonSerializer.Deserialize<RelayConnectionDetails>(command_object.command);

                                if (relayDetails == null)
                                {
                                    if (Agent.debug_mode)
                                        Logging.Error("Service.Setup_SignalR",
                                            "Failed to parse relay connection details", "relayDetails is null");
                                    return;
                                }

                                // Check if session already exists AND is still active
                                if (_activeRelaySessions.TryGetValue(relayDetails.session_id, out var existingCts))
                                {
                                    // Check if old session is still running or already cancelled
                                    bool isOldSessionActive = !existingCts.IsCancellationRequested;

                                    //Console.WriteLine($"[RELAY] Session {relayDetails.session_id} exists in dictionary - checking status...");
                                    //Console.WriteLine($"[RELAY] Old session active: {isOldSessionActive} (CTS cancelled: {existingCts.IsCancellationRequested})");

                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Relay session exists - checking status",
                                            $"session_id: {relayDetails.session_id}, active: {isOldSessionActive}");
                                    
                                    // Case 1: Old session is still active AND new Admin Public Key present -> Admin change
                                    if (isOldSessionActive && !string.IsNullOrEmpty(relayDetails.admin_public_key))
                                    {
                                        //Console.WriteLine($"[RELAY] Admin Public Key detected - ADMIN CHANGE - closing old connection...");

                                        if (Agent.debug_mode)
                                            Logging.Debug("Service.Setup_SignalR", "Admin change detected - closing old connection",
                                                $"session_id: {relayDetails.session_id}");
                                        
                                        // End old relay connection
                                        existingCts.Cancel();
                                        _activeRelaySessions.TryRemove(relayDetails.session_id, out _);
                                        
                                        // Wait 2 seconds for backend + agent to completely clean up
                                        //Console.WriteLine($"[RELAY] Waiting 2 seconds for complete cleanup (Admin kick)...");
                                        await Task.Delay(2000);
                                        //Console.WriteLine($"[RELAY] Cleanup delay complete");

                                        //Console.WriteLine($"[RELAY] Old connection closed - starting NEW connection with new Admin Key");

                                        if (Agent.debug_mode)
                                            Logging.Debug("Service.Setup_SignalR", "Old connection closed - reconnecting with new Admin",
                                                $"session_id: {relayDetails.session_id}");
                                    }
                                    // Case 2: Old session is DEAD (cancelled) -> Cleanup finished, reconnect
                                    else if (!isOldSessionActive)
                                    {
                                        //Console.WriteLine($"[RELAY] Old session is dead (cancelled) - removing from dictionary and reconnecting...");

                                        if (Agent.debug_mode)
                                            Logging.Debug("Service.Setup_SignalR", "Old session dead - cleanup and reconnect",
                                                $"session_id: {relayDetails.session_id}");

                                        // Remove dead session from dictionary
                                        _activeRelaySessions.TryRemove(relayDetails.session_id, out _);

                                        // Short delay for final cleanup
                                        await Task.Delay(500);

                                        //Console.WriteLine($"[RELAY] Dead session cleaned - starting NEW connection");
                                    }
                                    // Case 3: Old session active, NO Admin Public Key -> Duplicate, ignore
                                    else
                                    {
                                        //Console.WriteLine($"[RELAY] Session active and no Admin Key in command - ignoring duplicate");

                                        if (Agent.debug_mode)
                                            Logging.Debug("Service.Setup_SignalR", "Duplicate request - ignoring",
                                                $"session_id: {relayDetails.session_id}");
                                        return;
                                    }
                                }

                                if (Agent.debug_mode)
                                    Logging.Debug("Service.Setup_SignalR", "Relay Connection Request",
                                        $"session_id: {relayDetails.session_id}, local_port: {relayDetails.local_port}, protocol: {relayDetails.protocol}");

                                // Connect to relay server (new or after admin change)
                                _ = Task.Run((Func<Task>)(async () => await ConnectToRelayServer(
                                    relayDetails
                                )));
                            }
                            catch (Exception ex)
                            {
                                Logging.Error("Service.Setup_SignalR", "Failed to initiate relay connection",
                                    ex.ToString());
                            }
                        }
                        else if (command_object.type == 16) // Relay Close Connection
                        {
                            try
                            {
                                // Parse session_id aus command-Feld
                                var closeDetails =
                                    JsonSerializer.Deserialize<RelayCloseDetails>(command_object.command);

                                if (closeDetails == null || string.IsNullOrEmpty(closeDetails.session_id))
                                {
                                    if (Agent.debug_mode)
                                        Logging.Error("Service.Setup_SignalR", "Failed to parse relay close details",
                                            "closeDetails is null or session_id is missing");
                                    return;
                                }

                                if (Agent.debug_mode)
                                    Logging.Debug("Service.Setup_SignalR", "Relay Close Connection",
                                        $"session_id: {closeDetails.session_id}");

                                if (_activeRelaySessions.TryRemove(closeDetails.session_id, out var cts))
                                {
                                    cts.Cancel();
                                    cts.Dispose();

                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Relay connection closed",
                                            closeDetails.session_id);
                                }
                                else
                                {
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Relay session not found",
                                            closeDetails.session_id);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logging.Error("Service.Setup_SignalR", "Failed to close relay connection",
                                    ex.ToString());
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (Agent.debug_mode)
                            Logging.Error("Service.Setup_SignalR", "Failed to deserialize command object.",
                                ex.ToString());
                    }

                    // Example: If the command is "sync", send a message to the local server to force a sync with the remote server
                    if (command == "sync")
                        await Local_Server_Send_Message("sync");
                });

                // Receive a message from the remote server, process the command and send a response back to the remote server
                remote_server_client.On<string>("SendMessageToClientAndWaitForResponse", async (command) =>
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Setup_SignalR", "SendMessageToClientAndWaitForResponse", command);
                    
                    // Deserialisation of the entire JSON string
                    Command_Entity command_object = JsonSerializer.Deserialize<Command_Entity>(command);
                    // Example: If the type is 0, execute the powershell code and send the response back to the remote server if wait_response = true

                    string result = string.Empty;

                    try
                    {
                        if (command_object.type == 0 && _agentSettingsRemoteShellEnabled) // Remote Shell
                        {
                            if (OperatingSystem.IsWindows())
                                result = Windows.Helper.PowerShell.Execute_Script("Remote Shell",
                                    command_object.powershell_code, Convert.ToInt32(command_object.command));
                            else if (OperatingSystem.IsLinux())
                                result = Linux.Helper.Bash.Execute_Script("Remote Shell", true,
                                    command_object.powershell_code, Convert.ToInt32(command_object.command));
                            else if (OperatingSystem.IsMacOS())
                                result = MacOS.Helper.Zsh.Execute_Script("Remote Shell", true,
                                    command_object.powershell_code, Convert.ToInt32(command_object.command));
                            
                            if (Agent.debug_mode)
                                Logging.Debug("Client", "PowerShell executed", result);

                        }
                        else if (command_object.type == 1 && _agentSettingsRemoteFileBrowserEnabled) // File Browser
                        {
                            // if linux or macos convert the path to linux/macos path
                            if (!String.IsNullOrEmpty(command_object.file_browser_path) && OperatingSystem.IsLinux() ||
                                OperatingSystem.IsMacOS())
                                command_object.file_browser_path = command_object.file_browser_path.Replace("\\", "/");

                            // 0 = get drives, 1 = index, 2 = create dir, 3 = delete dir, 4 = move dir, 5 = rename dir, 6 = create file, 7 = delete file, 8 = move file, 9 = rename file, 10 = download file, 11 = upload file

                            // File Browser Command

                            if (command_object.file_browser_command == 0) // Get drives
                            {
                                result = IO.Get_Drives();
                            }

                            if (command_object.file_browser_command == 1) // index
                            {
                                // Get all directories and files in the specified path, create a json including date, size and file type
                                var directoryDetails = await IO.Get_Directory_Index(command_object.file_browser_path);
                                result = JsonSerializer.Serialize(directoryDetails,
                                    new JsonSerializerOptions { WriteIndented = true });
                            }
                            else if (command_object.file_browser_command == 2) // create dir
                            {
                                result = IO.Create_Directory(command_object.file_browser_path);
                            }
                            else if (command_object.file_browser_command == 3) // delete dir
                            {
                                result = IO.Delete_Directory(command_object.file_browser_path).ToString();
                            }
                            else if (command_object.file_browser_command == 4) // move dir
                            {
                                result = IO.Move_Directory(command_object.file_browser_path,
                                    command_object.file_browser_path_move);
                            }
                            else if (command_object.file_browser_command == 5) // rename dir
                            {
                                result = IO.Rename_Directory(command_object.file_browser_path,
                                    command_object.file_browser_path_move);
                            }
                            else if (command_object.file_browser_command == 6) // create file
                            {
                                result = await IO.Create_File(command_object.file_browser_path,
                                    command_object.file_browser_file_content);
                            }
                            else if (command_object.file_browser_command == 7) // delete file
                            {
                                result = IO.Delete_File(command_object.file_browser_path).ToString();
                            }
                            else if (command_object.file_browser_command == 8) // move file
                            {
                                result = IO.Move_File(command_object.file_browser_path,
                                    command_object.file_browser_path_move);
                            }
                            else if (command_object.file_browser_command == 9) // rename file
                            {
                                result = IO.Rename_File(command_object.file_browser_path,
                                    command_object.file_browser_path_move);
                            }
                            else if (command_object.file_browser_command == 10) // download file from file server
                            {
                                // download url with tenant guid, location guid & device name
                                string download_url = file_server + "/admin/files/download/device" + "?guid=" +
                                                      command_object.file_browser_file_guid + "&tenant_guid=" +
                                                      device_identity_object.tenant_guid + "&location_guid=" +
                                                      device_identity_object.location_guid + "&device_name=" +
                                                      device_identity_object.device_name + "&access_key=" +
                                                      device_identity_object.access_key + "&hwid=" +
                                                      device_identity_object.hwid;
                                
                                if (Agent.debug_mode)
                                    Logging.Debug("Service.Setup_SignalR", "Download URL", download_url);
                                
                                result = await Http.DownloadFileAsync(Global.Configuration.Agent.ssl, download_url,
                                    command_object.file_browser_path, device_identity_object.package_guid);
                                
                                if (Agent.debug_mode)
                                    Logging.Debug("Service.Setup_SignalR", "File downloaded", result);
                            }
                            else if (command_object.file_browser_command == 11) // upload file
                            {
                                string file_name = Path.GetFileName(command_object.file_browser_path);

                                // upload url with tenant guid, location guid & device name
                                string upload_url = file_server + "/admin/files/upload/device" + "?tenant_guid=" +
                                                    device_identity_object.tenant_guid + "&location_guid=" +
                                                    device_identity_object.location_guid + "&device_name=" +
                                                    device_identity_object.device_name + "&access_key=" +
                                                    device_identity_object.access_key + "&hwid=" +
                                                    device_identity_object.hwid;

                                if (Agent.debug_mode)
                                    Logging.Debug("Service.Setup_SignalR", "Upload URL", upload_url);

                                // Upload the file to the server
                                result = await Http.UploadFileAsync(Global.Configuration.Agent.ssl, upload_url, command_object.file_browser_path,
                                    device_identity_object.package_guid);
                            }
                        }
                        else if (command_object.type == 2 && _agentSettingsRemoteServiceManagerEnabled) // Service
                        {
                            // Deserialise the command_object.command json, using json document (action, name)
                            
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Service command", command_object.command);
                            
                            string action = String.Empty;
                            string name = String.Empty;

                            using (JsonDocument doc = JsonDocument.Parse(command_object.command))
                            {
                                JsonElement root = doc.RootElement;

                                // Access to the "action" field
                                action = root.GetProperty("action").GetString();

                                // Access to the "name" field
                                name = root.GetProperty("name").GetString();
                            }

                            // Execute
                            result = await Service.Action(action, name);
                            
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Service Action", result);
                        }
                        else if (command_object.type == 3 && _agentSettingsRemoteTaskManagerEnabled) // Task Manager Action
                        {
                            // Terminate process by pid
                            result = await Task_Manager.Terminate_Process_Tree(Convert.ToInt32(command_object.command));
                            
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Terminate Process", result);

                        }
                        else if (command_object.type == 4 && _agentSettingsRemoteScreenControlEnabled) // Remote Screen Control
                        {
                            // Check if the command requests connected users
                            if (command_object.command == "4")
                            {
                                // Get connected users from _clients, excluding those with "tray" suffix
                                List<string> connected_users = _clients.Keys
                                    .Where(u => !u.EndsWith("tray", StringComparison.OrdinalIgnoreCase))
                                    .ToList();

                                // Move the device name user (if existing) to the first position
                                if (connected_users.Contains(device_identity_object.device_name))
                                {
                                    connected_users.Remove(device_identity_object.device_name);
                                    connected_users.Insert(0, device_identity_object.device_name);
                                }
                                
                                // Convert the list to a comma separated string 
                                result = string.Join(",", connected_users);
                            }
                            else // Forward the command to the users process
                            {
                                if (!_remoteScreenControlAccessGranted || !_agentSettingsRemoteScreenControlEnabled)
                                {
                                    result = "Remote screen control access denied.";
                                    
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Remote Control access denied", result);
                                    
                                    // Return because no further action is required
                                    return;
                                }
                                
                                // Check if command_object.remote_control_username is allowed to use remote screen control (_remoteScreenControlGrantedUsers)
                                if (!_remoteScreenControlGrantedUsers.Contains(command_object.remote_control_username))
                                {
                                    result = "Remote screen control access denied for user: " +  command_object.remote_control_username;
                                    
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Remote Control access denied", result);
                                    
                                    // Return because no further action is required
                                    return;
                                }
                                
                                try
                                {
                                    Logging.Debug("Service.Setup_SignalR", "Remote Control command",
                                        command_object.command);

                                    // Check if ctrlaltdel and send through SAS instead
                                    if (command_object.remote_control_keyboard_input == "ctrlaltdel")
                                    {
                                        Logging.Debug("Service.Setup_SignalR", "Sending SAS for ctrlaltdel",
                                            command_object.remote_control_username);

                                        Sas_Diagnostics.LogContext();

                                        User32.SendSAS(false); // Send the SAS twice to ensure it is processed correctly
                                        //User32.SendSAS(true); // Send the SAS twice to ensure it is processed correctly
                                    }

                                    //  Create the JSON object
                                    var jsonObject = new
                                    {
                                        response_id = command_object.response_id,
                                        type = command_object.command,
                                        remote_control_screen_index = command_object.remote_control_screen_index,
                                        remote_control_mouse_action = command_object.remote_control_mouse_action,
                                        remote_control_mouse_xyz = command_object.remote_control_mouse_xyz,
                                        remote_control_keyboard_input = command_object.remote_control_keyboard_input,
                                        remote_control_keyboard_content = command_object.remote_control_keyboard_content,
                                        remote_control_elevation_username = command_object.remote_control_elevation_username,
                                        remote_control_elevation_password = command_object.remote_control_elevation_password,
                                        remote_control_render_mode = command_object.remote_control_render_mode,
                                    };

                                    // Convert the object into a JSON string (use minimal formatting for performance)
                                    string json = JsonSerializer.Serialize(jsonObject);
                                    
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Remote Control json", json);

                                    // Use the high-priority input command queue for mouse/keyboard commands
                                    // This prevents input commands from being blocked by screen capture or other operations
                                    QueueInputCommand(command_object.remote_control_username, json, command_object.response_id);
                                }
                                catch (Exception ex)
                                {
                                    Logging.Error("Service.Setup_SignalR", "Failed to execute remote control command.",
                                        ex.ToString());
                                }

                                // Return because no further action is required
                                return;
                            }
                        }
                        else if (command_object.type == 6 && _agentSettingsRemoteScreenControlEnabled) // Tray Icon - Show chat window
                        {
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Tray icon command", command_object.command);
                            
                            //  Create the JSON object
                            var jsonObject = new
                            {
                                response_id = command_object.response_id,
                                type = "show_chat_window",
                                command = command_object.command,
                            };

                            // Convert the object into a JSON string
                            string json = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
                            
                            if (Agent.debug_mode)
                                Logging.Debug("Service.Setup_SignalR", "Remote Control json", json);

                            // Send through local server to tray icon user process
                            await SendToClient(command_object.remote_control_username + "tray", json);
                            
                            // Return because no further action is required
                            return;
                        }
                        else if (command_object.type == 7) // Ask for remote screen control access
                        {
                            if (!_agentSettingsRemoteScreenControlEnabled)
                            {
                                _remoteScreenControlAccessGranted = false;
                                _remoteScreenControlGrantedUsers.Clear();
                                result = "Remote screen control access denied by policy settings.";
                            }
                            else
                            {
                                _remoteScreenControlAccessGranted = true;

                                if (_agentSettingsRemoteScreenControlUnattendedAccess)
                                {
                                    _remoteScreenControlGrantedUsers.Add(command_object.remote_control_username);
                                    result = "accepted";
                                    
                                    // Disable windows fast logon to prevent issues with cached credentials
                                    if (OperatingSystem.IsWindows())
                                    {
                                        // HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion\Winlogon
                                        
                                        // SyncForegroundPolicy = 1 (disables async logon)
                                        Registry.HKLM_Write_Value(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "SyncForegroundPolicy", "1");
                                    }
                                }
                                else
                                {
                                    // If windows fast logon is disabled, enable it back to previous state
                                    if (OperatingSystem.IsWindows())
                                    {
                                        // HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion\Winlogon
                                        
                                        // SyncForegroundPolicy = 0 or delete (enables async logon)
                                        Registry.HKLM_Delete_Value(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon", "SyncForegroundPolicy");
                                    }
                                    
                                    // Send request to tray icon to show chat window
                                    var jsonObject = new
                                    {
                                        response_id = command_object.response_id,
                                        type = "access_request",
                                        command = command_object.command
                                    };

                                    string json = JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions { WriteIndented = true });
                                    
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Remote Control access request json", json);

                                    await SendToClient(command_object.remote_control_username + "tray", json);

                                    result = "Remote screen control access request sent to the user.";
                                    
                                    // Return here to prevent losing the response_id context
                                    return;
                                }
                            }
                        }
                        else if (command_object.type == 9) // Power actions
                        {
                            if (command_object.command == "shutdown")
                                result = "shutdowndone";
                            else if (command_object.command == "reboot")
                                result = "rebootdone";
                            
                            // Send immediate response back to server
                            await remote_server_client.InvokeAsync("ReceiveClientResponse", command_object.response_id,
                                result, false);
                            
                            if (command_object.command == "shutdown")
                            {
                                if (OperatingSystem.IsWindows())
                                    result = Windows.Helper.PowerShell.Execute_Command("shutdown", "Stop-Computer -ComputerName localhost -Force", 60000);
                                else if (OperatingSystem.IsLinux())
                                    result = Linux.Helper.Bash.Execute_Script("shutdown", false, "sudo shutdown now");
                                else if (OperatingSystem.IsMacOS())
                                    result = MacOS.Helper.Zsh.Execute_Script("shutdown", false, "sudo shutdown now");
                            }
                            else if (command_object.command == "reboot")
                            {
                                if (OperatingSystem.IsWindows())
                                    result = Windows.Helper.PowerShell.Execute_Command("restart",
                                        "Restart-Computer -ComputerName localhost -Force", 60000);
                                else if (OperatingSystem.IsLinux())
                                    result = Linux.Helper.Bash.Execute_Script("restart", false, "sudo reboot");
                                else if (OperatingSystem.IsMacOS())
                                    result = MacOS.Helper.Zsh.Execute_Script("restart", false, "sudo reboot");
                            }
                            
                            return; // Return here to prevent sending a second response
                        }
                        else if (command_object.type == 10) // Event Log - Simple Commands (Windows only)
                        {
                            if (!OperatingSystem.IsWindows())
                            {
                                result = JsonSerializer.Serialize(new
                                {
                                    success = false,
                                    error = "Event Log is only available on Windows",
                                    timestamp = DateTime.UtcNow.ToString("o")
                                }, new JsonSerializerOptions { WriteIndented = true });
                            }
                            else
                            {
                                try
                                {
                                    // command_object.command contains the event log command as a simple string ("1", "3", "4")
                                    int eventLogCommand = Convert.ToInt32(command_object.command);
                                    
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Event Log command", eventLogCommand.ToString());
                                    
                                    if (eventLogCommand == 1) // Get available event logs
                                    {
                                        result = Eventlog.GetAvailableEventLogs();
                                    }
                                    else if (eventLogCommand == 3) // Get event log stats (requires log_name in future)
                                    {
                                        // Default to Application log for now
                                        string logName = "Application";
                                        result = Eventlog.GetEventLogStats(logName);
                                    }
                                    else if (eventLogCommand == 4) // Clear event log (requires log_name in future)
                                    {
                                        // Default to Application log for now
                                        string logName = "Application";
                                        result = Eventlog.ClearEventLog(logName);
                                    }
                                    else
                                    {
                                        result = JsonSerializer.Serialize(new
                                        {
                                            success = false,
                                            error = $"Unknown event log command: {eventLogCommand}. Use Type 11 for reading event logs.",
                                            timestamp = DateTime.UtcNow.ToString("o")
                                        }, new JsonSerializerOptions { WriteIndented = true });
                                    }
                                    
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Event Log result", result);
                                }
                                catch (Exception ex)
                                {
                                    Logging.Error("Service.Setup_SignalR", "Failed to execute event log command", ex.ToString());
                                    result = JsonSerializer.Serialize(new
                                    {
                                        success = false,
                                        error = ex.Message,
                                        timestamp = DateTime.UtcNow.ToString("o")
                                    }, new JsonSerializerOptions { WriteIndented = true });
                                }
                            }
                        }
                        else if (command_object.type == 11) // Event Log - Read Event Log with Parameters (Windows only)
                        {
                            if (!OperatingSystem.IsWindows())
                            {
                                result = JsonSerializer.Serialize(new
                                {
                                    success = false,
                                    error = "Event Log is only available on Windows",
                                    timestamp = DateTime.UtcNow.ToString("o")
                                }, new JsonSerializerOptions { WriteIndented = true });
                            }
                            else
                            {
                                try
                                {
                                    // Parse the command JSON to extract parameters for reading event logs
                                    using (JsonDocument doc = JsonDocument.Parse(command_object.command))
                                    {
                                        JsonElement root = doc.RootElement;

                                        string logName = root.TryGetProperty("log_name", out var logNameElement)
                                            ? logNameElement.GetString() ?? "Application"
                                            : "Application";

                                        int maxEntries = root.TryGetProperty("max_entries", out var maxEntriesElement)
                                            ? maxEntriesElement.GetInt32()
                                            : 100;

                                        byte? level = null;
                                        if (root.TryGetProperty("level", out var levelElement) && levelElement.ValueKind != JsonValueKind.Null)
                                            level = levelElement.GetByte();

                                        int? eventId = null;
                                        if (root.TryGetProperty("event_id", out var eventIdElement) && eventIdElement.ValueKind != JsonValueKind.Null)
                                            eventId = eventIdElement.GetInt32();

                                        DateTime? startTime = null;
                                        if (root.TryGetProperty("start_time", out var startTimeElement) && startTimeElement.ValueKind != JsonValueKind.Null)
                                        {
                                            string startTimeStr = startTimeElement.GetString() ?? string.Empty;
                                            if (!string.IsNullOrEmpty(startTimeStr))
                                            {
                                                // Support both ISO 8601 and "yyyy-MM-dd HH:mm:ss" formats
                                                if (DateTime.TryParse(startTimeStr, out DateTime parsedStartTime))
                                                    startTime = parsedStartTime;
                                            }
                                        }

                                        DateTime? endTime = null;
                                        if (root.TryGetProperty("end_time", out var endTimeElement) && endTimeElement.ValueKind != JsonValueKind.Null)
                                        {
                                            string endTimeStr = endTimeElement.GetString() ?? string.Empty;
                                            if (!string.IsNullOrEmpty(endTimeStr))
                                            {
                                                // Support both ISO 8601 and "yyyy-MM-dd HH:mm:ss" formats
                                                if (DateTime.TryParse(endTimeStr, out DateTime parsedEndTime))
                                                    endTime = parsedEndTime;
                                            }
                                        }

                                        string? providerName = null;
                                        if (root.TryGetProperty("provider_name", out var providerNameElement) && providerNameElement.ValueKind != JsonValueKind.Null)
                                        {
                                            providerName = providerNameElement.GetString();
                                            // Treat empty string as null
                                            if (string.IsNullOrWhiteSpace(providerName))
                                                providerName = null;
                                        }

                                        if (Agent.debug_mode)
                                            Logging.Debug("Service.Setup_SignalR", "Reading Event Log",
                                                $"LogName: {logName}, MaxEntries: {maxEntries}, Level: {level}, EventId: {eventId}, StartTime: {startTime}, EndTime: {endTime}, Provider: {providerName}");

                                        result = Eventlog.ReadEventLog(logName, maxEntries, level, eventId, startTime, endTime, providerName);
                                    }

                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Event Log read result", result);
                                }
                                catch (Exception ex)
                                {
                                    Logging.Error("Service.Setup_SignalR", "Failed to read event log", ex.ToString());
                                    result = JsonSerializer.Serialize(new
                                    {
                                        success = false,
                                        error = ex.Message,
                                        timestamp = DateTime.UtcNow.ToString("o")
                                    }, new JsonSerializerOptions { WriteIndented = true });
                                }
                            }
                        }
                        else if (command_object.type == 12) // Get eventlog stats
                        {
                            if (!OperatingSystem.IsWindows())
                            {
                                result = JsonSerializer.Serialize(new
                                {
                                    success = false,
                                    error = "Event Log is only available on Windows",
                                    timestamp = DateTime.UtcNow.ToString("o")
                                }, new JsonSerializerOptions { WriteIndented = true });
                            }
                            else
                            {
                                try
                                {
                                    string logName = command_object.command ?? "Application";
                                    result = Eventlog.GetEventLogStats(logName);
                                }
                                catch (Exception ex)
                                {
                                    Logging.Error("Service.Setup_SignalR", "Failed to get event log stats", ex.ToString());
                                    result = JsonSerializer.Serialize(new
                                    {
                                        success = false,
                                        error = ex.Message,
                                        timestamp = DateTime.UtcNow.ToString("o")
                                    }, new JsonSerializerOptions { WriteIndented = true });
                                }
                            }
                        }
                        else if (command_object.type == 13) // Clear eventlogs
                        {
                            if (!OperatingSystem.IsWindows())
                            {
                                result = JsonSerializer.Serialize(new
                                {
                                    success = false,
                                    error = "Event Log is only available on Windows",
                                    timestamp = DateTime.UtcNow.ToString("o")
                                }, new JsonSerializerOptions { WriteIndented = true });
                            }
                            else
                            {
                                try
                                {
                                    string logName = command_object.command ?? "Application";
                                    result = Eventlog.ClearEventLog(logName);
                                }
                                catch (Exception ex)
                                {
                                    Logging.Error("Service.Setup_SignalR", "Failed to clear event log", ex.ToString());
                                    result = JsonSerializer.Serialize(new
                                    {
                                        success = false,
                                        error = ex.Message,
                                        timestamp = DateTime.UtcNow.ToString("o")
                                    }, new JsonSerializerOptions { WriteIndented = true });
                                }
                            }
                        }
                        else if (command_object.type == 15) // Virtual Display Management
                        {
                            try
                            {
                                if (!OperatingSystem.IsWindows())
                                {
                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.Setup_SignalR", "Virtual Display not supported", "Only available on Windows");
                                    
                                    result = "Virtual display driver is only supported on Windows";
                                }
                                else
                                {
                                    // Parse command JSON
                                    using (JsonDocument doc = JsonDocument.Parse(command_object.command))
                                    {
                                        var root = doc.RootElement;
                                        string action = root.GetProperty("action").GetString();

                                        if (Agent.debug_mode)
                                            Logging.Debug("Service.Setup_SignalR", "Virtual Display Command", $"Action: {action}");
                                        
                                        // Fire-and-Forget: Execute in background and send result when done
                                        var responseId = command_object.response_id;
                                        var rootElementCopy = root.Clone();
                                        
                                        _ = Task.Run(async () =>
                                        {
                                            string taskResult;
                                            try
                                            {
                                                taskResult = action switch
                                                {
                                                    "checkStatus" => await Windows.Helper.ScreenControl.VirtualDisplayDriver.CheckStatus(),
                                                    "install" => await Windows.Helper.ScreenControl.VirtualDisplayDriver.InstallDriver(),
                                                    "uninstall" => await Windows.Helper.ScreenControl.VirtualDisplayDriver.UninstallDriver(),
                                                    "applyConfig" => await Windows.Helper.ScreenControl.VirtualDisplayDriver.ApplyConfig(
                                                        rootElementCopy.TryGetProperty("configBase64", out var cfg) ? cfg.GetString() ?? "" : ""
                                                    ),
                                                    _ => $"Unknown action: {action}"
                                                };

                                                if (Agent.debug_mode)
                                                    Logging.Debug("Service.Setup_SignalR", "Virtual Display Result", taskResult);
                                            }
                                            catch (Exception ex)
                                            {
                                                taskResult = $"Error: {ex.Message}";
                                                Logging.Error("Service.Setup_SignalR", "Virtual Display background task error", ex.ToString());
                                            }

                                            // Send result back to server using the response queue system
                                            CompleteResponse(responseId, taskResult);
                                            
                                            if (Agent.debug_mode)
                                                Logging.Debug("Service.Setup_SignalR", "Virtual Display response queued", taskResult);
                                        });
                                        
                                        // Setze result auf null, damit der untere Block keine Antwort sendet
                                        // (die Antwort wird im Background-Task gesendet)
                                        result = null;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logging.Error("Service.Setup_SignalR", "Failed to execute virtual display command", ex.ToString());
                                result = $"Error: {ex.Message}";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result = ex.Message;
                        Logging.Error("Service.Setup_SignalR", "Failed to execute file browser command.",
                            ex.ToString());
                    }

                    // Send the response back to the server using the response queue system
                    // (if we execute a single operation that doesnt require a response, we need to set result to null in that operation to prevent this from executing)
                    if (!String.IsNullOrEmpty(result))
                    {
                        if (Agent.debug_mode)
                            Logging.Debug("Client", "Queuing response for server",
                                "result_length: " + result?.Length + " response_id: " + command_object.response_id);
                        
                        CompleteResponse(command_object.response_id, result);
                    }

                    await Task.CompletedTask;
                });

                // Start the connection
                await remote_server_client.StartAsync();

                remote_server_client_setup = true;
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.Setup_SignalR", "Connected to the remote server.", "");
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Error("Service.Setup_SignalR", "Failed to start SignalR.", ex.ToString());
            }
        }
        
        #endregion

        #region User agent process monitoring

        // Cache for known running process PIDs - avoids repeated full process scans
        private int _cachedSystemProcessPid = -1;
        private readonly ConcurrentDictionary<uint, int> _cachedUserProcessPids = new ConcurrentDictionary<uint, int>(); // SessionId -> PID (Windows)
        private readonly ConcurrentDictionary<string, int> _cachedLinuxUserProcessPids = new ConcurrentDictionary<string, int>(); // Username -> PID (Linux)
        private readonly ConcurrentDictionary<string, int> _cachedMacOSUserProcessPids = new ConcurrentDictionary<string, int>(); // Username -> PID (macOS)

        private async Task CheckUserProcessStatus()
        {
            if (OperatingSystem.IsWindows())
            {
                await CheckUserProcessStatus_Windows();
            }
            else if (OperatingSystem.IsLinux())
            {
                //await CheckUserProcessStatus_Linux();
            }
            else if (OperatingSystem.IsMacOS())
            {
                //await CheckUserProcessStatus_MacOS();
            }
        }

        /// <summary>
        /// Windows-specific implementation for user process monitoring.
        /// Starts user agent processes in logged-in user sessions.
        /// </summary>
        private async Task CheckUserProcessStatus_Windows()
        {
            // Prevent multiple simultaneous executions
            if (_isCheckingUserProcesses)
            {
                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckUserProcess.Windows", "Already checking processes, skipping", "");
                return;
            }

            _isCheckingUserProcesses = true;

            try
            {
                // First check if cached processes are still running
                bool systemProcessRunning = IsCachedProcessRunning(_cachedSystemProcessPid);

                // Get active sessions
                var activeSessions = Windows.Helper.ScreenControl.WindowsSession.GetActiveSessions();
                
                // Check cached user processes
                bool allUserProcessesRunning = true;
                foreach (var sessionId in activeSessions)
                {
                    if (sessionId == 0) continue;
                    if (!Windows.Helper.ScreenControl.WindowsSession.IsUserLoggedIntoSession(sessionId)) continue;

                    if (_cachedUserProcessPids.TryGetValue(sessionId, out int cachedPid))
                    {
                        if (!IsCachedProcessRunning(cachedPid))
                        {
                            _cachedUserProcessPids.TryRemove(sessionId, out _);
                            allUserProcessesRunning = false;
                        }
                    }
                    else
                    {
                        allUserProcessesRunning = false;
                    }
                }

                // FAST PATH: If all cached processes are still running, we're done!
                if (systemProcessRunning && allUserProcessesRunning)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckUserProcess.Windows", "All cached processes still running", "Fast path - no enumeration needed");
                    return;
                }

                // SLOW PATH: Need to enumerate processes - but only ONCE for all checks
                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckUserProcess.Windows", "Need process enumeration",
                        $"SystemRunning: {systemProcessRunning}, AllUserRunning: {allUserProcessesRunning}");

                // Get ALL processes once and filter in memory (much faster than multiple GetProcessesByName calls)
                var allProcesses = Process.GetProcesses();
                
                try
                {
                    // Find our target processes by name
                    var systemProcesses = allProcesses.Where(p =>
                    {
                        try { return p.ProcessName == "NetLock_RMM_User_Process"; }
                        catch { return false; }
                    }).ToList();

                    var uacProcesses = allProcesses.Where(p =>
                    {
                        try { return p.ProcessName == "NetLock_RMM_User_Process_UAC"; }
                        catch { return false; }
                    }).ToList();

                    // Check system process
                    if (!systemProcessRunning)
                    {
                        foreach (var process in systemProcesses)
                        {
                            try
                            {
                                if (!process.HasExited)
                                {
                                    systemProcessRunning = true;
                                    _cachedSystemProcessPid = process.Id;

                                    if (Agent.debug_mode)
                                        Logging.Debug("Service.CheckUserProcess.Windows", "Found system process",
                                            $"PID: {process.Id}");
                                    break;
                                }
                            }
                            catch { }
                        }

                        // Start system process if not running
                        if (!systemProcessRunning)
                        {
                            if (Agent.debug_mode)
                                Logging.Debug("Service.CheckUserProcess.Windows", "Starting system process",
                                    $"Path: {Application_Paths.netlock_rmm_user_agent_path}");

                            bool success = Windows.Helper.ScreenControl.Win32Interop.CreateInteractiveSystemProcess(
                                commandLine: Application_Paths.netlock_rmm_user_agent_path,
                                targetSessionId: 0,
                                hiddenWindow: false,
                                out var procInfo
                            );

                            if (success)
                            {
                                _cachedSystemProcessPid = (int)procInfo.dwProcessId;
                                await Task.Delay(500);
                            }
                        }
                    }

                    // Check user processes for each session
                    foreach (var sessionId in activeSessions)
                    {
                        if (sessionId == 0) continue;
                        if (!Windows.Helper.ScreenControl.WindowsSession.IsUserLoggedIntoSession(sessionId)) continue;
                        
                        // Skip if we already have a cached running process for this session
                        if (_cachedUserProcessPids.ContainsKey(sessionId)) continue;

                        bool processIsRunning = false;
                        
                        foreach (var process in uacProcesses)
                        {
                            try
                            {
                                if (!process.HasExited)
                                {
                                    uint processSessionId = Windows.Helper.ScreenControl.WindowsSession.GetProcessSessionId(process.Id);
                                    if (processSessionId == sessionId)
                                    {
                                        processIsRunning = true;
                                        _cachedUserProcessPids[sessionId] = process.Id;
                                        
                                        if (Agent.debug_mode)
                                            Logging.Debug("Service.CheckUserProcess.Windows", "Found user process",
                                                $"PID: {process.Id}, SessionId: {sessionId}");
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }

                        // Start process if not running
                        if (!processIsRunning)
                        {
                            if (Agent.debug_mode)
                                Logging.Debug("Service.CheckUserProcess.Windows", "Starting user process",
                                    $"SessionId: {sessionId}, Path: {Application_Paths.netlock_rmm_user_agent_uac_path}");

                            bool success = Windows.Helper.ScreenControl.Win32Interop.CreateProcessInUserSession(
                                commandLine: Application_Paths.netlock_rmm_user_agent_uac_path,
                                targetSessionId: (int)sessionId,
                                hiddenWindow: false,
                                out var procInfo
                            );

                            if (success)
                            {
                                _cachedUserProcessPids[sessionId] = (int)procInfo.dwProcessId;
                                await Task.Delay(500);
                            }
                        }
                    }
                }
                finally
                {
                    // Dispose all process handles to avoid memory leak
                    foreach (var process in allProcesses)
                    {
                        try { process.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.CheckUserProcess.Windows", 
                    "Exception while checking or starting user processes.",
                    ex.ToString());
            }
            finally
            {
                _isCheckingUserProcesses = false;
            }
        }

        /// <summary>
        /// Linux-specific implementation for user process monitoring.
        /// Starts user agent processes in logged-in user sessions using loginctl and sudo.
        /// </summary>
        private async Task CheckUserProcessStatus_Linux()
        {
            // Prevent multiple simultaneous executions
            if (_isCheckingUserProcesses)
            {
                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckUserProcess.Linux", "Already checking processes, skipping", "");
                return;
            }

            _isCheckingUserProcesses = true;

            try
            {
                // Get active user sessions
                var activeSessions = Linux.Helper.LinuxSession.GetActiveSessions();

                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckUserProcess.Linux", "Found sessions", $"Count: {activeSessions.Count}");

                // Check cached user processes - remove stale entries
                var usersToCheck = new List<Linux.Helper.LinuxSession.UserSession>();
                foreach (var session in activeSessions)
                {
                    if (session.UserId == 0) continue; // Skip root

                    if (_cachedLinuxUserProcessPids.TryGetValue(session.Username, out int cachedPid))
                    {
                        if (!IsCachedProcessRunning(cachedPid))
                        {
                            _cachedLinuxUserProcessPids.TryRemove(session.Username, out _);
                            usersToCheck.Add(session);
                        }
                    }
                    else
                    {
                        usersToCheck.Add(session);
                    }
                }

                // FAST PATH: If all cached processes are still running, we're done!
                if (usersToCheck.Count == 0)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckUserProcess.Linux", "All cached processes still running", "Fast path");
                    return;
                }

                // Check and start processes for users without running agents
                foreach (var session in usersToCheck)
                {
                    Console.WriteLine($"[CheckUserProcess.Linux] Processing user: {session.Username}, SessionType: {session.SessionType}, Display: {session.Display}, WaylandDisplay: {session.WaylandDisplay}");
                    
                    // Check if process is already running for this user (no UAC on Linux)
                    bool processIsRunning = Linux.Helper.LinuxSession.IsProcessRunningForUser("NetLock_RMM_User_Process", session.Username);

                    if (processIsRunning)
                    {
                        int pid = Linux.Helper.LinuxSession.FindProcessByNameAndUser("NetLock_RMM_User_Process", session.Username);
                        if (pid > 0)
                        {
                            _cachedLinuxUserProcessPids[session.Username] = pid;
                            
                            if (Agent.debug_mode)
                                Logging.Debug("Service.CheckUserProcess.Linux", "Found existing user process",
                                    $"User: {session.Username}, PID: {pid}");
                        }
                        continue;
                    }

                    // Start process in user session (no UAC on Linux)
                    Console.WriteLine($"[CheckUserProcess.Linux] Starting user process for {session.Username}...");
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckUserProcess.Linux", "Starting user process",
                            $"User: {session.Username}, UID: {session.UserId}, Path: {Application_Paths.netlock_rmm_user_agent_path}");

                    bool success = Linux.Helper.LinuxSession.CreateProcessInUserSession(
                        commandPath: Application_Paths.netlock_rmm_user_agent_path,
                        username: session.Username,
                        uid: session.UserId,
                        display: session.Display,
                        hiddenWindow: false,
                        out int processId,
                        sessionType: session.SessionType,
                        waylandDisplay: session.WaylandDisplay
                    );
                    
                    Console.WriteLine($"[CheckUserProcess.Linux] CreateProcessInUserSession result: success={success}, PID={processId}");

                    if (success && processId > 0)
                    {
                        _cachedLinuxUserProcessPids[session.Username] = processId;
                        
                        if (Agent.debug_mode)
                            Logging.Debug("Service.CheckUserProcess.Linux", "Process started successfully",
                                $"User: {session.Username}, PID: {processId}");
                        
                        await Task.Delay(500);
                    }
                    else
                    {
                        Console.WriteLine($"[CheckUserProcess.Linux] Failed to start process for {session.Username}");
                        if (Agent.debug_mode)
                            Logging.Debug("Service.CheckUserProcess.Linux", "Failed to start process",
                                $"User: {session.Username}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.CheckUserProcess.Linux",
                    "Exception while checking or starting user processes.",
                    ex.ToString());
            }
            finally
            {
                _isCheckingUserProcesses = false;
            }
        }

        /// <summary>
        /// macOS-specific implementation for user process monitoring.
        /// Starts user agent processes in logged-in user sessions using launchctl asuser.
        /// </summary>
        private async Task CheckUserProcessStatus_MacOS()
        {
            // Prevent multiple simultaneous executions
            if (_isCheckingUserProcesses)
            {
                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckUserProcess.MacOS", "Already checking processes, skipping", "");
                return;
            }

            _isCheckingUserProcesses = true;

            try
            {
                // Get active user sessions
                var activeSessions = MacOS.Helper.MacOSSession.GetActiveSessions();

                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckUserProcess.MacOS", "Found sessions", $"Count: {activeSessions.Count}");

                // Check cached user processes - remove stale entries
                var usersToCheck = new List<MacOS.Helper.MacOSSession.UserSession>();
                foreach (var session in activeSessions)
                {
                    if (session.UserId == 0) continue; // Skip root

                    if (_cachedMacOSUserProcessPids.TryGetValue(session.Username, out int cachedPid))
                    {
                        if (!IsCachedProcessRunning(cachedPid))
                        {
                            _cachedMacOSUserProcessPids.TryRemove(session.Username, out _);
                            usersToCheck.Add(session);
                        }
                    }
                    else
                    {
                        usersToCheck.Add(session);
                    }
                }

                // FAST PATH: If all cached processes are still running, we're done!
                if (usersToCheck.Count == 0)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckUserProcess.MacOS", "All cached processes still running", "Fast path");
                    return;
                }

                // Check and start processes for users without running agents
                foreach (var session in usersToCheck)
                {
                    // Check if process is already running for this user (no UAC on macOS)
                    bool processIsRunning = MacOS.Helper.MacOSSession.IsProcessRunningForUser("NetLock_RMM_User_Process", session.Username);

                    if (processIsRunning)
                    {
                        int pid = MacOS.Helper.MacOSSession.FindProcessByNameAndUser("NetLock_RMM_User_Process", session.Username);
                        if (pid > 0)
                        {
                            _cachedMacOSUserProcessPids[session.Username] = pid;
                            
                            if (Agent.debug_mode)
                                Logging.Debug("Service.CheckUserProcess.MacOS", "Found existing user process",
                                    $"User: {session.Username}, PID: {pid}");
                        }
                        continue;
                    }

                    // Start process in user session (no UAC on macOS)
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckUserProcess.MacOS", "Starting user process",
                            $"User: {session.Username}, UID: {session.UserId}, Path: {Application_Paths.netlock_rmm_user_agent_path}");

                    bool success = MacOS.Helper.MacOSSession.CreateProcessInUserSession(
                        commandPath: Application_Paths.netlock_rmm_user_agent_path,
                        username: session.Username,
                        uid: session.UserId,
                        hiddenWindow: false,
                        out int processId
                    );

                    if (success && processId > 0)
                    {
                        _cachedMacOSUserProcessPids[session.Username] = processId;
                        
                        if (Agent.debug_mode)
                            Logging.Debug("Service.CheckUserProcess.MacOS", "Process started successfully",
                                $"User: {session.Username}, PID: {processId}");
                        
                        await Task.Delay(500);
                    }
                    else
                    {
                        if (Agent.debug_mode)
                            Logging.Debug("Service.CheckUserProcess.MacOS", "Failed to start process",
                                $"User: {session.Username}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.CheckUserProcess.MacOS",
                    "Exception while checking or starting user processes.",
                    ex.ToString());
            }
            finally
            {
                _isCheckingUserProcesses = false;
            }
        }

        /// <summary>
        /// Quickly checks if a cached process is still running by PID.
        /// This is MUCH faster than Process.GetProcessesByName() because it doesn't enumerate all processes.
        /// </summary>
        private bool IsCachedProcessRunning(int pid)
        {
            if (pid <= 0) return false;
            
            try
            {
                var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                // Process with this PID doesn't exist
                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion
        
        #region Tray Icon Process Monitoring (Windows, Linux & MacOS)
        
        /// <summary>
        /// Loads and checks tray icon settings to determine if tray icon should be enabled
        /// </summary>
        private bool IsTrayIconEnabled()
        {
            try
            {
                // Check if config file exists
                if (!File.Exists(Application_Paths.tray_icon_settings_json_path))
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.IsTrayIconEnabled", "Tray icon config not found, defaulting to disabled", 
                            $"Path: {Application_Paths.tray_icon_settings_json_path}");
                    return false; // Default: disabled if no config exists
                }

                // Read encrypted config file
                string encryptedJson = File.ReadAllText(Application_Paths.tray_icon_settings_json_path);
                
                if (string.IsNullOrWhiteSpace(encryptedJson))
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.IsTrayIconEnabled", "Tray icon config is empty, defaulting to disabled", "");
                    return false;
                }

                // Decrypt the config
                string decryptedJson = Global.Encryption.String_Encryption.Decrypt(encryptedJson, Application_Settings.NetLock_Local_Encryption_Key);
                
                if (string.IsNullOrWhiteSpace(decryptedJson))
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.IsTrayIconEnabled", "Failed to decrypt tray icon config, defaulting to disabled", "");
                    return false;
                }

                // Parse JSON
                using var jsonDoc = System.Text.Json.JsonDocument.Parse(decryptedJson);
                var root = jsonDoc.RootElement;
                
                // Check if TrayIcon section exists
                if (!root.TryGetProperty("TrayIcon", out var trayIconElement))
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.IsTrayIconEnabled", "TrayIcon section not found in config, defaulting to disabled", "");
                    return false;
                }
                
                // Check if Enabled property exists in TrayIcon section
                if (!trayIconElement.TryGetProperty("Enabled", out var enabledElement))
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.IsTrayIconEnabled", "TrayIcon.Enabled property not found, defaulting to disabled", "");
                    return false;
                }
                
                // Get enabled status
                bool enabled = enabledElement.GetBoolean();
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.IsTrayIconEnabled", $"Tray icon enabled status: {enabled}", "");
                
                return enabled;
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Error("Service.IsTrayIconEnabled", "Error loading tray icon settings, defaulting to disabled", ex.ToString());
                return false; // Default to disabled on error
            }
        }
        
        /// <summary>
        /// Terminates all running tray icon processes
        /// </summary>
        private void TerminateTrayIconProcesses()
        {
            try
            {
                var trayIconProcesses = Process.GetProcessesByName("NetLock_RMM_Tray_Icon");
                
                foreach (var process in trayIconProcesses)
                {
                    if (process != null && !process.HasExited)
                    {
                        try
                        {
                            if (Agent.debug_mode)
                                Logging.Debug("Service.TerminateTrayIconProcesses", 
                                    "Terminating tray icon process", 
                                    $"PID: {process.Id}");
                            
                            process.Kill();
                            process.WaitForExit(5000); // Wait max 5 seconds for graceful exit
                            
                            if (Agent.debug_mode)
                                Logging.Debug("Service.TerminateTrayIconProcesses", 
                                    "Tray icon process terminated", 
                                    $"PID: {process.Id}");
                        }
                        catch (Exception ex)
                        {
                            if (Agent.debug_mode)
                                Logging.Error("Service.TerminateTrayIconProcesses", 
                                    "Failed to terminate tray icon process", 
                                    $"PID: {process.Id}, Error: {ex.Message}");
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.TerminateTrayIconProcesses", 
                    "Exception while terminating tray icon processes", 
                    ex.ToString());
            }
        }
        
        private async Task CheckTrayIconProcessStatus()
        {
            try
            {
                // Check if tray icon should be enabled
                bool trayIconEnabled = IsTrayIconEnabled();
                
                if (!trayIconEnabled)
                {
                    // Tray icon is disabled - terminate any running instances
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckTrayIconProcess", 
                            "Tray icon is disabled in config, terminating processes", "");
                    
                    TerminateTrayIconProcesses();
                    return;
                }
                
                // Platform-specific implementations
                if (OperatingSystem.IsWindows())
                {
                    await CheckTrayIconProcessStatus_Windows();
                }
                else if (OperatingSystem.IsLinux())
                {
                    //await CheckTrayIconProcessStatus_Linux();
                }
                else if (OperatingSystem.IsMacOS())
                {
                    //await CheckTrayIconProcessStatus_MacOS();
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.CheckTrayIconProcess", "Exception while checking or starting tray icon process.",
                    ex.ToString());
            }
        }
        
        /// <summary>
        /// Windows-specific implementation for tray icon process monitoring.
        /// </summary>
        private async Task CheckTrayIconProcessStatus_Windows()
        {
            // Get all active sessions (including login screen)
            var activeSessions = Windows.Helper.ScreenControl.WindowsSession.GetActiveSessions();

            if (Agent.debug_mode)
                Logging.Debug("Service.CheckTrayIconProcess.Windows", "Active sessions found", $"Count: {activeSessions.Count}");

            // Check and start tray icon for each logged in user session
            foreach (var sessionId in activeSessions)
            {
                // Check if a user is logged in
                bool isUserLoggedIn = Windows.Helper.ScreenControl.WindowsSession.IsUserLoggedIntoSession(sessionId);

                // Only for logged in users (not for login screen/session 0)
                if (!isUserLoggedIn || sessionId == 0)
                    continue;

                // Check if tray icon is already running in this session
                bool trayIconRunning = false;
                var trayIconProcesses = Process.GetProcessesByName("NetLock_RMM_Tray_Icon");

                foreach (var process in trayIconProcesses)
                {
                    if (process != null && !process.HasExited)
                    {
                        uint traySessionId = Windows.Helper.ScreenControl.WindowsSession.GetProcessSessionId(process.Id);

                        if (traySessionId == sessionId)
                        {
                            trayIconRunning = true;

                            if (Agent.debug_mode)
                                Logging.Debug("Service.CheckTrayIconProcess.Windows",
                                    "Tray icon is running.",
                                    $"PID: {process.Id}, SessionId: {sessionId}");
                            break;
                        }
                    }
                }

                if (!trayIconRunning)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckTrayIconProcess.Windows",
                            "Starting tray icon in session",
                            $"SessionId: {sessionId}, Path: {Application_Paths.program_files_tray_icon_path}");

                    bool success = Windows.Helper.ScreenControl.Win32Interop.CreateProcessInUserSession(
                        commandLine: Application_Paths.program_files_tray_icon_path,
                        targetSessionId: (int)sessionId,
                        hiddenWindow: false,
                        out var trayProcInfo
                    );

                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckTrayIconProcess.Windows",
                            "Tray icon start result",
                            $"Success: {success}, SessionId: {sessionId}");

                    if (success)
                        await Task.Delay(500);
                }
            }
        }
        
        /// <summary>
        /// Linux-specific implementation for tray icon process monitoring.
        /// </summary>
        private async Task CheckTrayIconProcessStatus_Linux()
        {
            Console.WriteLine("[CheckTrayIconProcess.Linux] Starting tray icon check...");
            
            // Get active user sessions
            var activeSessions = Linux.Helper.LinuxSession.GetActiveSessions();

            Console.WriteLine($"[CheckTrayIconProcess.Linux] Found {activeSessions.Count} active sessions");
            
            if (Agent.debug_mode)
                Logging.Debug("Service.CheckTrayIconProcess.Linux", "Found sessions", $"Count: {activeSessions.Count}");

            // Check and start tray icon for each user session
            foreach (var session in activeSessions)
            {
                if (session.UserId == 0) continue; // Skip root
                
                Console.WriteLine($"[CheckTrayIconProcess.Linux] Checking session for user: {session.Username}, UID: {session.UserId}");

                // Check if tray icon is already running for this user
                bool trayIconRunning = Linux.Helper.LinuxSession.IsProcessRunningForUser("NetLock_RMM_Tray_Icon", session.Username);
                
                Console.WriteLine($"[CheckTrayIconProcess.Linux] Tray icon running for {session.Username}: {trayIconRunning}");

                if (trayIconRunning)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckTrayIconProcess.Linux",
                            "Tray icon is running.",
                            $"User: {session.Username}");
                    continue;
                }

                // Start tray icon in user session
                Console.WriteLine($"[CheckTrayIconProcess.Linux] Starting tray icon for user: {session.Username}, SessionType: {session.SessionType}, Path: {Application_Paths.program_files_tray_icon_path}");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckTrayIconProcess.Linux",
                        "Starting tray icon in session",
                        $"User: {session.Username}, Path: {Application_Paths.program_files_tray_icon_path}");

                bool success = Linux.Helper.LinuxSession.CreateProcessInUserSession(
                    commandPath: Application_Paths.program_files_tray_icon_path,
                    username: session.Username,
                    uid: session.UserId,
                    display: session.Display,
                    hiddenWindow: false,
                    out int processId,
                    sessionType: session.SessionType,
                    waylandDisplay: session.WaylandDisplay
                );

                Console.WriteLine($"[CheckTrayIconProcess.Linux] Start result: success={success}, PID={processId}");

                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckTrayIconProcess.Linux",
                        "Tray icon start result",
                        $"Success: {success}, User: {session.Username}, PID: {processId}");

                if (success)
                    await Task.Delay(500);
            }
        }
        
        /// <summary>
        /// macOS-specific implementation for tray icon process monitoring.
        /// </summary>
        private async Task CheckTrayIconProcessStatus_MacOS()
        {
            Console.WriteLine("[CheckTrayIconProcess.MacOS] Starting tray icon check...");
            
            // Get active user sessions
            var activeSessions = MacOS.Helper.MacOSSession.GetActiveSessions();

            Console.WriteLine($"[CheckTrayIconProcess.MacOS] Found {activeSessions.Count} active sessions");
            
            if (Agent.debug_mode)
                Logging.Debug("Service.CheckTrayIconProcess.MacOS", "Found sessions", $"Count: {activeSessions.Count}");

            // Check and start tray icon for each user session
            foreach (var session in activeSessions)
            {
                if (session.UserId == 0) continue; // Skip root
                
                Console.WriteLine($"[CheckTrayIconProcess.MacOS] Checking session for user: {session.Username}, UID: {session.UserId}");

                // Check if tray icon is already running for this user
                bool trayIconRunning = MacOS.Helper.MacOSSession.IsProcessRunningForUser("NetLock_RMM_Tray_Icon", session.Username);
                
                Console.WriteLine($"[CheckTrayIconProcess.MacOS] Tray icon running for {session.Username}: {trayIconRunning}");

                if (trayIconRunning)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("Service.CheckTrayIconProcess.MacOS",
                            "Tray icon is running.",
                            $"User: {session.Username}");
                    continue;
                }

                // Start tray icon in user session
                Console.WriteLine($"[CheckTrayIconProcess.MacOS] Starting tray icon for user: {session.Username}, Path: {Application_Paths.program_files_tray_icon_path}");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckTrayIconProcess.MacOS",
                        "Starting tray icon in session",
                        $"User: {session.Username}, Path: {Application_Paths.program_files_tray_icon_path}");

                bool success = MacOS.Helper.MacOSSession.CreateProcessInUserSession(
                    commandPath: Application_Paths.program_files_tray_icon_path,
                    username: session.Username,
                    uid: session.UserId,
                    hiddenWindow: false,
                    out int processId
                );

                Console.WriteLine($"[CheckTrayIconProcess.MacOS] Start result: success={success}, PID={processId}");

                if (Agent.debug_mode)
                    Logging.Debug("Service.CheckTrayIconProcess.MacOS",
                        "Tray icon start result",
                        $"Success: {success}, User: {session.Username}, PID: {processId}");

                if (success)
                    await Task.Delay(500);
            }
        }
        
        #endregion

        #region Remote Agent Local Server

        private const int Remote_Agent_Local_Port = 7338;

        private ConcurrentDictionary<string, TcpClient>
            _clients = new ConcurrentDictionary<string, TcpClient>(); // Clients by username

        private TcpListener _listener;
        private CancellationTokenSource _cancellationTokenSourceLocal = new CancellationTokenSource();
        private SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(100); // Limit to 100 concurrent clients
        private const int BufferSize = 10 * 1024 * 1024; // 10 MB Buffer

        public async Task Local_Server_Start()
        {
            try
            {
                if (Agent.debug_mode)
                    Logging.Debug("Service.Remote_Agent_Local_Server_Start", "Starting server...", "");

                _listener = new TcpListener(IPAddress.Parse("127.0.0.1"), Remote_Agent_Local_Port);
                _listener.Start();
                Logging.Debug("Service.Remote_Agent_Local_Server_Start", "Server started. Waiting for connections...",
                    "");

                while (!_cancellationTokenSourceLocal.Token.IsCancellationRequested)
                {
                    await _connectionSemaphore.WaitAsync(); // Throttle connections
                    var client = await _listener.AcceptTcpClientAsync();
                    if (client != null)
                    {
                        _ = Local_Server_Handle_Client(client,
                            _cancellationTokenSourceLocal.Token); // Handle client asynchronously
                    }
                }
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Error("Service.Remote_Agent_Local_Server_Start", "Error starting server.", ex.ToString());
            }
        }

        private async Task Local_Server_Handle_Client(TcpClient client, CancellationToken cancellationToken)
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[BufferSize];

            try
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive,
                    true); // Enable KeepAlive

                // Wait for the client to send the username first
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                string initialMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                string[] messageParts = initialMessage.Split('$');

                if (messageParts[0] == "username")
                {
                    string username = messageParts[1];
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Remote_Agent_Local_Server_Handle_Client", "Client connected", username);
                    
                    // Add the client to the dictionary
                    _clients[username] = client;

                    // Handle messages from the client
                    await HandleClientMessages(username, client, stream, cancellationToken);
                }
            }
            catch (IOException ex)
            {
            }
            catch (Exception ex)
            {
                Logging.Error("Service.Remote_Agent_Local_Server_Handle_Client", "Error handling client.",
                    ex.ToString());
            }
            finally
            {
                stream.Close();
                client.Close();
                _clients.TryRemove(client.Client.RemoteEndPoint.ToString(), out _); // Remove from dictionary
                _connectionSemaphore.Release(); // Release the connection slot
            }
        }

        private async Task HandleClientMessages(string username, TcpClient client, NetworkStream stream,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[BufferSize];
        
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0) // Client disconnected
                        break;
        
                    // Check if message starts with "screen_capture$" - if so, treat as binary
                    string header = Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 100));
        
                    if (header.StartsWith("screen_capture$"))
                    {
                        // Binary message: screen_capture$response_id$[binary data]
                        int firstDollar = header.IndexOf('$');
                        int secondDollar = header.IndexOf('$', firstDollar + 1);
        
                        if (secondDollar > firstDollar)
                        {
                            string responseId = header.Substring(firstDollar + 1, secondDollar - firstDollar - 1);
        
                            // Calculate where binary data starts
                            string headerPart = $"screen_capture${responseId}$";
                            int headerBytes = Encoding.UTF8.GetByteCount(headerPart);
        
                            // Only log if debug mode is enabled
                            if (Agent.debug_mode)
                            {
                                Logging.DebugLazy("HandleClientMessages", "Binary message detected",
                                    () => $"ResponseID: {responseId}, HeaderBytes: {headerBytes}");
                            }
        
                            // Collect binary data from initial buffer
                            List<byte> binaryData = new List<byte>();
                            if (bytesRead > headerBytes)
                            {
                                binaryData.AddRange(buffer.Skip(headerBytes).Take(bytesRead - headerBytes));
                            }
        
                            // Read remaining data (if any)
                            while (true)
                            {
                                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                                if (bytesRead == 0)
                                    break;
        
                                binaryData.AddRange(buffer.Take(bytesRead));
        
                                if (bytesRead < buffer.Length)
                                    break;
                            }
        
                            // Validate JPEG header
                            bool isValidJpeg = binaryData.Count >= 2 && binaryData[0] == 0xFF && binaryData[1] == 0xD8;
        
                            // Only log in debug mode - this is called VERY frequently
                            if (Agent.debug_mode)
                            {
                                Logging.DebugLazy("HandleClientMessages", "Binary data collected",
                                    () => $"Size: {binaryData.Count} bytes, Valid JPEG: {isValidJpeg}");
                                
                                if (binaryData.Count >= 20)
                                {
                                    Logging.DebugLazy("HandleClientMessages", "First 20 bytes", 
                                        () => string.Join(" ", binaryData.Take(20).Select(b => $"0x{b:X2}")));
                                }
                            }
        
                            if (!isValidJpeg && Agent.debug_mode)
                            {
                                Logging.ErrorLazy("HandleClientMessages", "Invalid JPEG data received!",
                                    () => $"First bytes: 0x{binaryData[0]:X2} 0x{binaryData[1]:X2}, Username: {username}");
                            }
        
                            // Send binary data directly to server
                            try
                            {
                                await Remote_Control_Send_Screen(responseId, binaryData.ToArray());
                            }
                            catch (Exception ex)
                            {
if (Agent.debug_mode)
    Logging.ErrorLazy("HandleClientMessages", "Failed to send binary data to server", () => ex.ToString());

                            }
                        }
                    }
                    else
                    {
                        // Text message - handle normally
                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        
                        if (Agent.debug_mode)
                        {
                            Logging.Debug("HandleClientMessages", $"Message from user {username}",
                                "not displaying due to high data usage");
                        }
        
                        try
                        {
                            if (Agent.debug_mode)
                            {
                                if (Agent.debug_mode)
                                    Logging.Remote_Control("HandleClientMessages", "Processing message", "Task triggered");
                            }
                            await ProcessMessage(message);
                        }
                        catch (Exception ex)
                        {
                            if (Agent.debug_mode)
                                Logging.ErrorLazy("ProcessMessage", "Error invoking client response.", () => ex.ToString());
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                if (Agent.debug_mode)
                {
                    Logging.ErrorLazy("HandleClientMessages", "Client disconnected due to IOException",
                        () => ex.ToString());
                }
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.ErrorLazy("HandleClientMessages", "Error handling messages from client.", () => ex.ToString());
            }
            finally
            {
                stream.Close();
                client.Close();
                _clients.TryRemove(username, out _);
                
                if (Agent.debug_mode)
                {
                    if (Agent.debug_mode)
                        Logging.Debug("HandleClientMessages", "Client disconnected", username);
                }
            }
        }

        private async Task ProcessMessage(string message)
        {
            try
            {
                Logging.Remote_Control("Service.Remote_Agent_Server_ProcessMessage", "Processing message",
                    "Task triggered");

                // Split message per $
                string[] messageParts = message.Split('$');

                // Handle specific commands, e.g., template code
                if (messageParts[0] == "screen_capture")
                {
                    // Respond with device identity
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Remote_Agent_Server_ProcessMessage", "Message Received", messageParts[1]);
                    
                    try
                    {
                        //await remote_server_client.InvokeAsync("ReceiveClientResponse", messageParts[1], messageParts[2]);

                        await Remote_Control_Send_Screen(messageParts[1], messageParts[2]);
                    }
                    catch (Exception ex)
                    {
                        Logging.Error("Service.Remote_Agent_Server_ProcessMessage", "Error invoking client response.",
                            ex.ToString());
                    }
                }
                else if (messageParts[0] == "screen_indexes")
                {
                    await remote_server_client.SendAsync("ReceiveClientResponse", messageParts[1], messageParts[2], false);
                }
                else if (messageParts[0] == "users")
                {
                    await remote_server_client.SendAsync("ReceiveClientResponse", messageParts[1], messageParts[2], false);
                }
                else if (messageParts[0] == "clipboard_content")
                {
                    await remote_server_client.SendAsync("ReceiveClientResponse", messageParts[1], messageParts[2], false);
                }
                else if (messageParts[0] == "chat_message")
                {
                    await remote_server_client.SendAsync("ReceiveClientResponse", messageParts[1], messageParts[2], true);
                }
                else if (messageParts[0] == "remote_access_response")
                {

                    if (messageParts[2] == "accepted")
                    {
                        _remoteScreenControlGrantedUsers.Add(messageParts[3]);
                    }
                    else
                    {
                        if (!String.IsNullOrEmpty(messageParts[3]))
                            if (_remoteScreenControlGrantedUsers.Contains(messageParts[3]))
                                _remoteScreenControlGrantedUsers.Remove(messageParts[3]);
                    }

                    await remote_server_client.SendAsync("ReceiveClientResponse", messageParts[1], messageParts[2], false);
                }
                else if (messageParts[0] == "elevation_result")
                {
                    // Format: elevation_result${response_id}$success%{true/false}$message%{message}$new_pid%{pid}
                    string responseId = messageParts.Length > 1 ? messageParts[1] : "";
                    
                    // Parse key-value pairs from remaining parts
                    bool success = false;
                    string resultMessage = "";
                    string newPid = "";
                    
                    for (int i = 2; i < messageParts.Length; i++)
                    {
                        string part = messageParts[i];
                        if (part.StartsWith("success%"))
                        {
                            success = part.Substring(8).ToLower() == "true";
                        }
                        else if (part.StartsWith("message%"))
                        {
                            resultMessage = part.Substring(8);
                        }
                    }
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Service.ProcessMessage", "Elevation result received",
                            $"response_id: {responseId}, success: {success}, message: {resultMessage}, new_pid: {newPid}");
                    
                    // Create JSON result to send back to server
                    var resultObject = new
                    {
                        success = success,
                        message = resultMessage,
                        new_pid = newPid
                    };
                    
                    string jsonResult = JsonSerializer.Serialize(resultObject);
                    
                    // Send response to remote server
                    await remote_server_client.SendAsync("ReceiveClientResponse", responseId, jsonResult, false);
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Service.ProcessMessage", "Elevation result sent to server",
                            $"response_id: {responseId}");
                }
                else
                {
                    Logging.Debug("Service.Remote_Agent_Server_ProcessMessage", "Unknown message type",
                        messageParts[0]);
                }
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Error("Service.Remote_Agent_Server_ProcessMessage", "Error processing message.", ex.ToString());
            }
        }

        public async Task SendToClient(string username, string message)
        {
            try
            {
                if (Agent.debug_mode)
                    Logging.Debug("Service.Remote_Agent_Server_SendToClient", "Sending message to client", message);

                if (_clients.TryGetValue(username, out TcpClient client) && client.Connected)
                {
                    NetworkStream stream = client.GetStream();
                    // Append a newline character to the message to signify the end of the message
                    string messageWithDelimiter = message + "\n"; // Use "\n" or any other delimiter
                    byte[] messageBytes = Encoding.UTF8.GetBytes(messageWithDelimiter);
                    await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                    await stream.FlushAsync();
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Service.Remote_Agent_Server_SendToClient", "Sent message to client", message);
                }
                else
                {
                    Logging.Error("Service.Remote_Agent_Server_SendToClient", "No client connected with username",
                        username);
                }
            }
            catch (Exception ex)
            {
                Logging.Error("Service.Remote_Agent_Server_SendToClient", "Failed to send message to client",
                    ex.ToString());
            }
        }

        private async Task Remote_Control_Send_Screen(string response_id, string message)
        {
            try
            {
                // Deserialise device identity
                var jsonDocument = JsonDocument.Parse(device_identity_json);
                var deviceIdentityElement = jsonDocument.RootElement.GetProperty("device_identity");

                Device_Identity device_identity_object = JsonSerializer.Deserialize<Device_Identity>(deviceIdentityElement.ToString());

                if (Agent.debug_mode)
                    Logging.Debug("device_identity_object", "", device_identity_object.package_guid);
                
                // Create the new full JSON object
                var fullJson = new
                {
                    device_identity = device_identity_object, // Original deserialized device identity
                    remote_control = new // Manually added section
                    {
                        response_id = response_id,
                        result = message
                    }
                };

                // Serialize the full JSON back into a string
                string outputJson =
                    JsonSerializer.Serialize(fullJson, new JsonSerializerOptions { WriteIndented = true });
                
                if (Agent.debug_mode)
                    Logging.Debug("Remote_Control_Send_Screen", "outputJson", outputJson);

                // Create a HttpClient instance
                using (var httpClient = new HttpClient())
                {
                    // Set the content type header
                    httpClient.DefaultRequestHeaders.Accept.Add(
                        new MediaTypeWithQualityHeaderValue("application/json"));
                    httpClient.DefaultRequestHeaders.Add("Package-Guid", device_identity_object.package_guid);

                    Logging.Debug("Remote_Control_Send_Screen", "communication_server",
                        Global.Configuration.Agent.http_https + remote_server_url_command + "/Agent/Windows/Remote/Command");

                    // Send the JSON data to the server
                    var response = await httpClient.PostAsync(
                        Global.Configuration.Agent.http_https + remote_server_url_command + "/Agent/Windows/Remote/Command",
                        new StringContent(outputJson, Encoding.UTF8, "application/json"));

                    // Check if the request was successful
                    if (response.IsSuccessStatusCode)
                    {
                        // Request was successful, handle the response
                        var result = await response.Content.ReadAsStringAsync();
                        
                        if (Agent.debug_mode)
                            Logging.Debug("Remote_Control_Send_Screen", "result", result);
                    }
                    else
                    {
                        // Request failed, handle the error
                        Logging.Debug("Remote_Control_Send_Screen", "request",
                            "Request failed: " + response.StatusCode + " " + response.Content.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Error("Remote_Control_Send_Screen", "failed", ex.ToString());
            }
        }

        private async Task Remote_Control_Send_Screen(string response_id, byte[] binaryData)
        {
            try
            {
                // Validate JPEG before sending
                bool isValidJpeg = binaryData.Length >= 2 && binaryData[0] == 0xFF && binaryData[1] == 0xD8;
        
                if (Agent.debug_mode)
                    Logging.Debug("Remote_Control_Send_Screen", "Preparing to send binary data",
                        $"Size: {binaryData.Length} bytes, Valid JPEG: {isValidJpeg}");
        
                if (binaryData.Length >= 20)
                {
                    string firstBytes = string.Join(" ", binaryData.Take(20).Select(b => $"0x{b:X2}"));
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Remote_Control_Send_Screen", "First 20 bytes before sending", firstBytes);
                }
        
                var jsonDocument = JsonDocument.Parse(device_identity_json);
                var deviceIdentityElement = jsonDocument.RootElement.GetProperty("device_identity");
                Device_Identity device_identity_object = JsonSerializer.Deserialize<Device_Identity>(deviceIdentityElement.ToString());
        
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("Package-Guid", device_identity_object.package_guid);
        
                    var multipartContent = new MultipartFormDataContent();
        
                    var fullJson = new { device_identity = device_identity_object };
                    var deviceIdentityJson = JsonSerializer.Serialize(fullJson);
                    multipartContent.Add(new StringContent(deviceIdentityJson, Encoding.UTF8, "application/json"), "device_identity");
                    multipartContent.Add(new StringContent(response_id), "response_id");
                    multipartContent.Add(new ByteArrayContent(binaryData), "screenshot", "screenshot.jpg");
        
                    if (Agent.debug_mode)
                        Logging.Debug("Remote_Control_Send_Screen", "Sending HTTP POST", $"{binaryData.Length} bytes");
        
                    var response = await httpClient.PostAsync(
                        Global.Configuration.Agent.http_https + remote_server_url_command + "/Agent/Windows/Remote/Command",
                        multipartContent);
        
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsStringAsync();
                        
                        if (Agent.debug_mode)
                            Logging.Debug("Remote_Control_Send_Screen", "Upload successful", result);
                    }
                    else
                    {
                        Logging.Error("Remote_Control_Send_Screen", "Upload failed",
                            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Error("Remote_Control_Send_Screen", "Binary upload failed", ex.ToString());
            }
        }

        public void Stop()
        {
            _cancellationTokenSourceLocal.Cancel();
            _listener.Stop();
            
            if (Agent.debug_mode)
                Logging.Debug("Service.Remote_Agent_Server_Stop", "Server stopped.", "");
        }

        #endregion

        #region ServerConfig
    
        private async Task LoadServerConfig()
        {
            try
            {
                string agentSettingsJson = await File.ReadAllTextAsync(Application_Paths.agent_settings_json_path);

                agentSettingsJson = String_Encryption.Decrypt(agentSettingsJson, Application_Settings.NetLock_Local_Encryption_Key);

                // Deserialize the JSON string into AgentSettings object
                var agentSettings = JsonSerializer.Deserialize<AgentSettings>(agentSettingsJson);

                if (agentSettings != null)
                {
                    _agentSettingsRemoteServiceEnabled = agentSettings.RemoteServiceEnabled;
                    _agentSettingsRemoteShellEnabled = agentSettings.RemoteShellEnabled;
                    _agentSettingsRemoteFileBrowserEnabled = agentSettings.RemoteFileBrowserEnabled;
                    _agentSettingsRemoteTaskManagerEnabled = agentSettings.RemoteTaskManagerEnabled;
                    _agentSettingsRemoteServiceManagerEnabled = agentSettings.RemoteServiceManagerEnabled;
                    _agentSettingsRemoteScreenControlEnabled = agentSettings.RemoteScreenControlEnabled;
                    _agentSettingsRemoteScreenControlUnattendedAccess = agentSettings.RemoteScreenControlUnattendedAccess;

                    Logging.Debug("Service.LoadServerConfig", "Agent settings loaded",
                        $"RemoteServiceEnabled: {_agentSettingsRemoteServiceEnabled}, RemoteShellEnabled: {_agentSettingsRemoteShellEnabled}, RemoteFileBrowserEnabled: {_agentSettingsRemoteFileBrowserEnabled}, RemoteTaskManagerEnabled: {_agentSettingsRemoteTaskManagerEnabled}, RemoteServiceManagerEnabled: {_agentSettingsRemoteServiceManagerEnabled}, RemoteScreenControlEnabled: {_agentSettingsRemoteScreenControlEnabled}");
                }

                // Get access key & authorized state
                access_key = Global.Initialization.Server_Config.Access_Key();
                authorized = Global.Initialization.Server_Config.Authorized();

                // Read device_identity.json file & decrypt
                string jsonString = await File.ReadAllTextAsync(Application_Paths.device_identity_json_path);
                device_identity_json = String_Encryption.Decrypt(jsonString, Application_Settings.NetLock_Local_Encryption_Key);

                Console.Write("device_identity_json " + device_identity_json);
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.LoadServerConfig", "Device identity loaded", device_identity_json);
                
                // Check servers | We do not want to spam the server with requests here.
                if (authorized && !remote_server_status || !file_server_status)
                    await Global.Initialization.Check_Connection.Check_Servers();
            }
            catch (Exception ex)
            {
                if (Agent.debug_mode)
                    Logging.Error("Service.LoadServerConfig", "Error loading server configuration", ex.ToString());
            }
        }

        #endregion
        
        #region Relay Connection Methods
        
        /// <summary>
        /// Connects to relay server and authenticates the device
        /// </summary>
        private async Task ConnectToRelayServer(RelayConnectionDetails relayDetails)
        {
            TcpClient relayClient = null;
            TcpClient localClient = null;
            Stream relayStream = null; // Changed from NetworkStream to Stream to support SslStream
            NetworkStream localStream = null;
            
            string relayServer = string.Empty;
            int relayPort = 7081; // Default Relay Port
            
            var cts = new CancellationTokenSource();
            _activeRelaySessions.TryAdd(relayDetails.session_id, cts);
            
            // E2EE RSA variables
            System.Security.Cryptography.RSA agentRsa = null;
            string agentPublicKeyPem = null;
            System.Security.Cryptography.RSA adminRsa = null; // Admin Public Key (must live for session!)
            
            try
            {
                Console.WriteLine($"[RELAY] ========== Starting Relay Connection ==========");
                Console.WriteLine($"[RELAY] Session ID: {relayDetails.session_id}");
                Console.WriteLine($"[RELAY] Local Port: {relayDetails.local_port}");
                Console.WriteLine($"[RELAY] Protocol: {relayDetails.protocol}");
                Console.WriteLine($"[RELAY] Remote Server URL: {remote_server_url_command}");
                Console.WriteLine($"[RELAY] Relay Server Info: {relayServer}");
                Console.WriteLine($"[RELAY] Has Server Public Key: {!string.IsNullOrEmpty(relayDetails.public_key)}");
                Console.WriteLine($"[RELAY] Server Fingerprint: {relayDetails.fingerprint}");
                
                // Extract relay server and port
                var match = Regex.Match(relay_server, @"^(.*):(\d+)$");
                if (match.Success)
                {
                    relayServer = match.Groups[1].Value;
                    relayPort = int.Parse(match.Groups[2].Value);
                }
                else // broken
                {
                    Logging.Error("Service.ConnectToRelayServer", "Invalid relay server format", relay_server);
                    return;
                }

                Console.WriteLine($"[RELAY] Extracted Server: {relayServer}");
                Console.WriteLine($"[RELAY] Using Port: {relayPort}");
                
                // Server Identity Verification (TOFU - Trust On First Use)
                if (!string.IsNullOrEmpty(relayDetails.public_key) && !string.IsNullOrEmpty(relayDetails.fingerprint))
                {
                    Console.WriteLine($"[RELAY] Verifying server identity...");
                    Console.WriteLine($"[RELAY] Server Fingerprint: {relayDetails.fingerprint}");

                    // TOFU: Persistent Fingerprint Verification (MITM Protection!)
                    string serverIdentifier = $"{relayServer}:{relayPort}";

                    if (!Relay.ServerTrustStore.VerifyServerFingerprint(serverIdentifier, relayDetails.public_key, relayDetails.fingerprint))
                    {
                        Console.WriteLine($"[RELAY] SECURITY: Server fingerprint verification FAILED!");
                        Console.WriteLine($"[RELAY] POSSIBLE MITM ATTACK DETECTED!");
                        Console.WriteLine($"[RELAY] Connection to {serverIdentifier} REJECTED for security reasons.");

                        if (Agent.debug_mode)
                            Logging.Debug("Service.ConnectToRelayServer", "MITM detected", 
                                $"Server {serverIdentifier} fingerprint mismatch - connection rejected");

                        throw new SecurityException($"Server fingerprint verification failed for {serverIdentifier}. Possible MITM attack!");
                    }

                    Console.WriteLine($"[RELAY] Server fingerprint verified (TOFU protection active)");
                }
                else
                {
                    Console.WriteLine($"[RELAY] No server public key - cannot verify server identity (MITM risk!)");
                    Logging.Error("Service.ConnectToRelayServer", "No public key", 
                        "Server did not provide public key for verification");
                }
                
                // Generate or reuse Agent RSA keypair for E2EE with Admin (SESSION-BASED!)
                if (!_sessionKeypairs.TryGetValue(relayDetails.session_id, out var keypair))
                {
                    // First connection for this session - generate new keypair
                    Console.WriteLine($"[RELAY] Generating Agent RSA-4096 keypair for E2EE (NEW for session)...");
                    agentRsa = System.Security.Cryptography.RSA.Create(4096);
                    agentPublicKeyPem = agentRsa.ExportRSAPublicKeyPem();

                    // Store keypair for this session
                    _sessionKeypairs.TryAdd(relayDetails.session_id, (agentRsa, agentPublicKeyPem));

                    Console.WriteLine($"[RELAY] Agent RSA keypair generated and stored for session");
                    Console.WriteLine($"[RELAY] Agent Public Key length: {agentPublicKeyPem.Length} chars");
                    Console.WriteLine($"[RELAY] Agent Public Key (first 100 chars): {agentPublicKeyPem.Substring(0, Math.Min(100, agentPublicKeyPem.Length))}...");
                }
                else
                {
                    // Reuse stored keypair
                    agentRsa = keypair.rsa;
                    agentPublicKeyPem = keypair.publicKeyPem;
                    Console.WriteLine($"[RELAY] Reusing stored Agent RSA keypair for this session");
                    Console.WriteLine($"[RELAY] Agent Public Key length: {agentPublicKeyPem.Length} chars");
                    Console.WriteLine($"[RELAY] Agent Public Key (first 100 chars): {agentPublicKeyPem.Substring(0, Math.Min(100, agentPublicKeyPem.Length))}...");
                }
                
                Console.WriteLine($"[RELAY] Connecting to: {relayServer}:{relayPort}");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Connecting to relay server", 
                        $"server: {relayServer}, session: {relayDetails.session_id}");
                
                // TCP Client to relay server
                Console.WriteLine($"[RELAY] Creating TcpClient...");
                relayClient = new TcpClient();
                
                // TCP optimizations for E2EE + RDP
                relayClient.NoDelay = true; // Disable Nagle's algorithm for low-latency
                relayClient.ReceiveBufferSize = 131072; // 128KB
                relayClient.SendBufferSize = 131072; // 128KB
                
                Console.WriteLine($"[RELAY] Attempting TCP connection...");
                await relayClient.ConnectAsync(relayServer, relayPort);
                
                // Enable TCP KeepAlive
                relayClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                
                Console.WriteLine($"[RELAY] TCP Connection established!");
                
                // Establish TLS connection if SSL is enabled
                if (Global.Configuration.Agent.ssl)
                {
                    var sslStream = new SslStream(
                        relayClient.GetStream(),
                        false,
                        (sender, certificate, chain, errors) =>
                        {
                            // Validate server certificate
                            if (errors == SslPolicyErrors.None)
                            {
                                Console.WriteLine($"[RELAY TLS] Server certificate valid");
                                return true;
                            }
                            
                            // Do not allow self-signed certificates (common in internal networks)
                            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                            {
                                Console.WriteLine($"[RELAY TLS] Certificate chain error - rejecting. Probably self signed.");
                                return false;
                            }
                            
                            // Check for certificate name mismatch (IP vs hostname)
                            if (errors == SslPolicyErrors.RemoteCertificateNameMismatch)
                            {
                                Console.WriteLine($"[RELAY TLS] Certificate name mismatch (common with IP addresses) - accepting");
                                return true;
                            }
                            
                            Console.WriteLine($"[RELAY TLS] Certificate validation failed: {errors}");
                            Logging.Error("Service.ConnectToRelayServer", "TLS certificate validation failed", 
                                $"Errors: {errors}, Server: {relayServer}");
                            return false;
                        },
                        null
                    );
                    
                    try
                    {
                        Console.WriteLine($"[RELAY TLS] Establishing TLS connection...");
                        await sslStream.AuthenticateAsClientAsync(relayServer, null, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, true);
                        
                        Console.WriteLine($"[RELAY TLS] Connection established!");
                        Console.WriteLine($"[RELAY TLS] Protocol: {sslStream.SslProtocol}");
                        Console.WriteLine($"[RELAY TLS] Cipher: {sslStream.CipherAlgorithm} ({sslStream.CipherStrength} bits)");
                        Console.WriteLine($"[RELAY TLS] Hash: {sslStream.HashAlgorithm} ({sslStream.HashStrength} bits)");
                        Console.WriteLine($"[RELAY TLS] Key Exchange: {sslStream.KeyExchangeAlgorithm} ({sslStream.KeyExchangeStrength} bits)");
                        
                        if (Agent.debug_mode)
                            Logging.Debug("Service.ConnectToRelayServer", "TLS established", 
                                $"Protocol: {sslStream.SslProtocol}, Cipher: {sslStream.CipherAlgorithm}");
                        
                        relayStream = sslStream;
                        Console.WriteLine($"[RELAY] Secure stream obtained (TLS)");
                    }
                    catch (Exception tlsEx)
                    {
                        Console.WriteLine($"[RELAY TLS] Failed to establish TLS: {tlsEx.Message}");
                        Logging.Error("Service.ConnectToRelayServer", "TLS handshake failed", tlsEx.ToString());
                        
                        sslStream?.Dispose();
                        throw;
                    }
                }
                else
                {
                    // Use plain TCP without TLS
                    relayStream = relayClient.GetStream();
                    Console.WriteLine($"[RELAY] Using plain TCP connection (SSL disabled)");
                    
                    if (Agent.debug_mode)
                        Logging.Debug("Service.ConnectToRelayServer", "Connected to relay server without TLS", 
                            $"session: {relayDetails.session_id}");
                }
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Connected to relay server", 
                        $"session: {relayDetails.session_id}, SSL: {Global.Configuration.Agent.ssl}");
                
                // Parse json: {"device_identity": {...}}
                Console.WriteLine($"[RELAY] Parsing device identity JSON...");
                Console.WriteLine($"[RELAY] Device Identity JSON length: {device_identity_json?.Length ?? 0} bytes");
                
                var jsonDocument = JsonDocument.Parse(device_identity_json);
                var deviceIdentityElement = jsonDocument.RootElement.GetProperty("device_identity");
                var deviceIdentity = JsonSerializer.Deserialize<Device_Identity>(deviceIdentityElement.ToString());
                
                Console.WriteLine($"[RELAY] Device identity parsed successfully");
                Console.WriteLine($"[RELAY] Access Key: {deviceIdentity.access_key}");
                Console.WriteLine($"[RELAY] Device Name: {deviceIdentity.device_name}");
                Console.WriteLine($"[RELAY] HWID: {deviceIdentity.hwid}");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Device identity parsed", 
                        $"access_key: {deviceIdentity.access_key}, hwid: {deviceIdentity.hwid}");
                
                Console.WriteLine($"[RELAY] Preparing authentication data...");
                
                // Build auth data with agent public key for E2EE key exchange
                object authData;
                
                if (!string.IsNullOrEmpty(agentPublicKeyPem))
                {
                    Console.WriteLine($"[RELAY] Including Agent Public Key for E2EE");
                    authData = new
                    {
                        session_id = relayDetails.session_id,
                        device_identity = new
                        {
                            device_name = deviceIdentity.device_name,
                            access_key = deviceIdentity.access_key,
                            hwid = deviceIdentity.hwid,
                            tenant_guid = deviceIdentity.tenant_guid,
                            location_guid = deviceIdentity.location_guid,
                            platform = deviceIdentity.platform,
                            agent_version = deviceIdentity.agent_version,
                            package_guid = deviceIdentity.package_guid,
                            ip_address_internal = deviceIdentity.ip_address_internal,
                            operating_system = deviceIdentity.operating_system,
                            domain = deviceIdentity.domain,
                            antivirus_solution = deviceIdentity.antivirus_solution,
                            firewall_status = deviceIdentity.firewall_status,
                            architecture = deviceIdentity.architecture,
                            last_boot = deviceIdentity.last_boot,
                            timezone = deviceIdentity.timezone,
                            cpu = deviceIdentity.cpu,
                            cpu_usage = deviceIdentity.cpu_usage,
                            mainboard = deviceIdentity.mainboard,
                            gpu = deviceIdentity.gpu,
                            ram = deviceIdentity.ram,
                            ram_usage = deviceIdentity.ram_usage,
                            tpm = deviceIdentity.tpm,
                            environment_variables = deviceIdentity.environment_variables,
                            last_active_user = deviceIdentity.last_active_user
                        },
                        agent_public_key = agentPublicKeyPem
                    };
                }
                else
                {
                    Console.WriteLine($"[RELAY] Auth without E2EE (no agent keypair)");
                    authData = new
                    {
                        session_id = relayDetails.session_id,
                        device_identity = new
                        {
                            device_name = deviceIdentity.device_name,
                            access_key = deviceIdentity.access_key,
                            hwid = deviceIdentity.hwid,
                            tenant_guid = deviceIdentity.tenant_guid,
                            location_guid = deviceIdentity.location_guid,
                            platform = deviceIdentity.platform,
                            agent_version = deviceIdentity.agent_version,
                            package_guid = deviceIdentity.package_guid,
                            ip_address_internal = deviceIdentity.ip_address_internal,
                            operating_system = deviceIdentity.operating_system,
                            domain = deviceIdentity.domain,
                            antivirus_solution = deviceIdentity.antivirus_solution,
                            firewall_status = deviceIdentity.firewall_status,
                            architecture = deviceIdentity.architecture,
                            last_boot = deviceIdentity.last_boot,
                            timezone = deviceIdentity.timezone,
                            cpu = deviceIdentity.cpu,
                            cpu_usage = deviceIdentity.cpu_usage,
                            mainboard = deviceIdentity.mainboard,
                            gpu = deviceIdentity.gpu,
                            ram = deviceIdentity.ram,
                            ram_usage = deviceIdentity.ram_usage,
                            tpm = deviceIdentity.tpm,
                            environment_variables = deviceIdentity.environment_variables,
                            last_active_user = deviceIdentity.last_active_user
                        }
                    };
                }
                
                string authJson = JsonSerializer.Serialize(authData);
                Console.WriteLine($"[RELAY] Auth JSON size: {authJson.Length} bytes");

                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Auth JSON prepared with agent public key", 
                        $"json_size: {authJson.Length}, has_agent_pubkey: {!string.IsNullOrEmpty(agentPublicKeyPem)}");
                
                Console.WriteLine($"[RELAY] Sending authentication to server...");
                byte[] authBytes = Encoding.UTF8.GetBytes(authJson);
                await relayStream.WriteAsync(authBytes, 0, authBytes.Length, cts.Token);
                await relayStream.FlushAsync(cts.Token);
                
                Console.WriteLine($"[RELAY] Authentication sent ({authBytes.Length} bytes)");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Authentication sent", $"session: {relayDetails.session_id}");
                
                // Wait for auth response
                Console.WriteLine($"[RELAY] Waiting for authentication response...");
                byte[] responseBuffer = new byte[8192]; // Larger for public key
                int bytesRead = await relayStream.ReadAsync(responseBuffer, 0, responseBuffer.Length, cts.Token);
                string response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);
                
                Console.WriteLine($"[RELAY] Received response ({bytesRead} bytes)");
                Console.WriteLine($"[RELAY] Response content: {response}");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Auth response received", 
                        $"bytes: {bytesRead}, content: {response}");
                
                RelayAuthResponse authResponse = null;
                try
                {
                    authResponse = JsonSerializer.Deserialize<RelayAuthResponse>(response);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[RELAY] ERROR - Failed to parse auth response as JSON: {ex.Message}");
                    Console.WriteLine($"[RELAY] Raw response: {response}");
                    Logging.Error("Service.ConnectToRelayServer", "Invalid JSON in auth response", 
                        $"session: {relayDetails.session_id}, response: {response}, error: {ex.Message}");
                    return;
                }
                
                if (authResponse == null)
                {
                    Console.WriteLine($"[RELAY] ERROR - Auth response deserialized to null");
                    Logging.Error("Service.ConnectToRelayServer", "Auth response is null", 
                        $"session: {relayDetails.session_id}");
                    return;
                }
                
                if (!authResponse.success)
                {
                    Console.WriteLine($"[RELAY] Authentication failed: {authResponse.message}");
                    Console.WriteLine($"[RELAY] This indicates the Relay Server rejected our authentication");
                    Console.WriteLine($"[RELAY] Possible causes:");
                    Console.WriteLine($"[RELAY]   1. Invalid Access Key or HWID");
                    Console.WriteLine($"[RELAY]   2. Device not found in database");
                    Console.WriteLine($"[RELAY]   3. Session ID mismatch");
                    Console.WriteLine($"[RELAY]   4. Invalid JSON format in auth request");
                    Console.WriteLine($"[RELAY]   5. Server-side authentication error");
                    
                    Logging.Error("Service.ConnectToRelayServer", "Authentication failed", 
                        $"session: {relayDetails.session_id}, message: {authResponse.message}");
                    return;
                }
                
                Console.WriteLine($"[RELAY] Authentication successful!");
                
                // E2EE initialization with admin public key from SignalR command
                RelayEncryption relayEncryption = null;

                Console.WriteLine($"[RELAY E2EE] === ADMIN KEY SOURCE CHECK ===");
                Console.WriteLine($"[RELAY E2EE] Admin Public Key in SignalR Command: {!string.IsNullOrEmpty(relayDetails.admin_public_key)}");
                
                if (!string.IsNullOrEmpty(relayDetails.admin_public_key) && agentRsa != null)
                {
                    Console.WriteLine($"[RELAY] Admin Public Key received from SignalR Command");
                    Console.WriteLine($"[RELAY] Admin Public Key length: {relayDetails.admin_public_key.Length} chars");
                    Console.WriteLine($"[RELAY E2EE] Admin Public Key (first 100 chars):");
                    Console.WriteLine($"[RELAY E2EE]   {relayDetails.admin_public_key.Substring(0, Math.Min(100, relayDetails.admin_public_key.Length))}");
                    Console.WriteLine($"[RELAY E2EE] === END KEY DEBUG ===");
                    
                    try
                    {
                        // Import admin public key from SignalR command
                        adminRsa = System.Security.Cryptography.RSA.Create();
                        adminRsa.ImportFromPem(relayDetails.admin_public_key);
                        Console.WriteLine($"[RELAY] Admin Public Key imported ({adminRsa.KeySize} bits)");
                        
                        // Initialize E2EE
                        relayEncryption = new RelayEncryption(agentRsa, adminRsa);
                        Console.WriteLine($"[RELAY] E2EE initialized (Agent <-> Admin)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[RELAY] Failed to initialize E2EE: {ex.Message}");
                        Logging.Error("Service.ConnectToRelayServer", "E2EE initialization failed", ex.ToString());
                        
                        // Cleanup on error
                        adminRsa?.Dispose();
                        adminRsa = null;
                    }
                }
                else
                {
                    Console.WriteLine($"[RELAY] No Admin Public Key in SignalR Command - E2EE not available");
                    Console.WriteLine($"[RELAY] Reason: No admin_public_key in SignalR Command");
                    if (agentRsa == null)
                        Console.WriteLine($"[RELAY] ERROR: No agent RSA keypair available!");
                }
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Authentication successful", $"session: {relayDetails.session_id}");
                
                // Connect to local service with timeout
                Console.WriteLine($"[RELAY] Connecting to local service 127.0.0.1:{relayDetails.local_port}...");
                localClient = new TcpClient();
                
                // TCP optimizations for RDP
                localClient.NoDelay = true; // Disable Nagle's algorithm
                localClient.ReceiveBufferSize = 131072; // 128KB
                localClient.SendBufferSize = 131072; // 128KB
                
                // Set timeout for connection (10 seconds)
                var connectTask = localClient.ConnectAsync("127.0.0.1", relayDetails.local_port);
                var timeoutTask = Task.Delay(10000, cts.Token);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    Console.WriteLine($"[RELAY] Connection to local service timed out after 10 seconds");
                    Logging.Error("Service.ConnectToRelayServer", "Connection to local service timed out", 
                        $"session: {relayDetails.session_id}, local_port: {relayDetails.local_port}");
                    throw new TimeoutException($"Connection to local service on port {relayDetails.local_port} timed out after 10 seconds");
                }
                
                // Wait for connect task to get exceptions
                await connectTask;
                
                // Enable TCP KeepAlive
                localClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                
                Console.WriteLine($"[RELAY] Connected to local service!");
                
                localStream = localClient.GetStream();
                Console.WriteLine($"[RELAY] Local NetworkStream obtained");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Connected to local service", 
                        $"session: {relayDetails.session_id}, local_port: {relayDetails.local_port}");
                
                // Start bidirectional relay with E2EE (if relayEncryption was initialized)
                Console.WriteLine($"[RELAY] Starting bidirectional relay...");
                Console.WriteLine($"[RELAY] E2EE Status: {(relayEncryption != null ? "Enabled" : "Disabled")}");
                
                if (relayEncryption != null)
                {
                    Console.WriteLine($"[RELAY] E2EE enabled (Agent <-> Admin)");
                    Console.WriteLine($"[RELAY] Data flow will be:");
                    Console.WriteLine($"[RELAY]   - Relay -> Local: Receive encrypted, decrypt, forward plaintext");
                    Console.WriteLine($"[RELAY]   - Local -> Relay: Receive plaintext, encrypt, forward encrypted");
                    Console.WriteLine($"[RELAY] ");
                    Console.WriteLine($"[RELAY] IMPORTANT: The Admin MUST send the encrypted Session-Key as the first packet!");
                    Console.WriteLine($"[RELAY]            Without it, the Agent cannot encrypt outgoing data.");
                }
                else
                {
                    Console.WriteLine($"[RELAY] No E2EE - relay will run in plaintext mode");
                }
                
                Console.WriteLine($"[RELAY] Starting data relay for {relayDetails.session_id}-relay-to-local (encryption: {relayEncryption != null}, encrypt: False)");
                Console.WriteLine($"[RELAY] Starting data relay for {relayDetails.session_id}-local-to-relay (encryption: {relayEncryption != null}, encrypt: True)");
                
                var relayToLocal = RelayDataAsync(relayStream, localStream, $"{relayDetails.session_id}-relay-to-local", cts.Token, relayEncryption, false);
                var localToRelay = RelayDataAsync(localStream, relayStream, $"{relayDetails.session_id}-local-to-relay", cts.Token, relayEncryption, true);
                
                Console.WriteLine($"[RELAY] Bidirectional relay started!");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Bidirectional relay started", $"session: {relayDetails.session_id}");
                
                // Wait until one of the two tasks ends, then cancel the other
                await Task.WhenAny(relayToLocal, localToRelay);
                
                Console.WriteLine($"[RELAY] Relay connection ended, cancelling...");
                
                // Cancel the cancellation token to stop both relay tasks
                cts.Cancel();
                
                // Wait for both tasks to ensure they are finished
                try
                {
                    await Task.WhenAll(relayToLocal, localToRelay);
                }
                catch (OperationCanceledException)
                {
                    // Expected when one of the tasks was cancelled
                    Console.WriteLine($"[RELAY] Tasks cancelled (expected)");
                }
                
                Console.WriteLine($"[RELAY] Relay ended gracefully");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Relay ended", $"session: {relayDetails.session_id}");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[RELAY] Session cancelled: {relayDetails.session_id}");
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Relay session cancelled", $"session: {relayDetails.session_id}");
            }
            catch (TimeoutException ex)
            {
                Console.WriteLine($"[RELAY] Timeout error: {ex.Message}");
                Logging.Error("Service.ConnectToRelayServer", "Local service connection timeout", 
                    $"session: {relayDetails.session_id}, error: {ex.Message}");
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"[RELAY] Socket error: {ex.SocketErrorCode} - {ex.Message}");
                Logging.Error("Service.ConnectToRelayServer", "Network connection error", 
                    $"session: {relayDetails.session_id}, SocketErrorCode: {ex.SocketErrorCode}, error: {ex.Message}");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[RELAY] IO error: {ex.Message}");
                Logging.Error("Service.ConnectToRelayServer", "Stream I/O error", 
                    $"session: {relayDetails.session_id}, error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RELAY] Unexpected error: {ex.GetType().Name} - {ex.Message}");
                Console.WriteLine($"[RELAY] Stack trace: {ex.StackTrace}");
                Logging.Error("Service.ConnectToRelayServer", "Relay connection error", 
                    $"session: {relayDetails.session_id}, error: {ex.Message}");
            }
            finally
            {
                Console.WriteLine($"[RELAY] Cleaning up resources for session {relayDetails.session_id}...");
                
                // E2EE Cleanup: Dispose RSA keys (do NOT delete from dictionary - session is still running!)
                try
                {
                    adminRsa?.Dispose();
                    Console.WriteLine($"[RELAY] Admin RSA key disposed");
                }
                catch { }
                
                // IMPORTANT: do NOT dispose agentRsa - it lives in the _sessionKeypairs dictionary!

                // Cleanup - Dispose instead of Close for better resource cleanup
                try
                {
                    localStream?.Dispose();
                    Console.WriteLine($"[RELAY] Local stream disposed");
                }
                catch { }
                
                try
                {
                    localClient?.Dispose();
                    Console.WriteLine($"[RELAY] Local client disposed");
                }
                catch { }
                
                try
                {
                    relayStream?.Dispose();
                    Console.WriteLine($"[RELAY] Relay stream disposed");
                }
                catch { }
                
                try
                {
                    relayClient?.Dispose();
                    Console.WriteLine($"[RELAY] Relay client disposed");
                }
                catch { }
                
                _activeRelaySessions.TryRemove(relayDetails.session_id, out _);
                Console.WriteLine($"[RELAY] Session removed from active sessions");
                
                try
                {
                    cts?.Dispose();
                    Console.WriteLine($"[RELAY] CancellationTokenSource disposed");
                }
                catch { }
                
                Console.WriteLine($"[RELAY] ========== Cleanup Complete ==========");
                Console.WriteLine($"[RELAY] Note: Agent RSA keypair kept in memory for potential reconnection");
                
                if (Agent.debug_mode)
                    Logging.Debug("Service.ConnectToRelayServer", "Relay session cleaned up", $"session: {relayDetails.session_id}");
            }
        }
        
        /// <summary>
        /// Relays data bidirectionally between two streams with length-prefix protocol for E2EE
        /// </summary>
        private async Task RelayDataAsync(Stream source, Stream destination, string direction, 
            CancellationToken cancellationToken, RelayEncryption encryption = null, bool encrypt = false)
        {
            long totalBytes = 0;
            
            // Logging already done before this method is called
            
            try
            {
                if (encryption != null)
                {
                    // E2EE Mode: Use length-prefix protocol
                    totalBytes = await RelayDataWithLengthPrefixAsync(source, destination, direction, cancellationToken, encryption, encrypt);
                }
                else
                {
                    // Plaintext Mode: Direct relay
                    totalBytes = await RelayDataPlaintextAsync(source, destination, direction, cancellationToken);
                }
                
                //Console.WriteLine($"[RELAY] {direction}: {totalBytes / 1024} KB transferred");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[RELAY] {direction}: Cancelled (total: {totalBytes / 1024} KB)");
                if (Agent.debug_mode)
                    Logging.Debug("Service.RelayDataAsync", "Relay cancelled", direction);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RELAY] {direction}: Stream error - {ex.GetType().Name}: {ex.Message}");
                if (Agent.debug_mode)
                    Logging.Debug("Service.RelayDataAsync", "Relay stream ended", $"{direction}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Relay with length-prefix protocol for E2EE (Format: [Length:4][EncryptedData])
        /// </summary>
        private async Task<long> RelayDataWithLengthPrefixAsync(Stream source, Stream destination, string direction,
            CancellationToken cancellationToken, RelayEncryption encryption, bool encrypt)
        {
            byte[] buffer = new byte[65536];
            byte[] lengthPrefix = new byte[4];
            long totalBytes = 0;
            bool sessionKeyReceived = false;
            
            while (!cancellationToken.IsCancellationRequested)
            {
                if (encrypt)
                {
                    // IMPORTANT: Wait until session key is established (received from decrypt stream)
                    if (!encryption.IsSessionKeyEstablished())
                    {
                        // Wait max 30 seconds for session key (increased from 10s for slow networks)
                        Console.WriteLine($"[RELAY E2EE] {direction}: Waiting for Session-Key to be established...");
                        Console.WriteLine($"[RELAY E2EE] {direction}: This key must be sent by the Admin/Backend first");
                        
                        for (int i = 0; i < 300; i++) // 300 * 100ms = 30 seconds
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Console.WriteLine($"[RELAY E2EE] {direction}: Cancelled while waiting for Session-Key");
                                return totalBytes;
                            }
                            
                            await Task.Delay(100, cancellationToken);
                            
                            if (encryption.IsSessionKeyEstablished())
                            {
                                Console.WriteLine($"[RELAY E2EE] {direction}: Session-Key established after {i * 100}ms");
                                break;
                            }
                            
                            // Log every 5 seconds
                            if (i > 0 && i % 50 == 0)
                            {
                                Console.WriteLine($"[RELAY E2EE] {direction}: Still waiting... ({i * 100}ms elapsed)");
                            }
                        }
                        
                        if (!encryption.IsSessionKeyEstablished())
                        {
                            Console.WriteLine($"[RELAY E2EE] {direction}: Session-Key not established after 30 seconds - aborting");
                            Console.WriteLine($"[RELAY E2EE] {direction}: This indicates the Admin/Backend is not sending the Session-Key");
                            Console.WriteLine($"[RELAY E2EE] {direction}: Possible causes:");
                            Console.WriteLine($"[RELAY E2EE] {direction}:   1. Admin disconnected before sending Session-Key");
                            Console.WriteLine($"[RELAY E2EE] {direction}:   2. Backend/Relay not forwarding Session-Key to Agent");
                            Console.WriteLine($"[RELAY E2EE] {direction}:   3. Network connectivity issues");
                            throw new TimeoutException("Session-Key not established after 30 seconds");
                        }
                    }
                    
                    // Local -> Relay: Read plaintext, encrypt, send with length-prefix
                    int bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0) break;
                    
                    byte[] plaintext = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, plaintext, 0, bytesRead);
                    
                    // Encrypt
                    byte[] ciphertext = encryption.Encrypt(plaintext);
                    
                    // Send length-prefix + ciphertext
                    byte[] lengthBytes = BitConverter.GetBytes(ciphertext.Length);
                    await destination.WriteAsync(lengthBytes, 0, 4, cancellationToken);
                    await destination.WriteAsync(ciphertext, 0, ciphertext.Length, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                    
                    totalBytes += bytesRead;
                    
                    //if (totalBytes % 102400 < 65536)
                      //  Console.WriteLine($"[RELAY] {direction}: {totalBytes / 1024} KB transferred");
                }
                else
                {
                    // FIRST packet is encrypted session key from admin!
                    if (!sessionKeyReceived)
                    {
                        Console.WriteLine($"[RELAY E2EE] {direction}: Waiting for first packet (encrypted Session-Key from Admin)...");
                        
                        // Read length-prefix
                        int keyRead = await ReadExactAsync(source, lengthPrefix, 0, 4, cancellationToken);
                        if (keyRead == 0)
                        {
                            Console.WriteLine($"[RELAY E2EE] {direction}: Connection closed before receiving Session-Key");
                            break;
                        }
                        
                        int encryptedKeyLength = BitConverter.ToInt32(lengthPrefix, 0);
                        Console.WriteLine($"[RELAY E2EE] {direction}: Received Session-Key length prefix: {encryptedKeyLength} bytes");
                        
                        if (encryptedKeyLength != 512) // RSA-4096 = 512 bytes
                        {
                            Console.WriteLine($"[RELAY E2EE] {direction}: WARNING - Unexpected Session-Key length: {encryptedKeyLength} (expected 512 for RSA-4096)");
                            Console.WriteLine($"[RELAY E2EE] {direction}: This might indicate a protocol mismatch or wrong RSA key size");
                        }
                        
                        // Read encrypted session key
                        byte[] encryptedSessionKey = new byte[encryptedKeyLength];
                        Console.WriteLine($"[RELAY E2EE] {direction}: Reading {encryptedKeyLength} bytes of encrypted Session-Key...");
                        await ReadExactAsync(source, encryptedSessionKey, 0, encryptedKeyLength, cancellationToken);
                        
                        Console.WriteLine($"[RELAY E2EE] {direction}: Encrypted Session-Key received, importing...");
                        
                        // Import session key
                        try
                        {
                            encryption.ImportEncryptedSessionKey(encryptedSessionKey);
                            sessionKeyReceived = true;
                            
                            Console.WriteLine($"[RELAY E2EE] {direction}: Session-Key successfully imported! ({encryptedKeyLength} bytes)");
                            Console.WriteLine($"[RELAY E2EE] {direction}: E2EE encryption is now active for this relay session");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[RELAY E2EE] {direction}: ERROR - Failed to import Session-Key: {ex.Message}");
                            Console.WriteLine($"[RELAY E2EE] {direction}: This indicates the Admin used wrong public key or encryption failed");
                            throw;
                        }
                        
                        continue; // Next packet is then data
                    }
                    
                    // Relay -> Local: Read length-prefix, read complete encrypted packet, decrypt
                    int read = await ReadExactAsync(source, lengthPrefix, 0, 4, cancellationToken);
                    if (read == 0) break;
                    
                    int encryptedLength = BitConverter.ToInt32(lengthPrefix, 0);
                    
                    // Validation
                    if (encryptedLength <= 0 || encryptedLength > 1048576) // Max 1MB
                    {
                        Console.WriteLine($"[RELAY E2EE] Invalid encrypted length: {encryptedLength}");
                        throw new InvalidDataException($"Invalid encrypted packet length: {encryptedLength}");
                    }
                    
                    // Read complete encrypted packet
                    byte[] ciphertext = new byte[encryptedLength];
                    await ReadExactAsync(source, ciphertext, 0, encryptedLength, cancellationToken);
                    
                    // Decrypt
                    byte[] plaintext = encryption.Decrypt(ciphertext);
                    
                    // Send plaintext
                    await destination.WriteAsync(plaintext, 0, plaintext.Length, cancellationToken);
                    await destination.FlushAsync(cancellationToken);
                    
                    totalBytes += plaintext.Length;
                    
                    //if (totalBytes % 102400 < 65536)
                      //  Console.WriteLine($"[RELAY] {direction}: {totalBytes / 1024} KB transferred");
                }
            }
            
            return totalBytes;
        }
        
        /// <summary>
        /// Plaintext relay without encryption
        /// </summary>
        private async Task<long> RelayDataPlaintextAsync(Stream source, Stream destination, string direction,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[65536];
            long totalBytes = 0;
            
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0) break;
                
                await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                
                totalBytes += bytesRead;
                
                //if (totalBytes % 102400 < 65536)
                  //  Console.WriteLine($"[RELAY] {direction}: {totalBytes / 1024} KB transferred");
            }
            
            return totalBytes;
        }
        
        /// <summary>
        /// Reads exactly the specified number of bytes from a stream
        /// </summary>
        private async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, cancellationToken);
                if (read == 0) return totalRead; // Stream ended
                totalRead += read;
            }
            return totalRead;
        }
        
        /// <summary>
        /// Details for Relay Connection Request (from command field)
        /// </summary>
        private class RelayConnectionDetails
        {
            public string session_id { get; set; }
            public int local_port { get; set; }
            public string protocol { get; set; }
            public string public_key { get; set; }
            public string fingerprint { get; set; }
            public string admin_public_key { get; set; } // For admin change detection
        }
        
        /// <summary>
        /// Details for Relay Close Connection (from command field)
        /// </summary>
        private class RelayCloseDetails
        {
            public string session_id { get; set; }
        }
        
        /// <summary>
        /// Response class for relay authentication
        /// </summary>
        private class RelayAuthResponse
        {
            public bool success { get; set; }
            public string message { get; set; }
            // Admin Public Key comes ONLY via SignalR (Type 14 Command), NOT via Auth-Response!
        }
        
        /// <summary>
        /// Encryption handler for relay connections (RSA E2EE between Agent and Admin)
        /// </summary>
        
        #endregion
        
    } 
}
