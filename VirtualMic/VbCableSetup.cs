using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using NAudio.CoreAudioApi;

namespace VRCGuiter.VirtualMic;

public sealed record SetupResult(bool Ok, string Message);

public static class VbCableSetup
{
    public const string ZipUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
    public const string PageUrl = "https://vb-audio.com/Cable/";
    public const string InstallerName = "VBCABLE_Setup_x64.exe";

    public static string WorkDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCGuiter", "vbcable");

    public static string LogPath => Path.Combine(WorkDir, "setup.log");

    public static async Task<SetupResult> RunAsync(IProgress<string> progress, CancellationToken ct)
    {
        bool installed;
        using (var en = new MMDeviceEnumerator()) installed = VirtualCable.FindVbCable(en) != null;

        string installerDir = "-";
        if (!installed)
        {
            try
            {
                Directory.CreateDirectory(WorkDir);
                string zip = Path.Combine(WorkDir, "VBCABLE_Driver_Pack45.zip");
                string exe = Path.Combine(WorkDir, InstallerName);
                if (!File.Exists(exe))
                {
                    progress.Report("VB-CABLE をダウンロード中…");
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("VRCGuiter/1.0");
                    var bytes = await http.GetByteArrayAsync(ZipUrl, ct);
                    await File.WriteAllBytesAsync(zip, bytes, ct);
                    progress.Report("展開中…");
                    ZipFile.ExtractToDirectory(zip, WorkDir, true);
                }
                if (!File.Exists(exe))
                    return new SetupResult(false, "VB-CABLE のインストーラーが見つかりませんでした。\n" + PageUrl + " から手動でインストールしてから、もう一度「VRCG を作成」を押してください。");
                installerDir = WorkDir;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new SetupResult(false, "VB-CABLE のダウンロードに失敗しました（" + ex.Message + "）。\n" +
                    "インターネット接続を確認するか、" + PageUrl + " から手動でインストールしてから、もう一度「VRCG を作成」を押してください。");
            }
        }

        progress.Report(installed
            ? "「VRCG」に名前を設定中…（管理者権限の確認が出ます）"
            : "インストール中… Windows のドライバ確認が出たら「インストール」を押してください");

        Directory.CreateDirectory(WorkDir);
        try { File.Delete(LogPath); } catch { }

        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = WorkDir,
        };
        psi.ArgumentList.Add("--setup");
        psi.ArgumentList.Add(installerDir);
        psi.ArgumentList.Add(LogPath);

        int code;
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return new SetupResult(false, "セットアップ用プロセスを起動できませんでした。");
            await p.WaitForExitAsync(ct);
            code = p.ExitCode;
        }
        catch (Win32Exception)
        {
            return new SetupResult(false, "管理者権限の確認がキャンセルされました。");
        }

        string log = "";
        try { if (File.Exists(LogPath)) log = File.ReadAllText(LogPath); } catch { }

        return code switch
        {
            0 => new SetupResult(true, "仮想マイク「VRCG」の準備ができました。"),
            2 => new SetupResult(false, "VB-CABLE をインストールしましたが、まだデバイスが見えません。\nWindows を再起動してから、もう一度このアプリを起動してください。\n\n" + log),
            3 => new SetupResult(false, "VB-CABLE のインストールに失敗しました。ドライバ確認で「インストール」を押したか確認してください。\n\n" + log),
            4 => new SetupResult(false, "VB-CABLE は入りましたが「VRCG」への改名に失敗しました。Windows のサウンド設定で「CABLE Output」を選んでも使えます。\n\n" + log),
            _ => new SetupResult(false, "セットアップが完了しませんでした（コード " + code + "）。\n\n" + log),
        };
    }
}
