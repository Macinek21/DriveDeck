using System;
using DriveDeck.App.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace DriveDeck.App
{
    sealed partial class App : Application
    {
        private bool _suspended;

        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            Resuming += OnResuming;
            UnhandledException += (s, e) =>
            {
                // Keep the dashcam alive: report instead of crashing on unexpected UI errors.
                e.Handled = true;
                AppLog.Error("Unhandled", e.Exception);
                try { AppHub.Notices.Post("crash", Severity.Warning, "Unexpected error", e.Message, TimeSpan.FromMinutes(1)); } catch { }
            };
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                AppHub.Settings = await SettingsStore.LoadAsync();
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                AppLog.Info("App start, version " + v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision);
                frame = new Frame();
                Window.Current.Content = frame;
                SystemNavigationManager.GetForCurrentView().BackRequested += (s, a) =>
                {
                    if (frame.CanGoBack) { a.Handled = true; frame.GoBack(); }
                };
            }
            if (frame.Content == null) frame.Navigate(typeof(MainPage), e.Arguments);
            Window.Current.Activate();
        }

        /// <summary>Windows suspends the app when it leaves the screen: close the current video properly and release the camera.</summary>
        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (MainPage.Current != null) await MainPage.Current.SuspendAsync();
                _suspended = true;
            }
            catch { }
            finally { deferral.Complete(); }
        }

        private async void OnResuming(object sender, object e)
        {
            if (!_suspended || MainPage.Current == null) return;
            _suspended = false;
            await MainPage.Current.ResumeAsync();
        }
    }
}
