namespace LostTech.WhichPython {
    using System;
    using System.Linq;

    using ManyConsole.CommandLineUtils;

    class ListCommand : ConsoleCommand {
        public override int Run(string[] remainingArguments) {
            foreach (var environment in PythonEnvironment.EnumerateEnvironments()
                                    .Concat(CondaEnvironment.EnumerateCondaEnvironments())) {
                Console.WriteLine(this.HomeOnly
                    ? environment.Home.FullName
                    : $"{environment.LanguageVersion?.ToString(2) ?? "??"}-{environment.Architecture?.ToString() ?? "???"} @ {environment.Home}");
            }

            return 0;
        }

        public bool HomeOnly { get; set; }

        public ListCommand() {
            this.IsCommand("list");

            this.HasOption("--home-only", "Only print home directory for each environment",
                onOff => this.HomeOnly = onOff == "on");
        }
    }
}
