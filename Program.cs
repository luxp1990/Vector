using System;
using Vector.Services;

namespace Vector;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (McpStdioBridge.HasBridgeArg(args))
            return McpStdioBridge.Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
