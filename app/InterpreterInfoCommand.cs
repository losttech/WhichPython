namespace LostTech.WhichPython {
    using System;
    using System.IO;
    using ManyConsole.CommandLineUtils;

    class InterpreterInfoCommand : ConsoleCommand {
        public override int Run(string[] remainingArguments) {
            this.CheckRequiredArguments();
            if (remainingArguments.Length < 1)
                throw new ArgumentNullException("python-executable");

            var interpreter = new FileInfo(remainingArguments[0]);
            var environment = PythonEnvironment.FromInterpreterChecked(interpreter);

            Console.WriteLine("ver: " + environment.LanguageVersion);
            Console.WriteLine("exe: " + environment.InterpreterPath.FullName);
            Console.WriteLine("home: " + environment.Home?.FullName);
            Console.WriteLine("dll: " + environment.DynamicLibraryPath?.FullName);

            return 0;
        }

        public InterpreterInfoCommand() {
            this.IsCommand("interpreter");
            this.HasAdditionalArguments(1, "<python-executable>");
        }
    }
}
