using System.Diagnostics;
using System.Net.Sockets;
using System.Windows.Forms;

namespace Wordbook;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Diag.Log("Program start, args=" + string.Join(",", args));
        try
        {
            var cfg = Config.Load();
            if (args.Contains("--serve"))
            {
                RunServe(cfg);
                return;
            }
            if (PortInUse(cfg.Port))
            {
                Process.Start(new ProcessStartInfo(cfg.HomeUrl) { UseShellExecute = true });
                return;
            }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var host = new AppHost(cfg);
            var openHome = args.Contains("--open");
            Application.Run(new TrayApp(cfg, host, openHome));
        }
        catch (Exception ex)
        {
            try
            {
                var log = Path.Combine(Path.GetTempPath(), "wordbook-crash.log");
                File.AppendAllText(log, DateTime.Now + " " + ex + Environment.NewLine);
                MessageBox.Show("生词本启动失败，详见日志：\n" + log + "\n\n" + ex.Message, "生词本",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* 忽略 */ }
        }
    }

    private static void RunServe(Config cfg)
    {
        using var host = new AppHost(cfg);
        host.Start();
        var done = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, _) => done.Set();
        done.Wait();
    }

    private static bool PortInUse(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(400) && client.Connected;
        }
        catch { return false; }
    }
}
