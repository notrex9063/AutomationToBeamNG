using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace AutomationToBeamNG
{
    // All engines (and the live link) go into one unpacked mod, mods/unpacked/AutoBeam, so BeamNG's mods
    // tab shows a single entry. Each engine lists the files it wrote in automation_to_beamng/<engine>.files;
    // files several engines write (the torque controller, Automation's engine scripts) stay until the last
    // engine using them is removed. BeamNG reads an unpacked mod straight from disk, so nothing has to be
    // unmounted to update it.
    static class AutoBeamPack
    {
        public const string Name = "AutoBeam";
        const string ListExt = ".files";

        public static string Dir(string mods) { return Path.Combine(mods, "unpacked", Name); }

        static string Full(string dir, string rel) { return Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)); }

        static List<string> Owned(string dir, string safe)
        {
            string f = Path.Combine(dir, Converter.MetaFolder.TrimEnd('/'), safe + ListExt);
            return File.Exists(f) ? File.ReadAllLines(f).Where(l => l.Length > 0).ToList() : new List<string>();
        }

        public static IEnumerable<string> Engines(string mods)
        {
            string meta = Path.Combine(Dir(mods), Converter.MetaFolder.TrimEnd('/'));
            if (!Directory.Exists(meta)) return new string[0];
            return Directory.GetFiles(meta, "*" + ListExt).Select(Path.GetFileNameWithoutExtension);
        }

        // files any other engine still uses
        static HashSet<string> OthersOwn(string mods, string safe)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Engines(mods))
                if (!e.Equals(safe, StringComparison.OrdinalIgnoreCase)) set.UnionWith(Owned(Dir(mods), e));
            return set;
        }

        static void Write(string path, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            for (int attempt = 0; ; attempt++)
            {
                try { File.WriteAllBytes(path, data); return; }
                catch (IOException) { if (attempt >= 20) throw; Thread.Sleep(100); }   // BeamNG reading it right now
            }
        }

        static void DeleteFile(string dir, string rel)
        {
            string p = Full(dir, rel);
            try { if (File.Exists(p)) File.Delete(p); } catch (Exception) { }
            // tidy up folders left empty
            try
            {
                for (string d = Path.GetDirectoryName(p); d != null && d.Length > dir.Length && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any(); d = Path.GetDirectoryName(d))
                    Directory.Delete(d);
            }
            catch (Exception) { }
        }

        // Writes (or replaces) one engine's files. Returns the files that changed, were added or removed.
        public static List<string> Install(string mods, string safe, List<KeyValuePair<string, byte[]>> files)
        {
            string dir = Dir(mods);
            Directory.CreateDirectory(dir);
            var others = OthersOwn(mods, safe);
            var now = new HashSet<string>(files.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
            var changed = new List<string>();
            foreach (var old in Owned(dir, safe))
                if (!now.Contains(old) && !others.Contains(old)) { DeleteFile(dir, old); changed.Add(old); }
            foreach (var f in files)
            {
                string p = Full(dir, f.Key);
                if (File.Exists(p) && new FileInfo(p).Length == f.Value.Length && File.ReadAllBytes(p).SequenceEqual(f.Value)) continue;
                Write(p, f.Value);
                changed.Add(f.Key);
            }
            Write(Path.Combine(dir, Converter.MetaFolder.TrimEnd('/'), safe + ListExt), new UTF8Encoding(false).GetBytes(string.Join("\r\n", now.OrderBy(x => x)) + "\r\n"));
            return changed;
        }

        // Removes one engine's files (not ones another engine still uses). Returns the files removed.
        public static List<string> Remove(string mods, string safe)
        {
            string dir = Dir(mods);
            var others = OthersOwn(mods, safe);
            var gone = new List<string>();
            foreach (var f in Owned(dir, safe))
                if (!others.Contains(f)) { DeleteFile(dir, f); gone.Add(f); }
            DeleteFile(dir, Converter.MetaFolder + safe + ListExt);
            DeleteFile(dir, Converter.MetaFolder + safe + ".txt");
            return gone;
        }

        public static long Size(string mods, string safe)
        {
            long n = 0;
            foreach (var f in Owned(Dir(mods), safe))
            {
                try { var fi = new FileInfo(Full(Dir(mods), f)); if (fi.Exists) n += fi.Length; } catch (Exception) { }
            }
            return n;
        }

        public static List<string> Files(string mods, string safe) { return Owned(Dir(mods), safe); }

        // The separate automation_*.zip mods from before the pack. Moved in while BeamNG is closed.
        public static List<string> LegacyZips(string mods)
        {
            if (string.IsNullOrEmpty(mods) || !Directory.Exists(mods)) return new List<string>();
            return new DirectoryInfo(mods).GetFiles("automation_*.zip").Select(f => f.FullName)
                .Concat(File.Exists(Path.Combine(mods, LiveLink.ModZip)) ? new[] { Path.Combine(mods, LiveLink.ModZip) } : new string[0]).ToList();
        }

        // Moves every old engine zip into the pack and deletes it. Only while BeamNG is closed.
        // Returns the engines moved; failures are added to errors.
        public static List<string> Migrate(string mods, List<string> errors)
        {
            var moved = new List<string>();
            if (BeamNGProcess.IsRunning()) return moved;
            foreach (var zip in LegacyZips(mods))
            {
                string file = Path.GetFileName(zip);
                try
                {
                    if (file.Equals(LiveLink.ModZip, StringComparison.OrdinalIgnoreCase)) { File.Delete(zip); continue; }   // the pack has its own link
                    string safe = Path.GetFileNameWithoutExtension(zip).Substring("automation_".Length);
                    var files = new List<KeyValuePair<string, byte[]>>();
                    using (var z = ZipFile.OpenRead(zip))
                        foreach (var e in z.Entries.Where(e => !e.FullName.EndsWith("/")))
                            files.Add(new KeyValuePair<string, byte[]>(e.FullName, JBeam.ReadBytes(e)));
                    Install(mods, safe, files);
                    File.Delete(zip);
                    moved.Add(safe);
                }
                catch (Exception ex) { errors.Add(file + ": " + ex.Message); }
            }
            return moved;
        }
    }
}
