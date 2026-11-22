using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CrashVault
{
    internal static class Program
    {
        private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupName = "CrashVault";
        private const string CollectorArgument = "--background";
        private static readonly string CollectorMutexName = @"Global\CrashVault.Collector." +
            WindowsIdentity.GetCurrent().User.Value;

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == CollectorArgument)
            {
                RunCollector();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                using (var store = new VaultStore())
                {
                    string startupIssue = ConfigureStartup();
                    try
                    {
                        if (!IsCollectorRunning())
                        {
                            using (Process worker = Process.Start(new ProcessStartInfo
                            {
                                FileName = Application.ExecutablePath,
                                Arguments = CollectorArgument,
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }))
                            {
                            }
                        }
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        const string startFailure = "Could not launch the background collector.";
                        startupIssue = startupIssue == null ? startFailure : startupIssue + " " + startFailure;
                    }

                    using (var form = new VaultForm(store, startupIssue))
                        Application.Run(form);
                }
            }
            catch (Exception error)
            {
                MessageBox.Show("Could not open the event history.\r\n\r\n" + error.Message,
                                "Event history", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string ConfigureStartup()
        {
            string command = "\"" + Application.ExecutablePath + "\" " + CollectorArgument;
            if (command.Length > 260)
                return "The executable path is too long to start automatically at sign-in.";

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupKey))
                {
                    if (!String.Equals(key.GetValue(StartupName) as string, command, StringComparison.Ordinal))
                        key.SetValue(StartupName, command, RegistryValueKind.String);
                }
                return null;
            }
            catch (Exception error)
            {
                if (!(error is UnauthorizedAccessException) && !(error is System.Security.SecurityException) &&
                    !(error is IOException))
                    throw;
                return "Windows did not allow automatic startup to be registered.";
            }
        }

        internal static bool IsCollectorRunning()
        {
            Mutex collector;
            if (!Mutex.TryOpenExisting(CollectorMutexName, out collector))
                return false;
            collector.Dispose();
            return true;
        }

        private static void RunCollector()
        {
            bool ownsMutex;
            using (var collector = new Mutex(true, CollectorMutexName, out ownsMutex))
            {
                if (!ownsMutex)
                    return;
                try
                {
                    while (true)
                    {
                        try
                        {
                            using (var store = new VaultStore())
                                new EventCollector(store).Run();
                        }
                        catch (IOException)
                        {
                            // The data folder may temporarily be unavailable.
                            Thread.Sleep(TimeSpan.FromSeconds(30));
                        }
                        catch (UnauthorizedAccessException)
                        {
                            Thread.Sleep(TimeSpan.FromSeconds(30));
                        }
                    }
                }
                finally
                {
                    collector.ReleaseMutex();
                }
            }
        }
    }
}
