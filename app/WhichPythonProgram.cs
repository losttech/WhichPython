namespace LostTech.WhichPython {
    using System;
    using System.Linq;
    using ManyConsole.CommandLineUtils;

    static class WhichPythonProgram {
        static int Main(string[] args) {
            return ConsoleCommandDispatcher.DispatchCommand(
                ConsoleCommandDispatcher.FindCommandsInSameAssemblyAs(typeof(WhichPythonProgram)),
                args, Console.Out);
        }
    }
}
