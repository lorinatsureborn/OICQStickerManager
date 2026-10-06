using System.IO;
using System.Text;
using OICQStickerManager.Services;

internal static class KeyLocatorTests
{
    internal static (string Name, Action Run)[] Cases =>
    [
        ("Key locator supports a large DOS header and an unnamed exception section", () => Locate(0x8664)),
        ("Key locator accepts raw alignment padding beyond the virtual section", LocateRawPadding),
        ("Key locator rejects a non-AMD64 module", () => Locate(0xaa64)),
        ("Disposed key watcher cannot start a background poll", DisposedWatcher),
    ];
    private static void LocateRawPadding()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Fixture(0x8664, rawPadding: true));
            Program.Require(QqKeyWatchService.StaticAnalysis.GetKeyFunctionRva(path) == 0x1000,
                "valid aligned sections were rejected or mapped to the wrong function");
        }
        finally { File.Delete(path); }
    }
    private static void DisposedWatcher()
    {
        using var logged = new ManualResetEventSlim();
        var watcher = new QqKeyWatchService(_ => { }, _ => logged.Set());
        watcher.Dispose();
        watcher.Start();
        try { Program.Require(!logged.Wait(300), "disposed watcher started polling again"); }
        finally { watcher.Dispose(); }
    }
    private static void Locate(ushort machine)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Fixture(machine));
            if (machine == 0x8664)
                Program.Require(QqKeyWatchService.StaticAnalysis.GetKeyFunctionRva(path) == 0x1000, "wrong function start");
            else
            {
                try { QqKeyWatchService.StaticAnalysis.GetKeyFunctionRva(path); }
                catch (InvalidDataException) { return; }
                throw new Exception("non-AMD64 machine was accepted");
            }
        }
        finally { File.Delete(path); }
    }
    private static byte[] Fixture(ushort machine, bool rawPadding = false)
    {
        int pe = machine == 0x8664 ? 0x10080 : 0x80;
        int raw = machine == 0x8664 ? 0x10400 : 0x400;
        byte[] bytes = new byte[raw + 1536];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);
        void At(int offset) => stream.Position = offset;
        At(0); writer.Write((ushort)0x5a4d);
        At(0x3c); writer.Write(pe);
        At(pe); writer.Write(0x4550); writer.Write(machine); writer.Write((ushort)3);
        At(pe + 20); writer.Write((ushort)240); writer.Write((ushort)0x2022);
        int opt = pe + 24;
        At(opt); writer.Write((ushort)0x20b);
        At(opt + 32); writer.Write(4096); writer.Write(512);
        At(opt + 56); writer.Write(0x4000); writer.Write(raw);
        At(opt + 108); writer.Write(16);
        At(opt + 112 + 24); writer.Write(0x3000); writer.Write(12);
        void Section(int index, string name, int rva, int pointer, uint flags)
        {
            At(opt + 240 + index * 40);
            writer.Write(Encoding.ASCII.GetBytes(name.PadRight(8, '\0')));
            writer.Write(rawPadding ? 128 : 512); writer.Write(rva); writer.Write(512); writer.Write(pointer);
            At(opt + 240 + index * 40 + 36); writer.Write(flags);
        }
        Section(0, ".text", 0x1000, raw, 0x60000020);
        Section(1, ".rdata", 0x2000, raw + 512, 0x40000040);
        Section(2, machine == 0x8664 ? ".unwind" : ".pdata", 0x3000, raw + 1024, 0x40000040);
        At(raw + 16); writer.Write(new byte[] { 0x48, 0x8d, 0x0d }); writer.Write(0x2000 - (0x1000 + 16 + 7));
        At(raw + 512); writer.Write(Encoding.ASCII.GetBytes("nt_sqlite3_key_v2: db=%p zDb=%s\0"));
        At(raw + 1024); writer.Write(0x1000); writer.Write(0x1040); writer.Write(0x3080);
        return bytes;
    }
}
