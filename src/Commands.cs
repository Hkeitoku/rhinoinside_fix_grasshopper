using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RhinoInside.GHFix;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class OpenGrasshopper : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            if (App.IsBusy)
            {
                TaskDialog.Show("GHFix", "Rhino / Grasshopperの起動を処理中です。複数回押さずに状態を確認してください。");
                return Result.Cancelled;
            }
            if (RhinoBridge.IsRhinoReady())
            {
                Log.Write("GH_BUTTON: Rhino ready; opening Grasshopper");
                if (RhinoBridge.TryOpenGrasshopper(out message))
                    return Result.Succeeded; // Editor visibility is checked after dispatch.
                Log.Write("GH_BUTTON_CANCELLED: " + message);
                TaskDialog.Show("GHFix", message);
                return Result.Cancelled; // Never show a Revit fatal-command error for a recoverable condition.
            }
            if (App.BeginBootstrap(data.Application, out message))
            {
                Log.Write("GH_BUTTON: official Rhino bootstrap queued");
                return Result.Succeeded;
            }
            Log.Write("GH_BUTTON: bootstrap unavailable: " + message);
            TaskDialog.Show("GHFix", message);
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Log.Write("GH_BUTTON_ERROR " + ex);
            message = "GHFix起動処理で例外が発生しました。ログ: " + Log.FileName;
            // Do not promote a recoverable GHFix exception to Revit's fatal command dialog.
            TaskDialog.Show("GHFix", message);
            return Result.Cancelled;
        }
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class Diagnostics : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        try
        {
            var api = typeof(UIApplication).Assembly.GetName().Version;
            var rhino = RhinoBridge.GetRhinoVersionDescription();
            var rir = AppDomain.CurrentDomain.GetAssemblies()
              .FirstOrDefault(a => (a.GetName().Name ?? "").StartsWith("RhinoInside.Revit", StringComparison.OrdinalIgnoreCase));
            var txt = $"GHFix 0.13.0-beta\nRevit API: {api}\n.NET Runtime: {Environment.Version}\nRhino: {rhino}\nRhino.Inside loaded: {(rir == null ? "not detected" : rir.GetName().Name + " " + rir.GetName().Version)}\nRhino ready: {RhinoBridge.IsRhinoReady()}\nGrasshopper editor: {RhinoBridge.GetGrasshopperEditorStateDescription()}\nLaunch pending: {App.IsBusy}\n\nLog: {Log.FileName}\n\n補足: GHエディタが未生成ならRhinoの _Grasshopper コマンドを実行します。起動完了は別途確認します。";
            Log.Write("DIAGNOSTICS " + txt.Replace('\n', ';'));
            TaskDialog.Show("GHFix 環境診断", txt);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Log.Write("DIAG_ERROR " + ex);
            message = ex.Message;
            return Result.Failed;
        }
    }
}

internal static class RhinoBridge
{
    private static Assembly? RhinoAssembly => AppDomain.CurrentDomain.GetAssemblies()
       .FirstOrDefault(a => string.Equals(a.GetName().Name, "RhinoCommon", StringComparison.OrdinalIgnoreCase));

    public static string GetRhinoVersionDescription()
    {
        var asm = RhinoAssembly;
        if (asm != null) return "RhinoCommon " + asm.GetName().Version + " (loaded)";
        // No hardcoded Rhino 8/9 DLL paths: Rhino.Inside chooses the Rhino engine itself.
        return "not loaded (Rhino.Inside selects engine at runtime)";
    }

    public static bool IsRhinoReady()
    {
        try
        {
            var asm = RhinoAssembly;
            if (asm == null) return false;
            var docType = asm.GetType("Rhino.RhinoDoc", false);
            var active = docType?.GetProperty("ActiveDoc", BindingFlags.Public | BindingFlags.Static);
            // Failure to inspect state means fail closed rather than loading another Rhino version.
            return active?.GetValue(null) != null;
        }
        catch (Exception ex)
        {
            Log.Write("RHINO_READY_CHECK " + ex.Message);
            return false;
        }
    }

    public static object? GetEditorWithCanvas(out bool canvasVisible)
    {
        canvasVisible = false;
        try
        {
            var instances = GetGrasshopperInstancesType();
            var canvas = instances?.GetProperty("ActiveCanvas", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (canvas == null) return null;

            var editor = GetExistingEditor();
            if (editor == null || HasFlag(editor, "IsDisposed") || HasFlag(canvas, "IsDisposed"))
                return null;

            var windowState = editor.GetType().GetProperty("WindowState")?.GetValue(editor)?.ToString();
            canvasVisible = windowState != "Minimized" && HasFlag(editor, "Visible") &&
                HasFlag(canvas, "Visible") && HasFlag(canvas, "IsHandleCreated");
            return editor;
        }
        catch
        {
            // Grasshopper can be creating the editor while Revit pumps messages.
            return null;
        }
    }

    internal static void HideStartupBanner()
    {
        try
        {
            var bannerType = GetGrasshopperInstancesType()?.Assembly.GetType("Grasshopper.GUI.GH_Banner");
            var hide = bannerType?.GetMethod("HideBanner", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (hide == null)
            {
                Log.Write("GH_BANNER_API_UNAVAILABLE");
                return;
            }

            var banner = System.Windows.Forms.Application.OpenForms
                .OfType<System.Windows.Forms.Form>()
                .FirstOrDefault(form => form.GetType() == bannerType && form.Visible);
            if (banner == null)
            {
                Log.Write("GH_BANNER_NOT_VISIBLE");
                return;
            }

            void Hide()
            {
                try
                {
                    if (banner.IsDisposed || !banner.Visible) return;
                    hide.Invoke(banner, null);
                    Log.Write("GH_BANNER_HIDE_REQUESTED visible=" + banner.Visible);
                }
                catch (Exception ex)
                {
                    Log.Write("GH_BANNER_HIDE_ERROR " + ex);
                }
            }

            if (banner.InvokeRequired)
                banner.BeginInvoke((Action)Hide);
            else
                Hide();
        }
        catch (Exception ex)
        {
            Log.Write("GH_BANNER_HIDE_ERROR " + ex);
        }
    }

    private static bool HasFlag(object control, string property)
        => control.GetType().GetProperty(property)?.GetValue(control) is true;

    public static string GetGrasshopperEditorStateDescription()
    {
        var gh = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
            string.Equals(a.GetName().Name, "Grasshopper", StringComparison.OrdinalIgnoreCase));
        if (gh == null) return "Grasshopper assembly not loaded";
        try
        {
            var editor = GetExistingEditor();
            if (editor == null) return "Grasshopper assembly loaded; editor not created";
            var editorWithCanvas = GetEditorWithCanvas(out var canvasVisible);
            if (canvasVisible) return "Canvas visible";
            return editorWithCanvas == null ? "Editor exists; waiting for canvas" : "Canvas exists; not visible";
        }
        catch (Exception ex) { return "Editor status unavailable: " + ex.GetType().Name; }
    }

    public static bool TryOpenGrasshopper(out string message)
    {
        message = string.Empty;
        try
        {
            if (!IsRhinoReady())
            {
                message = "Rhinoがまだ初期化されていません。";
                Log.Write("GH_NOT_READY");
                return false;
            }

            var editor = GetEditorWithCanvas(out _);
            if (editor != null)
            {
                if (ShowExistingEditor(editor, out var detail))
                {
                    Log.Write("GH_EDITOR_DIRECT_SHOW " + detail);
                    App.BeginEditorMonitor("existing editor");
                    return true;
                }
                Log.Write("GH_EXISTING_EDITOR_SHOW_FAILED " + detail);
            }

            var rhinoApp = RhinoAssembly!.GetType("Rhino.RhinoApp", throwOnError: true)!;
            var runScript = rhinoApp.GetMethod(
                "RunScript", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(bool) }, null)
                ?? throw new MissingMethodException(rhinoApp.FullName, "RunScript(string, bool)");

            // Rhino may start loading the editor before the call returns.
            // Keep the result in the log and judge success by editor visibility.
            try
            {
                var result = runScript.Invoke(null, new object[] { "_Grasshopper ", true });
                Log.Write("GH_COMMAND_RETURNED _Grasshopper result=" + result);
            }
            catch (TargetInvocationException ex)
            {
                Log.Write("GH_COMMAND_EXCEPTION " + (ex.InnerException ?? ex));
            }
            App.BeginEditorMonitor("Rhino _Grasshopper");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("GH_COMMAND_EXCEPTION " + (ex is TargetInvocationException ? ex.InnerException ?? ex : ex));
        }

        message = "Grasshopperの起動を要求できませんでした。Rhinoのコマンド欄から _Grasshopper を実行してください。\nログ: " + Log.FileName;
        return false;
    }

    /// <summary>
    /// Get already instantiated GH editor without creating a new editor or
    /// loading another Grasshopper.dll in the Revit process.
    /// </summary>
    private static object? GetExistingEditor()
    {
        var property = GetGrasshopperInstancesType()?.GetProperty(
            "DocumentEditor", BindingFlags.Public | BindingFlags.Static);
        return property?.GetValue(null);
    }

    private static Type? GetGrasshopperInstancesType()
        => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
            string.Equals(a.GetName().Name, "Grasshopper", StringComparison.OrdinalIgnoreCase))
            ?.GetType("Grasshopper.Instances", false);

    internal static bool ShowExistingEditor(object editor, out string detail)
    {
        detail = string.Empty;
        try
        {
            var type = editor.GetType();
            var disposed = type.GetProperty("IsDisposed", BindingFlags.Public | BindingFlags.Instance);
            if (disposed?.GetValue(editor) is bool isDisposed && isDisposed)
            {
                detail = "Form is already disposed";
                return false;
            }
            void ShowForm()
            {
                    var visible = type.GetProperty("Visible", BindingFlags.Public | BindingFlags.Instance);
                    if (!(visible?.GetValue(editor) is bool isVisible && isVisible))
                    {
                        var show = type.GetMethod("Show", BindingFlags.Public | BindingFlags.Instance,
                            null, Type.EmptyTypes, null);
                        if (show == null) throw new MissingMethodException(type.FullName, "Show()");
                        show.Invoke(editor, Array.Empty<object>());
                    }
                    var state = type.GetProperty("WindowState", BindingFlags.Public | BindingFlags.Instance);
                    if (state != null && state.GetValue(editor)?.ToString() == "Minimized")
                        state.SetValue(editor, Enum.Parse(state.PropertyType, "Normal"));
                    type.GetMethod("BringToFront", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null)?.Invoke(editor, Array.Empty<object>());
                    type.GetMethod("Activate", BindingFlags.Public | BindingFlags.Instance,
                        null, Type.EmptyTypes, null)?.Invoke(editor, Array.Empty<object>());
            }

            // Revit command runs on the UI thread normally. If an editor was
            // created on a separate thread, marshal to its control safely.
            var invokeRequired = type.GetProperty("InvokeRequired", BindingFlags.Public | BindingFlags.Instance);
            if (invokeRequired?.GetValue(editor) is bool needsInvoke && needsInvoke)
            {
                var beginInvoke = type.GetMethod("BeginInvoke", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(Delegate), typeof(object[]) }, null);
                if (beginInvoke == null) { detail = "BeginInvoke unavailable"; return false; }
                Action showOnEditorThread = () =>
                {
                    try { ShowForm(); }
                    catch (Exception ex)
                    {
                        Log.Write("GH_DIRECT_SHOW_CALLBACK_ERROR " + (ex is TargetInvocationException ? ex.InnerException ?? ex : ex));
                    }
                };
                beginInvoke.Invoke(editor, new object[] { showOnEditorThread, Array.Empty<object>() });
                detail = "marshaled to GH form UI thread";
            }
            else
            {
                ShowForm();
                detail = "Form.Show/Activate called on Revit UI thread";
            }
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.ToString();
            return false;
        }
    }
}
