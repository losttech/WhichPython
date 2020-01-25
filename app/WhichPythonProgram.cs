namespace WhichPython {
    using System;
    using System.Linq;
    using LostTech.WhichPython;

    static class WhichPythonProgram {
        static void Main() {
            foreach (var environment in PythonEnvironment.EnumerateEnvironments()
                                .Concat(CondaEnvironment.EnumerateCondaEnvironments())) {
                Console.WriteLine($"{environment.LanguageVersion?.ToString(2) ?? "??"}-{environment.Architecture?.ToString() ?? "???"} @ {environment.Home}");
            }
        }
    }
}
