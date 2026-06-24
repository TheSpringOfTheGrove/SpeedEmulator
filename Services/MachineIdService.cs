using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SpeedEmulator.Services;

public interface IMachineIdService
{
    string GetMachineCode();
}

public sealed class MachineIdService : IMachineIdService
{
    private const string OverrideEnvironmentVariable = "SPEEDEMULATOR_MACHINE_CODE_OVERRIDE";
    private readonly string storagePath;
    private string? cachedMachineCode;

    public MachineIdService()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "machine-id.txt");
    }

    public string GetMachineCode()
    {
        var overrideValue = Normalize(Environment.GetEnvironmentVariable(OverrideEnvironmentVariable));
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return overrideValue;
        }

        if (!string.IsNullOrWhiteSpace(cachedMachineCode))
        {
            return cachedMachineCode;
        }

        if (File.Exists(storagePath))
        {
            var persisted = Normalize(File.ReadAllText(storagePath));
            if (!string.IsNullOrWhiteSpace(persisted))
            {
                cachedMachineCode = persisted;
                return cachedMachineCode;
            }
        }

        cachedMachineCode = BuildMachineCode();
        Persist(cachedMachineCode);
        return cachedMachineCode;
    }

    private static string BuildMachineCode()
    {
        var raw = ReadWindowsMachineGuid();
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = $"{Environment.MachineName}|{Environment.UserDomainName}|{Environment.OSVersion.VersionString}";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(hash);
        return $"SE-{hex[..8]}-{hex[8..16]}-{hex[16..24]}-{hex[24..32]}";
    }

    private static string? ReadWindowsMachineGuid()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid",
                null) as string;
        }
        catch
        {
            return null;
        }
    }

    private void Persist(string machineCode)
    {
        var directory = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(storagePath, machineCode, Encoding.UTF8);
    }

    private static string Normalize(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }
}
