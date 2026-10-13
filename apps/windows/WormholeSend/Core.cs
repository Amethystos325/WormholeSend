using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace WormholeSend;

/// The Rust core (wormhole.dll, core-ffi/include/wormhole.h). Commands
/// return within milliseconds and may run on the UI thread; events arrive on
/// the core's dispatch thread and are handed to `onEvent` there.
sealed unsafe partial class Core : IDisposable
{
    const string Library = "wormhole";

    IntPtr handle;
    GCHandle self;
    readonly Action<CoreEvent> onEvent;

    Core(Action<CoreEvent> onEvent) => this.onEvent = onEvent;

    public static string Version => Marshal.PtrToStringUTF8(wh_version()) ?? "";

    /// Starts the services. Blocks while ports are bound and, on the first
    /// run, while the certificate is generated: call it off the UI thread.
    /// Returns the core and its settings; throws CoreException.
    public static (Core Core, JsonNode? Settings) Start(JsonObject config, Action<CoreEvent> onEvent)
    {
        var core = new Core(onEvent);
        core.self = GCHandle.Alloc(core);
        IntPtr handle;
        var response = Take(wh_start(config.ToJsonString(), &OnEvent, GCHandle.ToIntPtr(core.self), &handle));
        try
        {
            var result = Unwrap(response);
            core.handle = handle;
            return (core, result);
        }
        catch
        {
            core.self.Free();
            throw;
        }
    }

    /// Runs a command (core-ffi/README.md); returns its result or throws
    /// CoreException with a message for the user.
    public JsonNode? Command(string name, JsonObject? arguments = null)
    {
        if (handle == IntPtr.Zero) throw new CoreException("核心没有启动");
        var command = arguments ?? new JsonObject();
        command["command"] = name;
        return Unwrap(Take(wh_command(handle, command.ToJsonString())));
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        wh_stop(handle);
        handle = IntPtr.Zero;
        self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnEvent(IntPtr ctx, IntPtr json)
    {
        try
        {
            if (GCHandle.FromIntPtr(ctx).Target is not Core core) return;
            var text = Marshal.PtrToStringUTF8(json);
            Log.Write("core " + text);
            if (text is not null && CoreEvent.Parse(text) is { } parsed) core.onEvent(parsed);
        }
        catch (Exception e)
        {
            // Nothing may unwind into the core.
            Log.Write("event handler failed: " + e);
        }
    }

    static string Take(IntPtr pointer)
    {
        try
        {
            return Marshal.PtrToStringUTF8(pointer) ?? "";
        }
        finally
        {
            wh_free_string(pointer);
        }
    }

    static JsonNode? Unwrap(string response)
    {
        var json = JsonNode.Parse(response);
        if (json?["ok"]?.GetValue<bool>() == true) return json["result"];
        throw new CoreException(json?["error"]?.GetValue<string>() ?? "核心没有返回结果");
    }

    [LibraryImport(Library)]
    private static partial IntPtr wh_version();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr wh_start(string config, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> onEvent, IntPtr ctx, IntPtr* core);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr wh_command(IntPtr core, string command);

    [LibraryImport(Library)]
    private static partial void wh_free_string(IntPtr text);

    [LibraryImport(Library)]
    private static partial void wh_stop(IntPtr core);
}

sealed class CoreException(string message) : Exception(message);
