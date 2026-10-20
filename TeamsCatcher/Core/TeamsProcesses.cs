using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace TeamsCatcher.Core;

// Dźwięk nowego Teams odtwarza proces WebView2 (dziecko ms-teams.exe), a process loopback "z drzewem"
// na ms-teams.exe go nie łapie (sprawdzone na żywo). Dlatego nagrywamy każdy proces z rodziny Teams, który ma sesję audio.
public static class TeamsProcesses
{
    public static List<int> AudioPids(string processName)
    {
        var roots = Process.GetProcessesByName(processName).Select(p => p.Id);
        var family = Descendants(Snapshot(), roots);
        return SessionPids().Where(family.Contains).Order().ToList();
    }

    // Dźwięk prezentacji: udostępnione okno -> procesy tej aplikacji z sesją audio (np. proces audio przeglądarki);
    // cały ekran -> wszystkie procesy z dźwiękiem poza Teams i tą aplikacją.
    public static List<int> SharedAudioPids(CaptureTarget shared, string teamsProcess)
    {
        var snapshot = Snapshot();
        var teams = Process.GetProcessesByName(teamsProcess).Select(p => p.Id);
        return PickSharedAudio(SessionPids(), snapshot, teams, shared.Pid, Environment.ProcessId);
    }

    public static List<int> PickSharedAudio(IEnumerable<int> sessionPids, IReadOnlyList<(int Pid, int Parent)> processes,
        IEnumerable<int> teamsRoots, int sharedWindowPid, int ownPid)
    {
        var teams = Descendants(processes, teamsRoots);
        // przeglądarka: okno należy do procesu głównego, dźwięk gra jego proces potomny
        var app = sharedWindowPid != 0 ? Descendants(processes, [sharedWindowPid]) : null;
        return sessionPids.Where(pid => pid != 0 && pid != ownPid && !teams.Contains(pid) && (app == null || app.Contains(pid)))
            .Distinct().Order().ToList();
    }

    // Korzenie + wszyscy potomkowie (po PID rodzica).
    public static HashSet<int> Descendants(IEnumerable<(int Pid, int Parent)> processes, IEnumerable<int> roots)
    {
        var children = processes.Where(p => p.Pid != p.Parent).ToLookup(p => p.Parent, p => p.Pid);
        var result = new HashSet<int>();
        var queue = new Queue<int>(roots);
        while (queue.TryDequeue(out var pid))
            if (result.Add(pid))
                foreach (var child in children[pid]) queue.Enqueue(child);
        return result;
    }

    // Procesy z sesją audio na którymkolwiek aktywnym urządzeniu odtwarzania.
    static HashSet<int> SessionPids()
    {
        var pids = new HashSet<int>();
        using var devices = new MMDeviceEnumerator();
        foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++) pids.Add((int)sessions[i].GetProcessID);
            }
        }
        return pids;
    }

    static List<(int Pid, int Parent)> Snapshot()
    {
        var result = new List<(int, int)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == -1) return result;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32First(snap, ref entry); ok; ok = Process32Next(snap, ref entry))
                result.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
        }
        finally { CloseHandle(snap); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(nint snap, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(nint snap, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
}
