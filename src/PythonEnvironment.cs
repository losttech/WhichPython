namespace LostTech.WhichPython {
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using Microsoft.Win32;

    public class PythonEnvironment {
        public string Home { get; }
        public Version LanguageVersion { get; }
        public Architecture? Architecture { get; }

        public PythonEnvironment(string home, Version languageVersion, Architecture? architecture) {
            this.Home = home ?? throw new ArgumentNullException(nameof(home));
            this.LanguageVersion = languageVersion;
            this.Architecture = architecture;
        }

        public static IEnumerable<PythonEnvironment> EnumerateEnvironments(CancellationToken cancellation = default) {
            var found = new SortedSet<string>();

            foreach (var environment in EnumerateWindowsEnvironments(found, cancellation))
                yield return environment;
        }

        public static IEnumerable<PythonEnvironment> EnumerateCondaEnvironments(CancellationToken cancellation = default) {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (userHome == null)
                yield break;
            string condaEnvironmentsFilePath = Path.Combine(userHome, ".conda", "environments.txt");
            if (!File.Exists(condaEnvironmentsFilePath))
                yield break;
            cancellation.ThrowIfCancellationRequested();

            string[] pythonHomes;
            try {
                pythonHomes = File.ReadAllLines(condaEnvironmentsFilePath);
            } catch (FileNotFoundException) { yield break; }
              catch (DirectoryNotFoundException) { yield break; }

            foreach(string home in pythonHomes) {
                if (string.IsNullOrWhiteSpace(home))
                    continue;
                cancellation.ThrowIfCancellationRequested();

                var environment = DetectEnvironment(home, cancellation);
                if (environment != null)
                    yield return environment;
            }
        }

        static IEnumerable<PythonEnvironment> EnumerateWindowsEnvironments(ISet<string> enumerated, CancellationToken cancellation) {
            using (var systemEnvironments = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Python\PythonCore"))
                if (systemEnvironments != null) {
                    var abi = RuntimeInformation.ProcessArchitecture;
                    foreach (var environment in EnumerateEnvironments(enumerated, systemEnvironments, abi, cancellation))
                        yield return environment;
                }
        }

        static IEnumerable<PythonEnvironment> EnumerateEnvironments(ISet<string> enumerated, RegistryKey registryKey, Architecture abi, CancellationToken cancellation) {
            foreach (string majorVersion in registryKey.GetSubKeyNames()) {
                cancellation.ThrowIfCancellationRequested();

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

        static PythonEnvironment DetectEnvironment(string home, CancellationToken cancellation) {
            if (!Directory.Exists(home))
                return null;

            int? versionDuplet = Directory.EnumerateFiles(home, "python??.dll")
                .Concat(Directory.EnumerateFiles(home, "python??.so"))
                .Concat(Directory.EnumerateFiles(home, "python??.dylib"))
                .Select(name => {
                    name = Path.GetFileNameWithoutExtension(name);
                    name = name.Substring(name.Length - 2);
                    return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int versionDigits) ? (int?)versionDigits : null;
                }).WithCancellation(cancellation).Min();

            var version = versionDuplet == null ? null : new Version(versionDuplet.Value / 10, versionDuplet.Value % 10);

            return new PythonEnvironment(home, version, null);
        }
    }
}
