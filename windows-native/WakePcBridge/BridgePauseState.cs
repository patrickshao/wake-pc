using Microsoft.Win32;
using System.Diagnostics;
using System.Security;

namespace WakePcBridge;

internal static class BridgePauseState
{
    private const string KeyPath = @"Software\WakePcSleepBridgeRuntime";
    private const string ValueName = "PausedSessionId";

    public static bool IsPausedForCurrentSession()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return IsPausedValueForSession(
                key?.GetValue(ValueName),
                Process.GetCurrentProcess().SessionId);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            SecurityException)
        {
            // Fail open so an unreadable advisory marker cannot disable the bridge.
            return false;
        }
    }

    internal static bool IsPausedValueForSession(
        object? value,
        int currentSessionId) =>
        value is int pausedSessionId && pausedSessionId == currentSessionId;

    public static bool TryPauseForCurrentSession(out string? error)
    {
        try
        {
            using RegistryKey software = Registry.CurrentUser.OpenSubKey(
                "Software",
                writable: true) ?? throw new IOException(
                    "The current-user Software registry key is unavailable.");
            using RegistryKey key = software.CreateSubKey(
                "WakePcSleepBridgeRuntime",
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryOptions.Volatile) ?? throw new IOException(
                    "The bridge runtime registry key could not be created.");
            key.SetValue(
                ValueName,
                Process.GetCurrentProcess().SessionId,
                RegistryValueKind.DWord);
            error = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            SecurityException)
        {
            error = BridgeLog.Redact(exception.Message);
            return false;
        }
    }

    public static void ClearForCurrentSession()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                KeyPath,
                throwOnMissingSubKey: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            SecurityException)
        {
            throw new InvalidOperationException(
                "The current Windows session could not be resumed.",
                exception);
        }
    }
}
