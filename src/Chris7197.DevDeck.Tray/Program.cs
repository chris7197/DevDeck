using System.Threading;
using System.Windows.Forms;

namespace chris7197.DevDeck.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Local\chris7197.DevDeck.Tray", out bool ownsMutex);
        if (!ownsMutex) return;
        ApplicationConfiguration.Initialize();
        using var tray = new TrayApplicationContext();
        Application.Run(tray);
    }
}
