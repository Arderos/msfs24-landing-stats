using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandingStats.App.Presentation;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class WorkerStatusUiRegressionTests
{
    public static void RunAll()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var resources = Review089UiRegressionTests.LoadApplicationResources();
        var context = SynchronizationContext.Current; var mode = Application.Current.ShutdownMode;
        var language = LocalizationManager.EffectiveLanguage;
        Application.Current.Resources.MergedDictionaries.Add(resources);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        MainWindow? window = null;
        try
        {
            window = new MainWindow(); // Never Show: this test starts no runtime or real settings IO.
            // Construction applies the default (system) language. Select the
            // fixture language afterwards so the host locale cannot change it.
            LocalizationManager.Apply("ru");
            var names = new[] { "Simulator", "Controllers", "Analysis", "Recording", "Upload", "Cloud", "Api" };
            var statuses = names.Select((name, i) => new WorkerHealthItem(name,
                i == 1 ? WorkerIndicator.Failed : i == 2 ? WorkerIndicator.Recovering : WorkerIndicator.Healthy,
                i == 1 ? "Component owner did not answer its probe." : "", "", i == 1)).ToArray();
            window.RenderWorkerHealth(statuses);
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(1180, 760)); root.Arrange(new Rect(0, 0, 1180, 760)); root.UpdateLayout();
            var header = (StackPanel)window.FindName("WorkerStatusItems");
            Check(header.Children.Count == 7 && header.DesiredSize.Width <= header.ActualWidth, "Worker header clips at minimum width.");
            var overlay = (Grid)window.FindName("WorkerWarningOverlay");
            var text = ((TextBlock)window.FindName("WorkerWarningText")).Text;
            Check(overlay.Visibility == Visibility.Visible && text.Contains("перестал отвечать") && text.Contains("запись посадки продолжаются") && text.Contains("Последствия:"),
                "Warning omitted the actual cause or recording impact.");
            var link = FindLink(overlay);
            Check(link != null && new TextRange(link.ContentStart, link.ContentEnd).Text == "support@landingstats.app", "Support email is not a hyperlink.");
            var output = Environment.GetEnvironmentVariable("LANDINGSTATS_WORKER_UI_EVIDENCE");
            if (!string.IsNullOrEmpty(output))
            {
                var bitmap = new RenderTargetBitmap(1180, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(output); encoder.Save(stream);
            }
            window.RenderWorkerHealth(statuses.Select(item => new WorkerHealthItem(item.Key, item.Indicator, item.Detail, item.Reason, false)).ToArray());
            Check(overlay.Visibility == Visibility.Collapsed, "Acknowledged warning still covers the interface.");
            Check(header.Children.Count == 7, "Acknowledgement removed worker status.");
            LocalizationManager.Apply("en");
            window.RenderWorkerHealth(statuses);
            text = ((TextBlock)window.FindName("WorkerWarningText")).Text;
            Check(text.Contains("Impact:") && !text.Contains("Workers."), "English warning is missing translations.");
        }
        finally
        {
            if (window != null)
            {
                window.Closing -= (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosing", flags)!);
                window.Closed -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window, typeof(MainWindow).GetMethod("OnWindowClosed", flags)!);
                window.Close();
            }
            LocalizationManager.Apply(language); Application.Current.Resources.MergedDictionaries.Remove(resources);
            Application.Current.ShutdownMode = mode; SynchronizationContext.SetSynchronizationContext(context);
        }
    }
    private static Hyperlink? FindLink(DependencyObject root)
    {
        if (root is TextBlock text) { var link = text.Inlines.OfType<Hyperlink>().FirstOrDefault(); if (link != null) return link; }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var link = FindLink(VisualTreeHelper.GetChild(root, i)); if (link != null) return link; }
        return null;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
