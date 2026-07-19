using Microsoft.Build.Locator;

namespace NuGetAudit.Core;

/// <summary>
/// Registers an on-disk MSBuild instance so evaluation APIs can load Microsoft.Build (Core references it as compile-only).
/// </summary>
public static class MsBuildHostBootstrap
{
    private static readonly object SyncLock = new();
    private static bool _RegistrationAttempted;

    /// <summary>
    /// Attempts to register the default Visual Studio / Build Tools MSBuild instance once per process.
    /// </summary>
    public static void TryRegister()
    {
        lock (SyncLock)
        {
            if (MSBuildLocator.IsRegistered)
            {
                return;
            }

            if (_RegistrationAttempted)
            {
                return;
            }

            _RegistrationAttempted = true;
            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch (Exception RegistrationFailure)
            {
                DebugLog.WriteTrace("MsBuild", $"MSBuildLocator.RegisterDefaults failed: {RegistrationFailure.Message}");
            }
        }
    }
}
