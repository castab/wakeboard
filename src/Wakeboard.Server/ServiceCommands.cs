using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Wakeboard;

public static class ServiceCommands
{
    public const string ServiceName = "Wakeboard";
    private const string LegacyServiceName = "WakeboardWolHelper";
    private const string FirewallRule = "Wakeboard web interface";

    public static int Execute(string[] args, AppPaths paths)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Wakeboard service commands require Windows.");
        var command = args[0].ToLowerInvariant();
        if ((command is "install" or "update" or "uninstall") && !IsAdministrator())
        {
            RelaunchElevated(args);
            return 0;
        }
        return command switch
        {
            "install" => Install(args, paths),
            "update" => Update(paths),
            "uninstall" => Uninstall(args, paths),
            "status" => Status(paths),
            _ => throw new ArgumentException($"Unknown command '{command}'. Use install, update, status, or uninstall.")
        };
    }

    private static int Install(string[] args, AppPaths paths)
    {
        var port = ReadPort(args, 3001);
        StopAndDeleteService();
        var conflict = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().FirstOrDefault(endpoint => endpoint.Port == port);
        if (conflict is not null) throw new InvalidOperationException($"TCP port {port} is already in use. Choose another with --port <number>.");
        RemoveLegacyHelperService();

        Console.Write("Choose the shared Wakeboard password: ");
        var password = ReadSecret();
        Console.Write("Confirm the shared password: ");
        var confirmation = ReadSecret();
        if (password != confirmation) throw new InvalidOperationException("Passwords do not match.");
        if (password.Length < 8) throw new InvalidOperationException("The shared password must contain at least 8 characters.");

        Directory.CreateDirectory(paths.BinDirectory);
        Directory.CreateDirectory(paths.DataDirectory);
        var currentExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable.");
        if (!Path.GetFullPath(currentExecutable).Equals(Path.GetFullPath(paths.InstalledExecutable), StringComparison.OrdinalIgnoreCase))
            File.Copy(currentExecutable, paths.InstalledExecutable, true);

        SettingsStore.Save(paths, new AppSettings
        {
            Port = port,
            PasswordHash = SettingsStore.HashPassword(password),
            SessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        });
        MigrateExistingData(paths);
        ApplyPermissions(paths);
        RegisterService(paths);
        ConfigureFirewall(port);
        RunChecked("sc.exe", "start", ServiceName);
        Console.WriteLine($"Wakeboard is installed and starting at http://localhost:{port}");
        Console.WriteLine($"Persistent data: {paths.DataDirectory}");
        return 0;
    }

    private static int Update(AppPaths paths)
    {
        if (!File.Exists(paths.SettingsFile) || !File.Exists(paths.InstalledExecutable)) throw new InvalidOperationException("Wakeboard is not installed.");
        var source = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable."));
        if (source.Equals(Path.GetFullPath(paths.InstalledExecutable), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run 'update' from the new Wakeboard.exe outside the installation directory.");
        StopServiceIfPresent();
        File.Copy(source, paths.InstalledExecutable, true);
        ApplyPermissions(paths);
        RunChecked("sc.exe", "start", ServiceName);
        Console.WriteLine("Wakeboard was updated. Settings and saved hosts were preserved.");
        return 0;
    }

    private static int Status(AppPaths paths)
    {
        var result = Run("sc.exe", "query", ServiceName);
        Console.WriteLine(result.Output.Trim());
        if (File.Exists(paths.SettingsFile))
        {
            var settings = SettingsStore.Load(paths);
            Console.WriteLine($"URL: http://localhost:{settings.Port}");
            Console.WriteLine($"Data: {paths.ConfigFile}");
        }
        return result.ExitCode == 0 ? 0 : 1;
    }

    private static int Uninstall(string[] args, AppPaths paths)
    {
        StopAndDeleteService();
        Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        var purge = args.Any(argument => argument.Equals("--purge", StringComparison.OrdinalIgnoreCase));
        var target = purge ? paths.Root : paths.BinDirectory;
        ScheduleDeletion(target);
        Console.WriteLine(purge
            ? "Wakeboard was uninstalled and its data was scheduled for removal."
            : $"Wakeboard was uninstalled. Saved hosts remain in {paths.DataDirectory}");
        return 0;
    }

    private static void MigrateExistingData(AppPaths paths)
    {
        if (File.Exists(paths.ConfigFile)) return;
        var candidate = Path.Combine(Environment.CurrentDirectory, "data", "config.json");
        if (File.Exists(candidate))
        {
            File.Copy(candidate, paths.ConfigFile);
            Console.WriteLine($"Migrated existing hosts from {candidate}");
        }
    }

    private static void ApplyPermissions(AppPaths paths)
    {
        RunChecked("icacls.exe", paths.Root, "/inheritance:r", "/grant:r", "SYSTEM:(OI)(CI)F", "BUILTIN\\Administrators:(OI)(CI)F", "LOCAL SERVICE:(OI)(CI)RX");
        RunChecked("icacls.exe", paths.DataDirectory, "/grant:r", "LOCAL SERVICE:(OI)(CI)M");
    }

    private static void RegisterService(AppPaths paths)
    {
        RunChecked("sc.exe", "create", ServiceName, "binPath=", paths.InstalledExecutable, "start=", "auto", "obj=", "NT AUTHORITY\\LocalService", "DisplayName=", "Wakeboard");
        RunChecked("sc.exe", "description", ServiceName, "Wake-on-LAN dashboard and Windows network service.");
        RunChecked("sc.exe", "failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/30000");
    }

    private static void ConfigureFirewall(int port)
    {
        Run("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRule}");
        RunChecked("netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={FirewallRule}", "dir=in", "action=allow", "protocol=TCP", $"localport={port}", "profile=private");
    }

    private static void StopAndDeleteService()
    {
        if (!ServiceExists()) return;
        StopServiceIfPresent();
        RunChecked("sc.exe", "delete", ServiceName);
        Thread.Sleep(1000);
    }

    private static void RemoveLegacyHelperService()
    {
        if (Run("sc.exe", "query", LegacyServiceName).ExitCode != 0) return;
        Run("sc.exe", "stop", LegacyServiceName);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var query = Run("sc.exe", "query", LegacyServiceName);
            if (query.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) break;
            Thread.Sleep(500);
        }
        RunChecked("sc.exe", "delete", LegacyServiceName);
        Console.WriteLine("Removed the previous Wakeboard WoL helper service.");
    }

    private static void StopServiceIfPresent()
    {
        if (!ServiceExists()) return;
        Run("sc.exe", "stop", ServiceName);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var query = Run("sc.exe", "query", ServiceName);
            if (query.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(500);
        }
    }

    private static bool ServiceExists() => Run("sc.exe", "query", ServiceName).ExitCode == 0;

    private static int ReadPort(string[] args, int fallback)
    {
        var index = Array.FindIndex(args, argument => argument.Equals("--port", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return fallback;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var port) || port is < 1 or > 65535) throw new ArgumentException("--port requires a number from 1 to 65535.");
        return port;
    }

    private static string ReadSecret()
    {
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return builder.ToString(); }
            if (key.Key == ConsoleKey.Backspace && builder.Length > 0) { builder.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) { builder.Append(key.KeyChar); Console.Write('*'); }
        }
    }

    private static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static void RelaunchElevated(string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not request administrator elevation.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Elevated command failed with exit code {process.ExitCode}.");
    }

    private static void ScheduleDeletion(string target)
    {
        if (!Directory.Exists(target)) return;
        var command = $"timeout /t 2 /nobreak >nul & rmdir /s /q \"{target}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c \"{command}\"") { CreateNoWindow = true, UseShellExecute = false });
    }

    private static void RunChecked(string file, params string[] arguments)
    {
        var result = Run(file, arguments);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{file} failed ({result.ExitCode}): {result.Output.Trim()}");
    }

    private static (int ExitCode, string Output) Run(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error);
    }
}
