using System.Runtime.InteropServices;
using Rewind.Core;

namespace Rewind.Services;

public static unsafe class DisplayEnumerator
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid guid, out nint factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDescription
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public uint Rotation;
        public nint Monitor;
    }

    private static void Release(nint value)
    {
        if (value != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)value)[2])(value);
    }

    public static IReadOnlyList<CaptureDisplay> GetDisplays()
    {
        var displays = new List<CaptureDisplay>();
        nint factory = 0;
        try
        {
            var guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(guid, out factory));
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint adapterIndex = 0; adapterIndex < 16; adapterIndex++)
            {
                nint adapter = 0;
                if (enumAdapters(factory, adapterIndex, &adapter) < 0) break;
                try
                {
                    var description = new AdapterDescription();
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
                    if (getDescription(adapter, &description) < 0) continue;
                    var enumOutputs = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)adapter)[7];
                    for (uint outputIndex = 0; outputIndex < 16; outputIndex++)
                    {
                        nint output = 0;
                        if (enumOutputs(adapter, outputIndex, &output) < 0) break;
                        try
                        {
                            var outputDescription = new OutputDescription();
                            var getOutput = (delegate* unmanaged[Stdcall]<nint, OutputDescription*, int>)(*(nint**)output)[7];
                            if (getOutput(output, &outputDescription) < 0 || outputDescription.AttachedToDesktop == 0) continue;
                            var name = new string(outputDescription.DeviceName);
                            var bounds = outputDescription;
                            displays.Add(new CaptureDisplay(name, $"Display {displays.Count + 1}", (int)adapterIndex, (int)outputIndex,
                                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                                description.VendorId, bounds.Left == 0 && bounds.Top == 0));
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException) { }
        finally { Release(factory); }
        if (displays.Count > 0) return displays;
        return System.Windows.Forms.Screen.AllScreens.Select((screen, index) =>
            new CaptureDisplay(screen.DeviceName, $"Display {index + 1}", 0, index, screen.Bounds.Left,
                screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height, 0, screen.Primary)).ToArray();
    }
}
