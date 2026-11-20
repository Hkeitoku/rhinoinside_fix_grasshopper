using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using Autodesk.Revit.UI.Events;

namespace RhinoInside.GHFix;

/// <summary>
/// Adds a NEW independent panel to the EXISTING Rhino.Inside tab.
/// Never modifies Rhino.Inside binaries, buttons, Grasshopper plugins, or document data.
/// This is an unverified beta; it deliberately fails closed if Revit rejects late panel creation.
/// </summary>
public sealed class App : IExternalApplication
{
    internal const string TabName = "Rhino.Inside";
    internal const string PanelName = "GHFix";
    private UIControlledApplication? _controlled;
    private int _tries;
    private bool _panelRegistered;
    private bool _panelFailed;
    private static App? _instance;
    private static ExternalEvent? _ghEvent;
    private static readonly GHExternalEventHandler GhEventHandler = new();
    private static bool _pending;
    private static bool _raised;
    private static DateTime _pendingSince;
    private static DispatcherTimer? _editorTimer;
    private static bool _triedShowAfterLoad;
    private static DateTime _monitorStarted;
    private static DateTime _monitorLastLog;
    private static string _monitorRequest = "";

    public Result OnStartup(UIControlledApplication application)
    {
        _instance = this;
        _controlled = application;
        Log.Write("===== GHFix 0.13.0-beta starting =====");
        Log.Write("RevitAPIUI=" + typeof(UIApplication).Assembly.GetName().Version + " runtime=" + Environment.Version);
        try
        {
            _ghEvent = ExternalEvent.Create(GhEventHandler);
            application.Idling += OnIdling;
            // Rhino.Inside may register its tab before or after this add-in. No extra tab is created.
            TryCreatePanel(application, "OnStartup");
        }
        catch (Exception ex)
        {
            Log.Write("STARTUP_ERROR " + ex);
            // Never prevent Revit from opening due to this add-in.
        }
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try { application.Idling -= OnIdling; } catch { }
        try { _ghEvent?.Dispose(); } catch { }
        _ghEvent = null;
        _editorTimer?.Stop();
        _editorTimer = null;
        _instance = null;
        Log.Write("GHFix shutdown");
        return Result.Succeeded;
    }

    private void OnIdling(object? sender, IdlingEventArgs args)
    {
        try
        {
            // Revit API supplies UIApplication as the Idling sender.
            var uiApp = sender as UIApplication;
            if (uiApp == null) return;
            if (!_panelRegistered && !_panelFailed && ++_tries <= 60)
            {
                TryCreatePanel(uiApp, "Idling #" + _tries);
                if (_tries == 60 && !_panelRegistered)
                    Log.Write("RIBBON_TIMEOUT: Rhino.Inside tab was not available; panel intentionally not created elsewhere.");
            }

            if (!_pending || _raised) return;
            if ((DateTime.UtcNow - _pendingSince).TotalSeconds > 30)
            {
                _pending = false;
                Log.Write("LAUNCH_TIMEOUT: Rhino did not initialize within 30 seconds");
                TaskDialog.Show("GHFix", "Rhino の起動を確認できませんでした。Rhino.Inside の Rhino ボタンを押して起動できるか確認し、再度 GHFix を試してください。\n詳細: GHFix.log");
                return;
            }
            if (!RhinoBridge.IsRhinoReady()) return;
            Log.Write("Rhino initialized after official Rhino button. Raising GH external event.");
            if (_ghEvent == null)
            {
                _pending = false;
                Log.Write("LAUNCH_ERROR: ExternalEvent is null");
                return;
            }
            _raised = true;
            var request = _ghEvent.Raise();
            Log.Write("ExternalEvent.Raise result=" + request);
            // Non-accepted requests can be retried during future Idling events.
            if (request != ExternalEventRequest.Accepted) _raised = false;
        }
        catch (Exception ex)
        {
            Log.Write("IDLE_ERROR " + ex);
            _pending = false;
            _raised = false;
        }
    }

    private static void CheckCanvas(object? sender, EventArgs args)
    {
        var editor = RhinoBridge.GetEditorWithCanvas(out var canvasVisible);
        if (canvasVisible)
        {
            Log.Write("GH_CANVAS_VISIBLE: " + _monitorRequest);
            _editorTimer?.Stop();
            RhinoBridge.HideStartupBanner();
            return;
        }

        var elapsed = DateTime.UtcNow - _monitorStarted;
        if (elapsed.TotalSeconds >= 60)
        {
            Log.Write("GH_CANVAS_TIMEOUT: " + RhinoBridge.GetGrasshopperEditorStateDescription());
            _editorTimer?.Stop();
            return;
        }

        // Give the native launch a chance to show its window before restoring
        // an editor that Grasshopper has already created with a canvas.
        if (editor != null && !_triedShowAfterLoad && elapsed.TotalSeconds >= 2)
        {
            _triedShowAfterLoad = true;
            var shown = RhinoBridge.ShowExistingEditor(editor, out var detail);
            Log.Write("GH_CANVAS_RESTORE requested=" + shown + " " + detail);
        }
        if ((DateTime.UtcNow - _monitorLastLog).TotalSeconds >= 10)
        {
            Log.Write("GH_CANVAS_WAITING: " + RhinoBridge.GetGrasshopperEditorStateDescription());
            _monitorLastLog = DateTime.UtcNow;
        }
    }

    internal static void BeginEditorMonitor(string launchRequest)
    {
        if (_editorTimer == null)
        {
            _editorTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _editorTimer.Tick += CheckCanvas;
        }
        _monitorRequest = launchRequest;
        _monitorStarted = DateTime.UtcNow;
        _monitorLastLog = _monitorStarted;
        _triedShowAfterLoad = false;
        _editorTimer.Start();
    }

    private void TryCreatePanel(UIControlledApplication uiApp, string phase)
        => TryCreatePanelCore(() => uiApp.GetRibbonPanels(TabName), () => uiApp.CreateRibbonPanel(TabName, PanelName), phase);

    private void TryCreatePanel(UIApplication uiApp, string phase)
        => TryCreatePanelCore(() => uiApp.GetRibbonPanels(TabName), () => uiApp.CreateRibbonPanel(TabName, PanelName), phase);

    private void TryCreatePanelCore(Func<System.Collections.Generic.List<RibbonPanel>> getPanels, Func<RibbonPanel> createPanel, string phase)
    {
        if (_panelRegistered || _panelFailed) return;
        try
        {
            var panels = getPanels();
            if (panels.Count == 0 || !panels.Any(p => p.GetItems().Any(i => i.Name == "CommandGrasshopper")))
            {
                if (_tries == 0 || _tries == 1 || _tries == 20) Log.Write("WAIT_RHINO_TAB " + phase);
                return;
            }
            var existing = panels.FirstOrDefault(p => string.Equals(p.Name, PanelName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                // Never append duplicate buttons on same app startup.
                _panelRegistered = true;
                Log.Write("PANEL_EXISTS: " + PanelName);
                return;
            }
            var panel = createPanel();
            var dll = Assembly.GetExecutingAssembly().Location;

            var start = new PushButtonData("GHFixStart", "Grasshopper\n安全起動", dll, typeof(OpenGrasshopper).FullName!);
            start.ToolTip = "Rhino.InsideでGrasshopperエディタを開きます。";
            start.LongDescription = "既存エディタを表示します。初回はRhinoの _Grasshopper コマンドを実行します。";
            start.Image = LoadImage("ghfix_16.png");
            start.LargeImage = LoadImage("ghfix_32.png");
            panel.AddItem(start);

            var diag = new PushButtonData("GHFixDiagnose", "環境\n診断", dll, typeof(Diagnostics).FullName!);
            diag.ToolTip = "Revit / Rhino / .NET の環境、起動状態、ログの保存先を表示します。";
            diag.Image = LoadImage("diagnostic_16.png");
            diag.LargeImage = LoadImage("diagnostic_32.png");
            panel.AddItem(diag);

            _panelRegistered = true;
            Log.Write("PANEL_CREATED " + phase + " Rhino.Inside > GHFix (2 icon buttons)");
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex) when (
            ex.Message.IndexOf("Unexpected tab", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (_tries <= 1 || _tries == 20) Log.Write("RIBBON_NOT_READY " + phase);
        }
        catch (Exception ex)
        {
            _panelFailed = true;
            Log.Write("PANEL_FAILED " + phase + " " + ex);
        }
    }

    private static BitmapImage LoadImage(string shortFileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("RhinoInside.GHFix.Images." + shortFileName)
          ?? throw new InvalidOperationException("Missing embedded icon " + shortFileName);
        using (stream)
        {
            var result = new BitmapImage();
            result.BeginInit();
            result.CacheOption = BitmapCacheOption.OnLoad;
            result.StreamSource = stream;
            result.EndInit();
            result.Freeze();
            return result;
        }
    }

    internal static bool IsBusy => _pending || _raised || _editorTimer?.IsEnabled == true;

    internal static bool BeginBootstrap(UIApplication app, out string reason)
    {
        reason = string.Empty;
        if (_instance == null || _ghEvent == null)
        {
            reason = "GHFixが初期化されていません。Revitを再起動してください。";
            return false;
        }
        if (IsBusy)
        {
            reason = "Rhinoの起動を処理中です。";
            return false;
        }
        var candidates = new[] {
          "CustomCtrl_%CustomCtrl_%Rhino.Inside%Rhinoceros%CommandRhino",
          "CustomCtrl_%Rhino.Inside%Rhinoceros%CommandRhino",
          "CustomCtrl_%CustomCtrl_%Rhino.Inside%Rhino%CommandRhino"
        };
        foreach (var candidate in candidates)
        {
            try
            {
                var id = RevitCommandId.LookupCommandId(candidate);
                if (id == null) continue;
                if (!app.CanPostCommand(id))
                {
                    Log.Write("NOT_POSTABLE " + candidate);
                    continue;
                }
                // Revit itself queues the official Rhino command. Never queue the crashing GH command.
                app.PostCommand(id);
                _pending = true;
                _raised = false;
                _pendingSince = DateTime.UtcNow;
                Log.Write("POSTED_OFFICIAL_RHINO_COMMAND " + candidate);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("POST_RHINO_ERROR " + candidate + " " + ex.Message);
            }
        }
        reason = "Rhino.Insideの公式Rhinoコマンドを自動実行できませんでした。まず公式の「Rhino」ボタンで起動し、その後GHFixを押してください。";
        return false;
    }

    internal static void CompleteLaunch()
    {
        _pending = false;
        _raised = false;
    }
}

public sealed class GHExternalEventHandler : IExternalEventHandler
{
    public string GetName() => "GHFix deferred Grasshopper launcher";
    public void Execute(UIApplication app)
    {
        try
        {
            if (!RhinoBridge.TryOpenGrasshopper(out var detail))
            {
                Log.Write("DEFERRED_GH_COMMAND_NOT_DISPATCHED " + detail);
                TaskDialog.Show("GHFix", detail);
            }
        }
        catch (Exception ex) { Log.Write("DEFERRED_GH_ERROR " + ex); }
        finally { App.CompleteLaunch(); }
    }
}

internal static class Log
{
    public static readonly string FileName = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RhinoInside.GHFix", "GHFix.log");
    public static void Write(string s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FileName)!);
            File.AppendAllText(FileName, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + s + Environment.NewLine);
        }
        catch { /* logging cannot crash the host */ }
    }
}
