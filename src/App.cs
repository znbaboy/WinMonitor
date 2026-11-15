using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinMonitor.Core;
using WinMonitor.UI;

namespace WinMonitor
{
    /// <summary>The WinUI application (built in code, no XAML files).</summary>
    public sealed class App : Application, IXamlMetadataProvider
    {
        // XAML type info: WinUI controls, plus the Community Toolkit settings cards (Windows 11 Settings look)
        private readonly IXamlMetadataProvider[] _providers =
        {
            new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider(),
            new CommunityToolkit.WinUI.Controls.SettingsControlsRns.CommunityToolkit_WinUI_Controls_SettingsControls_XamlTypeInfo.XamlMetaDataProvider(),
        };
        private AppHost? _host;

        public App()
        {
            UnhandledException += (_, e) => { Program.Log(e.Exception); e.Handled = true; };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // The control styles (about 10 MB) load only when a normal window first opens: the panel does not need them.
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;   // the panel can be hidden; the app keeps running
            _host = new AppHost(Settings.Load());
        }

        private static bool _controls;

        /// <summary>Loads the WinUI control styles (once), before the first settings-type window is built.</summary>
        public static void EnsureControlResources()
        {
            if (_controls) return;
            _controls = true;
            Current.Resources.MergedDictionaries.Add(new XamlControlsResources());
        }

        public IXamlType GetXamlType(Type type)
        {
            foreach (var p in _providers) { var t = p.GetXamlType(type); if (t != null) return t; }
            return null!;
        }

        public IXamlType GetXamlType(string fullName)
        {
            foreach (var p in _providers) { var t = p.GetXamlType(fullName); if (t != null) return t; }
            return null!;
        }

        public XmlnsDefinition[] GetXmlnsDefinitions() => _providers.SelectMany(p => p.GetXmlnsDefinitions()).ToArray();
    }
}
