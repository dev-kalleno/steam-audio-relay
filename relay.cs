// steam-audio-relay
// Steam Remote Play records one playback device. Elgato Wave Link spreads your programs over several
// (Game, Browser, Voice chat...), so most of them never reach the stream. This tool listens to those
// other devices and mixes them into the one Steam records, while Steam is recording it. See README.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr client);
    void UnregisterEndpointNotificationCallback(IntPtr client);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    void OpenPropertyStore(int access, out IntPtr props);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out int state);
}

[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, ref Guid sessionGuid);
    void GetBufferSize(out uint frames);
    void GetStreamLatency(out long latency);
    void GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    void GetMixFormat(out IntPtr format);
    void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    void Start();
    void Stop();
    void Reset();
    void SetEventHandle(IntPtr handle);
    void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    void ReleaseBuffer(uint frames);
    void GetNextPacketSize(out uint frames);
}

[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioRenderClient
{
    void GetBuffer(uint frames, out IntPtr data);
    void ReleaseBuffer(uint frames, uint flags);
}

[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr notify);
    void UnregisterControlChangeNotify(IntPtr notify);
    void GetChannelCount(out uint count);
    void SetMasterVolumeLevel(float db, ref Guid context);
    void SetMasterVolumeLevelScalar(float level, ref Guid context);
    void GetMasterVolumeLevel(out float db);
    void GetMasterVolumeLevelScalar(out float level);
}

[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    void GetAudioSessionControl(ref Guid session, uint flags, out IntPtr control);
    void GetSimpleAudioVolume(ref Guid session, uint flags, out IntPtr volume);
    void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, out IAudioSessionControl2 session);
}

[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
    void GetGroupingParam(out Guid group);
    void SetGroupingParam(ref Guid group, ref Guid context);
    void RegisterAudioSessionNotification(IntPtr notify);
    void UnregisterAudioSessionNotification(IntPtr notify);
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetProcessId(out uint pid);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

class Endpoint
{
    public string Guid;   // e.g. {471386ae-d671-4b9b-8249-349f336c17b3}
    public string Name;   // e.g. System
    public string Device; // e.g. Elgato Wave XLR Pro
    public string FullId { get { return "{0.0.0.00000000}." + Guid; } }
    public override string ToString() { return Name + " (" + Device + ")"; }
}

static class Relay
{
    const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
    const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 2;

    static readonly Guid IidAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    static readonly Guid IidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    static readonly Guid IidRender = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    static readonly Guid IidEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
    static readonly Guid IidSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    static bool lastActive;
    static bool verbose;
    static volatile bool stop;          // asks the worker thread to finish
    static volatile float Level;        // peak of the last second, 0..1 (for the window)
    static volatile string Status = ""; // one line for the window
    static volatile bool Relaying;      // a session is copying audio right now

    // Undo each source device's own volume fader, so the stream gets full level whatever the fader says.
    // (Wave Link's channel faders are exactly these Windows device volumes.)
    static volatile bool FullVolume = true;
    // Per-source gain set in the window (device guid -> linear gain, 0 = muted). Replaced as a whole, never edited.
    static volatile Dictionary<string, float> Gains = new Dictionary<string, float>();
    // Per-source peak of the last 100 ms, for the meters in the window. Lock before use.
    static readonly Dictionary<string, float> Meters = new Dictionary<string, float>();

    static volatile bool OpenBigPicture;    // open Steam Big Picture on the PC when a stream starts

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    static void Log(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
        Console.WriteLine(line);
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "steam-audio-relay.log"), line + Environment.NewLine); } catch { }
    }

    // ---- device lookup (read from the registry, no COM enumeration needed) ----

    static List<Endpoint> ActiveRenderEndpoints()
    {
        var list = new List<Endpoint>();
        using (var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"))
        {
            if (root == null) return list;
            foreach (string name in root.GetSubKeyNames())
            {
                using (var k = root.OpenSubKey(name))
                {
                    if (k == null || !(k.GetValue("DeviceState") is int) || (int)k.GetValue("DeviceState") != 1) continue;
                    using (var p = k.OpenSubKey("Properties"))
                    {
                        if (p == null) continue;
                        string n = p.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                        string d = p.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                        if (n == null) continue;
                        list.Add(new Endpoint { Guid = name, Name = n, Device = d ?? "" });
                    }
                }
            }
        }
        return list;
    }

    static string NormGuid(string s)
    {
        return s.Trim().Trim('{', '}').ToLowerInvariant();
    }

    static Endpoint ByGuid(List<Endpoint> all, string wanted)
    {
        string w = NormGuid(wanted);
        foreach (var e in all) if (NormGuid(e.Guid) == w) return e;
        return null;
    }

    static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }

    // ---- stream detection ----

    static string LogPath()
    {
        string steam = null;
        try { steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; } catch { }
        if (string.IsNullOrEmpty(steam)) steam = @"C:\Program Files (x86)\Steam";
        return Path.Combine(steam.Replace('/', '\\'), "logs", "streaming_log.txt");
    }

    // Active if the last marker in Steam's streaming log is "Starting", not "Stopped".
    static bool StreamActive(string path)
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int n = (int)Math.Min(fs.Length, 262144);
                fs.Seek(-n, SeekOrigin.End);
                var buf = new byte[n];
                int got = fs.Read(buf, 0, n);
                string text = Encoding.UTF8.GetString(buf, 0, got);
                // Starting from the desktop writes "Starting desktop stream". Starting a game from the library
                // doesn't: there Steam logs "Streaming started to ..." and switches the activity to Game.
                // Switching between the two shows up as a "Stopped desktop stream" right before a new activity
                // line, so only a "Stopped" with nothing after it counts as the end.
                // (SecureDesktop is deliberately not a start marker: a real session end looks like "Stopped"
                // followed by a SecureDesktop activity, with nothing after it.)
                int a = Math.Max(
                    Math.Max(text.LastIndexOf(">>> Starting desktop stream", StringComparison.Ordinal),
                             text.LastIndexOf("Streaming started to ", StringComparison.Ordinal)),
                    Math.Max(text.LastIndexOf("setting activity to k_EStreamActivityGame", StringComparison.Ordinal),
                             text.LastIndexOf("setting activity to k_EStreamActivityDesktop", StringComparison.Ordinal)));
                // "Lost Video Stream" is what Steam writes when the whole session ends. It never shows up while you
                // just switch between desktop and game, so it's a reliable end marker for game streams, which
                // otherwise end without any "Stopped".
                int b = Math.Max(text.LastIndexOf(">>> Stopped desktop stream", StringComparison.Ordinal),
                                 text.LastIndexOf("Lost Video Stream", StringComparison.Ordinal));
                if (a < 0 && b < 0) return lastActive; // markers older than what we read: keep the last state
                lastActive = a > b;
                // Steam writes an "Audio source" line about once a minute while streaming. If the newest stream
                // line (that or a start marker) is much older than that, Steam probably crashed mid-stream and
                // never wrote an end. The file's own timestamp isn't used: starting a game on the PC makes Steam
                // write unrelated lines into this log and would keep the "stream" alive.
                if (lastActive)
                {
                    DateTime t = LastStamp(text, "Audio source [");
                    foreach (string m in new[] { "Streaming started to ", ">>> Starting desktop stream", "setting activity to k_EStreamActivity" })
                    {
                        DateTime x = LastStamp(text, m);
                        if (x > t) t = x;
                    }
                    if (t != DateTime.MinValue && (DateTime.Now - t).TotalSeconds > 100) lastActive = false;
                }
            }
        }
        catch { }
        return lastActive;
    }

    // Time of the newest log line containing the marker ("[2026-11-03 12:22:42][...] text"), or MinValue.
    static DateTime LastStamp(string text, string marker)
    {
        int i = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return DateTime.MinValue;
        int s = text.LastIndexOf('\n', i) + 1;
        DateTime t;
        if (text.Length >= s + 20 && text[s] == '[' &&
            DateTime.TryParseExact(text.Substring(s + 1, 19), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out t))
            return t;
        return DateTime.MinValue;
    }

    // The last "Game Recording - game stopped" line of Steam's streaming log, or null. Steam writes it whenever a
    // game it knows about exits while streaming.
    static string LastGameStop(string path)
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int n = (int)Math.Min(fs.Length, 262144);
                fs.Seek(-n, SeekOrigin.End);
                var buf = new byte[n];
                int got = fs.Read(buf, 0, n);
                string text = Encoding.UTF8.GetString(buf, 0, got);
                int i = text.LastIndexOf("Game Recording - game stopped", StringComparison.Ordinal);
                if (i < 0) return null;
                int s = text.LastIndexOf('\n', i) + 1;
                int e = text.IndexOf('\n', i);
                return (e < 0 ? text.Substring(s) : text.Substring(s, e - s)).Trim();
            }
        }
        catch { return null; }
    }

    // ---- stream detection, the direct way ----
    //
    // While a Remote Play stream runs, steam.exe keeps a loopback capture open on the device it records
    // ("Recording on device: ..." in its log). Windows lists that as an active audio session of steam.exe on that
    // device, from the moment the client connects until it disconnects, also through alt-tab and game starts.
    // That is exactly the condition this tool cares about - "Steam is recording the target right now" - so it is
    // the primary signal. Steam's log is only the fallback for when the sessions can't be read.

    static readonly Dictionary<uint, bool> steamPids = new Dictionary<uint, bool>();

    static bool IsSteam(uint pid)
    {
        if (pid == 0) return false;
        bool known;
        if (steamPids.TryGetValue(pid, out known)) return known;
        bool isSteam = false;
        try
        {
            using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
                isSteam = string.Equals(p.ProcessName, "steam", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        if (steamPids.Count > 64) steamPids.Clear(); // pids get reused, don't remember them forever
        steamPids[pid] = isSteam;
        return isSteam;
    }

    // true/false: steam.exe has / doesn't have an active audio session on the target. null: couldn't tell.
    static bool? SteamRecording(Endpoint target)
    {
        IMMDeviceEnumerator en = null;
        IMMDevice dev = null;
        object mgr = null;
        IAudioSessionEnumerator list = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            en.GetDevice(target.FullId, out dev);
            Guid iid = IidSessionManager2;
            dev.Activate(ref iid, 23, IntPtr.Zero, out mgr);
            ((IAudioSessionManager2)mgr).GetSessionEnumerator(out list);
            int n;
            list.GetCount(out n);
            bool found = false;
            for (int i = 0; i < n && !found; i++)
            {
                IAudioSessionControl2 c;
                list.GetSession(i, out c);
                try
                {
                    int state;
                    c.GetState(out state);
                    if (state == 1) // active
                    {
                        uint pid;
                        c.GetProcessId(out pid);
                        found = IsSteam(pid);
                    }
                }
                finally { Marshal.ReleaseComObject(c); }
            }
            return found;
        }
        catch { return null; }
        finally
        {
            if (list != null) Marshal.ReleaseComObject(list);
            if (mgr != null) Marshal.ReleaseComObject(mgr);
            if (dev != null) Marshal.ReleaseComObject(dev);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    static bool detectBySession; // which signal answered last (for the grace period and the log)

    static bool StreamActiveNow(Endpoint target, string logPath)
    {
        bool? rec = SteamRecording(target);
        detectBySession = rec.HasValue;
        return rec.HasValue ? rec.Value : StreamActive(logPath);
    }

    // ---- audio ----

    static IAudioClient OpenClient(IMMDeviceEnumerator en, string id)
    {
        IMMDevice dev;
        en.GetDevice(id, out dev);
        object o;
        Guid iid = IidAudioClient;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        Marshal.ReleaseComObject(dev);
        return (IAudioClient)o;
    }

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint ms);

    // The "auto" sources: every other playback device of the driver the target belongs to. With Wave Link
    // those are the channel devices (Game, Browser, Voice chat...), i.e. everything that never reaches
    // "System" by itself. Taking them directly instead of a Wave Link mix means nothing can loop back.
    static List<Endpoint> SiblingSources(Endpoint target)
    {
        var list = new List<Endpoint>();
        foreach (var e in ActiveRenderEndpoints())
            if (e.Device == target.Device && e.Name != target.Name) list.Add(e);
        return list;
    }

    class Src
    {
        public Endpoint E;
        public IAudioClient Client;
        public IAudioCaptureClient Capture;
        public int Channels;
        public float[] Ring;    // queued audio, interleaved in the target's channel layout
        public int Read, Count; // in frames
        public bool Primed;     // has enough queued to be mixed without gaps
        public IAudioEndpointVolume Vol; // the device's volume fader, may be null
        public string Key;      // normalized guid, for Gains and Meters
        public float Gain = 1f, GainTarget = 1f, Peak;
    }

    // Converts interleaved frames from sch channels to tch channels.
    static void ToLayout(float[] src, int sch, float[] dst, int tch, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            int si = i * sch, di = i * tch;
            if (sch == tch) { for (int c = 0; c < tch; c++) dst[di + c] = src[si + c]; }
            else if (sch == 1) { for (int c = 0; c < tch; c++) dst[di + c] = src[si]; }
            else if (tch == 2)
            {
                // surround -> stereo: fronts, plus centre and the rear/side pairs a bit quieter (LFE dropped)
                float l = src[si], r = src[si + 1];
                if (sch > 2) { l += 0.707f * src[si + 2]; r += 0.707f * src[si + 2]; }
                for (int c = 4; c + 1 < sch; c += 2) { l += 0.707f * src[si + c]; r += 0.707f * src[si + c + 1]; }
                dst[di] = l; dst[di + 1] = r;
            }
            else { for (int c = 0; c < tch; c++) dst[di + c] = c < sch ? src[si + c] : 0f; }
        }
    }

    static void Push(Src s, float[] data, int frames, int tch, int cap)
    {
        for (int i = 0; i < frames; i++)
        {
            if (s.Count == cap) { s.Read = (s.Read + 1) % cap; s.Count--; } // full: drop the oldest frame
            int w = ((s.Read + s.Count) % cap) * tch, d = i * tch;
            for (int c = 0; c < tch; c++) s.Ring[w + c] = data[d + c];
            s.Count++;
        }
    }

    static IAudioEndpointVolume OpenVolume(IMMDeviceEnumerator en, string id)
    {
        IMMDevice dev;
        en.GetDevice(id, out dev);
        object o;
        Guid iid = IidEndpointVolume;
        dev.Activate(ref iid, 23, IntPtr.Zero, out o);
        Marshal.ReleaseComObject(dev);
        return (IAudioEndpointVolume)o;
    }

    // One relay session: mixes the sources into the target. Returns when keepRunning() says false, throws on
    // device errors.
    static void RunSession(List<Endpoint> sources, Endpoint target, Func<bool> keepRunning)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IAudioClient ren = null;
        IntPtr tfmt = IntPtr.Zero;
        var srcs = new List<Src>();
        timeBeginPeriod(1); // 1 ms timer, so the short sleeps below really are short
        try
        {
            ren = OpenClient(en, target.FullId);
            ren.GetMixFormat(out tfmt);
            int tch = Marshal.ReadInt16(tfmt, 2);
            int rate = Marshal.ReadInt32(tfmt, 4);
            if (Marshal.ReadInt16(tfmt, 14) != 32) throw new Exception("The target device doesn't use 32-bit float audio.");
            Guid session = Guid.Empty;
            ren.Initialize(0, 0, 2000000, 0, tfmt, ref session);
            object o;
            Guid g = IidRender;
            ren.GetService(ref g, out o);
            var render = (IAudioRenderClient)o;
            uint bufFrames;
            ren.GetBufferSize(out bufFrames);

            int cap = rate / 2; // half a second per source at most
            var names = new List<string>();
            foreach (var e in sources)
            {
                IntPtr sfmt = IntPtr.Zero;
                IAudioClient c = null;
                try
                {
                    c = OpenClient(en, e.FullId);
                    c.GetMixFormat(out sfmt);
                    int sch = Marshal.ReadInt16(sfmt, 2), srate = Marshal.ReadInt32(sfmt, 4), sbits = Marshal.ReadInt16(sfmt, 14);
                    if (srate != rate || sbits != 32)
                    {
                        Log("Skipping " + e + ": " + srate + " Hz, " + sbits + " bit doesn't match the target.");
                        Marshal.ReleaseComObject(c);
                        continue;
                    }
                    Guid s2 = Guid.Empty;
                    c.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000, 0, sfmt, ref s2);
                    Guid gc = IidCapture;
                    object co;
                    c.GetService(ref gc, out co);
                    c.Start();
                    IAudioEndpointVolume vol = null;
                    try { vol = OpenVolume(en, e.FullId); } catch { }
                    srcs.Add(new Src { E = e, Client = c, Capture = (IAudioCaptureClient)co, Channels = sch, Ring = new float[cap * tch], Vol = vol, Key = NormGuid(e.Guid) });
                    names.Add(e.Name + (sch != tch ? " (" + sch + " ch)" : ""));
                }
                catch (Exception ex)
                {
                    Log("Skipping " + e + ": " + ex.Message);
                    if (c != null) try { Marshal.ReleaseComObject(c); } catch { }
                }
                finally { if (sfmt != IntPtr.Zero) Marshal.FreeCoTaskMem(sfmt); }
            }
            if (srcs.Count == 0) throw new Exception("None of the source devices could be opened.");

            // Gain per source: the slider from the window, times whatever undoes the device's own fader.
            // Loopback audio comes after that fader, so at -9 dB we multiply by +9 dB (capped at +30 dB).
            Action updateGains = delegate
            {
                var gains = Gains;
                foreach (var s in srcs)
                {
                    float ug;
                    if (!gains.TryGetValue(s.Key, out ug)) ug = 1f;
                    float comp = 1f;
                    if (FullVolume && s.Vol != null)
                        try { float db; s.Vol.GetMasterVolumeLevel(out db); comp = (float)Math.Min(31.6, Math.Pow(10, -db / 20.0)); } catch { }
                    s.GainTarget = ug * comp;
                    lock (Meters) Meters[s.Key] = s.Peak;
                    s.Peak = 0;
                }
            };
            updateGains();
            foreach (var s in srcs) s.Gain = s.GainTarget;

            ren.Start();
            Relaying = true;
            Log("Relaying " + string.Join(", ", names.ToArray()) + " -> " + target.Name + " (" + tch + " ch, " + rate + " Hz)");
            Status = "Relaying " + srcs.Count + (srcs.Count == 1 ? " device" : " devices");

            int prime = rate / 50;            // 20 ms queued before a source joins the mix
            int want = rate * 30 / 1000;      // keep 30 ms in the target's buffer
            int maxQueue = rate * 100 / 1000; // more than this queued means we fell behind: catch up
            var tmp = new float[0];
            var conv = new float[0];
            var mix = new float[0];
            float peak = 0, peak5 = 0;
            var nextLevel = DateTime.UtcNow.AddMilliseconds(100);
            var nextLevelLog = DateTime.UtcNow.AddSeconds(5);
            while (keepRunning())
            {
                if (DateTime.UtcNow >= nextLevel)
                {
                    Level = peak;
                    if (peak > peak5) peak5 = peak;
                    peak = 0;
                    nextLevel = DateTime.UtcNow.AddMilliseconds(100);
                    updateGains();
                }
                if (verbose && DateTime.UtcNow >= nextLevelLog)
                {
                    Log("Level (last 5 s, 0..1): " + peak5.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                    peak5 = 0;
                    nextLevelLog = DateTime.UtcNow.AddSeconds(5);
                }

                // 1. collect what the sources have played since the last round
                int alive = 0;
                foreach (var s in srcs)
                {
                    if (s.Capture == null) continue;
                    try
                    {
                        uint packet;
                        s.Capture.GetNextPacketSize(out packet);
                        while (packet > 0)
                        {
                            IntPtr data; uint frames, flags; ulong dp, qp;
                            s.Capture.GetBuffer(out data, out frames, out flags, out dp, out qp);
                            int n = (int)frames;
                            if (conv.Length < n * tch) conv = new float[n * tch];
                            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(conv, 0, n * tch);
                            else
                            {
                                if (tmp.Length < n * s.Channels) tmp = new float[n * s.Channels];
                                Marshal.Copy(data, tmp, 0, n * s.Channels);
                                ToLayout(tmp, s.Channels, conv, tch, n);
                                // apply the gain, gliding to a new value over this packet so changes don't click
                                float g0 = s.Gain, g1 = s.GainTarget, pk = s.Peak;
                                for (int i = 0; i < n; i++)
                                {
                                    float gn = g0 + (g1 - g0) * i / n;
                                    for (int c = 0; c < tch; c++)
                                    {
                                        float v = conv[i * tch + c] * gn;
                                        conv[i * tch + c] = v;
                                        if (v < 0) v = -v;
                                        if (v > pk) pk = v;
                                    }
                                }
                                s.Gain = g1;
                                s.Peak = pk;
                            }
                            s.Capture.ReleaseBuffer(frames);
                            Push(s, conv, n, tch, cap);
                            s.Capture.GetNextPacketSize(out packet);
                        }
                        alive++;
                    }
                    catch (Exception ex)
                    {
                        // one channel device going away shouldn't take the others down
                        Log("Lost " + s.E + ": " + ex.Message);
                        try { Marshal.ReleaseComObject(s.Client); } catch { }
                        s.Capture = null; s.Client = null; s.Count = 0; s.Primed = false;
                    }
                    if (!s.Primed && s.Count >= prime) s.Primed = true;
                }
                if (alive == 0) throw new Exception("All source devices are gone.");

                // 2. top the target's buffer up with the mix (silence where nothing is playing)
                uint pad;
                ren.GetCurrentPadding(out pad);
                int toWrite = Math.Min((int)(bufFrames - pad), want - (int)pad);
                if (toWrite > 0)
                {
                    int len = toWrite * tch;
                    if (mix.Length < len) mix = new float[len];
                    Array.Clear(mix, 0, len);
                    foreach (var s in srcs)
                    {
                        if (!s.Primed) continue;
                        int n = Math.Min(s.Count, toWrite);
                        for (int i = 0; i < n; i++)
                        {
                            int r = ((s.Read + i) % cap) * tch, d = i * tch;
                            for (int c = 0; c < tch; c++) mix[d + c] += s.Ring[r + c];
                        }
                        s.Read = (s.Read + n) % cap;
                        s.Count -= n;
                        if (s.Count == 0) s.Primed = false; // ran dry (or stopped playing): queue up again first
                        else if (s.Count > maxQueue) { int drop = s.Count - prime; s.Read = (s.Read + drop) % cap; s.Count -= drop; }
                    }
                    for (int i = 0; i < len; i++)
                    {
                        float v = mix[i];
                        if (v > 1f) mix[i] = v = 1f; else if (v < -1f) mix[i] = v = -1f;
                        if (v < 0) v = -v;
                        if (v > peak) peak = v;
                    }
                    IntPtr dst;
                    render.GetBuffer((uint)toWrite, out dst);
                    Marshal.Copy(mix, 0, dst, len);
                    render.ReleaseBuffer((uint)toWrite, 0);
                }
                Thread.Sleep(3); // loopback delivers nothing during silence, so poll instead of waiting on events
            }
        }
        finally
        {
            Relaying = false;
            lock (Meters) Meters.Clear();
            timeEndPeriod(1);
            foreach (var s in srcs)
            {
                try { if (s.Client != null) s.Client.Stop(); } catch { }
                try { if (s.Client != null) Marshal.ReleaseComObject(s.Client); } catch { }
            }
            try { if (ren != null) ren.Stop(); } catch { }
            if (tfmt != IntPtr.Zero) Marshal.FreeCoTaskMem(tfmt);
            if (ren != null) Marshal.ReleaseComObject(ren);
            Marshal.ReleaseComObject(en);
        }
    }

    // ---- relay worker (used by the console mode and by the window) ----

    static void Worker(Func<List<Endpoint>> getSources, Endpoint target, bool always)
    {
        string path = LogPath();
        Log(always ? "Running continuously." : "Waiting for a Remote Play stream (until Steam records " + target.Name + ")");
        Status = always ? "Starting..." : "Waiting for a Remote Play stream";
        bool sessionStarted = false; // true from the first relay of a stream until it has really ended
        int hits = 0;
        var lastRelayEnd = DateTime.MinValue;
        while (!stop)
        {
            // two positive checks in a row, half a second apart, so a blip can't start anything
            bool go = always;
            if (!go)
            {
                if (StreamActiveNow(target, path)) hits++; else hits = 0;
                go = hits >= 2;
            }
            if (go)
            {
                hits = 0;
                Log(always ? "Starting relay." : "Stream active (" + (detectBySession ? "Steam is recording " + target.Name : "according to Steam's log") + "), starting relay.");
                if (!sessionStarted)
                {
                    sessionStarted = true;
                    if (OpenBigPicture) LaunchBigPicture();
                }
                var lastCheck = DateTime.UtcNow;
                var lastSeenActive = DateTime.UtcNow;
                bool active = true;
                bool ended = false;
                string gameStopSeen = LastGameStop(path); // only game endings after this point count
                var lastGsCheck = DateTime.UtcNow;
                try
                {
                    RunSession(getSources(), target, () =>
                    {
                        if (stop) return false;
                        if (OpenBigPicture && (DateTime.UtcNow - lastGsCheck).TotalSeconds >= 2)
                        {
                            lastGsCheck = DateTime.UtcNow;
                            string gs = LastGameStop(path);
                            if (gs != null && gs != gameStopSeen)
                            {
                                // When the game you started from Big Picture ends, Steam falls back to the normal
                                // window. Put Big Picture back after a moment.
                                gameStopSeen = gs;
                                ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(2000); LaunchBigPicture(); });
                            }
                        }
                        if (!always && (DateTime.UtcNow - lastCheck).TotalSeconds >= 0.5)
                        {
                            lastCheck = DateTime.UtcNow;
                            active = StreamActiveNow(target, path);
                            if (active) lastSeenActive = DateTime.UtcNow;
                        }
                        // Steam's recording stays open through alt-tab and game starts, so 3 seconds without it
                        // means the stream is over. The log fallback needs 15: there a "Stopped" is followed
                        // by a new "Starting" a few seconds later when the stream only switches modes.
                        return active || (DateTime.UtcNow - lastSeenActive).TotalSeconds < (detectBySession ? 3 : 15);
                    });
                    ended = true;
                    if (!stop) Log("Steam stopped recording, relay paused.");
                }
                catch (Exception e)
                {
                    // Steam rebuilds its virtual device when a session restarts; just reconnect.
                    Log("Error (0x" + Marshal.GetHRForException(e).ToString("X8") + "): " + e.Message + " - reconnecting");
                    Status = "Error, reconnecting: " + e.Message;
                    Thread.Sleep(500);
                }
                Level = 0;
                if (ended) lastRelayEnd = DateTime.UtcNow; // only when Steam stopped recording, not on a reconnect after an error
                if (!stop) Status = always ? "Reconnecting..." : "Waiting for a Remote Play stream";
            }
            else
            {
                // The relay itself follows Steam's recording to the second. The session as a whole (Big Picture)
                // is only called over after 20 quiet seconds: launching a game from the library makes Steam end
                // its stream and start a new one 5 to 10 seconds later, and that shouldn't close anything.
                if (sessionStarted && (DateTime.UtcNow - lastRelayEnd).TotalSeconds >= 20)
                {
                    sessionStarted = false;
                    Log("Remote Play session over.");
                    if (OpenBigPicture) CloseBigPicture();
                }
                for (int i = 0; i < 5 && !stop; i++) Thread.Sleep(100);
            }
        }
        Status = "Stopped";
        Level = 0;
    }

    static void LaunchBigPicture() { SteamUrl("steam://open/bigpicture", "Opened Steam Big Picture."); }

    static void CloseBigPicture() { SteamUrl("steam://close/bigpicture", "Closed Steam Big Picture."); }

    static void SteamUrl(string url, string done)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            Log(done);
        }
        catch (Exception e) { Log("Steam URL failed (" + url + "): " + e.Message); }
    }

    // ---- settings and autostart (window mode) ----

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "steam-audio-relay";

    static string SettingsPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "steam-audio-relay", "settings.ini");
    }

    static Dictionary<string, string> LoadSettings()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (string line in File.ReadAllLines(SettingsPath()))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
        }
        catch { }
        return d;
    }

    static readonly object settingsLock = new object();

    // Read-modify-write of the settings file. Keys that aren't touched stay as they are.
    static void UpdateSettings(Action<Dictionary<string, string>> change)
    {
        lock (settingsLock)
        {
            var d = LoadSettings();
            change(d);
            try
            {
                string p = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                var sb = new StringBuilder();
                foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(p, sb.ToString());
            }
            catch { }
        }
    }

    static bool AutostartEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunValue) != null;
    }

    static void SetAutostart(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k == null) return;
            if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --minimized");
            else k.DeleteValue(RunValue, false);
        }
    }

    // ---- the window ----

    static readonly Color CBg = Color.FromArgb(27, 27, 27), CCard = Color.FromArgb(42, 42, 42), CCardHi = Color.FromArgb(56, 56, 56),
        CText = Color.FromArgb(242, 242, 242), CSub = Color.FromArgb(160, 160, 160), CAccent = Color.FromArgb(76, 194, 255),
        CTrack = Color.FromArgb(70, 70, 70), CGreen = Color.FromArgb(47, 209, 91), CRed = Color.FromArgb(255, 110, 110),
        CRedBg = Color.FromArgb(74, 38, 38);
    static readonly Font FTitle = new Font("Segoe UI Semibold", 13f), FName = new Font("Segoe UI Semibold", 9.75f),
        FText = new Font("Segoe UI", 9f), FSmall = new Font("Segoe UI", 8.25f), FCaps = new Font("Segoe UI Semibold", 8f);
    const ControlStyles Painted = ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static GraphicsPath Round(RectangleF r, float rad)
    {
        float d = rad * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void FillRound(Graphics g, Color c, RectangleF r, float rad)
    {
        using (var p = Round(r, rad))
        using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    class Item
    {
        public Endpoint E;
        public override string ToString()
        {
            return E.Name + " (" + E.Device + ")  [" + E.Guid.Trim('{', '}').Substring(0, 8) + "]";
        }
    }

    // The icon is built into the exe (see build.cmd); fall back to the stock one if that ever fails.
    static Icon AppIcon(Size? size)
    {
        try
        {
            using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico"))
                if (s != null) return size.HasValue ? new Icon(s, size.Value) : new Icon(s);
        }
        catch { }
        return SystemIcons.Application;
    }

    // On/off switch.
    class Toggle : Control
    {
        bool on;
        public event EventHandler Changed; // only fires on clicks, not when On is set from code
        public bool On { get { return on; } set { if (on != value) { on = value; Invalidate(); } } }
        public Toggle() { SetStyle(Painted, true); Size = new Size(44, 24); Cursor = Cursors.Hand; BackColor = CCard; }
        protected override void OnClick(EventArgs e)
        {
            on = !on;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            if (on) FillRound(g, CAccent, r, r.Height / 2);
            else using (var p = Round(r, r.Height / 2)) using (var pen = new Pen(CSub, 1.5f)) g.DrawPath(pen, p);
            float d = Height - 10, x = on ? Width - d - 5 : 5;
            using (var b = new SolidBrush(on ? Color.FromArgb(20, 20, 20) : CSub)) g.FillEllipse(b, x, 5, d, d);
        }
    }

    // Volume slider with the level meter drawn inside the track.
    class Fader : Control
    {
        int val = 100;
        float level;
        bool drag;
        public bool Dim;
        public event EventHandler Changed;
        public int Value { get { return val; } set { val = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        public float LevelValue { set { if (Math.Abs(level - value) > 0.003f) { level = value; Invalidate(); } } }
        public Fader() { SetStyle(Painted, true); Size = new Size(236, 24); Cursor = Cursors.Hand; BackColor = CCard; }
        void SetFromX(int x)
        {
            int v = (int)Math.Round((x - 10) * 100.0 / (Width - 20));
            v = Math.Max(0, Math.Min(100, v));
            if (v == val) return;
            val = v;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
        protected override void OnMouseDown(MouseEventArgs e) { drag = true; SetFromX(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var track = new RectangleF(2, Height / 2f - 5, Width - 4, 10);
            FillRound(g, CTrack, track, 5);
            // meter: -60 dB .. 0 dB across the track
            float m = level <= 0.001f ? 0 : (float)Math.Max(0, Math.Min(1, (20 * Math.Log10(level) + 60) / 60));
            if (m > 0.02f) FillRound(g, CGreen, new RectangleF(track.X, track.Y + 2, Math.Max(8, track.Width * m), 6), 3);
            float kx = 10 + (Width - 20) * val / 100f;
            using (var b = new SolidBrush(Dim ? CSub : CAccent)) g.FillEllipse(b, kx - 8, Height / 2f - 8, 16, 16);
        }
    }

    // Plain level meter (for the output).
    class Meter : Control
    {
        float level;
        public float LevelValue { set { if (Math.Abs(level - value) > 0.003f) { level = value; Invalidate(); } } }
        public Meter() { SetStyle(Painted, true); Size = new Size(200, 10); BackColor = CCard; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var track = new RectangleF(0, 0, Width, Height);
            FillRound(g, CTrack, track, Height / 2f);
            float m = level <= 0.001f ? 0 : (float)Math.Max(0, Math.Min(1, (20 * Math.Log10(level) + 60) / 60));
            if (m > 0.02f) FillRound(g, CGreen, new RectangleF(2, 2, Math.Max(8, (Width - 4) * m), Height - 4), (Height - 4) / 2f);
        }
    }

    // Speaker icon that toggles mute.
    class MuteButton : Control
    {
        bool muted;
        public event EventHandler Changed;
        public bool Muted { get { return muted; } set { if (muted != value) { muted = value; Invalidate(); } } }
        public MuteButton() { SetStyle(Painted, true); Size = new Size(32, 32); Cursor = Cursors.Hand; BackColor = CCard; }
        protected override void OnClick(EventArgs e)
        {
            muted = !muted;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            Color c = muted ? CRed : CText;
            if (muted) using (var p = Round(new RectangleF(1, 1, 30, 30), 6)) using (var pen = new Pen(CRed, 1.2f)) g.DrawPath(pen, p);
            using (var pen = new Pen(c, 1.7f))
            {
                pen.LineJoin = LineJoin.Round; pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawPolygon(pen, new[] { new PointF(8, 13), new PointF(11.5f, 13), new PointF(16, 9), new PointF(16, 23), new PointF(11.5f, 19), new PointF(8, 19) });
                if (muted)
                {
                    g.DrawLine(pen, 20, 13, 25, 19);
                    g.DrawLine(pen, 25, 13, 20, 19);
                }
                else
                {
                    g.DrawArc(pen, 14, 11.5f, 8, 9, -55, 110);
                    g.DrawArc(pen, 12, 8, 13, 16, -55, 110);
                }
            }
        }
    }

    class PillButton : Control
    {
        public bool Primary = true;
        public PillButton() { SetStyle(Painted, true); Size = new Size(96, 32); Cursor = Cursors.Hand; BackColor = CBg; }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            if (Primary) FillRound(g, CAccent, r, r.Height / 2);
            else using (var p = Round(r, r.Height / 2)) using (var pen = new Pen(CSub, 1.3f)) g.DrawPath(pen, p);
            TextRenderer.DrawText(g, Text, FName, ClientRectangle, Primary ? Color.FromArgb(15, 15, 15) : CText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    static Color BadgeColor(string name, int index)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("game")) return Color.FromArgb(214, 84, 84);
        if (n.Contains("browser") || n.Contains("chrome")) return Color.FromArgb(142, 92, 247);
        if (n.Contains("voice") || n.Contains("chat")) return Color.FromArgb(232, 190, 30);
        if (n.Contains("sound")) return Color.FromArgb(245, 138, 31);
        if (n.Contains("alert")) return Color.FromArgb(84, 132, 164);
        if (n.Contains("music")) return Color.FromArgb(47, 191, 113);
        var pal = new[] { Color.FromArgb(61, 139, 253), Color.FromArgb(226, 85, 161), Color.FromArgb(47, 191, 113), Color.FromArgb(108, 122, 137) };
        return pal[index % pal.Length];
    }

    // One channel of the stream mix: badge, name, mute, volume with meter.
    class ChannelRow : Control
    {
        public Endpoint E;
        public string Key;
        public MuteButton Mute = new MuteButton();
        public Fader Fader = new Fader();
        Color badge;
        public ChannelRow(Endpoint e, Color badgeColor)
        {
            SetStyle(Painted, true);
            E = e; Key = NormGuid(e.Guid); badge = badgeColor;
            Size = new Size(580, 46);
            Mute.Location = new Point(232, 7);
            Fader.Location = new Point(274, 11);
            Controls.Add(Mute);
            Controls.Add(Fader);
            Mute.Changed += delegate { Sync(); };
            Fader.Changed += delegate { Invalidate(); };
            Sync();
        }
        public void Sync()
        {
            Color c = Mute.Muted ? CRedBg : CCard;
            Mute.BackColor = c; Fader.BackColor = c; Fader.Dim = Mute.Muted;
            Mute.Invalidate(); Fader.Invalidate(); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(CBg);
            FillRound(g, Mute.Muted ? CRedBg : CCard, new RectangleF(0, 0, Width, Height), 8);
            FillRound(g, badge, new RectangleF(8, 8, 30, 30), 7);
            TextRenderer.DrawText(g, E.Name.Substring(0, 1).ToUpperInvariant(), FName, new Rectangle(8, 8, 30, 30), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, E.Name, FName, new Rectangle(48, 0, 180, Height), Mute.Muted ? CSub : CText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Mute.Muted ? "muted" : Fader.Value + " %", FText, new Rectangle(514, 0, 56, Height), CSub, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }
    }

    // A setting: title, one line of explanation, switch on the right.
    class OptionRow : Control
    {
        public Toggle T = new Toggle();
        string title, sub;
        public OptionRow(string title, string sub)
        {
            SetStyle(Painted, true);
            this.title = title; this.sub = sub;
            Size = new Size(580, 50);
            T.Location = new Point(580 - 44 - 14, 13);
            Controls.Add(T);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(CBg);
            FillRound(g, CCard, new RectangleF(0, 0, Width, Height), 8);
            TextRenderer.DrawText(g, title, FName, new Rectangle(14, 6, 480, 20), CText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, sub, FSmall, new Rectangle(14, 26, 490, 18), CSub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // The card with the target device and the meter of what goes into it.
    class OutputCard : Control
    {
        public ComboBox Target = new ComboBox();
        public Meter Out = new Meter();
        public OutputCard()
        {
            SetStyle(Painted, true);
            Size = new Size(580, 66);
            Target.SetBounds(14, 30, 340, 24);
            Target.DropDownStyle = ComboBoxStyle.DropDownList;
            Target.FlatStyle = FlatStyle.Flat;
            Target.BackColor = CCardHi;
            Target.ForeColor = CText;
            Target.Font = FText;
            Target.DrawMode = DrawMode.OwnerDrawFixed;
            Target.ItemHeight = 18;
            Target.DrawItem += delegate(object s, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var b = new SolidBrush(sel ? CAccent : CCardHi)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, Target.Items[e.Index].ToString(), FText, e.Bounds, sel ? Color.Black : CText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            Out.SetBounds(372, 38, 194, 10);
            Controls.Add(Target);
            Controls.Add(Out);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(CBg);
            FillRound(g, CCard, new RectangleF(0, 0, Width, Height), 8);
            TextRenderer.DrawText(g, "Plays into (the device Steam records)", FSmall, new Rectangle(14, 8, 340, 18), CSub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "Stream level", FSmall, new Rectangle(372, 8, 194, 18), CSub, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    class MainForm : Form
    {
        Panel body = new Panel();
        PictureBox pic = new PictureBox();
        Label lblTitle = new Label(), lblStatus = new Label(), lblMix = new Label(), lblMixSub = new Label(), lblOpt = new Label(), lblRefresh = new Label(), lblNone = new Label();
        PillButton btnToggle = new PillButton();
        OutputCard output = new OutputCard();
        List<ChannelRow> rows = new List<ChannelRow>();
        OptionRow optFull = new OptionRow("Full volume in the stream", "The stream gets 100 % even when a channel's fader in Wave Link is turned down."),
            optStream = new OptionRow("Only while streaming", "Relay only while a Steam Remote Play stream is running."),
            optAuto = new OptionRow("Start with Windows", "Starts minimized to the tray."),
            optBig = new OptionRow("Big Picture", "Open Steam Big Picture when a stream starts, close it when it ends.");
        NotifyIcon tray = new NotifyIcon();
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        bool startHidden, loading, reallyExit, wantRun, preview;
        int ticks;
        Thread worker;

        static Label Caption(Label l, string text, Font f, Color c)
        {
            l.Text = text; l.Font = f; l.ForeColor = c; l.BackColor = CBg; l.AutoSize = true;
            return l;
        }

        public MainForm(bool hidden, bool previewOnly)
        {
            startHidden = hidden;
            preview = previewOnly;
            Text = "Steam Audio Relay";
            Icon = AppIcon(null);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = CBg;
            ForeColor = CText;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = previewOnly ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
            if (previewOnly) Location = new Point(-4000, -4000);

            body.Dock = DockStyle.Fill;
            body.AutoScroll = true;
            body.BackColor = CBg;
            Controls.Add(body);

            pic.SetBounds(20, 16, 36, 36);
            pic.SizeMode = PictureBoxSizeMode.Zoom;
            try { pic.Image = AppIcon(new Size(48, 48)).ToBitmap(); } catch { }
            Caption(lblTitle, "Steam Audio Relay", FTitle, CText).Location = new Point(64, 12);
            Caption(lblStatus, "", FText, CSub).Location = new Point(66, 38);
            btnToggle.Location = new Point(504, 18);
            btnToggle.Click += delegate
            {
                if (worker != null) { wantRun = false; StopWorker(); }
                else { wantRun = true; StartWorker(); }
            };
            Caption(lblMix, "STREAM VOLUME", FCaps, CSub).Location = new Point(22, 74);
            Caption(lblMixSub, "How loud each channel is in the stream. This changes nothing in Wave Link.", FSmall, CSub).Location = new Point(22, 92);
            Caption(lblRefresh, "Refresh", FSmall, CAccent).Location = new Point(552, 74);
            lblRefresh.Cursor = Cursors.Hand;
            lblRefresh.Click += delegate { LoadDevices(); BuildRows(); if (wantRun) StartWorker(); };
            Caption(lblNone, "No other playback devices found for this target.", FText, CSub);
            lblNone.Visible = false;
            Caption(lblOpt, "OPTIONS", FCaps, CSub);
            body.Controls.AddRange(new Control[] { pic, lblTitle, lblStatus, btnToggle, lblMix, lblMixSub, lblRefresh, lblNone, output, lblOpt, optFull, optStream, optAuto, optBig });

            output.Target.SelectedIndexChanged += delegate
            {
                if (loading) return;
                string tgt = SelectedGuid() ?? "";
                UpdateSettings(d => { d["target"] = tgt; });
                BuildRows();
                if (wantRun) StartWorker();
            };
            optFull.T.Changed += delegate
            {
                FullVolume = optFull.T.On;
                bool v = optFull.T.On;
                UpdateSettings(d => { d["fullvolume"] = v ? "1" : "0"; });
            };
            optStream.T.Changed += delegate
            {
                bool v = optStream.T.On;
                UpdateSettings(d => { d["always"] = v ? "0" : "1"; });
                if (wantRun) StartWorker();
            };
            optAuto.T.Changed += delegate
            {
                try { SetAutostart(optAuto.T.On); }
                catch (Exception e) { Status = "Couldn't change autostart: " + e.Message; }
            };
            optBig.T.Changed += delegate
            {
                OpenBigPicture = optBig.T.On;
                bool v = optBig.T.On;
                UpdateSettings(d => { d["bigpicture"] = v ? "1" : "0"; });
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { ShowWindow(); });
            menu.Items.Add("Exit", null, delegate { reallyExit = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.Icon = AppIcon(SystemInformation.SmallIconSize);
            tray.Text = "Steam Audio Relay";
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.Visible = !previewOnly;

            loading = true;
            var s = LoadSettings();
            optFull.T.On = !s.ContainsKey("fullvolume") || s["fullvolume"] == "1";
            FullVolume = optFull.T.On;
            optStream.T.On = !(s.ContainsKey("always") && s["always"] == "1");
            optBig.T.On = s.ContainsKey("bigpicture") && s["bigpicture"] == "1";
            OpenBigPicture = optBig.T.On;
            try { optAuto.T.On = AutostartEnabled(); } catch { }
            LoadDevices();
            loading = false;
            BuildRows();

            timer.Interval = 60;
            timer.Tick += delegate { Tick(); };
            timer.Start();

            if (previewOnly)
            {
                // sample values so a screenshot shows what the window looks like in use
                Status = "Relaying " + rows.Count + " devices";
                Relaying = true;
                var sample = new[] { 0.5f, 0.02f, 0f, 0.12f, 0f, 0.3f };
                for (int i = 0; i < rows.Count; i++) rows[i].Fader.LevelValue = sample[i % sample.Length];
                output.Out.LevelValue = 0.6f;
                Tick();
                return;
            }

            wantRun = true;
            if (output.Target.SelectedItem != null) StartWorker();
            else Status = "Pick the device Steam records.";
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { } // dark title bar
        }

        void Tick()
        {
            bool relaying = Relaying && (worker != null || preview);
            lblStatus.Text = Status;
            lblStatus.ForeColor = relaying ? CGreen : CSub;
            bool running = worker != null || preview;
            string t = running ? "Stop" : "Start";
            if (btnToggle.Text != t) { btnToggle.Primary = !running; btnToggle.Text = t; }
            if (preview) return;
            foreach (var r in rows)
            {
                float v = 0;
                if (relaying) lock (Meters) Meters.TryGetValue(r.Key, out v);
                r.Fader.LevelValue = v;
            }
            output.Out.LevelValue = relaying ? Level : 0;
            if (++ticks % 16 == 0)
            {
                string tt = "Steam Audio Relay: " + Status;
                tray.Text = tt.Length > 63 ? tt.Substring(0, 63) : tt;
            }
        }

        string SelectedGuid()
        {
            var it = output.Target.SelectedItem as Item;
            return it == null ? null : it.E.Guid;
        }

        void LoadDevices()
        {
            bool was = loading;
            loading = true;
            var s = LoadSettings();
            string old = SelectedGuid() ?? (s.ContainsKey("target") ? s["target"] : "");
            var all = ActiveRenderEndpoints();
            all.Sort((a, b) => string.Compare(a.Name + a.Device, b.Name + b.Device, StringComparison.OrdinalIgnoreCase));
            output.Target.Items.Clear();
            foreach (var e in all) output.Target.Items.Add(new Item { E = e });
            if (!string.IsNullOrEmpty(old))
                foreach (Item it in output.Target.Items)
                    if (NormGuid(it.E.Guid) == NormGuid(old)) { output.Target.SelectedItem = it; break; }
            if (output.Target.SelectedItem == null)
            {
                var systems = new List<Item>();
                foreach (Item it in output.Target.Items) if (it.E.Name == "System") systems.Add(it);
                if (systems.Count == 1) output.Target.SelectedItem = systems[0];
            }
            loading = was;
        }

        // The channel rows: the other devices of the target's driver. Without such siblings (no Wave Link) every
        // other playback device is offered, muted until the user turns it on.
        void BuildRows()
        {
            foreach (var r in rows) { body.Controls.Remove(r); r.Dispose(); }
            rows.Clear();
            var t = output.Target.SelectedItem as Item;
            if (t != null)
            {
                var s = preview ? new Dictionary<string, string>() : LoadSettings(); // screenshots show the defaults
                var list = SiblingSources(t.E);
                bool siblings = list.Count > 0;
                if (!siblings)
                    foreach (var e in ActiveRenderEndpoints())
                        if (NormGuid(e.Guid) != NormGuid(t.E.Guid)) list.Add(e);
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                int i = 0;
                foreach (var e in list)
                {
                    var row = new ChannelRow(e, BadgeColor(e.Name, i++));
                    string k = row.Key;
                    int lvl;
                    if (s.ContainsKey("lvl_" + k) && int.TryParse(s["lvl_" + k], out lvl)) row.Fader.Value = lvl;
                    row.Mute.Muted = s.ContainsKey("mute_" + k) ? s["mute_" + k] == "1" : !siblings;
                    row.Sync();
                    row.Fader.Changed += delegate { SaveRow(row); };
                    row.Mute.Changed += delegate { SaveRow(row); };
                    rows.Add(row);
                    body.Controls.Add(row);
                }
            }
            PublishGains();
            Relayout();
        }

        void SaveRow(ChannelRow row)
        {
            string k = row.Key, lvl = row.Fader.Value.ToString(), m = row.Mute.Muted ? "1" : "0";
            UpdateSettings(d => { d["lvl_" + k] = lvl; d["mute_" + k] = m; });
            PublishGains();
        }

        // Hands the slider positions to the audio thread. The curve is quadratic, so 50 % is about -12 dB.
        void PublishGains()
        {
            var g = new Dictionary<string, float>();
            foreach (var r in rows) g[r.Key] = r.Mute.Muted ? 0f : (r.Fader.Value / 100f) * (r.Fader.Value / 100f);
            Gains = g;
        }

        void Relayout()
        {
            body.AutoScrollPosition = Point.Empty;
            int y = 116;
            foreach (var r in rows) { r.Location = new Point(20, y); y += 50; }
            lblNone.Visible = rows.Count == 0;
            if (rows.Count == 0) { lblNone.Location = new Point(22, y + 4); y += 34; }
            output.Location = new Point(20, y + 4); y += 4 + 66 + 18;
            lblOpt.Location = new Point(22, y); y += 22;
            foreach (var o in new[] { optFull, optStream, optAuto, optBig }) { o.Location = new Point(20, y); y += 54; }
            y += 12;
            int max = Screen.FromControl(this).WorkingArea.Height - 80;
            bool scroll = y > max;
            body.AutoScrollMinSize = scroll ? new Size(0, y) : Size.Empty;
            ClientSize = new Size(620 + (scroll ? SystemInformation.VerticalScrollBarWidth : 0), Math.Min(y, max));
        }

        void StartWorker()
        {
            StopWorker();
            var t = output.Target.SelectedItem as Item;
            if (t == null) { Status = "Pick the device Steam records."; return; }
            if (rows.Count == 0) { Status = "Nothing to relay: no other playback devices."; return; }
            var list = new List<Endpoint>();
            foreach (var r in rows) list.Add(r.E);
            PublishGains();
            stop = false;
            Endpoint te = t.E;
            bool always = !optStream.T.On;
            worker = new Thread(delegate() { Worker(delegate { return list; }, te, always); });
            worker.IsBackground = true;
            worker.Start();
        }

        void StopWorker()
        {
            stop = true;
            if (worker != null) worker.Join(3000);
            worker = null;
            Status = "Stopped";
            Level = 0;
        }

        void ShowWindow()
        {
            startHidden = false;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void SetVisibleCore(bool value)
        {
            // started with --minimized: live in the tray, don't show the window
            if (startHidden)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && !preview && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            timer.Stop();
            if (!preview) StopWorker();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosing(e);
        }
    }

    static int RunUi(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // --screenshot <file.png>: draw the window into a picture and exit (for the README, nothing is relayed)
        string shot = Arg(args, "--screenshot");
        if (shot != null)
        {
            using (var f = new MainForm(false, true))
            {
                f.Show();
                Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            return 0;
        }

        bool created;
        using (var mutex = new Mutex(true, "steam-audio-relay", out created))
        {
            if (!created)
            {
                MessageBox.Show("Steam Audio Relay is already running (look in the tray).", "Steam Audio Relay");
                return 1;
            }
            Application.Run(new MainForm(Array.IndexOf(args, "--minimized") >= 0, false));
            return 0;
        }
    }

    // ---- console mode ----

    static void PrintList(List<Endpoint> all)
    {
        Console.WriteLine("Active playback devices (name, device, id):");
        foreach (var e in all) Console.WriteLine("  " + e.Name + "  |  " + e.Device + "  |  " + e.Guid);
    }

    static void PrintUsage()
    {
        Console.WriteLine("Run without arguments to open the window. Command line:");
        Console.WriteLine("Usage: steam-audio-relay-cli.exe [--target <id>] [--source <id>] [--always] [--selftest] [--list] [--verbose]");
        Console.WriteLine("  --list      show playback devices and their ids");
        Console.WriteLine("  --target    device to play into (id from --list). Optional if only one device is named \"System\".");
        Console.WriteLine("  --source    device(s) to copy from, comma separated. Default: all other playback devices of the target's driver.");
        Console.WriteLine("  --always    don't wait for a Steam stream, relay until closed");
        Console.WriteLine("  --selftest  open both devices, run for 2 seconds, exit");
        Console.WriteLine("  --verbose   log the audio level every 5 seconds (to check that signal arrives)");
        Console.WriteLine("  --raw       don't undo the source devices' volume faders (by default the stream gets full level)");
        Console.WriteLine("  --minimized (window mode) start in the tray");
    }

    [STAThread]
    static int Main(string[] args)
    {
        bool cli = false;
        foreach (string f in new[] { "--list", "--help", "-h", "--selftest", "--target", "--source", "--always", "--verbose", "--raw" })
            if (Array.IndexOf(args, f) >= 0) cli = true;
        if (!cli) return RunUi(args);

        AttachConsole(-1); // so the windowed exe can print into the terminal it was started from

        if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0) { PrintUsage(); return 0; }

        var all = ActiveRenderEndpoints();
        if (Array.IndexOf(args, "--list") >= 0) { PrintList(all); return 0; }

        verbose = Array.IndexOf(args, "--verbose") >= 0;
        FullVolume = Array.IndexOf(args, "--raw") < 0;
        bool always = Array.IndexOf(args, "--always") >= 0;
        bool selftest = Array.IndexOf(args, "--selftest") >= 0;

        // target: explicit id, or the one device called "System"
        Endpoint target = null;
        string tgtArg = Arg(args, "--target");
        if (tgtArg != null)
        {
            target = ByGuid(all, tgtArg);
            if (target == null) { Log("No active playback device with id " + tgtArg + ". Try --list."); return 3; }
        }
        else
        {
            var cands = all.FindAll(e => e.Name == "System");
            if (cands.Count == 1) target = cands[0];
            else
            {
                Log(cands.Count == 0 ? "No playback device named \"System\" found." : "More than one playback device is named \"System\", so I can't guess.");
                Log("Pick the right one with --target <id>.");
                PrintList(all);
                return 3;
            }
        }

        bool created;
        using (var mutex = new Mutex(true, "steam-audio-relay", out created))
        {
            if (!created) { Log("Already running."); return 1; }

            // sources: explicit ids (comma separated), or all other channels of the target's device
            Func<List<Endpoint>> getSources;
            string srcArg = Arg(args, "--source");
            if (srcArg != null && srcArg != "auto")
            {
                var fixedSources = new List<Endpoint>();
                foreach (string id in srcArg.Split(','))
                {
                    var e = ByGuid(all, id);
                    if (e == null) { Log("No active playback device with id " + id + ". Try --list."); return 3; }
                    fixedSources.Add(e);
                }
                getSources = delegate { return fixedSources; };
            }
            else
            {
                Endpoint tgt = target;
                getSources = delegate { return SiblingSources(tgt); };
                if (getSources().Count == 0) { Log("The target's device has no other playback devices. Pass --source <id>."); return 3; }
            }
            Log("To: " + target);

            if (selftest)
            {
                var until = DateTime.UtcNow.AddSeconds(2);
                try { RunSession(getSources(), target, () => DateTime.UtcNow < until); Log("Self-test ok."); return 0; }
                catch (Exception e) { Log("Self-test failed: " + e.Message); return 2; }
            }

            Worker(getSources, target, always);
            return 0;
        }
    }
}
