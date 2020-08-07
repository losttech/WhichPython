namespace LostTech.WhichPython {
    using System;
    using System.Linq;

    using ManyConsole.CommandLineUtils;

    class ListCommand : ConsoleCommand {
        public override int Run(string[] remainingArguments) {
            foreach (var environment in PythonEnvironment.EnumerateEnvironments()
                                    .Concat(CondaEnvironment.EnumerateCondaEnvironments())) {
                Console.WriteLine($"{environment.LanguageVersion?.ToString(2) ?? "??"}-{environment.Architecture?.ToString() ?? "???"} @ {environment.Home}");
            }

            return 0;
        }

        public ListCommand() {
            this.IsCommand("list");
        }
    }
}
