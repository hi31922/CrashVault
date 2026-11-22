using System;
using System.Globalization;

namespace CrashVault
{
    internal static class EventGuidance
    {
        private const int Low = 1;
        private const int Medium = 2;
        private const int High = 3;

        public static void Apply(VaultEvent entry)
        {
            if (SetKnownGuidance(entry))
                return;

            string source = string.IsNullOrWhiteSpace(entry.Provider)
                ? "Windows"
                : entry.Provider.Trim();
            string id = entry.EventId.ToString(CultureInfo.InvariantCulture);

            switch (entry.WindowsLevel)
            {
                case 1:
                    Set(entry, High, "Critical event from " + source,
                        "The source marked event " + id + " as critical. The Windows message may identify the affected component.",
                        "Save open work. If the PC is unstable, back up important files.",
                        "Check the Windows details and nearby entries for a matching symptom or a repeated failure.",
                        "If the problem continues, share the source, event ID, and details with the component's support team.");
                    break;
                case 2:
                    Set(entry, Medium, "Error from " + source,
                        "The source recorded a failed operation. Event " + id + " may affect a specific app, device, or Windows feature.",
                        "Read the Windows details to identify the affected component.",
                        "If you can reproduce the problem, check for updates to that component and note any recent changes.",
                        "Keep the source, event ID, and error code if you need support.");
                    break;
                case 3:
                    Set(entry, Low, "Warning from " + source,
                        "The source recorded a potential or temporary problem as event " + id + ".",
                        "If everything is working, watch for repeats.",
                        "If it happens again with a visible problem, use the Windows details to identify the affected component.");
                    break;
                default:
                    Set(entry, Low, "Event from " + source,
                        "The source recorded event " + id + ".",
                        "Check the Windows details if it happened when you noticed a problem.");
                    break;
            }
        }

        private static bool SetKnownGuidance(VaultEvent entry)
        {
            string provider = (entry.Provider ?? string.Empty).Trim().ToUpperInvariant();
            int id = entry.EventId;

            switch (provider)
            {
                case "MICROSOFT-WINDOWS-DISTRIBUTEDCOM":
                case "DISTRIBUTEDCOM":
                    if (id == 10016)
                        return Set(entry, Low, "Component permissions check",
                            "Windows recorded a permission check while starting a component. This event is often expected.",
                            "If apps and Windows features are working, no action is needed.",
                            "If an app fails at the same time, check for an app update and share the event details with its publisher.");
                    if (id == 10010)
                        return Set(entry, Medium, "Component took too long to start",
                            "A Windows or app component missed its registration deadline. Its parent app or feature may have opened slowly or failed.",
                            "Open the affected app or feature again.",
                            "If it keeps failing, check for app or Windows updates and save the component ID from Windows details.");
                    break;
                case "MICROSOFT-WINDOWS-KERNEL-POWER":
                    if (id == 41)
                        return UnexpectedRestart(entry);
                    break;
                case "EVENTLOG":
                    if (id == 6008)
                        return UnexpectedRestart(entry);
                    break;
                case "MICROSOFT-WINDOWS-WER-SYSTEMERRORREPORTING":
                case "WER-SYSTEMERRORREPORTING":
                case "BUGCHECK":
                    if (id == 1001 && string.Equals(entry.Channel, "System", StringComparison.OrdinalIgnoreCase))
                        return Set(entry, High, "Windows restarted after a stop error",
                            "Windows recorded a system crash and restarted. The event may include a stop code and the location of a memory dump.",
                            "Save the stop code and check for driver, hardware, or software changes around the first crash.",
                            "Install available Windows and device updates. If a new driver preceded the crashes, consider rolling back that driver in Device Manager.",
                            "Back up important files if the crashes repeat and share the stop code and dump location with support.");
                    break;
                case "APPLICATION ERROR":
                    if (id == 1000)
                        return AppProblem(entry, "An app crashed", "Windows closed an app after it encountered an error. The Windows details may name the app and the module that failed.");
                    break;
                case "APPLICATION HANG":
                case "APPHANG":
                    if (id == 1002)
                        return AppProblem(entry, "An app stopped responding", "Windows recorded an app that stopped responding. The Windows details may identify the app.");
                    break;
                case ".NET RUNTIME":
                    if (id == 1026)
                        return AppProblem(entry, "An app stopped after a .NET error", "A .NET app stopped after an exception it could not handle.");
                    break;
                case "DISPLAY":
                    if (id == 4101)
                        return Set(entry, Medium, "Graphics driver recovered",
                            "Windows reset the graphics driver after it stopped responding. The screen may have flickered or an app may have closed.",
                            "Reopen any affected app.",
                            "If it repeats, check for a graphics driver update through Windows Update or the PC maker.",
                            "If the issue began right after a graphics driver update, consider rolling back that driver in Device Manager.");
                    break;
                case "MICROSOFT-WINDOWS-WHEA-LOGGER":
                case "WHEA-LOGGER":
                    if (id == 1 || id == 18 || id == 20 || id == 46)
                        return Set(entry, High, "Uncorrected hardware error",
                            "Windows logged a hardware error it could not correct. The affected component may appear in the details.",
                            "Save your work and back up important files.",
                            "Run the PC manufacturer's hardware diagnostics and record any test codes.",
                            "If the error repeats or the PC freezes, share this event and the diagnostic results with the PC manufacturer.");
                    if (id == 17 || id == 19 || id == 21 || id == 47)
                        return Set(entry, Medium, "Corrected hardware error",
                            "Windows recorded a hardware error that was corrected. Repeated entries for the same component can help identify a stability issue.",
                            "Check the Windows details for the component and watch for repeats.",
                            "If the same error keeps appearing or the PC becomes unstable, run the manufacturer's hardware diagnostics.");
                    break;
                case "DISK":
                    if (id == 7)
                        return Set(entry, High, "Drive reported a bad block",
                            "Windows could not use part of a drive. Important files on that drive could be at risk.",
                            "Back up important files to another drive before running repairs.",
                            "Identify the drive in Windows details and run its manufacturer's health diagnostics.",
                            "If the diagnostics fail or bad blocks keep appearing, arrange to replace the drive.");
                    if (id == 11 || id == 51)
                        return StorageProblem(entry, "Drive communication error",
                            "Windows recorded a problem communicating with a drive or its controller.");
                    if (id == 157)
                        return Set(entry, Medium, "Drive disconnected unexpectedly",
                            "A drive became unavailable without completing the normal removal process.",
                            "If you unplugged an external drive, use Safely Remove Hardware before unplugging it next time.",
                            "If it disconnected on its own, back up its files when it is accessible and check its connection. Run drive diagnostics if it repeats.");
                    if (id == 153)
                        return StorageProblem(entry, "Windows retried a drive operation",
                            "A drive operation did not finish on the first attempt. Windows retried it.");
                    break;
                case "NTFS":
                case "MICROSOFT-WINDOWS-NTFS":
                    if (id == 55)
                        return Set(entry, High, "File system corruption detected",
                            "Windows found damaged file system information on a drive. Files on that drive could be at risk.",
                            "Back up accessible files before attempting repairs.",
                            "Identify the affected drive in Windows details. Open its Properties, then Tools, then Error checking.",
                            "If Windows keeps finding errors, run the drive manufacturer's diagnostics or contact support.");
                    break;
                case "STORAHCI":
                case "STORNVME":
                case "STORPORT":
                case "MICROSOFT-WINDOWS-STORPORT":
                case "IASTORA":
                case "IASTORAC":
                case "IASTORAVC":
                case "IASTORVD":
                    if (id == 129)
                        return StorageProblem(entry, "Storage controller reset",
                            "A storage request timed out, so Windows attempted to reset the affected connection.");
                    break;
                case "MICROSOFT-WINDOWS-KERNEL-PNP":
                case "KERNEL-PNP":
                    if (id == 219 && (entry.Detail ?? string.Empty).IndexOf("WudfRd", StringComparison.OrdinalIgnoreCase) >= 0)
                        return Set(entry, Low, "Windows may retry loading a device driver",
                            "A device driver could not load on the first attempt. Windows can retry after its driver framework starts.",
                            "If the device works normally, no action is needed.",
                            "If the warning repeats or the device fails, check it in Device Manager and look for a driver update.");
                    if (id == 219 || id == 411)
                        return DeviceProblem(entry, "Device driver could not start",
                            "Windows recorded a problem starting a device or loading its driver. The details identify the device.");
                    break;
                case "MICROSOFT-WINDOWS-DRIVERFRAMEWORKS-USERMODE":
                case "DRIVERFRAMEWORKS-USERMODE":
                    if (id == 10110 || id == 10111)
                        return DeviceProblem(entry, "Device driver stopped responding",
                            "A user-mode driver failed. One or more devices may be temporarily unavailable.");
                    break;
                case "MICROSOFT-WINDOWS-DNS-CLIENT":
                case "DNS CLIENT EVENTS":
                    if (id == 1014)
                        return Set(entry, Low, "Network name lookup timed out",
                            "Windows did not get a reply when it tried to look up a network name. A website or app may have been slow to connect.",
                            "If everything works now, watch for repeats.",
                            "If several sites fail, reconnect to Wi-Fi or Ethernet and check whether another device on the same network can reach them.");
                    break;
                case "MICROSOFT-WINDOWS-WLAN-AUTOCONFIG":
                    if (id == 10002)
                        return Set(entry, Low, "Wireless add-on stopped",
                            "A module used by the Windows wireless service stopped. Wi-Fi may continue to work normally.",
                            "If you lost Wi-Fi, reconnect to your network.",
                            "If Wi-Fi keeps dropping, check for a wireless driver update from the PC maker.");
                    break;
                case "MICROSOFT-WINDOWS-TIME-SERVICE":
                    if (id == 134)
                        return Set(entry, Low, "Windows could not reach its time source",
                            "The time service could not resolve the name of its time server and will retry.",
                            "If the clock is wrong, check your connection and open Settings > Time & language > Date & time to sync again.",
                            "For a work or school PC with repeated failures, contact its administrator.");
                    break;
                case "SERVICE CONTROL MANAGER":
                    switch (id)
                    {
                        case 7000:
                        case 7001:
                        case 7009:
                        case 7011:
                        case 7023:
                        case 7024:
                        case 7026:
                        case 7031:
                        case 7034:
                            return Set(entry, Medium, "A service or driver failed",
                                "A background service or driver failed to start, stopped, or took too long to respond. Windows details name what was affected.",
                                "Check whether the related app, device, or Windows feature still works.",
                                "If the issue repeats, install updates for the affected component and keep the service name and error code for support.");
                    }
                    break;
                case "MICROSOFT-WINDOWS-TPM-WMI":
                case "TPM-WMI":
                    if (id == 1795 || id == 1796)
                        return Set(entry, Medium, "Secure Boot update did not apply",
                            "Windows recorded an error while trying to update Secure Boot settings in the firmware.",
                            "Install pending Windows updates and restart normally.",
                            "If the same error recurs, check Windows Security and the PC maker's Secure Boot or firmware guidance. Keep the error code from the details.");
                    if (id == 1801)
                        return Set(entry, Medium, "Secure Boot certificates need attention",
                            "Windows reported that newer Secure Boot certificates still need to be applied to the device firmware.",
                            "Install pending Windows updates and restart normally.",
                            "If the event continues, check Windows Security and look up the PC manufacturer's Secure Boot certificate guidance.");
                    if (id == 1802 || id == 1803)
                        return Set(entry, Medium, "Secure Boot update needs firmware support",
                            "Windows recorded a platform limitation or a missing manufacturer-signed key while updating Secure Boot.",
                            "Check Windows Security for the certificate update status.",
                            "Look up the PC model in the manufacturer's Secure Boot guidance before changing firmware settings.");
                    break;
                case "MICROSOFT-WINDOWS-WINDOWSUPDATECLIENT":
                    if (id == 20)
                        return Set(entry, Medium, "Windows update failed to install",
                            "Windows could not install an update. The details may include its name and error code.",
                            "Open Settings > Windows Update, restart if prompted, then check for updates again.",
                            "If the same update still fails, use the Windows Update troubleshooter and note the error code.");
                    break;
                case "MICROSOFT-WINDOWS-KERNEL-PROCESSOR-POWER":
                    if (id == 37)
                        return Set(entry, Low, "Firmware limited CPU speed",
                            "The PC's firmware temporarily capped processor speed. A power policy or hardware condition can cause this.",
                            "If performance is normal, watch for repeats.",
                            "If the PC repeatedly slows down, check the manufacturer's power settings and diagnostics.");
                    break;
                case "MICROSOFT-WINDOWS-RESOURCE-EXHAUSTION-DETECTOR":
                    if (id == 2004)
                        return Set(entry, Medium, "Windows was running low on memory",
                            "Windows detected low virtual memory and recorded which apps were using the most.",
                            "Save work and close unused apps.",
                            "Check Task Manager for an app whose memory use keeps growing, then check for updates to it if the problem repeats.");
                    break;
                case "MICROSOFT-WINDOWS-WMI":
                    if (id == 63)
                        return Set(entry, Low, "Management component registered",
                            "A Windows Management Instrumentation provider registered to run under the LocalSystem account.",
                            "Check the provider in Windows details if you are investigating a recent software change.",
                            "If it belongs to a managed work PC and you cannot identify it, ask its administrator.");
                    break;
            }

            return false;
        }

        private static bool UnexpectedRestart(VaultEvent entry)
        {
            return Set(entry, High, "Unexpected shutdown or restart",
                "Windows started again after an unclean shutdown. A crash, freeze, or loss of power can all produce this event.",
                "Check the nearby events for a stop error, device failure, or other clue from before the shutdown.",
                "If it repeats, back up important files, check the power connection, and run the PC manufacturer's diagnostics.");
        }

        private static bool AppProblem(VaultEvent entry, string title, string explanation)
        {
            return Set(entry, Medium, title, explanation,
                "Reopen the app and check for updates if it fails again.",
                "If the same action keeps triggering it, note the action and share the app name and Windows details with its publisher.");
        }

        private static bool StorageProblem(VaultEvent entry, string title, string explanation)
        {
            return Set(entry, Medium, title, explanation,
                "If the warning keeps appearing or the drive disconnects or slows down, back up important files.",
                "Check the connection for an external drive and run the drive manufacturer's diagnostics for repeated failures.");
        }

        private static bool DeviceProblem(VaultEvent entry, string title, string explanation)
        {
            return Set(entry, Medium, title, explanation,
                "Check the affected device in Device Manager. For an external device, reconnect it and try again.",
                "If the device still fails, check Windows Update or the PC maker for its driver. If the issue began after a driver update, consider rolling it back.");
        }

        private static bool Set(VaultEvent entry, int severity, string title, string explanation, params string[] steps)
        {
            entry.Severity = severity;
            entry.Title = title;
            entry.Explanation = explanation;
            entry.Steps = string.Join("\n", steps);
            return true;
        }
    }
}
