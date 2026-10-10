using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using LandingStats.App.Models;
using LandingStats.App.Settings;

namespace LandingStats.App.Controls;

/// <summary>Native WPF debrief scene. Only recorded positions move the aircraft.</summary>
public sealed class LandingReplayView : Grid
{
    private static readonly Brush Muted = Brush("#8A837B");
    private static readonly Brush Ink = Brush("#EAE5DE");
    private readonly Viewport3D _viewport = new Viewport3D { IsHitTestVisible = false, ClipToBounds = true };
    private readonly PerspectiveCamera _camera = new PerspectiveCamera { FieldOfView = 50, NearPlaneDistance = .5, FarPlaneDistance = 100000 };
    private readonly ModelVisual3D _scene = new ModelVisual3D();
    private Grid _footer = null!;
    private readonly Model3DGroup _aircraft = new Model3DGroup();
    private readonly QuaternionRotation3D _rotation = new QuaternionRotation3D();
    private readonly ScaleTransform3D _poseVisibility = new ScaleTransform3D();
    private readonly TranslateTransform3D _translation = new TranslateTransform3D();
    private readonly ReplayMetricsOverlay _metrics = new ReplayMetricsOverlay();
    private readonly ReplayControlOverlay _controls = new ReplayControlOverlay();
    private readonly ReplayRunwayOverlay _runwayOffset = new ReplayRunwayOverlay();
    private LandingReplayRunwayOffset? _offset;
    private readonly TextBlock _hint = Text(Muted, 10);
    private readonly TextBlock _message = Text(Muted, 13);
    private readonly TextBlock _clock = Text(Ink, 11);
    private readonly Slider _slider = new Slider { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), IsMoveToPointEnabled = true };
    private readonly Button _play = new Button { MinWidth = 62, Height = 26, Padding = new Thickness(8, 0, 8, 0) };
    private readonly Button _reset = new Button { Height = 26, Padding = new Thickness(8, 0, 8, 0) };
    private readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Render);
    private LandingRecord? _record;
    private IReadOnlyList<LandingRecord> _contacts = Array.Empty<LandingRecord>();
    private LandingReplayTrack? _track;
    private LandingReplayPose _pose;
    private bool _hasPose;
    private bool _visualUpdatePending;
    private bool _resetCameraPending;
    private bool _settingSlider;
    private bool _playing;
    private double _time;
    private long _lastTicks;
    private Point? _drag;
    private double _azimuth;
    private double _elevation = 27;
    private double _distance = 150;
    private double _visualOffset;
    private readonly ReplayLandingGear _gear = new ReplayLandingGear();
    private ReplayGearRegistration? _gearRegistration;

    public LandingReplayView()
    {
        Background = Brush("#10100F");
        ClipToBounds = true;
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(42) });
        var canvas = new Grid { Background = Brushes.Transparent, ClipToBounds = true };
        canvas.Children.Add(_viewport); _viewport.Camera = _camera; _viewport.Children.Add(_scene);
        _metrics.Margin = new Thickness(20, 14, 0, 0); _metrics.HorizontalAlignment = HorizontalAlignment.Left; _metrics.VerticalAlignment = VerticalAlignment.Top;
        _controls.Margin = new Thickness(0, 14, 20, 0); _controls.HorizontalAlignment = HorizontalAlignment.Right; _controls.VerticalAlignment = VerticalAlignment.Top;
        canvas.Children.Add(_metrics); canvas.Children.Add(_controls);
        _runwayOffset.HorizontalAlignment = HorizontalAlignment.Center; _runwayOffset.VerticalAlignment = VerticalAlignment.Bottom;
        _runwayOffset.Margin = new Thickness(0, 0, 0, 12); canvas.Children.Add(_runwayOffset);
        _hint.HorizontalAlignment = HorizontalAlignment.Right; _hint.VerticalAlignment = VerticalAlignment.Bottom; _hint.Margin = new Thickness(12);
        _hint.IsHitTestVisible = false; canvas.Children.Add(_hint);
        canvas.SizeChanged += (_, e) => _hint.Visibility = _track != null && e.NewSize.Width >= 1000 ? Visibility.Visible : Visibility.Collapsed;
        _message.TextWrapping = TextWrapping.Wrap; _message.MaxWidth = 480; _message.TextAlignment = TextAlignment.Center;
        _message.HorizontalAlignment = HorizontalAlignment.Center; _message.VerticalAlignment = VerticalAlignment.Center; canvas.Children.Add(_message);
        canvas.MouseLeftButtonDown += (_, e) => { if (_track == null) return; _drag = e.GetPosition(canvas); canvas.CaptureMouse(); e.Handled = true; };
        canvas.MouseLeftButtonUp += (_, e) => { _drag = null; canvas.ReleaseMouseCapture(); e.Handled = true; };
        canvas.LostMouseCapture += (_, __) => _drag = null;
        canvas.MouseMove += (_, e) =>
        {
            if (!_drag.HasValue || e.LeftButton != MouseButtonState.Pressed) return;
            var next = e.GetPosition(canvas); var delta = next - _drag.Value; _drag = next;
            _azimuth -= delta.X * .006; _elevation = Math.Max(5, Math.Min(85, _elevation + delta.Y * .25)); UpdateCamera();
        };
        canvas.MouseWheel += (_, e) =>
        {
            if (_track == null) return;
            _distance = Math.Max(35, Math.Min(20000, _distance * Math.Exp(-e.Delta / 120.0 * .15))); UpdateCamera(); e.Handled = true;
        };
        Children.Add(canvas);
        var footer = new Grid { Margin = new Thickness(20, 0, 12, 0) };
        _footer = footer;
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_play); _clock.VerticalAlignment = VerticalAlignment.Center; _clock.HorizontalAlignment = HorizontalAlignment.Center;
        SetColumn(_clock, 1); footer.Children.Add(_clock); SetColumn(_slider, 2); footer.Children.Add(_slider); SetColumn(_reset, 3); footer.Children.Add(_reset);
        SetRow(footer, 1); Children.Add(footer);
        _play.Click += (_, __) => TogglePlayback();
        _reset.Click += (_, __) => ResetCamera();
        _slider.ValueChanged += (_, __) => { if (_settingSlider) return; StopPlayback(); SetTimeCore(_slider.Value, true); };
        _timer.Interval = TimeSpan.FromMilliseconds(33); _timer.Tick += OnTick;
        Unloaded += (_, __) => StopPlayback();
        IsVisibleChanged += (_, __) =>
        {
            if (!IsVisible) StopPlayback();
            else ApplyPendingTime();
        };
        foreach (var button in new[] { _play, _reset }) { button.Background = Brush("#25231F"); button.Foreground = Ink; button.BorderBrush = Brush("#4A433B"); }
        var transform = new Transform3DGroup(); transform.Children.Add(_poseVisibility); transform.Children.Add(new RotateTransform3D(_rotation)); transform.Children.Add(_translation); _aircraft.Transform = transform;
        _aircraft.Children.Add(ReplayAircraftModel.CreateAirframe()); _aircraft.Children.Add(_gear.Model); RefreshLocalization(); ShowUnavailable();
    }

    public event EventHandler<LandingChartHoverEventArgs>? TimeChanged;
    public double CurrentTimeSeconds => _time;
    public bool IsPlaybackAvailable => _track != null;
    public LandingRecord? Record
    {
        get => _record;
        set => SetRecord(value);
    }

    public void SetRecord(LandingRecord? value, IEnumerable<LandingRecord>? contacts = null)
    {
        StopPlayback(); _record = value; _hasPose = false; _gearRegistration = null; _offset = null;
        _visualUpdatePending = false; _resetCameraPending = true;
        _contacts = value == null ? Array.Empty<LandingRecord>() : LandingReplayContacts.ForEpisode(value, contacts ?? Array.Empty<LandingRecord>());
        if (!LandingReplayTrack.TryCreate(value, out _track)) { _scene.Content = null; ShowUnavailable(); return; }
        LandingReplayRunwayOffset.TryCreate(value!, _track!, out _offset);
        _metrics.SetTrack(_track, _offset?.HeadingTrueDegrees); _controls.SetInputs(ReplayControlInputs.Resolve(value, _contacts));
        _runwayOffset.SetRunway(value!.RunwayGeometry);
        // A generic mesh has no measured datum/gear geometry. Register its
        // rest height ONCE; never clamp individual frames (which erases bounces).
        _visualOffset = ReplayGearRegistration.VisualOffset(_track!);
        _settingSlider = true; _slider.Minimum = _track!.StartSeconds; _slider.Maximum = _track.EndSeconds; _settingSlider = false;
        _slider.IsEnabled = true; _play.IsEnabled = true; _reset.IsEnabled = true; _viewport.Visibility = Visibility.Visible;
        _footer.Visibility = Visibility.Visible;
        _hint.Visibility = _viewport.ActualWidth >= 1000 ? Visibility.Visible : Visibility.Collapsed;
        _metrics.Visibility = _controls.Visibility = _runwayOffset.Visibility = Visibility.Visible;
        BuildScene(); _visualUpdatePending = true;
        SetTimeCore(Math.Max(_track.StartSeconds, Math.Min(_track.EndSeconds, 0)), false); RefreshLocalization();
    }

    public void SetTime(double timeSeconds) => SetTimeCore(timeSeconds, false);

    public void RefreshLocalization()
    {
        _play.Content = LocalizationManager.Text(_playing ? "Replay.Pause" : "Replay.Play");
        _reset.Content = LocalizationManager.Text("Replay.ResetView");
        _hint.Text = LocalizationManager.Text("Replay.Hint");
        _metrics.InvalidateVisual(); _controls.RefreshLocalization(); _runwayOffset.RefreshLocalization();
        if (_track == null) _message.Text = LocalizationManager.Text("Replay.Unavailable");
        else if (!_hasPose) _message.Text = LocalizationManager.Text("Replay.Gap");
        else if (!_visualUpdatePending) UpdateReadouts();
    }

    private void SetTimeCore(double timeSeconds, bool notify)
    {
        if (_track == null || !LandingReplayTrack.Finite(timeSeconds)) return;
        var time = Math.Max(_track.StartSeconds, Math.Min(_track.EndSeconds, timeSeconds));
        if (_time != time) _visualUpdatePending = true;
        _time = time;
        // Chart hover also selects replay time while its tab is collapsed.
        // Retain that selection without running suspension or touching visuals.
        if (!HasHiddenVisualAncestor()) ApplyPendingTime();
        if (notify) TimeChanged?.Invoke(this, new LandingChartHoverEventArgs(_time));
    }

    private bool HasHiddenVisualAncestor()
    {
        if (IsVisible) return false;
        // Detached visible controls are also used by offscreen rendering/tests;
        // only an explicitly hidden/collapsed presentation defers its updates.
        for (DependencyObject? current = this; current != null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement element && element.Visibility != Visibility.Visible) return true;
        return false;
    }

    private void ApplyPendingTime()
    {
        if (!_visualUpdatePending || _track == null) return;
        _visualUpdatePending = false;
        _settingSlider = true; _slider.Value = _time; _settingSlider = false;
        _clock.Text = _time.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + "s";
        _hasPose = _track.TrySample(_time, out _pose);
        _message.Visibility = _hasPose ? Visibility.Collapsed : Visibility.Visible;
        if (_hasPose)
        {
            _rotation.Quaternion = _pose.Orientation;
            _translation.OffsetX = _pose.Position.X; _translation.OffsetY = _pose.Position.Y + _visualOffset; _translation.OffsetZ = _pose.Position.Z;
            _gearRegistration?.Update(_pose);
            if (_resetCameraPending) ResetCamera();
            else UpdateCamera();
        }
        else _message.Text = LocalizationManager.Text("Replay.Gap");
        UpdateReadouts();
        // Hide stale pose across gaps while keeping the full recorded trail.
        _poseVisibility.ScaleX = _poseVisibility.ScaleY = _poseVisibility.ScaleZ = _hasPose ? 1 : 0;
    }

    private void ShowUnavailable()
    {
        _footer.Visibility = _hint.Visibility = Visibility.Collapsed;
        _viewport.Visibility = Visibility.Collapsed; _message.Visibility = Visibility.Visible;
        _message.Text = LocalizationManager.Text("Replay.Unavailable"); _clock.Text = "—";
        _metrics.SetTrack(null); _controls.SetInputs(null);
        _metrics.Visibility = _controls.Visibility = _runwayOffset.Visibility = Visibility.Collapsed;
        _slider.IsEnabled = false; _play.IsEnabled = false; _reset.IsEnabled = false;
    }

    private void UpdateReadouts()
    {
        _metrics.Update(_time, _hasPose ? _pose : (LandingReplayPose?)null);
        _controls.Update(_time, _hasPose);
        double? meters = _hasPose && _offset != null && _offset.TryGetMeters(_pose, out var value) ? value : (double?)null;
        _runwayOffset.Update(meters, _record?.RunwayGeometry?.WidthMeters ?? 0);
    }

    private void TogglePlayback()
    {
        if (_track == null) return;
        if (_playing) { StopPlayback(); return; }
        if (_time >= _track.EndSeconds) SetTimeCore(_track.StartSeconds, true);
        _playing = true; _lastTicks = Stopwatch.GetTimestamp(); _timer.Start(); RefreshLocalization();
    }

    internal void PausePlayback() => StopPlayback();
    private void StopPlayback() { _playing = false; _timer.Stop(); _play.Content = LocalizationManager.Text("Replay.Play"); }
    private void OnTick(object? sender, EventArgs e)
    {
        if (!_playing || _track == null || !IsVisible) { StopPlayback(); return; }
        var ticks = Stopwatch.GetTimestamp(); var elapsed = (ticks - _lastTicks) / (double)Stopwatch.Frequency; _lastTicks = ticks;
        SetTimeCore(_time + elapsed, true); if (_time >= _track.EndSeconds) StopPlayback();
    }

    private void ResetCamera()
    {
        if (!_hasPose) return;
        _resetCameraPending = false;
        _distance = 150; _elevation = 27;
        var offset = LandingReplayTrack.FrontLeftCameraOffset(_pose.HeadingDegrees, _distance, _elevation);
        _azimuth = Math.Atan2(offset.X, offset.Z); UpdateCamera();
    }

    private void UpdateCamera()
    {
        if (!_hasPose) return;
        var target = _pose.Position + new Vector3D(0, _visualOffset, 0); var elevation = _elevation * Math.PI / 180;
        var offset = new Vector3D(Math.Sin(_azimuth) * Math.Cos(elevation) * _distance,
            Math.Sin(elevation) * _distance, Math.Cos(_azimuth) * Math.Cos(elevation) * _distance);
        _camera.Position = target + offset; _camera.LookDirection = -offset; _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void BuildScene()
    {
        if (_track == null) return;
        var scene = new Model3DGroup(); scene.Children.Add(new AmbientLight(Color.FromRgb(100, 104, 110)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(220, 218, 207), new Vector3D(-1, -2, -1)));
        var trail = new MeshGeometry3D();
        for (var i = 1; i < _track.Frames.Count; i++)
        {
            var a = _track.Frames[i - 1]; var b = _track.Frames[i];
            if (b.TimeSeconds - a.TimeSeconds > LandingReplayTrack.MaximumInterpolationGapSeconds) continue;
            Ribbon(trail, a.Position + new Vector3D(0, _visualOffset, 0), b.Position + new Vector3D(0, _visualOffset, 0), .28);
        }
        var surface = LandingReplaySurface.Build(_track, _record?.RunwayGeometry);
        _gearRegistration = new ReplayGearRegistration(_gear, _track, surface, _visualOffset,
            contactTimesSeconds: LandingReplayContacts.ContactTimes(_record!, _track, _contacts));
        AddFrozen(scene, surface.Terrain, Material("#191D1C"));
        AddFrozen(scene, surface.Pavement, Material("#36383A"));
        AddFrozen(scene, surface.Markings, Material("#C2BFB5", true));
        foreach (var designation in surface.Designations) scene.Children.Add(designation);
        AddFrozen(scene, trail, Material("#FA7849", true));
        var marker = new MeshGeometry3D();
        foreach (var contact in LandingReplayContacts.GroundPositions(_record!, _track, _contacts))
        {
            var ground = contact; ground.Y = surface.HeightAt(ground.X, ground.Z) ?? ground.Y;
            Ribbon(marker, ground + new Vector3D(-7, .1, 0), ground + new Vector3D(7, .1, 0), .3);
            Ribbon(marker, ground + new Vector3D(0, .1, -7), ground + new Vector3D(0, .1, 7), .3);
        }
        AddFrozen(scene, marker, Material("#E9CA66", true)); scene.Children.Add(_aircraft); _scene.Content = scene;
    }

    private static void Ribbon(MeshGeometry3D mesh, Point3D a, Point3D b, double halfWidth)
    {
        var direction = b - a; if (direction.LengthSquared < 1e-12) return;
        var side = Vector3D.CrossProduct(direction, new Vector3D(0, 1, 0));
        if (side.LengthSquared < 1e-12) side = new Vector3D(1, 0, 0);
        side.Normalize(); side *= halfWidth; Quad(mesh, a - side, a + side, b + side, b - side);
    }
    private static void Quad(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c, Point3D d) { Triangle(mesh, a, b, c); Triangle(mesh, a, c, d); }
    private static void Triangle(MeshGeometry3D mesh, Point3D a, Point3D b, Point3D c)
    { var index = mesh.Positions.Count; mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.TriangleIndices.Add(index); mesh.TriangleIndices.Add(index + 1); mesh.TriangleIndices.Add(index + 2); }
    private static void AddFrozen(Model3DGroup scene, MeshGeometry3D mesh, Material material, bool freezeModel = true)
    {
        if (mesh.Positions.Count == 0) return;
        mesh.Freeze(); var model = new GeometryModel3D(mesh, material) { BackMaterial = material }; if (freezeModel) model.Freeze(); scene.Children.Add(model);
    }
    private static TextBlock Text(Brush foreground, double size) => new TextBlock { Foreground = foreground, FontSize = size, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Margin = new Thickness(0, 0, 0, 4) };
    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private static Material Material(string color, bool emissive = false)
    {
        Material material = new DiffuseMaterial(Brush(color));
        // An emissive-only WPF material has no opaque diffuse surface and can
        // disappear behind a subsequently rendered runway even when above it.
        if (emissive) { var group = new MaterialGroup(); group.Children.Add(new DiffuseMaterial(Brush("#000000"))); group.Children.Add(new EmissiveMaterial(Brush(color))); material = group; }
        material.Freeze(); return material;
    }
}
