namespace WhichPython {
    using System;
    using LostTech.WhichPython;

    static class WhichPythonProgram {
        static void Main() {
            foreach (var environment in PythonEnvironment.EnumerateEnvironments())
                Console.WriteLine($"{environment.LanguageVersion.ToString(2)}-{environment.Architecture}");
        }
    }
}
