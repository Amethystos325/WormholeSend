using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace WormholeSend;

/// `--selftest`: drives the home window the way OLE drag and drop does,
/// without the mouse. It calls the window's registered IDropTarget with
/// screen points, and checks with WindowFromPoint (which OLE uses to find
/// the target) that the sensor and the halo catch drags while transparent
/// pixels let them through. Prints one line per check; exit code 0 = pass.
static class SelfTest
{
    const uint DROPEFFECT_COPY = OleTarget.DROPEFFECT_COPY;

    public static async Task<int> Run(IslandModel model, IslandWindow window)
    {
        var failures = 0;
        void Check(bool ok, string what)
        {
            Log.Write($"{(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }

        await Task.Delay(800);
        var hwnd = new WindowInteropHelper(window).Handle;
        Native.GetWindowRect(hwnd, out var frame);
        var scale = window.Screen.Scale;
        Native.POINT At(double x, double y) =>
            new() { X = frame.Left + (int)Math.Round(x * scale), Y = frame.Top + (int)Math.Round(y * scale) };
        bool Hits(double x, double y) => Native.GetAncestor(Native.WindowFromPoint(At(x, y)), Native.GA_ROOT) == hwnd;

        Log.Write($"window {frame.Right - frame.Left}x{frame.Bottom - frame.Top} at {frame.Left},{frame.Top}, scale {scale}");
        Check(Hits(280, 3), "idle: the sensor takes the pointer");
        Check(!Hits(280, 30), "idle: below the sensor passes through");
        Check(!Hits(60, 3), "idle: beside the sensor passes through");

        var target = OleTarget.Of(hwnd);
        Check(target is not null, "the window is a registered drop target");
        if (target is null) return 1;
        var files = DemoFiles.Make().Take(1).ToArray();
        using var data = target.Data(files);
        uint Enter(double x, double y) => target.DragEnter(data, At(x, y));
        uint Over(double x, double y) => target.DragOver(At(x, y));
        uint Drop(double x, double y) => target.Drop(data, At(x, y));
        void Leave() => target.DragLeave();

        var effect = Enter(280, 3);
        Check(model.Phase is Phase.Target && effect == DROPEFFECT_COPY, $"enter at the sensor: target, copy ({model.Phase.Describe()}, {effect})");
        await Task.Delay(700);
        Check(Hits(280, 60), "target: the island takes the pointer");
        Check(Hits(85, 60), "target: the halo left of the island takes the pointer");
        Check(!Hits(40, 60), "target: beyond the halo passes through");
        Check(!Hits(280, 200), "target: below the halo passes through");

        // Across the sensor, the halo, the avatar and the strip: WPF
        // raises DragLeave and DragEnter between elements, which must
        // not count as leaving the window.
        foreach (var (x, y) in new[] { (280.0, 6.0), (90.0, 30.0), (160.0, 40.0), (280.0, 60.0) }) Over(x, y);
        await Task.Delay(150);
        Check(model.Phase is Phase.Target, $"moving between elements stays on target ({model.Phase.Describe()})");

        Over(280, 100);
        Check(model.Phase == new Phase.Picker(1), $"down into the strip: the list, middle device hot ({model.Phase.Describe()})");
        Over(470, 100);
        Check(model.Phase == new Phase.Picker(2), $"right: the offline device hot ({model.Phase.Describe()})");
        await Task.Delay(150);

        Leave();
        await Task.Delay(150);
        Check(model.Phase is Phase.Hint or Phase.Idle, $"leaving the window: hint, then idle once no button is down ({model.Phase.Describe()})");
        await Task.Delay(400);
        Check(model.Phase is Phase.Idle, $"drag over: idle ({model.Phase.Describe()})");

        Enter(280, 3);
        effect = Drop(280, 40);
        Check(model.Phase is Phase.Sending && effect == DROPEFFECT_COPY, $"drop on the target: sending, copy ({model.Phase.Describe()}, {effect})");
        Check(model.Transfer?.Files.SequenceEqual(files) == true, "the dropped files are sent");
        await Task.Delay(700);
        Check(Hits(280, 30), "sending: the island takes clicks");

        Enter(280, 3);
        Check(model.Phase is Phase.Sending, "a second drag while sending is ignored");
        Leave();
        await Task.Delay(3200);
        Check(model.Phase is Phase.Done or Phase.Idle, $"the send finishes ({model.Phase.Describe()})");
        await Task.Delay(2500);
        Check(model.Phase is Phase.Idle, $"and the island collapses ({model.Phase.Describe()})");
        Check(Hits(280, 3) && !Hits(280, 30), "idle again: only the sensor takes the pointer");
        Log.Write(failures == 0 ? "PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}

/// A window's registered OLE drop target, called through its COM vtable
/// as DoDragDrop would.
sealed unsafe class OleTarget
{
    public const uint DROPEFFECT_COPY = 1;
    const uint MK_LBUTTON = 1;
    readonly IntPtr target;

    OleTarget(IntPtr target) => this.target = target;

    public static OleTarget? Of(IntPtr hwnd)
    {
        var target = Native.GetProp(hwnd, "OleDropTargetInterface");
        return target == IntPtr.Zero ? null : new OleTarget(target);
    }

    IntPtr Slot(int index) => (*(IntPtr**)target)[index];

    public ComData Data(string[] files) =>
        new(Marshal.GetComInterfaceForObject(new DataObject(DataFormats.FileDrop, files), typeof(ComDataObject)));

    public uint DragEnter(ComData data, Native.POINT point)
    {
        uint effect = DROPEFFECT_COPY;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, Native.POINT, uint*, int>)Slot(3))(target, data.Pointer, MK_LBUTTON, point, &effect);
        return effect;
    }

    public uint DragOver(Native.POINT point)
    {
        uint effect = DROPEFFECT_COPY;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, Native.POINT, uint*, int>)Slot(4))(target, MK_LBUTTON, point, &effect);
        return effect;
    }

    public void DragLeave() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(5))(target);

    public uint Drop(ComData data, Native.POINT point)
    {
        uint effect = DROPEFFECT_COPY;
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, Native.POINT, uint*, int>)Slot(6))(target, data.Pointer, MK_LBUTTON, point, &effect);
        return effect;
    }

    public sealed class ComData(IntPtr pointer) : IDisposable
    {
        public IntPtr Pointer { get; } = pointer;
        public void Dispose() => Marshal.Release(Pointer);
    }
}
