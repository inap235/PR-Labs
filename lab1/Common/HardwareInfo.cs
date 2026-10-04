using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Labs.Lab1.Common;

/// <summary>Prints the hardware and runtime facts the report needs.</summary>
public static class HardwareInfo
{
    public static void Print()
    {
        Console.WriteLine("== Runtime ==");
        Console.WriteLine($"OS:                 {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Runtime:            {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"Language:           C# (compiled by the .NET {Environment.Version.Major} SDK)");
        Console.WriteLine($"Logical processors: {Environment.ProcessorCount}");
        Console.WriteLine($"Memory visible:     {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F1} GB");
        Console.WriteLine($"GC:                 {(GCSettings.IsServerGC ? "server" : "workstation")}, latency mode {GCSettings.LatencyMode}");
        Console.WriteLine($"Stopwatch:          high resolution = {Stopwatch.IsHighResolution}, {Stopwatch.Frequency:N0} ticks/s");

        if (!OperatingSystem.IsWindows())
            return;

        Console.WriteLine();
        Console.WriteLine("== CPU ==");
        PowerShell("Get-CimInstance Win32_Processor | ForEach-Object { " +
                   "\"Model:              $($_.Name.Trim())\"; " +
                   "\"Physical cores:     $($_.NumberOfCores)\"; " +
                   "\"Logical processors: $($_.NumberOfLogicalProcessors)\"; " +
                   "\"Max clock:          $($_.MaxClockSpeed) MHz\" }");
        Console.WriteLine();
        Console.WriteLine("== RAM ==");
        PowerShell("Get-CimInstance Win32_PhysicalMemory | ForEach-Object { " +
                   "\"$([math]::Round($_.Capacity/1GB)) GB  $($_.ConfiguredClockSpeed) MT/s  $($_.Manufacturer)\" }");
        Console.WriteLine();
        Console.WriteLine("== Disks (a USB drive shows BusType USB) ==");
        PowerShell("Get-PhysicalDisk | ForEach-Object { " +
                   "\"$($_.FriendlyName)  type=$($_.MediaType)  bus=$($_.BusType)  size=$([math]::Round($_.Size/1GB)) GB\" }");
        Console.WriteLine();
        Console.WriteLine("== USB controllers (the USB version of the port) ==");
        PowerShell("Get-CimInstance Win32_USBController | ForEach-Object { $_.Name }");
    }

    private static void PowerShell(string command)
    {
        try
        {
            var start = new ProcessStartInfo("powershell", ["-NoProfile", "-Command", command])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start)!;
            Console.Write(process.StandardOutput.ReadToEnd());
            process.WaitForExit();
        }
        catch (Exception e)
        {
            Console.WriteLine($"(could not query: {e.Message})");
        }
    }
}
