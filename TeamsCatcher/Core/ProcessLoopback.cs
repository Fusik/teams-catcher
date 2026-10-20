namespace TeamsCatcher.Core;

// WASAPI process loopback: dźwięk tylko z drzewa procesów o danym PID (Windows 10 2004+).
// NAudio 2.2.1 ma tylko struktury, nie ma gotowej klasy, więc minimalny interop COM.
using System.Runtime.InteropServices;

public static class ProcessLoopback
{
    public const int SampleRate = 48000, Channels = 2, Bits = 16;

    // Nagrywa do `onData` aż do anulowania. Zwraca liczbę bajtów i pakietów oznaczonych jako cisza.
    public static async Task<(long bytes, long silentPackets)> CaptureAsync(int pid, Action<byte[], int> onData, CancellationToken ct)
    {
        // aktywacja z wątku MTA: z STA (UI) zwracany obiekt nie daje się rzutować na IAudioClient (E_NOINTERFACE)
        var client = await Task.Run(() => ActivateAsync(pid));
        var fmt = new WaveFormatEx { wFormatTag = 1, nChannels = (ushort)Channels, nSamplesPerSec = SampleRate, wBitsPerSample = (ushort)Bits,
            nBlockAlign = (ushort)(Channels * Bits / 8), nAvgBytesPerSec = SampleRate * Channels * Bits / 8 };
        const uint Loopback = 0x00020000, EventCallback = 0x00040000, AutoConvertPcm = 0x80000000, SrcDefaultQuality = 0x08000000;
        Check(client.Initialize(0, Loopback | EventCallback | AutoConvertPcm | SrcDefaultQuality, 2_000_000, 0, ref fmt, 0));
        using var ready = new AutoResetEvent(false);
        Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
        var iid = typeof(IAudioCaptureClient).GUID;
        Check(client.GetService(ref iid, out var svc));
        var capture = (IAudioCaptureClient)svc;
        Check(client.Start());
        long total = 0, silent = 0;
        var buf = Array.Empty<byte>();
        await Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                ready.WaitOne(100);
                while (capture.GetNextPacketSize(out var frames) == 0 && frames > 0)
                {
                    Check(capture.GetBuffer(out var data, out var n, out var flags, out _, out _));
                    var len = (int)n * fmt.nBlockAlign;
                    if (buf.Length < len) buf = new byte[len];
                    if ((flags & 0x2) != 0) { Array.Clear(buf, 0, len); silent++; } // AUDCLNT_BUFFERFLAGS_SILENT
                    else Marshal.Copy(data, buf, 0, len);
                    Check(capture.ReleaseBuffer(n));
                    onData(buf, len);
                    total += len;
                }
            }
        });
        client.Stop();
        Marshal.ReleaseComObject(capture);
        Marshal.ReleaseComObject(client);
        return (total, silent);
    }

    static Task<IAudioClient> ActivateAsync(int pid)
    {
        var p = new ActivationParams { ActivationType = 1 /* PROCESS_LOOPBACK */, TargetProcessId = (uint)pid, Mode = 0 /* include tree */ };
        var pParams = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        Marshal.StructureToPtr(p, pParams, false);
        var pv = new PropVariantBlob { vt = 65 /* VT_BLOB */, cbSize = (uint)Marshal.SizeOf<ActivationParams>(), pBlobData = pParams };
        var handler = new Handler();
        var iid = typeof(IAudioClient).GUID;
        Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, ref pv, handler, out _));
        return handler.Tcs.Task.ContinueWith(t => { Marshal.FreeHGlobal(pParams); return t.Result; });
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    sealed class Handler : IActivateCompletionHandler, IAgileObject
    {
        public readonly TaskCompletionSource<IAudioClient> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateOperation op)
        {
            op.GetActivateResult(out var hr, out var iface);
            if (hr < 0) Tcs.SetException(Marshal.GetExceptionForHR(hr)!); else Tcs.SetResult((IAudioClient)iface);
        }
    }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync(string path, ref Guid riid, ref PropVariantBlob p, IActivateCompletionHandler handler, out IActivateOperation op);

    [StructLayout(LayoutKind.Sequential)] struct ActivationParams { public int ActivationType; public uint TargetProcessId; public int Mode; }
    [StructLayout(LayoutKind.Explicit)] struct PropVariantBlob { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public uint cbSize; [FieldOffset(16)] public nint pBlobData; }
    [StructLayout(LayoutKind.Sequential, Pack = 2)] struct WaveFormatEx { public ushort wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAgileObject { }
    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateCompletionHandler { void ActivateCompleted(IActivateOperation op); }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormatEx format, nint sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint h);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object svc);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePos, out ulong qpcPos);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
