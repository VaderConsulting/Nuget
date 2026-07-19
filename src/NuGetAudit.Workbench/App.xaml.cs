using System.Windows;
using NuGetAudit.Core;

namespace NuGetAudit.Workbench;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs StartupArgs)
    {
        MsBuildHostBootstrap.TryRegister();
        base.OnStartup(StartupArgs);
    }
}
