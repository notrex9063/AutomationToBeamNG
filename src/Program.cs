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

        static void ShowCrash(Exception ex)
        {
            MessageBox.Show("Something went wrong:\n\n" + (ex != null ? ex.Message : "unknown error") + "\n\n" + (ex != null ? ex.StackTrace : ""),
                "Automation to BeamNG", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
