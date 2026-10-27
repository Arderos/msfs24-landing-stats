using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LandingStats.App.Presentation;
using LandingStats.App.Runtime;
using LandingStats.App.Settings;

namespace LandingStats.App;

public partial class MainWindow
{
    private readonly WorkerHealthPresentation _workerHealth = new WorkerHealthPresentation();
    private DispatcherTimer? _workerHealthTimer;
    private string _workerHeaderState = "", _workerWarningState = "";
    private IReadOnlyList<WorkerHealthItem> _workerItems = Array.Empty<WorkerHealthItem>();
    private void StartWorkerHealthPresentation()
    {
        _workerHealthTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _workerHealthTimer.Tick += OnWorkerHealthTick;
        _workerHealthTimer.Start(); RefreshWorkerHealthPresentation();
    }
    private void OnWorkerHealthTick(object? sender, EventArgs args) => RefreshWorkerHealthPresentation();
    private void StopWorkerHealthPresentation()
    {
        if (_workerHealthTimer == null) return;
        _workerHealthTimer.Stop(); _workerHealthTimer.Tick -= OnWorkerHealthTick; _workerHealthTimer = null;
    }
    private void RefreshWorkerHealthPresentation()
    {
        if (_isClosed || WorkerStatusItems == null) return;
        var runtime = Volatile.Read(ref _runtime);
        var health = runtime?.Health ?? Array.Empty<SupervisionSnapshot>();
        _workerItems = _workerHealth.Update(health, Stopwatch.GetTimestamp(), runtime?.CloudEnabled == true, runtime?.IntegrationApiEnabled == true);
        RenderWorkerHealth(_workerItems);
    }
    internal void RenderWorkerHealth(IReadOnlyList<WorkerHealthItem> items)
    {
        var key = LocalizationManager.EffectiveLanguage + string.Join("|", items.Select(item => item.Key + ":" + item.Indicator + ":" + item.Detail + ":" + item.Reason));
        if (key != _workerHeaderState)
        {
            _workerHeaderState = key; WorkerStatusItems.Children.Clear();
            foreach (var item in items)
            {
                var name = LocalizationManager.Text("Workers." + item.Key);
                var state = LocalizationManager.Text("Workers." + item.Indicator);
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0),
                    ToolTip = name + ": " + state + (item.Detail.Length == 0 ? "" : "\n" + item.Detail) };
                AutomationProperties.SetName(panel, name + ": " + state);
                panel.Children.Add(new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center,
                    Fill = (Brush)FindResource(item.Indicator == WorkerIndicator.Healthy ? "SuccessBrush" : item.Indicator == WorkerIndicator.Failed ? "DangerBrush" : "AmberBrush") });
                panel.Children.Add(new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(6, 0, 0, 0), Foreground = (Brush)FindResource("MutedTextBrush") });
                WorkerStatusItems.Children.Add(panel);
            }
        }
        var warnings = items.Where(item => item.Warning).ToArray();
        var warningKey = LocalizationManager.EffectiveLanguage + string.Join("|", warnings.Select(item => item.Key + ":" + item.Detail + ":" + item.Reason));
        if (warningKey != _workerWarningState)
        {
            _workerWarningState = warningKey;
            WorkerWarningText.Text = string.Join("\n\n", warnings.Select(item =>
                LocalizationManager.Text("Workers." + item.Key) + "\n" +
                LocalizationManager.Format("Workers.CauseFormat", WorkerFailureCause(item)) + "\n" +
                LocalizationManager.Format("Workers.ImpactFormat", LocalizationManager.Text("Workers.Impact." + item.Key))));
        }
        WorkerWarningOverlay.Visibility = warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        // No ShowDialog, activation, focus transfer, runtime command or recording gate.
    }
    private static string WorkerFailureCause(WorkerHealthItem item)
    {
        if (item.Reason == "worker_exit_pending") return LocalizationManager.Text("Workers.ExitPending");
        if (item.Detail.Contains("did not answer its probe")) return LocalizationManager.Text("Workers.Unresponsive");
        if (item.Detail.Contains("startup expired")) return LocalizationManager.Text("Workers.StartExpired");
        if (item.Detail.Contains("operation exceeded")) return LocalizationManager.Text("Workers.OperationExpired");
        if (item.Detail.Contains("ended unexpectedly")) return LocalizationManager.Text("Workers.Exited");
        if (item.Detail.Length != 0) return item.Detail;
        return LocalizationManager.Text("Workers.NotSettled");
    }
    private void OnWorkerWarningOk(object sender, RoutedEventArgs args)
    {
        _workerHealth.Acknowledge(_workerItems.Where(item => item.Warning).Select(item => item.Key));
        RefreshWorkerHealthPresentation();
    }
    private void OnWorkerSupportClick(object sender, RoutedEventArgs args)
    {
        try { Process.Start(new ProcessStartInfo("mailto:support@landingstats.app") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception || error is InvalidOperationException)
        { WorkerSupportError.Text = LocalizationManager.Text("Workers.MailUnavailable"); WorkerSupportError.Visibility = Visibility.Visible; }
    }
}
