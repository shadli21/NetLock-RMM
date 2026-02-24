using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;

namespace NetLock_RMM_User_Process.Windows.Helper;


/// <summary>
/// Handles elevation of the current process to administrator privileges using provided credentials.
/// </summary>
public static class ProcessElevation
{
    /// <summary>
    /// Result of an elevation attempt.
    /// </summary>
    public class ElevationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int? NewProcessId { get; set; }
    }

    /// <summary>
    /// Checks if the current process is running with administrator privileges.
    /// </summary>
    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses domain and username from various formats:
    /// - DOMAIN\Username -> (DOMAIN, Username)
    /// - .\Username -> (local machine, Username)
    /// - username@domain.com -> (domain.com, username)
    /// - Username -> (local machine, Username)
    /// </summary>
    private static (string domain, string username) ParseDomainAndUsername(string input)
    {
        if (string.IsNullOrEmpty(input))
            return (Environment.MachineName, input);

        // Format: DOMAIN\Username or .\Username
        if (input.Contains("\\"))
        {
            var parts = input.Split('\\', 2);
            string domain = parts[0];
            string username = parts[1];
            
            // "." means local machine
            if (domain == ".")
            {
                domain = Environment.MachineName;
            }
            
            return (domain, username);
        }
        
        // Format: username@domain.com (UPN)
        if (input.Contains("@"))
        {
            var parts = input.Split('@', 2);
            return (parts[1], parts[0]);
        }
        
        // Just username - use local machine
        return (Environment.MachineName, input);
    }

    /// <summary>
    /// Attempts to restart the current process with administrator privileges using the provided credentials.
    /// </summary>
    /// <param name="username">The username for authentication. Can include domain: DOMAIN\Username, .\Username (local), or username@domain.com</param>
    /// <param name="password">The password for the user</param>
    /// <returns>ElevationResult indicating success or failure with details</returns>
    public static ElevationResult ElevateWithCredentials(string username, string password)
    {
        var result = new ElevationResult();
        Console.WriteLine("[ProcessElevation] Starting ElevateWithCredentials...");

        try
        {
            // Check if already running as admin
            if (IsRunningAsAdmin())
            {
                Console.WriteLine("[ProcessElevation] FAILED: Process is already running with administrator privileges.");
                result.Success = false;
                result.Message = "Process is already running with administrator privileges.";
                return result;
            }

            // Parse domain from username
            var (actualDomain, actualUsername) = ParseDomainAndUsername(username);
            Console.WriteLine($"[ProcessElevation] Parsed credentials - Domain provided: {!string.IsNullOrEmpty(actualDomain)}, Username provided: {!string.IsNullOrEmpty(actualUsername)}");

            // Get the current executable path
            string exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                Console.WriteLine("[ProcessElevation] FAILED: Could not determine the current executable path.");
                result.Success = false;
                result.Message = "Could not determine the current executable path.";
                return result;
            }
            Console.WriteLine($"[ProcessElevation] Executable path: {exePath}");

            // Convert password to SecureString
            var securePassword = new SecureString();
            foreach (char c in password)
            {
                securePassword.AppendChar(c);
            }
            securePassword.MakeReadOnly();

            // Create process start info with credentials
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--elevated", // Signal that this is an elevated start
                UseShellExecute = false, // Required for credentials
                UserName = actualUsername,
                Domain = actualDomain,
                Password = securePassword,
                LoadUserProfile = true,
                CreateNoWindow = false,
                Verb = "runas", // Request elevation (Note: This may not work with UseShellExecute=false)
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath)
            };

            // For running as admin with credentials, we need to use a different approach
            // We'll use "runas" with cmd to achieve elevation
            // Or use CreateProcessWithLogonW with elevated token

            Console.WriteLine($"[ProcessElevation] Attempting to restart process with provided credentials...");

            // Try the standard approach first
            try
            {
                Console.WriteLine("[ProcessElevation] Trying standard Process.Start approach...");
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    Console.WriteLine($"[ProcessElevation] SUCCESS: Process started with PID {process.Id}");
                    result.Success = true;
                    result.NewProcessId = process.Id;
                    result.Message = $"Process successfully started with PID {process.Id}. Current process will now exit.";
                    return result;
                }
                Console.WriteLine("[ProcessElevation] Process.Start returned null.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 740) // ERROR_ELEVATION_REQUIRED
            {
                // Elevation required - need to use a different method
                Console.WriteLine($"[ProcessElevation] Standard approach failed with ERROR_ELEVATION_REQUIRED (740). Trying PowerShell method...");
            }

            // Alternative approach: Use PowerShell to run as admin with credentials
            Console.WriteLine("[ProcessElevation] Falling back to PowerShell elevation method...");
            result = TryElevateWithPowerShell(exePath, actualUsername, password, actualDomain);
            
            if (result.Success)
                Console.WriteLine($"[ProcessElevation] SUCCESS via PowerShell: {result.Message}");
            else
                Console.WriteLine($"[ProcessElevation] FAILED via PowerShell: {result.Message}");
            
            return result;
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine($"[ProcessElevation] FAILED with Win32Exception: Error code {ex.NativeErrorCode}, Message: {ex.Message}");
            result.Success = false;
            result.Message = $"Windows error ({ex.NativeErrorCode}): {ex.Message}";
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProcessElevation] FAILED with Exception: {ex.Message}");
            result.Success = false;
            result.Message = $"Error during elevation: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Uses PowerShell to start a process as admin with credentials.
    /// </summary>
    private static ElevationResult TryElevateWithPowerShell(string exePath, string username, string password, string domain)
    {
        var result = new ElevationResult();
        Console.WriteLine("[ProcessElevation] Starting TryElevateWithPowerShell...");

        try
        {
            // Escape special characters in password for PowerShell
            string escapedPassword = password.Replace("'", "''").Replace("`", "``").Replace("$", "`$");
            string escapedExePath = exePath.Replace("'", "''");

            // Build the PowerShell command
            string fullUsername = string.IsNullOrEmpty(domain) ? username : $"{domain}\\{username}";
            Console.WriteLine($"[ProcessElevation] PowerShell: Credentials prepared");
            
            // Create a PowerShell script that:
            // 1. Creates credentials
            // 2. Starts the process with those credentials using Start-Process -Verb RunAs
            string psScript = $@"
$securePassword = ConvertTo-SecureString '{escapedPassword}' -AsPlainText -Force
$credential = New-Object System.Management.Automation.PSCredential('{fullUsername}', $securePassword)
Start-Process -FilePath '{escapedExePath}' -ArgumentList '--elevated' -Credential $credential -Verb RunAs -PassThru | Select-Object -ExpandProperty Id
";

            Console.WriteLine("[ProcessElevation] Starting PowerShell process...");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var psProcess = Process.Start(psi);
            if (psProcess == null)
            {
                Console.WriteLine("[ProcessElevation] FAILED: PowerShell process could not be started.");
                result.Success = false;
                result.Message = "Failed to start PowerShell process.";
                return result;
            }

            string output = psProcess.StandardOutput.ReadToEnd();
            string error = psProcess.StandardError.ReadToEnd();
            psProcess.WaitForExit(30000); // 30 second timeout
            
            Console.WriteLine($"[ProcessElevation] PowerShell exit code: {psProcess.ExitCode}");
            if (!string.IsNullOrWhiteSpace(output))
                Console.WriteLine($"[ProcessElevation] PowerShell output: {output.Trim()}");
            if (!string.IsNullOrWhiteSpace(error))
                Console.WriteLine($"[ProcessElevation] PowerShell error: {error.Trim()}");

            if (psProcess.ExitCode == 0 && int.TryParse(output.Trim(), out int newPid))
            {
                Console.WriteLine($"[ProcessElevation] SUCCESS: New process started with PID {newPid}");
                result.Success = true;
                result.NewProcessId = newPid;
                result.Message = $"Process successfully started with admin rights (PID: {newPid}). Current process will now exit.";
            }
            else
            {
                Console.WriteLine($"[ProcessElevation] FAILED: PowerShell elevation failed. Exit code: {psProcess.ExitCode}");
                result.Success = false;
                result.Message = $"PowerShell elevation failed. Exit code: {psProcess.ExitCode}. Error: {error}";
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProcessElevation] FAILED with exception: {ex.Message}");
            result.Success = false;
            result.Message = $"PowerShell elevation error: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Attempts elevation using CreateProcessWithLogonW API for more direct control.
    /// This is an alternative to PowerShell-based elevation.
    /// </summary>
    /// <param name="username">The username for authentication. Can include domain: DOMAIN\Username, .\Username (local), or username@domain.com</param>
    /// <param name="password">The password for the user</param>
    /// <returns>ElevationResult indicating success or failure with details</returns>
    public static ElevationResult ElevateWithLogonApi(string username, string password)
    {
        var result = new ElevationResult();
        Console.WriteLine("[ProcessElevation] Starting ElevateWithLogonApi...");

        try
        {
            // Parse domain from username
            var (actualDomain, actualUsername) = ParseDomainAndUsername(username);
            Console.WriteLine($"[ProcessElevation] LogonApi: Credentials parsed - Domain provided: {!string.IsNullOrEmpty(actualDomain)}, Username provided: {!string.IsNullOrEmpty(actualUsername)}");

            string exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                Console.WriteLine("[ProcessElevation] FAILED: Could not determine the current executable path.");
                result.Success = false;
                result.Message = "Could not determine the current executable path.";
                return result;
            }
            Console.WriteLine($"[ProcessElevation] LogonApi: Executable path: {exePath}");

            // Use CreateProcessWithLogonW
            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>()
            };
            
            PROCESS_INFORMATION processInfo;

            string workingDir = System.IO.Path.GetDirectoryName(exePath);
            Console.WriteLine($"[ProcessElevation] LogonApi: Calling CreateProcessWithLogonW...");

            bool success = CreateProcessWithLogonW(
                actualUsername,
                actualDomain,
                password,
                LOGON_WITH_PROFILE,
                null,
                $"\"{exePath}\" --elevated",
                CREATE_NEW_CONSOLE,
                IntPtr.Zero,
                workingDir,
                ref startupInfo,
                out processInfo
            );

            if (success)
            {
                Console.WriteLine($"[ProcessElevation] SUCCESS: Process started with PID {processInfo.dwProcessId}");
                result.Success = true;
                result.NewProcessId = (int)processInfo.dwProcessId;
                result.Message = $"Process started with new credentials (PID: {processInfo.dwProcessId}). Current process will now exit.";

                // Close handles
                CloseHandle(processInfo.hProcess);
                CloseHandle(processInfo.hThread);
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                Console.WriteLine($"[ProcessElevation] FAILED: CreateProcessWithLogonW error code {error}: {new Win32Exception(error).Message}");
                result.Success = false;
                result.Message = $"CreateProcessWithLogonW failed with error code {error}: {new Win32Exception(error).Message}";
            }

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProcessElevation] FAILED with exception: {ex.Message}");
            result.Success = false;
            result.Message = $"LogonApi elevation error: {ex.Message}";
            return result;
        }
    }

    #region Native Methods

    private const int LOGON_WITH_PROFILE = 0x00000001;
    private const int CREATE_NEW_CONSOLE = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithLogonW(
        string lpUsername,
        string lpDomain,
        string lpPassword,
        int dwLogonFlags,
        string lpApplicationName,
        string lpCommandLine,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    #endregion
}


