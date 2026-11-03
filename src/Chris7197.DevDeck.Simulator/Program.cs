using System.Windows.Forms;

namespace chris7197.DevDeck.Simulator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SimulatorForm(args.Length > 0 ? args[0] : null));
    }
}
