CrashVault

CrashVault is a Windows desktop app for reviewing crashes, warnings, and system errors. It collects events from the Windows Application and System logs and other enabled diagnostic channels that it can read.
Use

    Open CrashVault.exe to start the viewer and its background collector.
    Search by event, source, or event ID, or filter by priority.
    Select an event to see its Windows details, an explanation, and suggested troubleshooting steps. Use Copy details to copy the selected event.

The collector starts with up to seven days of available history for each newly tracked log and checks for new events in the background. Closing the viewer leaves the collector running. CrashVault registers the collector to start when the current Windows user signs in.

Event history is stored locally in %LOCALAPPDATA%\CrashVault\Events.sqlite3.
Build on Windows

CrashVault is written in C# with Windows Forms and targets .NET Framework 4.8. It uses the SQLite library included with Windows.

From the folder containing Build.ps1, run:

.\Build.ps1

The script uses the Windows .NET Framework C# compiler and writes CrashVault.exe to the parent folder by default. Use -OutputPath to choose a different destination.
License

CrashVault is available under the MIT License.
