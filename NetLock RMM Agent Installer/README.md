# NetLock RMM Agent Installer

## Overview

The NetLock RMM Agent Installer is a cross-platform tool for installing, repairing, and uninstalling the NetLock RMM agents.

## Usage

### Basic Commands

```bash
# Clean installation with server configuration
./NetLock_RMM_Agent_Installer clean /path/to/server_config.json

# Repair (fix) with server configuration
./NetLock_RMM_Agent_Installer fix /path/to/server_config.json

# Uninstall
./NetLock_RMM_Agent_Installer uninstall
```

### Optional Parameters

| Parameter | Short Form | Description |
|-----------|------------|-------------|
| `--hidden` | `-h` | Hides the console window (Windows only) |
| `--no-log` / `--nolog` | - | Deletes all installer logs after completion |
| `--temp <path>` | `-t <path>` | Uses a custom temporary directory |

### Examples

#### Standard Installation
```bash
./NetLock_RMM_Agent_Installer clean /home/user/server_config.json
```

#### Installation with Custom Temp Directory
```bash
# Linux/macOS
./NetLock_RMM_Agent_Installer clean /home/user/server_config.json --temp /mnt/custom/temp

# Windows
NetLock_RMM_Agent_Installer.exe clean C:\configs\server_config.json --temp D:\CustomTemp
```

#### Silent Installation (Windows)
```bash
NetLock_RMM_Agent_Installer.exe clean C:\configs\server_config.json --hidden --no-log
```

#### Installation with All Options Combined
```bash
# Linux
sudo ./NetLock_RMM_Agent_Installer clean /etc/netlock/server_config.json --temp /var/tmp/netlock --no-log

# Windows
NetLock_RMM_Agent_Installer.exe clean C:\configs\server_config.json --temp E:\Temp --hidden --no-log
```

## Parameter Details

### `--temp` / `-t`

The `--temp` parameter allows you to specify a custom temporary directory. This is useful when:

- The default temp directory does not have enough disk space
- Specific security policies require a different directory
- The default temp directory is not writable

**Default Temp Directories:**
- **Windows:** `C:\temp`
- **Linux:** `/tmp`
- **macOS:** `/tmp`

**Note:** The specified directory must exist or the installer must have write permissions to create it.

### `--hidden` / `-h`

Hides the console window during installation. Only available on Windows. Useful for automated/silent installations.

### `--no-log` / `--nolog`

Deletes all installer logs after successful installation. Useful for installations where no log files should remain on the system.

## Requirements

- **Administrative privileges required:** The installer must be run with Administrator (Windows) or root privileges (Linux/macOS).
- **Network connection:** Connection to the update and trust servers must be available.

## Supported Platforms

| Operating System | Architecture |
|------------------|--------------|
| Windows | x64, ARM64 |
| Linux | x64, ARM64 |
| macOS | x64, ARM64 |


