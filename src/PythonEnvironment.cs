namespace LostTech.WhichPython {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text.RegularExpressions;
    using System.Threading;
    using Microsoft.Win32;

    public class PythonEnvironment {
        public string InterpreterPath { get; }
        public string Home { get; }
        public Version LanguageVersion { get; }
        public Architecture? Architecture { get; }

        public PythonEnvironment(string interpreterPath, string home, Version languageVersion, Architecture? architecture) {
            this.InterpreterPath = interpreterPath ?? throw new ArgumentNullException(nameof(interpreterPath));
            this.Home = home ?? throw new ArgumentNullException(nameof(home));
            this.LanguageVersion = languageVersion;
            this.Architecture = architecture;
        }

        public static IEnumerable<PythonEnvironment> EnumerateEnvironments(CancellationToken cancellation = default) {
            var found = new SortedSet<string>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                foreach (var environment in EnumerateWindowsEnvironments(found, cancellation))
                    yield return environment;

            foreach(var environment in EnumeratePathEnvironments(found, cancellation))
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

        public static IEnumerable<PythonEnvironment> EnumeratePathEnvironments(IEnumerable<string> paths, string interpreterFileNameMask = "python*", CancellationToken cancellation = default){
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (interpreterFileNameMask == null) throw new ArgumentNullException(nameof(interpreterFileNameMask));

            foreach(string directory in paths){
                if (!Directory.Exists(directory))
                    continue;
                foreach(string potentialInterpreter in Directory.EnumerateFiles(directory, interpreterFileNameMask)) {
                    cancellation.ThrowIfCancellationRequested();

                    var env = TryDetectEnvironmentFromInterpreter(potentialInterpreter, cancellation);
                    if (env != null)
                        yield return env;
                }
            }
        }

        static IEnumerable<PythonEnvironment> EnumeratePathEnvironments(ISet<string> enumerated, CancellationToken cancellation){
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            char pathSeparator = windows ? ';' : ':';
            string[] paths = Environment.GetEnvironmentVariable("PATH")?.Split(pathSeparator);
            string interpreterFileNameMask = windows ? "python.exe" : "python?.?";
            if (paths == null)
                yield break;

            foreach(var env in EnumeratePathEnvironments(paths, interpreterFileNameMask, cancellation))
                if (enumerated.Add(env.Home))
                    yield return env;
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
                    if (home == null) continue;
                    string interpreterPath = Path.Combine(home, "python.exe");
                    if (!File.Exists(interpreterPath)) continue;
                    if (enumerated.Add(home)) {
                        Version.TryParse(majorVersion, out var version);
                        yield return new PythonEnvironment(interpreterPath, home, version, abi);
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

            string interpreterPath = Path.Combine(home, "python.exe");
            if (!File.Exists(interpreterPath)) return null;
            return new PythonEnvironment(interpreterPath, home: home, version, null);
        }

        static readonly Regex FileNameVersionRegex = new Regex(@"[a-zA-Z]+(?<ver>\d\.\d)(\.exe)?");
        static PythonEnvironment TryDetectEnvironmentFromInterpreter(string potentialInterpreter, CancellationToken cancellation = default) {
            if (!File.Exists(potentialInterpreter)) return null;

            cancellation.ThrowIfCancellationRequested();

            var match = FileNameVersionRegex.Match(potentialInterpreter);
            if (!match.Success) return null;
            if (!Version.TryParse(match.Groups["ver"].Value, out var version)) return null;
            string home = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)){
                home = TryLocateSo(version);
            }
            return home != null
                ? new PythonEnvironment(interpreterPath: potentialInterpreter, home: home, languageVersion: version, architecture: null)
                : null;
        }

        static string TryLocateSo(Version version) {
            // HACK always returns global installation, if any at all
            string fileName = FormattableString.Invariant($"libpython{version.Major}.{version.Minor}.so");
            var locateStartInfo = new ProcessStartInfo("locate", fileName){
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            string[] paths;

            try{
                var locate = Process.Start(locateStartInfo);
                if (!locate.WaitForExit(500)){
                    locate.Kill();
                    return null;
                }
                paths = locate.StandardOutput.ReadToEnd()
                    .Split(new []{Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries)
                    .ToArray();
                if (paths.Length == 1) return Path.GetDirectoryName(paths[0]);
                return null;
            } catch(PlatformNotSupportedException) {
                return null;
            } catch(System.ComponentModel.Win32Exception notFound) when (notFound.NativeErrorCode == 2){
                return null;
            }
        }
    }
}
