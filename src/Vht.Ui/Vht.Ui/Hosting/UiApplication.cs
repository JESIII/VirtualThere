using System;
using System.Windows;

namespace Vht.Ui.Hosting;

/// <summary>
/// The WPF application both role executables run. Creating it merges the shared theme, so a
/// window can be constructed with the styles it references already in
/// <see cref="Application.Resources"/>.
/// </summary>
/// <remarks>
/// This type exists because a WPF *library* cannot own an <c>ApplicationDefinition</c>: the
/// generated <c>Main</c> only exists for an Exe/WinExe project, and the entry point now belongs
/// to the role executable (<see cref="UiHost.Run"/>). The resources that used to live in
/// <c>App.xaml</c> are in <c>Themes/AppResources.xaml</c> and are merged here.
/// </remarks>
internal sealed class UiApplication : Application
{
    internal UiApplication()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Vht.Ui;component/Themes/AppResources.xaml", UriKind.Absolute),
        });
    }
}
