using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetLock_RMM_User_Process.Helper.Keyboard;
using NetLock_RMM_User_Process.Windows.Keyboard;
using NetLock_RMM_User_Process.Windows.Helper;
using NetLock_RMM_User_Process.Windows.Mouse;
using NetLock_RMM_User_Process.Windows.ScreenControl;
using NetLock_RMM_User_Process.Linux.Keyboard;
using NetLock_RMM_User_Process.Linux.Mouse;
using NetLock_RMM_User_Process.Linux.ScreenControl;
using NetLock_RMM_User_Process.MacOS.Keyboard;
using NetLock_RMM_User_Process.MacOS.Mouse;
using NetLock_RMM_User_Process.MacOS.ScreenControl;
using WindowsInput;
using static System.Net.Mime.MediaTypeNames;

class UserClient
{
    private TcpClient _client;
    private NetworkStream _stream;
    private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
    private Task _messageHandlerTask;
    private bool _isConnecting = false;
    
    // Remote session state tracking
    private bool _remoteSessionActive = false;
    private DateTime _lastScreenCaptureTime = DateTime.MinValue;
    private readonly TimeSpan _sessionIdleTimeout = TimeSpan.FromSeconds(5); // Consider session ended after 5s of no screen captures
    private Timer _sessionIdleTimer;
    
    // Double-click detection
    private DateTime _lastLeftClickTime = DateTime.MinValue;
    private const int DOUBLE_CLICK_DETECTION_MS = 500; // Detection window for double-clicks

    public class Command
    {
        public string response_id { get; set; }
        public string type { get; set; }
        public string remote_control_screen_index { get; set; }
        public string remote_control_mouse_action { get; set; }
        public string remote_control_mouse_xyz { get; set; }
        public string remote_control_keyboard_input { get; set; }
        public string remote_control_keyboard_content { get; set; }
        
        // For process elevation with credentials (Windows only)
        // Username can include domain: DOMAIN\username or .\username
        public string remote_control_elevation_username { get; set; }
        public string remote_control_elevation_password { get; set; }
        public int remote_control_render_mode { get; set; }
    }

    public async Task Local_Server_Connect()
    {
        // Start monitoring connection immediately - it will handle initial connection and reconnections
        _ = MonitorConnectionAsync(_cancellationTokenSource.Token);
        
        // Wait a moment to allow initial connection attempt
        await Task.Delay(1000);
    }


    private ConcurrentQueue<Command> _commandQueue = new ConcurrentQueue<Command>();

    /// <summary>
    /// Called whenever remote session activity is detected (e.g., screen capture request).
    /// Manages animation state and session idle timeout.
    /// </summary>
    private void OnRemoteSessionActivity()
    {
        _lastScreenCaptureTime = DateTime.Now;
        
        // Start remote session if not already active
        if (!_remoteSessionActive)
        {
            _remoteSessionActive = true;
            
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("Remote session started - disabling Windows animations for better performance.");

                AnimationManager.DisableAnimations();
            }
        }
        
        // Reset or start the idle timer
        _sessionIdleTimer?.Dispose();
        _sessionIdleTimer = new Timer(OnSessionIdleTimeout, null, _sessionIdleTimeout, Timeout.InfiniteTimeSpan);
    }
    
    /// <summary>
    /// Called when the session has been idle for too long (no screen captures).
    /// Restores animations and marks session as ended.
    /// This also releases Desktop Duplication resources, allowing other
    /// instances (e.g., in other sessions) to capture the screen.
    /// </summary>
    private void OnSessionIdleTimeout(object state)
    {
        if (_remoteSessionActive)
        {
            _remoteSessionActive = false;
            
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("Remote session ended (idle timeout) - restoring Windows animations.");
                AnimationManager.RestoreAnimations();
                
                // IMPORTANT: Release Desktop Duplication resources!
                // This allows other instances (e.g., user session process after login)
                // to capture the screen without conflicts.
                Console.WriteLine("Releasing Desktop Duplication resources for other sessions...");
                foreach (var capture in _captureInstances.Values)
                {
                    try
                    {
                        capture?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error disposing capture instance: {ex.Message}");
                    }
                }
                _captureInstances.Clear();
            }
            
            // Linux: Stop PipeWire screen capture to release the screen share
            if (OperatingSystem.IsLinux() && _linuxCaptureInstance != null)
            {
                Console.WriteLine("Remote session ended (idle timeout) - stopping Linux screen capture.");
                
                try
                {
                    _linuxCaptureInstance.Dispose();
                    _linuxCaptureInstance = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping Linux screen capture: {ex.Message}");
                }
            }
            
            // macOS: Stop screen capture
            if (OperatingSystem.IsMacOS() && _macOSCaptureInstance != null)
            {
                Console.WriteLine("Remote session ended (idle timeout) - stopping macOS screen capture.");
                
                try
                {
                    _macOSCaptureInstance.Dispose();
                    _macOSCaptureInstance = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping macOS screen capture: {ex.Message}");
                }
            }
        }
        
        _sessionIdleTimer?.Dispose();
        _sessionIdleTimer = null;
    }

    private async Task Local_Server_Handle_Server_Messages(CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine("Listening for messages from the server...");

            // Buffer to read incoming data
            byte[] buffer = new byte[4096]; // Adjust the size of the buffer as needed
            StringBuilder messageBuilder = new StringBuilder();

            while (!cancellationToken.IsCancellationRequested && _client?.Connected == true)
            {
                if (_stream?.CanRead == true)
                {
                    // Read the incoming message length
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (bytesRead == 0)
                    {
                        // Server closed the connection
                        Console.WriteLine("Server closed the connection.");
                        break;
                    }

                    if (bytesRead > 0)
                    {
                        // Convert the byte array to string
                        string messageChunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        messageBuilder.Append(messageChunk);

                        // Check if the message is complete
                        if (IsCompleteJson(messageBuilder.ToString(), out string completeMessage))
                        {
                            // Clear the builder for the next message
                            messageBuilder.Clear();

                            try
                            {
                                Console.WriteLine($"Received complete message: {completeMessage}");

                                // Deserialize the complete message to Command object
                                Command command = JsonSerializer.Deserialize<Command>(completeMessage);

                                if (command != null)
                                {
                                    // Enqueue the command for processing
                                    _commandQueue.Enqueue(command);
                                }
                                else
                                {
                                    Console.WriteLine("Failed to deserialize command.");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to deserialize message: {ex.Message}");
                            }
                        }
                    }
                }
                else
                {
                    // Stream is not readable, likely disconnected
                    break;
                }

                // Process commands from the queue
                while (_commandQueue.TryDequeue(out Command queuedCommand))
                {
                    // Process the command asynchronously
                    await ProcessCommand(queuedCommand);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Listening was canceled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while listening for server messages: {ex.Message}");
            // Mark connection as broken so MonitorConnectionAsync can handle reconnection
            try
            {
                _client?.Close();
            }
            catch { }
        }
        finally
        {
            Console.WriteLine("Message handler task ended.");
        }
    }

    // Helper method to check if the message is complete JSON
    private bool IsCompleteJson(string json, out string completeJson)
    {
        completeJson = null;

        // Check for valid JSON (starting and ending braces)
        if (json.Trim().StartsWith("{") && json.Trim().EndsWith("}"))
        {
            // Count the braces
            int openBraces = 0;
            int closeBraces = 0;

            foreach (char c in json)
            {
                if (c == '{') openBraces++;
                if (c == '}') closeBraces++;
            }

            // If the count of open and close braces is the same, we have a complete JSON object
            if (openBraces == closeBraces)
            {
                completeJson = json;
                return true;
            }
        }

        return false;
    }

    private async Task ProcessMessageAsync(string message)
    {
        try
        {
            // Deserialize the message to Command object
            Command command = JsonSerializer.Deserialize<Command>(message);

            if (command != null)
            {
                // Enqueue the command for processing
                _commandQueue.Enqueue(command);
            }
            else
            {
                Console.WriteLine("Failed to deserialize command.");
            }
        }
        catch (JsonException jsonEx)
        {
            Console.WriteLine($"Failed to deserialize message: {jsonEx.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while processing the message: {ex.Message}");
        }
    }

    // Big WIP
    private async Task ProcessCommand(Command command)
    {
        try
        {
            Console.WriteLine($"Processing command: {command.type} with response ID: {command.response_id}");
            Console.WriteLine($"Mouse Action: {command.remote_control_mouse_action}, Mouse XYZ: {command.remote_control_mouse_xyz}, Keyboard Input: {command.remote_control_keyboard_input}");

            switch (command.type)
            {
                    // Ersetze den Fall "0" in ProcessCommand wie folgt:
                    case "0": // Screen Capture
                        // Track remote session activity and manage animations
                        OnRemoteSessionActivity();
                        // Sequential processing per screen to prevent out-of-order frame delivery
                        _ = Task.Run(() => CaptureAndSendScreenshotSequential(command));
                    break;

                case "1": // Move Mouse / Clicks
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string[] mouseCoordinates = command.remote_control_mouse_xyz.Split(',');
                            int x = Convert.ToInt32(mouseCoordinates[0]);
                            int y = Convert.ToInt32(mouseCoordinates[1]);
                            int screenIndex = Convert.ToInt32(command.remote_control_screen_index);

                            // Check if this is a click action or just a move
                            bool isClickAction = command.remote_control_mouse_action != "4";

                            if (OperatingSystem.IsWindows())
                            {
                                if (isClickAction)
                                {
                                    // Click actions: Move with lock + perform action
                                    await MouseControl.MoveMouse(x, y, screenIndex);

                                    switch (command.remote_control_mouse_action)
                                    {
                                        case "0": // Left Click
                                            // Double-click detection: If two clicks arrive within the detection window
                                            var now = DateTime.Now;
                                            var timeSinceLastClick = (now - _lastLeftClickTime).TotalMilliseconds;

                                            if (timeSinceLastClick < DOUBLE_CLICK_DETECTION_MS && timeSinceLastClick > 0)
                                            {
                                                // This is a double-click!
                                                Console.WriteLine($"Double-click detected! ({timeSinceLastClick:F0}ms since last click)");
                                                await MouseControl.DoubleClickMouse();
                                                _lastLeftClickTime = DateTime.MinValue; // Reset to prevent triple-click
                                            }
                                            else
                                            {
                                                // Single click
                                                await MouseControl.LeftClickMouse();
                                                _lastLeftClickTime = now;
                                            }
                                            break;

                                        case "1": // Right Click
                                            await MouseControl.RightClickMouse();
                                            break;

                                        case "2": // Mouse Down
                                            await MouseControl.LeftMouseDown();
                                            break;

                                        case "3": // Mouse Up
                                            await MouseControl.LeftMouseUp();
                                            break;
                                    }
                                }
                                else
                                {
                                    // Move only: Use non-blocking move (no lock)
                                    await MouseControl.MoveMouseNoLock(x, y, screenIndex);
                                }
                            }
                            else if (OperatingSystem.IsLinux())
                            {
                                if (isClickAction)
                                {
                                    // Click actions: Move with lock + perform action
                                    await MouseControlLinux.MoveMouse(x, y, screenIndex);

                                    switch (command.remote_control_mouse_action)
                                    {
                                        case "0": // Left Click
                                            var now = DateTime.Now;
                                            var timeSinceLastClick = (now - _lastLeftClickTime).TotalMilliseconds;

                                            if (timeSinceLastClick < DOUBLE_CLICK_DETECTION_MS && timeSinceLastClick > 0)
                                            {
                                                Console.WriteLine($"Double-click detected! ({timeSinceLastClick:F0}ms since last click)");
                                                await MouseControlLinux.DoubleClickMouse();
                                                _lastLeftClickTime = DateTime.MinValue;
                                            }
                                            else
                                            {
                                                await MouseControlLinux.LeftClickMouse();
                                                _lastLeftClickTime = now;
                                            }
                                            break;

                                        case "1": // Right Click
                                            await MouseControlLinux.RightClickMouse();
                                            break;

                                        case "2": // Mouse Down
                                            await MouseControlLinux.LeftMouseDown();
                                            break;

                                        case "3": // Mouse Up
                                            await MouseControlLinux.LeftMouseUp();
                                            break;
                                    }
                                }
                                else
                                {
                                    // Move only: Use non-blocking move (no lock)
                                    await MouseControlLinux.MoveMouseNoLock(x, y, screenIndex);
                                }
                            }
                            else if (OperatingSystem.IsMacOS())
                            {
                                if (isClickAction)
                                {
                                    // Click actions: Move with lock + perform action
                                    await MouseControlMacOS.MoveMouse(x, y, screenIndex);

                                    switch (command.remote_control_mouse_action)
                                    {
                                        case "0": // Left Click
                                            var now = DateTime.Now;
                                            var timeSinceLastClick = (now - _lastLeftClickTime).TotalMilliseconds;

                                            if (timeSinceLastClick < DOUBLE_CLICK_DETECTION_MS && timeSinceLastClick > 0)
                                            {
                                                Console.WriteLine($"Double-click detected! ({timeSinceLastClick:F0}ms since last click)");
                                                await MouseControlMacOS.DoubleClickMouse();
                                                _lastLeftClickTime = DateTime.MinValue;
                                            }
                                            else
                                            {
                                                await MouseControlMacOS.LeftClickMouse();
                                                _lastLeftClickTime = now;
                                            }
                                            break;

                                        case "1": // Right Click
                                            await MouseControlMacOS.RightClickMouse();
                                            break;

                                        case "2": // Mouse Down
                                            await MouseControlMacOS.LeftMouseDown();
                                            break;

                                        case "3": // Mouse Up
                                            await MouseControlMacOS.LeftMouseUp();
                                            break;
                                    }
                                }
                                else
                                {
                                    // Move only: Use non-blocking move (no lock)
                                    await MouseControlMacOS.MoveMouseNoLock(x, y, screenIndex);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Mouse control error: {ex.Message}");
                        }
                    });
                    break;
                case "2": // Keyboard Input
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var input = command.remote_control_keyboard_input?.Trim();

                            if (string.IsNullOrEmpty(input))
                                return;

                            var inputLower = input.ToLowerInvariant();

                            // If shift is in the first part of the input, we assume it's a modifier key
                            bool shift = inputLower.StartsWith("shift+");

                            Console.WriteLine($"Processing keyboard input: {inputLower}, Shift: {shift}");

                            if (OperatingSystem.IsWindows())
                            {
                                if (inputLower == "ctrl+keya")
                                    KeyboardControl.SendCtrlA();
                                else if (inputLower == "ctrl+keyc")
                                    KeyboardControl.SendCtrlC();
                                else if (inputLower == "ctrl+keyv")
                                    KeyboardControl.SendCtrlV(command.remote_control_keyboard_content);
                                else if (inputLower == "ctrl+keyx")
                                    KeyboardControl.SendCtrlX();
                                else if (inputLower == "ctrl+keyz")
                                    KeyboardControl.SendCtrlZ();
                                else if (inputLower == "ctrl+keyy")
                                    KeyboardControl.SendCtrlY();
                                else if (inputLower == "ctrl+keys")
                                    KeyboardControl.SendCtrlS();
                                else if (inputLower == "ctrl+keyn")
                                    KeyboardControl.SendCtrlN();
                                else if (inputLower == "ctrl+keyp")
                                    KeyboardControl.SendCtrlP();
                                else if (inputLower == "ctrl+keyf")
                                    KeyboardControl.SendCtrlF();
                                else if (inputLower == "ctrl+shift+keyt")
                                    KeyboardControl.SendCtrlShiftT();
                                else if (inputLower == "ctrlaltdel")
                                    KeyboardControl.SendCtrlAltDelete();
                                else if (inputLower == "ctrl+backspace")
                                    KeyboardControl.SendCtrlBackspace();
                                else if (inputLower == "ctrl+arrowleft")
                                    KeyboardControl.SendCtrlArrowLeft();
                                else if (inputLower == "ctrl+arrowright")
                                    KeyboardControl.SendCtrlArrowRight();
                                else if (inputLower == "ctrl+arrowup")
                                    KeyboardControl.SendCtrlArrowUp();
                                else if (inputLower == "ctrl+arrowdown")
                                    KeyboardControl.SendCtrlArrowDown();
                                else if (inputLower == "ctrl+shift+arrowleft")
                                    KeyboardControl.SendCtrlArrowLeft();
                                else if (inputLower == "ctrl+shift+arrowright")
                                    KeyboardControl.SendCtrlArrowRight();
                                else if (inputLower == "ctrl+keyr")
                                    KeyboardControl.SendCtrlR();
                                else
                                {
                                    if (command.remote_control_keyboard_input.Length > 1)
                                    {
                                        if (shift)
                                            inputLower = inputLower.Replace("shift+", ""); // Remove shift from the input for ASCII mapping

                                        var asciiCode = Keys.MapKeyStringToAscii(inputLower);
                                        if (asciiCode.HasValue)
                                        {
                                            await KeyboardControl.SendKey(asciiCode.Value, shift);
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Unknown keyboard input: {input}");
                                        }
                                    }
                                    else
                                    {
                                        var sim1 = new InputSimulator();
                                        sim1.Keyboard.TextEntry(command.remote_control_keyboard_input);
                                    }
                                }
                            }
                            else if (OperatingSystem.IsLinux())
                            {
                                if (inputLower == "ctrl+keya")
                                    KeyboardControlLinux.SendCtrlA();
                                else if (inputLower == "ctrl+keyc")
                                    KeyboardControlLinux.SendCtrlC();
                                else if (inputLower == "ctrl+keyv")
                                    KeyboardControlLinux.SendCtrlV(command.remote_control_keyboard_content);
                                else if (inputLower == "ctrl+keyx")
                                    KeyboardControlLinux.SendCtrlX();
                                else if (inputLower == "ctrl+keyz")
                                    KeyboardControlLinux.SendCtrlZ();
                                else if (inputLower == "ctrl+keyy")
                                    KeyboardControlLinux.SendCtrlY();
                                else if (inputLower == "ctrl+keys")
                                    KeyboardControlLinux.SendCtrlS();
                                else if (inputLower == "ctrl+keyn")
                                    KeyboardControlLinux.SendCtrlN();
                                else if (inputLower == "ctrl+keyp")
                                    KeyboardControlLinux.SendCtrlP();
                                else if (inputLower == "ctrl+keyf")
                                    KeyboardControlLinux.SendCtrlF();
                                else if (inputLower == "ctrl+shift+keyt")
                                    KeyboardControlLinux.SendCtrlShiftT();
                                else if (inputLower == "ctrlaltdel")
                                    KeyboardControlLinux.SendCtrlAltDelete();
                                else if (inputLower == "ctrl+backspace")
                                    KeyboardControlLinux.SendCtrlBackspace();
                                else if (inputLower == "ctrl+arrowleft")
                                    KeyboardControlLinux.SendCtrlArrowLeft();
                                else if (inputLower == "ctrl+arrowright")
                                    KeyboardControlLinux.SendCtrlArrowRight();
                                else if (inputLower == "ctrl+arrowup")
                                    KeyboardControlLinux.SendCtrlArrowUp();
                                else if (inputLower == "ctrl+arrowdown")
                                    KeyboardControlLinux.SendCtrlArrowDown();
                                else if (inputLower == "ctrl+shift+arrowleft")
                                    KeyboardControlLinux.SendCtrlArrowLeft();
                                else if (inputLower == "ctrl+shift+arrowright")
                                    KeyboardControlLinux.SendCtrlArrowRight();
                                else if (inputLower == "ctrl+keyr")
                                    KeyboardControlLinux.SendCtrlR();
                                else
                                {
                                    if (command.remote_control_keyboard_input.Length > 1)
                                    {
                                        if (shift)
                                            inputLower = inputLower.Replace("shift+", "");

                                        var asciiCode = Keys.MapKeyStringToAscii(inputLower);
                                        if (asciiCode.HasValue)
                                        {
                                            await KeyboardControlLinux.SendKey(asciiCode.Value, shift);
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Unknown keyboard input: {input}");
                                        }
                                    }
                                    else
                                    {
                                        // Use xdotool/ydotool for single character input on Linux
                                        KeyboardControlLinux.TypeText(command.remote_control_keyboard_input);
                                    }
                                }
                            }
                            else if (OperatingSystem.IsMacOS())
                            {
                                // macOS keyboard shortcuts (Ctrl -> Cmd mapping is handled in KeyboardControlMacOS)
                                if (inputLower == "ctrl+keya")
                                    KeyboardControlMacOS.SendCtrlA();
                                else if (inputLower == "ctrl+keyc")
                                    KeyboardControlMacOS.SendCtrlC();
                                else if (inputLower == "ctrl+keyv")
                                    KeyboardControlMacOS.SendCtrlV(command.remote_control_keyboard_content);
                                else if (inputLower == "ctrl+keyx")
                                    KeyboardControlMacOS.SendCtrlX();
                                else if (inputLower == "ctrl+keyz")
                                    KeyboardControlMacOS.SendCtrlZ();
                                else if (inputLower == "ctrl+keyy")
                                    KeyboardControlMacOS.SendCtrlY();
                                else if (inputLower == "ctrl+keys")
                                    KeyboardControlMacOS.SendCtrlS();
                                else if (inputLower == "ctrl+keyn")
                                    KeyboardControlMacOS.SendCtrlN();
                                else if (inputLower == "ctrl+keyp")
                                    KeyboardControlMacOS.SendCtrlP();
                                else if (inputLower == "ctrl+keyf")
                                    KeyboardControlMacOS.SendCtrlF();
                                else if (inputLower == "ctrl+shift+keyt")
                                    KeyboardControlMacOS.SendCtrlShiftT();
                                else if (inputLower == "ctrlaltdel")
                                    KeyboardControlMacOS.SendCtrlAltDelete(); // Opens Force Quit on macOS
                                else if (inputLower == "ctrl+backspace")
                                    KeyboardControlMacOS.SendCtrlBackspace();
                                else if (inputLower == "ctrl+arrowleft")
                                    KeyboardControlMacOS.SendCtrlArrowLeft();
                                else if (inputLower == "ctrl+arrowright")
                                    KeyboardControlMacOS.SendCtrlArrowRight();
                                else if (inputLower == "ctrl+arrowup")
                                    KeyboardControlMacOS.SendCtrlArrowUp();
                                else if (inputLower == "ctrl+arrowdown")
                                    KeyboardControlMacOS.SendCtrlArrowDown();
                                else if (inputLower == "ctrl+shift+arrowleft")
                                    KeyboardControlMacOS.SendCtrlArrowLeft();
                                else if (inputLower == "ctrl+shift+arrowright")
                                    KeyboardControlMacOS.SendCtrlArrowRight();
                                else if (inputLower == "ctrl+keyr")
                                    KeyboardControlMacOS.SendCtrlR();
                                else
                                {
                                    if (command.remote_control_keyboard_input.Length > 1)
                                    {
                                        if (shift)
                                            inputLower = inputLower.Replace("shift+", "");

                                        var asciiCode = Keys.MapKeyStringToAscii(inputLower);
                                        if (asciiCode.HasValue)
                                        {
                                            await KeyboardControlMacOS.SendKey(asciiCode.Value, shift);
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Unknown keyboard input: {input}");
                                        }
                                    }
                                    else
                                    {
                                        // Use AppleScript for single character input on macOS
                                        KeyboardControlMacOS.TypeText(command.remote_control_keyboard_input);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Keyboard control error: {ex.Message}");
                        }
                    });
                    break;
                case "3":
                    int screen_indexes = 0;
                    if (OperatingSystem.IsWindows())
                        screen_indexes = OldScreenCapture.Get_Screen_Indexes();
                    else if (OperatingSystem.IsLinux())
                    {
                        if (_linuxCaptureInstance == null)
                        {
                            _linuxCaptureInstance = new ScreenCaptureLinux(0);
                            _linuxCaptureInstance.Initialize();
                        }
                        screen_indexes = _linuxCaptureInstance.GetScreenCount();
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        if (_macOSCaptureInstance == null)
                        {
                            _macOSCaptureInstance = new ScreenCaptureMacOS(0);
                            _macOSCaptureInstance.Initialize();
                        }
                        screen_indexes = _macOSCaptureInstance.GetScreenCount();
                    }

                    await Local_Server_Send_Message($"screen_indexes${command.response_id}${screen_indexes}");

                    break;
                case "6": // Get clipboard from user
                    if (OperatingSystem.IsWindows())
                    {
                        KeyboardControl.SendCtrlC();
                        await Task.Delay(200); // Wait for clipboard to update
                        string clipboardContent = User32.GetClipboardText();
                        await Local_Server_Send_Message($"clipboard_content${command.response_id}$clipboard_content%{clipboardContent}");
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        KeyboardControlLinux.SendCtrlC();
                        await Task.Delay(200); // Wait for clipboard to update
                        string clipboardContent = KeyboardControlLinux.GetClipboardText();
                        await Local_Server_Send_Message($"clipboard_content${command.response_id}$clipboard_content%{clipboardContent}");
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        KeyboardControlMacOS.SendCmdC(); // macOS uses Cmd+C
                        await Task.Delay(200); // Wait for clipboard to update
                        string clipboardContent = KeyboardControlMacOS.GetClipboardText();
                        await Local_Server_Send_Message($"clipboard_content${command.response_id}$clipboard_content%{clipboardContent}");
                    }
                    break;
                case "7": // Send text
                    Console.WriteLine($"Sending text: {command.remote_control_keyboard_input}");

                    if (OperatingSystem.IsWindows())
                    {
                        var sim = new InputSimulator();
                        sim.Keyboard.TextEntry(command.remote_control_keyboard_input);
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        KeyboardControlLinux.TypeText(command.remote_control_keyboard_input);
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        KeyboardControlMacOS.TypeText(command.remote_control_keyboard_input);
                    }
                    break;
                case "8": // Elevate process with credentials (Windows only)
                    Console.WriteLine($"Received elevation request");
                    
                    if (OperatingSystem.IsWindows())
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // Check if already running as admin
                                if (ProcessElevation.IsRunningAsAdmin())
                                {
                                    await Local_Server_Send_Message($"elevation_result${command.response_id}$success%false$message%Process is already running with administrator privileges.");
                                    Console.WriteLine("Elevation not required: already running as admin.");
                                    return;
                                }
                                
                                // Validate credentials are provided
                                if (string.IsNullOrEmpty(command.remote_control_elevation_username) || string.IsNullOrEmpty(command.remote_control_elevation_password))
                                {
                                    await Local_Server_Send_Message($"elevation_result${command.response_id}$success%false$message%Username and password are required for elevation.");
                                    Console.WriteLine("Elevation failed: Missing username or password.");
                                    return;
                                }
                                
                                Console.WriteLine($"Attempting elevation with provided credentials...");
                                
                                // Try elevation with LogonApi first (more reliable)
                                var result = ProcessElevation.ElevateWithLogonApi(
                                    command.remote_control_elevation_username,
                                    command.remote_control_elevation_password);
                                
                                if (!result.Success)
                                {
                                    // Fallback to PowerShell method
                                    Console.WriteLine("LogonApi elevation failed, trying PowerShell method...");
                                    result = ProcessElevation.ElevateWithCredentials(
                                        command.remote_control_elevation_username,
                                        command.remote_control_elevation_password);
                                }
                                
                                if (result.Success)
                                {
                                    Console.WriteLine($"Elevation successful! New process PID: {result.NewProcessId}");
                                    
                                    await Local_Server_Send_Message($"elevation_result${command.response_id}$success%true$message%{result.Message}");
                                    
                                    // Wait a moment for the message to be sent, then exit
                                    await Task.Delay(1000);
                                    
                                    Console.WriteLine("Exiting current process after successful elevation...");
                                    Environment.Exit(0);
                                }
                                else
                                {
                                    Console.WriteLine($"Elevation failed: {result.Message}");
                                    
                                    await Local_Server_Send_Message($"elevation_result${command.response_id}$success%false$message%{result.Message}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Elevation error: {ex.Message}");
                                await Local_Server_Send_Message($"elevation_result${command.response_id}$success%false$message%{ex.Message}");
                            }
                        });
                    }
                    else
                    {
                        // Elevation with credentials is Windows-specific
                        await Local_Server_Send_Message($"elevation_result${command.response_id}$success%false$message%Credential-based elevation is only supported on Windows.");
                    }
                    break;
                default:
                    Console.WriteLine("Unknown command type.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to process command: {ex.Message}");
        }
    }

    public async Task Local_Server_Send_Message(string message)
    {
        try
        {
            if (_stream != null && _client.Connected)
            {
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                await _stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                await _stream.FlushAsync();
                Console.WriteLine("Sent message to remote agent");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send message to the local server: {ex.Message}");
        }
    }

    // New method to send binary messages (e.g., image data)
    public async Task Local_Server_Send_Binary_Message(string messageType, string responseId, byte[] data)
    {
        try
        {
            if (_stream != null && _client.Connected)
            {
                // Create header with message type, response ID, and data length
                string header = $"{messageType}${responseId}${data.Length}$";
                byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                
                // Write header first
                await _stream.WriteAsync(headerBytes, 0, headerBytes.Length);
                
                // Write binary data directly
                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();
                
                Console.WriteLine($"Sent binary message ({messageType}) with {data.Length} bytes to remote agent");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to send binary message to the local server: {ex.Message}");
        }
    }

    private async Task MonitorConnectionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Check if the client is still connected
            if (_client == null || !_client.Connected)
            {
                if (!_isConnecting)
                {
                    _isConnecting = true;
                    Console.WriteLine("Disconnected from the server. Attempting to reconnect...");

                    // Try to reconnect
                    while ((_client == null || !_client.Connected) && !cancellationToken.IsCancellationRequested)
                    {
                        try
                        {
                            // Clean up existing connection
                            _stream?.Close();
                            _client?.Close();

                            _client = new TcpClient();
                            await _client.ConnectAsync("127.0.0.1", 7338);
                            _stream = _client.GetStream();

                            // Send username to identify the user
                            string username = Environment.UserName;
                            await Local_Server_Send_Message($"username${username}");
                            Console.WriteLine("Connected to the local server.");

                            // Start listening for messages (only if not already running)
                            if (_messageHandlerTask == null || _messageHandlerTask.IsCompleted)
                            {
                                _messageHandlerTask = Local_Server_Handle_Server_Messages(cancellationToken);
                            }
                            
                            _isConnecting = false;
                            break;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Reconnect attempt failed: {ex.Message}");
                            await Task.Delay(5000, cancellationToken); // Wait before retrying
                        }
                    }
                }
            }

            // Wait for a while before the next check
            await Task.Delay(5000, cancellationToken); // Check every 5 seconds
        }

        // Clean up resources if disconnected
        Disconnect();
    }

    public void Disconnect()
    {
        _cancellationTokenSource.Cancel();
        _stream?.Close();
        _client?.Close();
        
        // Restore animations if they were disabled
        if (_remoteSessionActive && OperatingSystem.IsWindows())
        {
            _remoteSessionActive = false;
            AnimationManager.RestoreAnimations();
        }
        
        // Cleanup session idle timer
        _sessionIdleTimer?.Dispose();
        _sessionIdleTimer = null;
        
        // Cleanup Desktop Duplication instances (Windows)
        foreach (var capture in _captureInstances.Values)
        {
            try
            {
                capture?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing capture instance: {ex.Message}");
            }
        }
        _captureInstances.Clear();
        
        // Cleanup Linux screen capture instance
        if (_linuxCaptureInstance != null)
        {
            try
            {
                _linuxCaptureInstance.Dispose();
                _linuxCaptureInstance = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing Linux capture instance: {ex.Message}");
            }
        }
        
        // Cleanup macOS screen capture instance
        if (_macOSCaptureInstance != null)
        {
            try
            {
                _macOSCaptureInstance.Dispose();
                _macOSCaptureInstance = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing macOS capture instance: {ex.Message}");
            }
        }
        
        // Cleanup Linux input resources
        if (OperatingSystem.IsLinux())
        {
            MouseControlLinux.Cleanup();
            KeyboardControlLinux.Cleanup();
        }
        
        // Cleanup macOS input resources
        if (OperatingSystem.IsMacOS())
        {
            MouseControlMacOS.Cleanup();
            KeyboardControlMacOS.Cleanup();
        }
        
        // Cleanup screen capture semaphores
        foreach (var semaphore in _screenCaptureLocks.Values)
        {
            try
            {
                semaphore?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disposing screen lock: {ex.Message}");
            }
        }
        _screenCaptureLocks.Clear();
        _latestScreenshotRequest.Clear();
        
        Console.WriteLine("Disconnected from the server.");
    }

    // Cache Desktop Duplication instances per screen to avoid re-initialization overhead (GPU mode)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DesktopDuplicationApiCapture> _captureInstances = 
        new System.Collections.Concurrent.ConcurrentDictionary<int, DesktopDuplicationApiCapture>();
    
    // Linux screen capture instance (nullable - created on first capture request)
    private ScreenCaptureLinux? _linuxCaptureInstance;
    
    // macOS screen capture instance (nullable - created on first capture request)
    private ScreenCaptureMacOS? _macOSCaptureInstance;
    
    // Semaphore per screen to ensure screenshots are processed sequentially (prevent old frames arriving after new ones)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _screenCaptureLocks = 
        new System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim>();
    
    // Track latest screenshot request per screen (for frame dropping)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Command> _latestScreenshotRequest = 
        new System.Collections.Concurrent.ConcurrentDictionary<int, Command>();

    /// <summary>
    /// Sequential wrapper for screenshot capture to prevent out-of-order frame delivery
    /// WITH FRAME DROPPING: Discards old requests if newer ones are waiting
    /// </summary>
    private async Task CaptureAndSendScreenshotSequential(Command command)
    {
        int screenIndex = Convert.ToInt32(command.remote_control_screen_index);
        
        // Update the latest request for this screen
        _latestScreenshotRequest.AddOrUpdate(screenIndex, command, (key, oldValue) => command);
        
        // Get or create semaphore for this screen
        var screenLock = _screenCaptureLocks.GetOrAdd(screenIndex, _ => new SemaphoreSlim(1, 1));
        
        // Try to acquire lock without waiting - if busy, skip this frame
        if (!screenLock.Wait(0))
        {
            Console.WriteLine($"[Screen {screenIndex}] Dropping frame - previous capture still in progress");
            return; // Drop this frame, it's outdated
        }
        
        try
        {
            // Double-check: Is this still the latest request?
            if (_latestScreenshotRequest.TryGetValue(screenIndex, out var latestRequest) && 
                latestRequest.response_id != command.response_id)
            {
                Console.WriteLine($"[Screen {screenIndex}] Dropping outdated frame (newer request arrived)");
                return; // Newer request has arrived, drop this one
            }
            
            await CaptureAndSendScreenshot(command);
        }
        finally
        {
            screenLock.Release();
        }
    }

    private async Task CaptureAndSendScreenshot(Command command)
    {
        try
        {
            byte[] imageBytes = null;
            
            if (OperatingSystem.IsWindows())
            {
                int screenIndex = Convert.ToInt32(command.remote_control_screen_index);
                
                // Determine render mode from command: 0 = CPU, 1 = GPU (default to GPU if not specified)
                bool useGpu = command.remote_control_render_mode != 0;
                
                if (useGpu)
                {
                    // GPU-accelerated Desktop Duplication API
                    var capture = _captureInstances.GetOrAdd(screenIndex, idx =>
                    {
                        var (adapterIndex, outputIndex) = DesktopDuplicationApiCapture.FindAdapterForScreen(idx);
                        var newCapture = new DesktopDuplicationApiCapture(adapterIndex, outputIndex);
                        newCapture.Initialize();
                        Console.WriteLine($"Created Desktop Duplication capture instance for screen {idx} (Adapter {adapterIndex}, Output {outputIndex})");
                        return newCapture;
                    });
                    
                    imageBytes = await capture.CaptureScreenToBytes(screenIndex);
                }
                else
                {
                    // CPU-based capture (OldScreenCapture)
                    imageBytes = await OldScreenCapture.CaptureScreenToBytes(screenIndex);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                int screenIndex = Convert.ToInt32(command.remote_control_screen_index);
                
                // Get or create Linux screen capture instance
                if (_linuxCaptureInstance == null)
                {
                    _linuxCaptureInstance = new ScreenCaptureLinux(screenIndex);
                    _linuxCaptureInstance.Initialize();
                    Console.WriteLine($"Created Linux screen capture instance for screen {screenIndex}");
                }
                
                // Capture screen
                imageBytes = await _linuxCaptureInstance.CaptureScreenToBytes(screenIndex);
            }
            else if (OperatingSystem.IsMacOS())
            {
                int screenIndex = Convert.ToInt32(command.remote_control_screen_index);
                
                // Get or create macOS screen capture instance
                if (_macOSCaptureInstance == null)
                {
                    _macOSCaptureInstance = new ScreenCaptureMacOS(screenIndex);
                    _macOSCaptureInstance.Initialize();
                    Console.WriteLine($"Created macOS screen capture instance for screen {screenIndex}");
                }
                
                // Capture screen
                imageBytes = await _macOSCaptureInstance.CaptureScreenToBytes(screenIndex);
            }

            if (imageBytes != null && imageBytes.Length > 0)
            {
                await Local_Server_Send_Binary_Message("screen_capture", command.response_id, imageBytes);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Screenshot-Fehler: {ex.Message}");
        }
    }
}

class Program
{
    static bool IsAlreadyRunningForCurrentUser()
    {
        try
        {
            string currentProcessName = Process.GetCurrentProcess().ProcessName;
            string currentUsername = Environment.UserName;
            int currentProcessId = Process.GetCurrentProcess().Id;

            // Get all processes with the same name
            Process[] processes = Process.GetProcessesByName(currentProcessName);

            foreach (Process process in processes)
            {
                // Skip the current process
                if (process.Id == currentProcessId)
                    continue;

                try
                {
                    // Try to get the username of the process owner
                    string processUsername = GetProcessOwner(process.Id);
                    
                    if (!string.IsNullOrEmpty(processUsername) && 
                        processUsername.Equals(currentUsername, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"Process is already running for user '{currentUsername}' (PID: {process.Id})");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not check process {process.Id}: {ex.Message}");
                }
            }
            
            Console.WriteLine("No existing process found for the current user.");

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking for existing process: {ex.Message}");
            return false;
        }
    }

    static string GetProcessOwner(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string query = $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}";
                using (var searcher = new System.Management.ManagementObjectSearcher(query))
                {
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        string[] argList = new string[] { string.Empty, string.Empty };
                        int returnVal = Convert.ToInt32(obj.InvokeMethod("GetOwner", argList));
                        if (returnVal == 0)
                        {
                            return argList[0]; // Username
                        }
                    }
                }
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // On Linux/macOS, use ps command to get process owner
                var psi = new ProcessStartInfo
                {
                    FileName = "ps",
                    Arguments = $"-o user= -p {processId}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process != null)
                    {
                        string output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit();
                        return output;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting process owner for PID {processId}: {ex.Message}");
        }

        return string.Empty;
    }

    static async Task Main(string[] args)
    {
        try
        {
            Console.WriteLine("Starting User Process...");

            // Windows-specific initialization
            if (OperatingSystem.IsWindows())
            {
                // Check if this process was started via elevation (old process needs time to exit)
                bool isElevatedStart = args.Contains("--elevated");
            
                if (isElevatedStart)
                {
                    Console.WriteLine("Process started via elevation. Waiting for old process to exit...");
                
                    // Wait for the old process to exit with retries
                    int maxRetries = 25;
                    int retryDelayMs = 500;
                
                    for (int i = 0; i < maxRetries; i++)
                    {
                        await Task.Delay(retryDelayMs);
                    
                        if (!IsAlreadyRunningForCurrentUser())
                        {
                            Console.WriteLine($"Old process exited after {(i + 1) * retryDelayMs}ms. Continuing...");
                            break;
                        }
                    
                        if (i == maxRetries - 1)
                        {
                            Console.WriteLine("Warning: Old process may still be running, but continuing anyway...");
                        }
                        else
                        {
                            Console.WriteLine($"Old process still running, retry {i + 1}/{maxRetries}...");
                        }
                    }
                }
                else
                {
                    // Normal start - check if the process is already running for the current user
                    if (IsAlreadyRunningForCurrentUser())
                    {
                        Console.WriteLine("Process is already running for the current user. Exiting...");
                        return;
                    }
                }
                
                Dpi.SetProcessDpiAwareness(Dpi.ProcessDpiAwareness.Process_Per_Monitor_DPI_Aware);

                // Start session monitoring for seamless transition between login screen and user session
                SessionManager.StartSessionMonitoring(1000);
                
                // Subscribe to session change events
                SessionManager.OnSessionChanged += (oldSession, newSession) =>
                {
                    Console.WriteLine($"Session transition detected: {oldSession} -> {newSession}");
                };
            }
            
            // Linux-specific initialization
            if (OperatingSystem.IsLinux())
            {
                Console.WriteLine("Running on Linux - initializing Linux screen capture and input...");
                // Linux initialization is done lazily when first capture/input is requested
            }
            
            // macOS-specific initialization
            if (OperatingSystem.IsMacOS())
            {
                Console.WriteLine("Running on macOS - initializing macOS screen capture and input...");
                // Initialize keyboard control (checks permissions)
                KeyboardControlMacOS.Initialize();
            }

            var client = new UserClient();
            await client.Local_Server_Connect();

            // Keep the application running until termination is requested.
            await Task.Delay(-1); // Block forever (or until you quit the app)
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
        }
        finally
        {
            // Clean up session monitoring (Windows only)
            if (OperatingSystem.IsWindows())
            {
                SessionManager.StopSessionMonitoring();
            }
        }
    }
}