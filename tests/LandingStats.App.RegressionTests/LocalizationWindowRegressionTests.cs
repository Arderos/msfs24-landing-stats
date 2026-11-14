using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using LandingStats.App.Settings;

namespace LandingStats.App.RegressionTests;

internal static class LocalizationWindowRegressionTests
{
    internal static void RunAll()
    {
        var application = Application.Current;
        var previousMode = application.ShutdownMode;
        var previousLanguage = LocalizationManager.EffectiveLanguage;
        var dictionaries = application.Resources.MergedDictionaries;
        var overrides = new ResourceDictionary { ["Top.Close"] = "Resource owner override" };
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            LocalizationManager.Apply("en");
            dictionaries.Add(overrides);
            var count = dictionaries.Count;
            using (var fixture = new Fixture())
            {
                foreach (var language in new[] { "en", "en", "ru", "ru", "en" })
                {
                    var oldText = fixture.Translated.Text;
                    fixture.ClearObservations();
                    // Same-language application must still restore the caller's
                    // culture; only redundant resource replacement is skipped.
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
                    LocalizationManager.Apply(language);
                    DrainDispatcher();
                    var expectedText = LocalizationManager.Text("Top.Settings");
                    Check(fixture.Translated.Text == expectedText && expectedText.Length != 0,
                        "Changing the language did not update existing DynamicResource text.");
                    Check(fixture.TextChanges.SequenceEqual(oldText == expectedText ? Array.Empty<string>() : new[] { expectedText }),
                        "A localization update exposed an empty or intermediate translated value.");
                    Check(fixture.Window.Title == "Landing Stats" && fixture.NativeTitles.Count == 0 && fixture.VisibilityTransitions == 0,
                        "Localization changed the native title or toggled WindowChrome visibility after first render.");
                    Check(fixture.Override.Text == "Resource owner override" && dictionaries.Count == count && ReferenceEquals(dictionaries.Last(), overrides),
                        "Applying localization moved its owned slot above later resource overrides or duplicated it.");
                    Check(LocalizationManager.EffectiveLanguage == language && CultureInfo.CurrentCulture.Name == LocalizationManager.CultureFor(language).Name &&
                        CultureInfo.CurrentUICulture.Name == LocalizationManager.CultureFor(language).Name &&
                        CultureInfo.DefaultThreadCurrentCulture!.Name == LocalizationManager.CultureFor(language).Name &&
                        CultureInfo.DefaultThreadCurrentUICulture!.Name == LocalizationManager.CultureFor(language).Name,
                        "Skipping redundant resources also skipped localization culture state.");
                    fixture.AssertIsolation();
                }
            }

            // The manager's cached instance can outlive its resource owner.
            // Applying the same language must reattach it if the owner removed
            // it; idempotence cannot rely only on EffectiveLanguage/static state.
            dictionaries.Remove(overrides);
            var active = dictionaries.Single(IsLocaleDictionary);
            dictionaries.Remove(active);
            Check(!application.Resources.Contains("Product.Name"), "The detached-resource fixture still has a product name.");
            LocalizationManager.Apply("en");
            Check(dictionaries.Count(IsLocaleDictionary) == 1 && Equals(application.Resources["Product.Name"], "Landing Stats"),
                "Same-language application did not restore an externally removed dictionary.");
        }
        finally
        {
            dictionaries.Remove(overrides);
            LocalizationManager.Apply(previousLanguage);
            application.ShutdownMode = previousMode;
        }
    }

    private static bool IsLocaleDictionary(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString.IndexOf("/Localization/Strings.", StringComparison.OrdinalIgnoreCase) >= 0;

    private sealed class Fixture : IDisposable
    {
        internal readonly Window Window;
        internal readonly TextBlock Translated = new TextBlock();
        internal readonly TextBlock Override = new TextBlock();
        internal readonly List<string> TextChanges = new List<string>();
        internal readonly List<string> NativeTitles = new List<string>();
        internal int VisibilityTransitions;
        private readonly HwndSource _source;
        private readonly HwndSourceHook _hook;
        private readonly IntPtr _handle;
        private readonly DependencyPropertyDescriptor _textProperty = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        private readonly EventHandler _textChanged;

        internal Fixture()
        {
            Translated.SetResourceReference(TextBlock.TextProperty, "Top.Settings");
            Override.SetResourceReference(TextBlock.TextProperty, "Top.Close");
            var root = new StackPanel { Background = Brushes.Black };
            root.Children.Add(Translated); root.Children.Add(Override);
            Window = new Window
            {
                Width = 640, Height = 360, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.CanResizeWithGrip, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                Background = Brushes.Black, Content = root,
            };
            Window.SetResourceReference(System.Windows.Window.TitleProperty, "Product.Name");
            WindowChrome.SetWindowChrome(Window, new WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
            });
            _handle = new WindowInteropHelper(Window).EnsureHandle();
            _source = HwndSource.FromHwnd(_handle)!;
            SetWindowLong(_handle, -20, GetWindowLong(_handle, -20) | 0x08000000); // WS_EX_NOACTIVATE
            _hook = ObserveAndIsolate; _source.AddHook(_hook);
            var contentRendered = false;
            Window.ContentRendered += (_, __) => contentRendered = true;
            Window.Show(); DrainDispatcher();
            Check(contentRendered, "The localization fixture did not complete its first content render.");
            AssertIsolation();
            _textChanged = (_, __) => TextChanges.Add(Translated.Text);
            _textProperty.AddValueChanged(Translated, _textChanged);
            ClearObservations();
        }

        internal void ClearObservations() { TextChanges.Clear(); NativeTitles.Clear(); VisibilityTransitions = 0; }
        internal void AssertIsolation() => Check(Window.IsVisible && !Window.IsActive && GetForegroundWindow() != _handle,
            "The visible offscreen localization fixture lost isolation.");

        private IntPtr ObserveAndIsolate(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x000C) NativeTitles.Add(Marshal.PtrToStringUni(lParam) ?? ""); // WM_SETTEXT
            if ((message == 0x007C || message == 0x007D) && wParam.ToInt64() == -16) // STYLECHANGING/CHANGED, GWL_STYLE
            {
                var oldStyle = Marshal.ReadInt32(lParam); var newStyle = Marshal.ReadInt32(lParam, 4);
                if (((oldStyle ^ newStyle) & 0x10000000) != 0) VisibilityTransitions++; // WS_VISIBLE
            }
            if (message == 0x0046) // WM_WINDOWPOSCHANGING: keep every native transition offscreen/non-activating.
            {
                var position = Marshal.PtrToStructure<WindowPosition>(lParam);
                position.Flags = (position.Flags | 0x0010u) & ~0x0004u;
                position.InsertAfter = new IntPtr(1);
                if ((position.Flags & 0x0002u) == 0) position.X = position.Y = -10000;
                Marshal.StructureToPtr(position, lParam, false);
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            _textProperty.RemoveValueChanged(Translated, _textChanged);
            _source.RemoveHook(_hook);
            Window.Close();
        }
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", ExactSpelling = true)]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
}
