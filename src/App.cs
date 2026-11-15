using System;
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
        private readonly Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider _provider = new();
        private AppHost? _host;

        public App()
        {
            UnhandledException += (_, e) => { Program.Log(e.Exception); e.Handled = true; };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;   // the panel can be hidden; the app keeps running
            _host = new AppHost(Settings.Load());
        }

        public IXamlType GetXamlType(Type type) => _provider.GetXamlType(type);
        public IXamlType GetXamlType(string fullName) => _provider.GetXamlType(fullName);
        public XmlnsDefinition[] GetXmlnsDefinitions() => _provider.GetXmlnsDefinitions();
    }
}
