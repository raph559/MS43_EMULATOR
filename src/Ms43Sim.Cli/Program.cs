namespace Ms43Sim.Cli;

public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        error.WriteLine("usage: ms43sim <command> [options]");
        return 2;
    }
}
