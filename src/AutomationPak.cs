// Read access to Automation's own game files. Automation is an Unreal Engine 4.27 game whose
// content sits in Content\Paks\*.pak (pak version 11, not encrypted, entries stored uncompressed).
// PakArchive indexes every pak and reads a file by its path; UPackage parses a cooked
// .uasset/.uexp pair (name map, imports, exports) and PropReader reads its tagged properties.
// Nothing is copied out of the game folder except what the user asks to export.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutomationToBeamNG
{
    class PakArchive : IDisposable
    {
        class Entry { public int Pak; public long Offset, Size; }

        readonly List<FileStream> paks = new List<FileStream>();
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        public readonly string GameDir;

        // gameDir: the folder holding AutomationGame\Content\Paks (Automation\UE427)
        public PakArchive(string gameDir)
        {
            GameDir = gameDir;
            string dir = Path.Combine(gameDir, @"AutomationGame\Content\Paks");
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("Automation's game files weren't found in " + gameDir);
            foreach (var f in Directory.GetFiles(dir, "*.pak").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
                paks.Add(fs);
                try { ReadIndex(fs, paks.Count - 1); }
                catch (Exception ex) { throw new InvalidDataException(Path.GetFileName(f) + ": " + ex.Message, ex); }
            }
        }

        public void Dispose() { foreach (var p in paks) p.Dispose(); paks.Clear(); }

        public IEnumerable<string> Files { get { return entries.Keys; } }
        public bool Exists(string path) { return entries.ContainsKey(path); }

        // "/Game/Engines/Meshes/X" (an object path) -> "AutomationGame/Content/Engines/Meshes/X"
        public static string FromObjectPath(string objectPath)
        {
            string p = objectPath;
            int dot = p.LastIndexOf('.');
            if (dot > p.LastIndexOf('/')) p = p.Substring(0, dot);
            if (p.StartsWith("/Game/")) return "AutomationGame/Content/" + p.Substring(6);
            if (p.StartsWith("/Engine/")) return "Engine/Content/" + p.Substring(8);
            return p.TrimStart('/');
        }

        public byte[] Read(string path)
        {
            Entry e;
            if (!entries.TryGetValue(path, out e)) return null;
            var fs = paks[e.Pak];
            var buf = new byte[e.Size];
            lock (fs)
            {
                fs.Seek(e.Offset, SeekOrigin.Begin);
                int got = 0;
                while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n <= 0) throw new EndOfStreamException(path); got += n; }
            }
            return buf;
        }

        static string FStr(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n == 0) return "";
            if (n < 0) { var b = r.ReadBytes(-n * 2); return Encoding.Unicode.GetString(b, 0, b.Length - 2); }
            var a = r.ReadBytes(n);
            return Encoding.UTF8.GetString(a, 0, a.Length - 1);
        }

        const uint PakMagic = 0x5A6F12E1;
        // Serialised FPakEntry header in front of an uncompressed, unencrypted entry's data (v11):
        // offset, size, uncompressed size (3 x int64), compression method (int32), SHA1 (20), flags (1), block size (4)
        const int EntryHeader = 53;

        void ReadIndex(FileStream fs, int pakNo)
        {
            var r = new BinaryReader(fs);
            fs.Seek(-204, SeekOrigin.End);
            if (r.ReadUInt32() != PakMagic) throw new InvalidDataException("not a supported pak (footer)");
            int version = r.ReadInt32();
            if (version < 10) throw new InvalidDataException("pak version " + version + " isn't supported");
            long indexOffset = r.ReadInt64();
            fs.Seek(-205, SeekOrigin.End);
            if (fs.ReadByte() != 0) throw new InvalidDataException("the pak index is encrypted");

            fs.Seek(indexOffset, SeekOrigin.Begin);
            string mount = FStr(r).Replace("../../../", "");
            r.ReadInt32();      // entry count
            r.ReadUInt64();     // path hash seed
            if (r.ReadInt32() != 0) { r.ReadInt64(); r.ReadInt64(); r.ReadBytes(20); }
            if (r.ReadInt32() == 0) return;     // no full directory index
            long dirOffset = r.ReadInt64(); r.ReadInt64(); r.ReadBytes(20);
            byte[] encoded = r.ReadBytes(r.ReadInt32());
            int plainCount = r.ReadInt32();
            var plain = new List<long[]>();     // offset, size, method
            for (int i = 0; i < plainCount; i++)
            {
                long off = r.ReadInt64(); r.ReadInt64(); long usize = r.ReadInt64();
                uint method = r.ReadUInt32();
                r.ReadBytes(20);
                if (method != 0) { int blocks = r.ReadInt32(); r.ReadBytes(blocks * 16); }
                byte flags = r.ReadByte(); r.ReadUInt32();
                plain.Add(new[] { off, usize, (long)method | ((flags & 1) != 0 ? 0x100L : 0L) });
            }

            fs.Seek(dirOffset, SeekOrigin.Begin);
            int dirs = r.ReadInt32();
            for (int i = 0; i < dirs; i++)
            {
                string dir = FStr(r);
                int files = r.ReadInt32();
                for (int j = 0; j < files; j++)
                {
                    string name = FStr(r);
                    int at = r.ReadInt32();
                    long off, size, method;
                    bool encrypted;
                    if (at >= 0)
                    {
                        uint v = BitConverter.ToUInt32(encoded, at);
                        int p = at + 4;
                        method = (v >> 23) & 0x3f;
                        encrypted = ((v >> 22) & 1) != 0;
                        if (((v >> 31) & 1) != 0) { off = BitConverter.ToUInt32(encoded, p); p += 4; } else { off = BitConverter.ToInt64(encoded, p); p += 8; }
                        if (((v >> 30) & 1) != 0) { size = BitConverter.ToUInt32(encoded, p); } else { size = BitConverter.ToInt64(encoded, p); }
                    }
                    else
                    {
                        var pe = plain[-at - 1];
                        off = pe[0]; size = pe[1]; method = pe[2] & 0xff; encrypted = (pe[2] & 0x100) != 0;
                    }
                    if (method != 0 || encrypted) continue;     // never seen in Automation; skipped rather than misread
                    string path = mount + dir + name;
                    if (!entries.ContainsKey(path)) entries[path] = new Entry { Pak = pakNo, Offset = off + EntryHeader, Size = size };
                }
            }
        }

        // Automation's UE4 folder (…\steamapps\common\Automation\UE427) from Steam's library list
        public static string FindGame()
        {
            try
            {
                var libs = new List<string>();
                string steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrEmpty(steam)) libs.Add(steam.Replace('/', '\\'));
                libs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
                foreach (var root in libs.ToList())
                {
                    string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
                foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string c = Path.Combine(lib, @"steamapps\common\Automation\UE427");
                    if (Directory.Exists(Path.Combine(c, @"AutomationGame\Content\Paks"))) return c;
                }
            }
            catch (Exception) { }
            return "";
        }
    }

    // A cooked UE 4.27 package: the .uasset header followed by the .uexp export data.
    class UPackage
    {
        public class Import { public string ClassPackage, ClassName, Name; public int Outer; }
        public class Export { public int Class, Super, Template, Outer; public string Name; public long SerialSize, SerialOffset; }

        public readonly string Path;                 // pak path without extension
        public readonly byte[] Data;
        public readonly List<string> Names = new List<string>();
        public readonly List<Import> Imports = new List<Import>();
        public readonly List<Export> Exports = new List<Export>();
        public int FileVersion;
        public uint PackageFlags;
        public long BulkDataStart;
        readonly PakArchive pak;
        byte[] ubulk, uptnl;

        public static UPackage Load(PakArchive pak, string objectPath)
        {
            string p = PakArchive.FromObjectPath(objectPath);
            var head = pak.Read(p + ".uasset");
            if (head == null) return null;
            var exp = pak.Read(p + ".uexp") ?? new byte[0];
            var all = new byte[head.Length + exp.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(exp, 0, all, head.Length, exp.Length);
            return new UPackage(pak, p, all);
        }

        UPackage(PakArchive pak, string path, byte[] data)
        {
            this.pak = pak; Path = path; Data = data;
            var r = new BinaryReader(new MemoryStream(data));
            if (r.ReadUInt32() != 0x9E2A83C1) throw new InvalidDataException(path + " isn't a UE package");
            int legacy = r.ReadInt32();
            if (legacy != -4) r.ReadInt32();
            FileVersion = r.ReadInt32();
            if (FileVersion == 0) FileVersion = 522;     // cooked packages leave it out: UE 4.27
            r.ReadInt32();     // licensee version
            if (legacy <= -2) { int n = r.ReadInt32(); r.ReadBytes(n * 20); }
            r.ReadInt32();     // total header size
            ReadFString(r);    // folder name
            PackageFlags = r.ReadUInt32();
            if ((PackageFlags & 0x2000) != 0) throw new InvalidDataException(path + " uses unversioned properties, which aren't supported");
            int nameCount = r.ReadInt32(), nameOffset = r.ReadInt32();
            if (FileVersion >= 516 && (PackageFlags & 0x80000000) == 0) ReadFString(r);
            if (FileVersion >= 459) { r.ReadInt32(); r.ReadInt32(); }
            int exportCount = r.ReadInt32(), exportOffset = r.ReadInt32();
            int importCount = r.ReadInt32(), importOffset = r.ReadInt32();
            r.ReadInt32();     // depends offset
            if (FileVersion >= 384) { r.ReadInt32(); r.ReadInt32(); }
            if (FileVersion >= 510) r.ReadInt32();
            r.ReadInt32();     // thumbnail table
            r.ReadBytes(16);   // guid
            int gens = r.ReadInt32(); r.ReadBytes(gens * 8);
            if (FileVersion >= 336) { r.ReadBytes(10); ReadFString(r); }
            if (FileVersion >= 444) { r.ReadBytes(10); ReadFString(r); }
            r.ReadUInt32();    // compression flags
            int chunks = r.ReadInt32(); r.ReadBytes(chunks * 16);
            r.ReadUInt32();    // package source
            int extra = r.ReadInt32(); for (int i = 0; i < extra; i++) ReadFString(r);
            if (legacy > -7) r.ReadInt32();
            r.ReadInt32();     // asset registry offset
            BulkDataStart = r.ReadInt64();

            r.BaseStream.Position = nameOffset;
            for (int i = 0; i < nameCount; i++) { Names.Add(ReadFString(r)); if (FileVersion >= 504) r.ReadUInt32(); }

            r.BaseStream.Position = importOffset;
            for (int i = 0; i < importCount; i++)
                Imports.Add(new Import { ClassPackage = ReadName(r), ClassName = ReadName(r), Outer = r.ReadInt32(), Name = ReadName(r) });

            r.BaseStream.Position = exportOffset;
            for (int i = 0; i < exportCount; i++)
            {
                var e = new Export { Class = r.ReadInt32(), Super = r.ReadInt32() };
                if (FileVersion >= 508) e.Template = r.ReadInt32();
                e.Outer = r.ReadInt32();
                e.Name = ReadName(r);
                r.ReadUInt32();    // object flags
                e.SerialSize = r.ReadInt64();
                e.SerialOffset = r.ReadInt64();
                r.ReadBytes(12);   // forced export, not for client, not for server
                r.ReadBytes(16);   // package guid
                r.ReadUInt32();    // package flags
                r.ReadInt32();     // not always loaded for editor game
                r.ReadInt32();     // is asset
                r.ReadBytes(20);   // dependency header
                Exports.Add(e);
            }
        }

        public static string ReadFString(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n == 0) return "";
            if (n < 0) { var b = r.ReadBytes(-n * 2); return Encoding.Unicode.GetString(b, 0, b.Length - 2); }
            var a = r.ReadBytes(n);
            return Encoding.UTF8.GetString(a, 0, a.Length - 1);
        }

        public string ReadName(BinaryReader r)
        {
            int i = r.ReadInt32(), num = r.ReadInt32();
            string s = i >= 0 && i < Names.Count ? Names[i] : "?";
            return num > 0 ? s + "_" + (num - 1) : s;
        }

        public string ClassOf(Export e)
        {
            if (e.Class < 0) return Imports[-e.Class - 1].Name;
            if (e.Class > 0) return Exports[e.Class - 1].Name;
            return "Class";
        }

        public Export FirstExport(string className)
        {
            return Exports.FirstOrDefault(e => ClassOf(e) == className);
        }

        // The object path of an import, e.g. "/Game/Engines/Materials/MI_X.MI_X"
        public string ImportPath(int index)
        {
            if (index >= 0) return null;
            var im = Imports[-index - 1];
            if (im.Outer == 0) return im.Name;
            string outer = ImportPath(im.Outer);
            return outer == null ? im.Name : outer + (im.Outer < 0 && Imports[-im.Outer - 1].Outer == 0 ? "." : ":") + im.Name;
        }

        public string ImportClass(int index) { return index < 0 ? Imports[-index - 1].ClassName : null; }

        public BinaryReader ExportReader(Export e)
        {
            var ms = new MemoryStream(Data, 0, Data.Length, false, true);
            ms.Position = e.SerialOffset;
            return new BinaryReader(ms);
        }

        public Dictionary<string, object> Properties(Export e)
        {
            var r = ExportReader(e);
            return new PropReader(this, r).ReadProps();
        }

        // Bulk data stored after the export data or in the .ubulk / .uptnl next to the package
        public byte[] Bulk(uint flags, long offset, long size)
        {
            const uint AtEndOfFile = 0x1, SeparateFile = 0x100, Optional = 0x800, NoOffsetFixUp = 0x10000;
            byte[] src;
            long at = offset;
            if ((flags & SeparateFile) != 0)
            {
                if ((flags & Optional) != 0) { if (uptnl == null) uptnl = pak.Read(Path + ".uptnl") ?? new byte[0]; src = uptnl; }
                else { if (ubulk == null) ubulk = pak.Read(Path + ".ubulk") ?? new byte[0]; src = ubulk; }
                if ((flags & NoOffsetFixUp) == 0 && at + size > src.Length) at -= BulkDataStart;
            }
            else
            {
                src = Data;
                if ((flags & AtEndOfFile) != 0 && (flags & NoOffsetFixUp) == 0) at += BulkDataStart;
            }
            if (at < 0 || at + size > src.Length) return null;
            var b = new byte[size];
            Buffer.BlockCopy(src, (int)at, b, 0, (int)size);
            return b;
        }
    }

    // A reference to another object: an export of the same package or an import.
    class UObjectRef
    {
        public int Index;
        public UObjectRef(int i) { Index = i; }
        public override string ToString() { return "ref " + Index; }
    }

    // Tagged (versioned) property serialisation. Values come back as int, float, bool, string
    // (names, strings, enums), UObjectRef, float[] (vectors, rotators, colours), nested
    // dictionaries (structs) and lists (arrays).
    class PropReader
    {
        readonly UPackage pkg;
        readonly BinaryReader r;
        public PropReader(UPackage pkg, BinaryReader r) { this.pkg = pkg; this.r = r; }

        class Tag { public string Name, Type, StructName, Inner, Value, EnumName; public int Size, Index; public bool Bool; }

        Tag ReadTag()
        {
            var t = new Tag { Name = pkg.ReadName(r) };
            if (t.Name == "None") return null;
            t.Type = pkg.ReadName(r);
            t.Size = r.ReadInt32();
            t.Index = r.ReadInt32();
            switch (t.Type)
            {
                case "StructProperty": t.StructName = pkg.ReadName(r); if (pkg.FileVersion >= 441) r.ReadBytes(16); break;
                case "BoolProperty": t.Bool = r.ReadByte() != 0; break;
                case "ByteProperty": case "EnumProperty": t.EnumName = pkg.ReadName(r); break;
                case "ArrayProperty": case "SetProperty": t.Inner = pkg.ReadName(r); break;
                case "MapProperty": t.Inner = pkg.ReadName(r); t.Value = pkg.ReadName(r); break;
            }
            if (pkg.FileVersion >= 503 && r.ReadByte() != 0) r.ReadBytes(16);
            return t;
        }

        public Dictionary<string, object> ReadProps()
        {
            var d = new Dictionary<string, object>();
            for (;;)
            {
                var t = ReadTag();
                if (t == null) return d;
                long start = r.BaseStream.Position;
                object v = null;
                try { v = ReadValue(t); }
                catch (Exception) { v = null; }
                r.BaseStream.Position = start + t.Size;
                string key = t.Index > 0 ? t.Name + "[" + t.Index + "]" : t.Name;
                d[key] = v;
            }
        }

        object ReadValue(Tag t)
        {
            switch (t.Type)
            {
                case "BoolProperty": return t.Bool;
                case "StructProperty": return ReadStruct(t.StructName);
                case "ByteProperty": return t.EnumName == "None" ? (object)(int)r.ReadByte() : pkg.ReadName(r);
                case "ArrayProperty":
                    {
                        int n = r.ReadInt32();
                        var list = new List<object>();
                        if (t.Inner == "StructProperty")
                        {
                            var inner = ReadTag();
                            for (int i = 0; i < n; i++) list.Add(ReadStruct(inner.StructName));
                        }
                        else for (int i = 0; i < n; i++) list.Add(ReadSimple(t.Inner, t.Size / Math.Max(1, n)));
                        return list;
                    }
                default: return ReadSimple(t.Type, t.Size);
            }
        }

        object ReadSimple(string type, int size)
        {
            switch (type)
            {
                case "IntProperty": return r.ReadInt32();
                case "UInt32Property": return (int)r.ReadUInt32();
                case "Int64Property": return r.ReadInt64();
                case "FloatProperty": return r.ReadSingle();
                case "DoubleProperty": return r.ReadDouble();
                case "BoolProperty": return r.ReadByte() != 0;
                case "ByteProperty": return size == 1 ? (object)(int)r.ReadByte() : pkg.ReadName(r);
                case "EnumProperty": case "NameProperty": return pkg.ReadName(r);
                case "StrProperty": return UPackage.ReadFString(r);
                case "ObjectProperty": case "ClassProperty": case "InterfaceProperty": return new UObjectRef(r.ReadInt32());
                case "SoftObjectProperty": case "SoftClassProperty": { string a = pkg.ReadName(r); string b = UPackage.ReadFString(r); return a + (b.Length > 0 ? ":" + b : ""); }
                case "TextProperty": return null;
                default: throw new NotSupportedException(type);
            }
        }

        object ReadStruct(string name)
        {
            switch (name)
            {
                case "Vector": case "Rotator": return Floats(3);
                case "Vector2D": return Floats(2);
                case "Vector4": case "Quat": case "LinearColor": case "Plane": return Floats(4);
                case "Color": { var b = r.ReadBytes(4); return new float[] { b[2] / 255f, b[1] / 255f, b[0] / 255f, b[3] / 255f }; }
                case "Guid": r.ReadBytes(16); return null;
                case "IntPoint": return new float[] { r.ReadInt32(), r.ReadInt32() };
                case "IntVector": return new float[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32() };
                case "Box": { var f = Floats(6); r.ReadByte(); return f; }
                case "Box2D": { var f = Floats(4); r.ReadByte(); return f; }
                case "BoxSphereBounds": return Floats(7);
                case "SoftObjectPath": case "SoftClassPath": { string a = pkg.ReadName(r); string b = UPackage.ReadFString(r); return a + (b.Length > 0 ? ":" + b : ""); }
                case "PerPlatformFloat": r.ReadInt32(); return r.ReadSingle();
                case "PerPlatformInt": case "PerPlatformBool": r.ReadInt32(); return r.ReadInt32();
                case "FrameNumber": return r.ReadInt32();
                case "DateTime": case "Timespan": return r.ReadInt64();
                default: return ReadProps();
            }
        }

        float[] Floats(int n) { var f = new float[n]; for (int i = 0; i < n; i++) f[i] = r.ReadSingle(); return f; }
    }
}
