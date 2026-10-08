using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;

namespace VRCGuiter.VirtualMic;

public static class SetupHelper
{
    public static int RunElevated(string installerDir, string logPath)
    {
        var log = new StringBuilder();
        int code;
        try
        {
            code = Run(installerDir, log);
        }
        catch (Exception ex)
        {
            log.AppendLine("例外: " + ex);
            code = 4;
        }
        try { File.WriteAllText(logPath, log.ToString()); } catch { }
        return code;
    }

    private static int Run(string installerDir, StringBuilder log)
    {
        using var en = new MMDeviceEnumerator();
        var cable = VirtualCable.FindVbCable(en);

        if (cable == null && installerDir != "-")
        {
            string exe = Path.Combine(installerDir, VbCableSetup.InstallerName);
            if (!File.Exists(exe)) { log.AppendLine("インストーラーが見つかりません: " + exe); return 3; }
            log.AppendLine("インストーラー実行: " + exe + " -i -h");
            var psi = new ProcessStartInfo(exe, "-i -h")
            {
                UseShellExecute = false,
                WorkingDirectory = installerDir,
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) { log.AppendLine("インストーラーを起動できませんでした"); return 3; }
                if (!p.WaitForExit((int)TimeSpan.FromMinutes(15).TotalMilliseconds)) { log.AppendLine("インストーラーがタイムアウト"); return 3; }
                log.AppendLine("インストーラー終了コード: " + p.ExitCode);
            }
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (cable == null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1500);
                try { cable = VirtualCable.FindVbCable(en); } catch { }
            }
            if (cable == null) { log.AppendLine("インストール後もデバイスが見つかりません（再起動が必要かもしれません）"); return 2; }
            log.AppendLine("検出: " + cable.Render.Name + " / " + cable.Capture.Name);
        }
        if (cable == null) { log.AppendLine("VB-CABLE が見つかりません"); return 2; }

        if (!cable.Capture.Name.StartsWith(VirtualCable.VrcgName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                EndpointRenamer.Rename(cable.Capture.Id, VirtualCable.VrcgName);
                log.AppendLine("改名: " + cable.Capture.Name + " -> " + VirtualCable.VrcgName);
            }
            catch (Exception ex)
            {
                log.AppendLine("改名失敗: " + ex);
                return 4;
            }
        }
        else log.AppendLine("既に VRCG です");
        return 0;
    }
}
