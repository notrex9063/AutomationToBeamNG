using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Automation to BeamNG Engine Converter")]
[assembly: System.Reflection.AssemblyProduct("AutomationToBeamNG")]
[assembly: System.Reflection.AssemblyVersion("3.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("3.1.0.0")]

namespace AutomationToBeamNG
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--make-icon") { WriteIcon(args[1]); return; }   // used by build.bat
            if (args.Length >= 2 && args[0] == "--export-parts") { ExportParts(args[1], args.Length > 2 ? args[2] : "."); return; }
            try { SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception);
            var settings = Settings.Load();
            Ui.Init(settings.Theme);
            Application.Run(new MainForm(settings, args.Length > 0 ? args[0] : null));
        }

        // Multi-size .ico (PNG frames) for the exe's file icon.
        static void WriteIcon(string path)
        {
            Ui.Init("Light");
            var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
            var frames = sizes.Select(sz =>
            {
                using (var bmp = AppIcon.Render(sz))
                using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); return ms.ToArray(); }
            }).ToList();
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(frames[i].Length); w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
            }
        }

        // AutomationToBeamNG.exe --export-parts <name or regex> [folder]
        // Writes each matching Automation engine part as <folder>\<part>\<part>.dae with its
        // materials and textures, plus export-log.txt.
        public static string ExportParts(string pattern, string outDir)
        {
            var log = new System.Text.StringBuilder();
            try
            {
                Directory.CreateDirectory(outDir);
                using (var pak = new PakArchive(PakArchive.FindGame()))
                {
                    var rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (var part in AutomationParts.EngineParts(pak).Where(p => rx.IsMatch(AutomationParts.ShortName(p))))
                    {
                        UMesh mesh;
                        try { mesh = AutomationParts.LoadMesh(pak, part); }
                        catch (NotSupportedException) { continue; }
                        catch (Exception ex) { log.AppendLine(part + ": " + ex.Message); continue; }
                        string name = "a2b_" + System.Text.RegularExpressions.Regex.Replace(mesh.Name, "[^A-Za-z0-9]+", "_").ToLowerInvariant();
                        string dir = Path.Combine(outDir, mesh.Name);
                        Directory.CreateDirectory(dir);
                        var res = AutomationParts.Export(pak, new[] { new AutomationParts.Placed { Mesh = mesh } }, name, "", null);
                        foreach (var f in res.Files) File.WriteAllBytes(Path.Combine(dir, f.Key), f.Value);
                        log.AppendLine(part + ": " + res.Triangles + " triangles, " + mesh.Sockets.Count + " sockets" + (mesh.Sockets.Count > 0 ? " (" + string.Join(", ", mesh.Sockets.Select(s => s.Name)) + ")" : ""));
                        foreach (var w in res.Warnings) log.AppendLine("   " + w);
                    }
                }
            }
            catch (Exception ex) { log.AppendLine("failed: " + ex.Message); }
            try { File.WriteAllText(Path.Combine(outDir, "export-log.txt"), log.ToString()); } catch (Exception) { }
            return log.ToString();
        }

        static void ShowCrash(Exception ex)
        {
            MessageBox.Show("Something went wrong:\n\n" + (ex != null ? ex.Message : "unknown error") + "\n\n" + (ex != null ? ex.StackTrace : ""),
                "Automation to BeamNG", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
