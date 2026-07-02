using System;
using Velopack;

namespace SpeedEmulator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
