using System;
using System.Net;
using Velopack;

namespace SpeedEmulator;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Stirptkm.License.Apply("C587FB8C-C434-4A98-87E3-2BEDD6DFAA1A");

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        VelopackApp.Build()
            .SetArgs(args)
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
