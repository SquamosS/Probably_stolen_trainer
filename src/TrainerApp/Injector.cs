using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PSTrainerApp;

/// <summary>
/// Loads a managed assembly into a running Unity (Mono) x64 process and calls a static method on it.
/// Uses the game's own mono-2.0-bdwgc.dll exports via small remote stubs (one remote thread per call).
/// </summary>
internal sealed class MonoInjector : IDisposable
{
    private readonly IntPtr h;
    private readonly IntPtr monoBase;
    private readonly Dictionary<string, long> exports;
    private readonly List<IntPtr> allocations = new();
    private IntPtr rootDomain;
    private byte[] preCode; // optional extra instructions run after attach, before the main call

    public MonoInjector(Process p)
    {
        h = OpenProcess(0x1F0FFF, false, p.Id);
        if (h == IntPtr.Zero) throw new InvalidOperationException("Can't open the game process (try running the trainer as administrator).");

        ProcessModule mono = null;
        foreach (ProcessModule m in p.Modules)
            if (m.ModuleName.StartsWith("mono", StringComparison.OrdinalIgnoreCase) && m.ModuleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && m.ModuleName.Contains("2.0", StringComparison.Ordinal))
            { mono = m; break; }
        if (mono == null) throw new InvalidOperationException("Mono runtime not loaded yet - wait for the game's main menu.");
        monoBase = mono.BaseAddress;
        exports = ReadExports(mono.FileName);
    }

    public void Dispose()
    {
        foreach (var a in allocations) VirtualFreeEx(h, a, IntPtr.Zero, 0x8000);
        allocations.Clear();
        if (h != IntPtr.Zero) CloseHandle(h);
    }

    /// <summary>Injects <paramref name="assembly"/> and runs ns.cls.method(string arg).</summary>
    public void InjectAndRun(byte[] assembly, string ns, string cls, string method, string stringArg)
    {
        rootDomain = (IntPtr)Call("mono_get_root_domain", attach: false);
        if (rootDomain == IntPtr.Zero) throw new InvalidOperationException("mono_get_root_domain returned null");

        IntPtr status = Alloc(8);
        IntPtr data = Write(assembly);
        long image = Call("mono_image_open_from_data", true, (long)data, assembly.Length, 1, (long)status);
        if (image == 0) throw new InvalidOperationException("mono_image_open_from_data failed (status " + ReadInt(status) + ")");

        long asm = Call("mono_assembly_load_from_full", true, image, (long)WriteStr("PSTrainerMod"), (long)status, 0);
        if (asm == 0) throw new InvalidOperationException("mono_assembly_load_from_full failed (status " + ReadInt(status) + ")");

        long img = Call("mono_assembly_get_image", true, asm);
        long klass = Call("mono_class_from_name", true, img, (long)WriteStr(ns), (long)WriteStr(cls));
        if (klass == 0) throw new InvalidOperationException("Class " + ns + "." + cls + " not found");
        long meth = Call("mono_class_get_method_from_name", true, klass, (long)WriteStr(method), 1);
        if (meth == 0) throw new InvalidOperationException("Method " + method + " not found");

        // mono_string_new + mono_runtime_invoke in ONE remote thread so the string can't be collected in between.
        IntPtr args = Alloc(8);
        IntPtr exc = Alloc(8);
        IntPtr cstr = WriteStr(stringArg);
        long fnStr = (long)monoBase + exports["mono_string_new"];
        var pre = new List<byte>();
        pre.AddRange(new byte[] { 0x48, 0xB9 }); pre.AddRange(BitConverter.GetBytes((long)rootDomain)); // mov rcx,domain
        pre.AddRange(new byte[] { 0x48, 0xBA }); pre.AddRange(BitConverter.GetBytes((long)cstr));       // mov rdx,cstr
        pre.AddRange(new byte[] { 0x48, 0xB8 }); pre.AddRange(BitConverter.GetBytes(fnStr));            // mov rax,mono_string_new
        pre.AddRange(new byte[] { 0xFF, 0xD0 });                                                        // call rax
        pre.AddRange(new byte[] { 0x48, 0xA3 }); pre.AddRange(BitConverter.GetBytes((long)args));       // mov [args],rax
        preCode = pre.ToArray();
        try { Call("mono_runtime_invoke", true, meth, 0, (long)args, (long)exc); }
        finally { preCode = null; }
        long excObj = ReadLong(exc);
        if (excObj != 0) throw new InvalidOperationException("The trainer code threw an exception inside the game (see the game's Player.log).");
    }

    // ------------------------------------------------------------------ remote calls
    private long Call(string export, bool attach, params long[] a)
    {
        if (!exports.TryGetValue(export, out long rva)) throw new InvalidOperationException("Missing mono export " + export);
        long fn = (long)monoBase + rva;
        IntPtr ret = Alloc(8);
        IntPtr threadSlot = Alloc(8);

        var c = new List<byte>();
        c.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x38 });                       // sub rsp,38h
        if (attach)
        {
            if (!exports.TryGetValue("mono_thread_attach", out long att)) throw new InvalidOperationException("Missing mono_thread_attach");
            c.AddRange(new byte[] { 0x48, 0xB9 }); c.AddRange(BitConverter.GetBytes((long)rootDomain));   // mov rcx,domain
            c.AddRange(new byte[] { 0x48, 0xB8 }); c.AddRange(BitConverter.GetBytes((long)monoBase + att)); // mov rax,attach
            c.AddRange(new byte[] { 0xFF, 0xD0 });                                                         // call rax
            c.AddRange(new byte[] { 0x48, 0xA3 }); c.AddRange(BitConverter.GetBytes((long)threadSlot));   // mov [slot],rax
        }
        if (preCode != null) c.AddRange(preCode);
        long Arg(int i) => i < a.Length ? a[i] : 0;
        c.AddRange(new byte[] { 0x48, 0xB9 }); c.AddRange(BitConverter.GetBytes(Arg(0)));   // mov rcx
        c.AddRange(new byte[] { 0x48, 0xBA }); c.AddRange(BitConverter.GetBytes(Arg(1)));   // mov rdx
        c.AddRange(new byte[] { 0x49, 0xB8 }); c.AddRange(BitConverter.GetBytes(Arg(2)));   // mov r8
        c.AddRange(new byte[] { 0x49, 0xB9 }); c.AddRange(BitConverter.GetBytes(Arg(3)));   // mov r9
        c.AddRange(new byte[] { 0x48, 0xB8 }); c.AddRange(BitConverter.GetBytes(fn));       // mov rax,fn
        c.AddRange(new byte[] { 0xFF, 0xD0 });                                              // call rax
        c.AddRange(new byte[] { 0x48, 0xA3 }); c.AddRange(BitConverter.GetBytes((long)ret)); // mov [ret],rax
        if (attach && exports.TryGetValue("mono_thread_detach", out long det))
        {
            c.AddRange(new byte[] { 0x48, 0xA1 }); c.AddRange(BitConverter.GetBytes((long)threadSlot)); // mov rax,[slot]
            c.AddRange(new byte[] { 0x48, 0x89, 0xC1 });                                              // mov rcx,rax
            c.AddRange(new byte[] { 0x48, 0xB8 }); c.AddRange(BitConverter.GetBytes((long)monoBase + det)); // mov rax,detach
            c.AddRange(new byte[] { 0xFF, 0xD0 });                                                      // call rax
        }
        c.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x38, 0xC3 });                            // add rsp,38h ; ret

        IntPtr code = Alloc(c.Count, executable: true);
        WriteAt(code, c.ToArray());
        IntPtr th = CreateRemoteThread(h, IntPtr.Zero, IntPtr.Zero, code, IntPtr.Zero, 0, out _);
        if (th == IntPtr.Zero) throw new InvalidOperationException("CreateRemoteThread failed (" + Marshal.GetLastWin32Error() + ")");
        uint wait = WaitForSingleObject(th, 15000);
        CloseHandle(th);
        if (wait != 0) throw new InvalidOperationException("Timed out calling " + export);
        return ReadLong(ret);
    }

    private IntPtr Alloc(int size, bool executable = false)
    {
        IntPtr p = VirtualAllocEx(h, IntPtr.Zero, (IntPtr)Math.Max(size, 16), 0x3000, executable ? 0x40u : 0x04u);
        if (p == IntPtr.Zero) throw new InvalidOperationException("VirtualAllocEx failed");
        allocations.Add(p);
        return p;
    }

    private IntPtr Write(byte[] bytes) { IntPtr p = Alloc(bytes.Length); WriteAt(p, bytes); return p; }
    private IntPtr WriteStr(string s) => Write(Encoding.UTF8.GetBytes(s + "\0"));

    private void WriteAt(IntPtr p, byte[] b)
    {
        if (!WriteProcessMemory(h, p, b, (IntPtr)b.Length, out _)) throw new InvalidOperationException("WriteProcessMemory failed");
    }

    private long ReadLong(IntPtr p) { var b = new byte[8]; ReadProcessMemory(h, p, b, (IntPtr)8, out _); return BitConverter.ToInt64(b, 0); }
    private int ReadInt(IntPtr p) => (int)ReadLong(p);

    // ------------------------------------------------------------------ PE export table (read from disk)
    private static Dictionary<string, long> ReadExports(string path)
    {
        byte[] f = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(f, 0x3C);
        int sections = BitConverter.ToUInt16(f, pe + 6);
        int optSize = BitConverter.ToUInt16(f, pe + 20);
        int opt = pe + 24;
        if (BitConverter.ToUInt16(f, opt) != 0x20B) throw new InvalidOperationException("Mono runtime is not 64-bit");
        int expRva = BitConverter.ToInt32(f, opt + 112);
        int secTable = opt + optSize;

        int Off(int rva)
        {
            for (int i = 0; i < sections; i++)
            {
                int s = secTable + i * 40;
                int va = BitConverter.ToInt32(f, s + 12), vs = Math.Max(BitConverter.ToInt32(f, s + 8), BitConverter.ToInt32(f, s + 16));
                int raw = BitConverter.ToInt32(f, s + 20);
                if (rva >= va && rva < va + vs) return rva - va + raw;
            }
            throw new InvalidOperationException("Bad RVA");
        }

        int e = Off(expRva);
        int count = BitConverter.ToInt32(f, e + 24);
        int funcs = Off(BitConverter.ToInt32(f, e + 28));
        int names = Off(BitConverter.ToInt32(f, e + 32));
        int ords = Off(BitConverter.ToInt32(f, e + 36));
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int n = Off(BitConverter.ToInt32(f, names + i * 4));
            int end = Array.IndexOf(f, (byte)0, n);
            string name = Encoding.ASCII.GetString(f, n, end - n);
            int ord = BitConverter.ToUInt16(f, ords + i * 2);
            d[name] = BitConverter.ToUInt32(f, funcs + ord * 4);
        }
        return d;
    }

    // ------------------------------------------------------------------ Win32
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
    [DllImport("kernel32", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, IntPtr size, uint type);
    [DllImport("kernel32", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attr, IntPtr stack, IntPtr start, IntPtr param, uint flags, out uint tid);
    [DllImport("kernel32", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
}
