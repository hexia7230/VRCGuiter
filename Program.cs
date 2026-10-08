using System.Windows.Forms;

namespace VRCGuiter;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--setup")
            return VirtualMic.SetupHelper.RunElevated(args[1], args[2]);

        if (args.Length >= 2 && args[0] == "--selftest")
            return Diagnostics.SelfTest.Run(args[1]);

        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "VRCGuiter エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }
}
