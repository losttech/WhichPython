namespace LostTech.WhichPython {
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;
    using Microsoft.Win32;

    public class PythonEnvironment {
        public string Home { get; }
        public Version LanguageVersion { get; }
        public Architecture Architecture { get; }

        public PythonEnvironment(string home, Version languageVersion, Architecture architecture) {
            this.Home = home ?? throw new ArgumentNullException(nameof(home));
            this.LanguageVersion = languageVersion;
            this.Architecture = architecture;
        }

        public static IEnumerable<PythonEnvironment> EnumerateEnvironments(CancellationToken cancellationToken = default) {
            var found = new SortedSet<string>();

            foreach (var environment in EnumerateWindowsEnvironments(found, cancellationToken))
                yield return environment;
        }

        static IEnumerable<PythonEnvironment> EnumerateWindowsEnvironments(ISet<string> enumerated, CancellationToken cancellationToken) {
            using (var systemEnvironments = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Python\PythonCore"))
                if (systemEnvironments != null) {
                    var abi = RuntimeInformation.ProcessArchitecture;
                    foreach (var environment in EnumerateEnvironments(enumerated, systemEnvironments, abi, cancellationToken))
                        yield return environment;
                }
        }

        static IEnumerable<PythonEnvironment> EnumerateEnvironments(ISet<string> enumerated, RegistryKey registryKey, Architecture abi, CancellationToken cancellationToken) {
            foreach (string majorVersion in registryKey.GetSubKeyNames()) {
                cancellationToken.ThrowIfCancellationRequested();

                using (var installPath = registryKey.OpenSubKey(majorVersion + @"\InstallPath")) {
                    if (installPath == null)
                        continue;

                    string home = installPath.GetValue("") as string;
                    if (home != null && enumerated.Add(home)) {
                        Version.TryParse(majorVersion, out var version);
                        yield return new PythonEnvironment(home, version, abi);
                    }
                }
            }
        }
    }
}
