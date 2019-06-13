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
    using static System.FormattableString;

    /// <summary>
    /// Provides information about a Python environment
    /// </summary>
    public class PythonEnvironment {
        /// <summary>
        /// Path to Python executable.
        /// Usually <c>python.exe</c> on Windows and <c>pythonX.X</c> on Linux
        /// </summary>
        public string InterpreterPath { get; }
        /// <summary>
        /// The value of interpreter's PYTHONHOME
        /// </summary>
        public string Home { get; }
        /// <summary>
        /// Path to the Python dynamic library (useful for embedding). E.g. <c>python36.dll</c> on Windows.
        /// </summary>
        public string DynamicLibraryPath {get;set;}
        /// <summary>
        /// Python language version.
        /// </summary>
        public Version LanguageVersion { get; }
        /// <summary>
        /// Interpreter architecture.
        /// </summary>
        public Architecture? Architecture { get; }

        /// <summary>
        /// Creates new instance of <see cref="PythonEnvironment"/>
        /// </summary>
        /// <param name="interpreterPath">Path to Python executable. Mandatory.
        /// Usually <c>python.exe</c> on Windows and <c>pythonX.X</c> on Linux.</param>
        /// <param name="home">The value of interpreter's PYTHONHOME. Optional.</param>
        /// <param name="dll">Path to the Python dynamic library (useful for embedding). Optional.</param>
        /// <param name="languageVersion">Python language version. Optional.</param>
        /// <param name="architecture">Interpreter architecture</param>
        public PythonEnvironment(string interpreterPath, string home, string dll, Version languageVersion, Architecture? architecture) {
            this.InterpreterPath = interpreterPath ?? throw new ArgumentNullException(nameof(interpreterPath));
            this.Home = home;
            this.DynamicLibraryPath = dll;
            this.LanguageVersion = languageVersion;
            this.Architecture = architecture;
        }

        /// <summary>
        /// Enumerate Python interpreters, installed for current user (including system-wide ones).
        /// <para>SECURITY: this invokes any files matching <c>python*</c> mask in PATH to determine if they are a Python interpreter.</para>
        /// </summary>
        public static IEnumerable<PythonEnvironment> EnumerateEnvironments(CancellationToken cancellation = default) {
            var found = new SortedSet<string>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                foreach (var environment in EnumerateRegistryEnvironments(found, cancellation))
                    yield return environment;

            foreach(var environment in EnumeratePathEnvironments(found, cancellation))
                yield return environment;
        }

        /// <summary>
        /// Enumerate Conda environments of the current user.
        /// </summary>
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

                var environment = DetectCondaEnvironment(home, cancellation);
                if (environment != null)
                    yield return environment;
            }
        }

        /// <summary>
        /// Enumerate Python interpreters in the specified directories.
        /// Does not go into subdirectories.
        /// <para>SECURITY: this invokes any files matching the mask to determine if they are a Python interpreter.</para>
        /// </summary>
        /// <param name="paths">Directories to search for interpreters.</param>
        /// <param name="interpreterFileNameMask">What an interpreter file name is expected to look like.</param>
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

        /// <summary>
        /// Enumerate Python environments, listed in the PATH environment variable.
        /// </summary>
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

        static IEnumerable<PythonEnvironment> EnumerateRegistryEnvironments(ISet<string> enumerated, CancellationToken cancellation) {
            foreach (var environment in EnumerateRegistryEnvironments(Registry.CurrentUser, enumerated, cancellation))
                yield return environment;
            foreach (var environment in EnumerateRegistryEnvironments(Registry.LocalMachine, enumerated, cancellation))
                yield return environment;
        }

        static IEnumerable<PythonEnvironment> EnumerateRegistryEnvironments(RegistryKey hive, ISet<string> enumerated, CancellationToken cancellation) {
            using (var registryEnvironments = hive.OpenSubKey(@"SOFTWARE\Python\PythonCore"))
                if (registryEnvironments != null) {
                    var abi = RuntimeInformation.ProcessArchitecture;
                    foreach (var environment in EnumerateRegistryEnvironments(enumerated, registryEnvironments, abi, cancellation))
                        yield return environment;
                }
        }

        static IEnumerable<PythonEnvironment> EnumerateRegistryEnvironments(ISet<string> enumerated, RegistryKey registryKey, Architecture abi, CancellationToken cancellation) {
            foreach (string majorVersion in registryKey.GetSubKeyNames()) {
                cancellation.ThrowIfCancellationRequested();

                using (var installPath = registryKey.OpenSubKey(majorVersion + @"\InstallPath")) {
                    if (installPath == null)
                        continue;

                    string home = installPath.GetValue("") as string;
                    if (home == null) continue;
                    string interpreterPath = Path.Combine(home, "python.exe");
                    if (!File.Exists(interpreterPath)) continue;
                    if (enumerated.Add(home))
                    {
                        bool hasVer = Version.TryParse(majorVersion, out var version);
                        string dll = hasVer ? GetDll(home, version) : null;
                        if (dll != null && !File.Exists(dll))
                            dll = null;
                        yield return new PythonEnvironment(interpreterPath, home, dll: dll, version, abi);
                    }
                }
            }
        }

        static string WindowsGetDll(string home, Version version)
            => Path.Combine(home, Invariant($"python{version.Major}{version.Minor}.dll"));
        static string UnixGetDll(string home, Version version)
            => Path.Combine(home, "lib",
                Invariant($"libpython{version.Major}.{version.Minor}m{DynamicLibraryExtension}"));
        static string GetDll(string home, Version version)
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? WindowsGetDll(home, version)
                : UnixGetDll(home, version);
        
        static readonly string DynamicLibraryExtension =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"
          : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib"
          : ".so";

        static PythonEnvironment DetectCondaEnvironment(string home, CancellationToken cancellation) {
            if (!Directory.Exists(home))
                return null;

            int? versionDoublet = TryGetVersionDoublet(home, cancellation);

            Version version = versionDoublet == null
                ? null
                : new Version(versionDoublet.Value / 10, versionDoublet.Value % 10);

            string interpreterPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(home, "python.exe")
                : version == null ? null
                    : Path.Combine(home, "bin",
                        Invariant($"python{version.Major}.{version.Minor}"));
            if (!File.Exists(interpreterPath)) return null;
            return new PythonEnvironment(interpreterPath, home: home,
                dll: version != null ? GetDll(home, version) : null,
                version, null);
        }

        static int? WindowsGetVersionDoublet(string home, CancellationToken cancellation) {
            return Directory.EnumerateFiles(home, "python??.dll")
                .Select(name => {
                    name = Path.GetFileNameWithoutExtension(name);
                    name = name.Substring(name.Length - 2);
                    return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int versionDigits) ? (int?)versionDigits : null;
                }).WithCancellation(cancellation).Min();
        }

        static int? UnixGetVersionDoublet(string home, CancellationToken cancellation) {
            string lib = Path.Combine(home, "lib");
            string extension = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";
            return Directory.EnumerateFiles(lib, searchPattern: "libpython?.?m" + extension)
                .Select(name => {
                    name = Path.GetFileNameWithoutExtension(name);
                    string version = name.Substring(name.Length - 4, 3);
                    version = $"{version[0]}{version[2]}";
                    return int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out int versionDigits) ? (int?)versionDigits : null;
                }).WithCancellation(cancellation).Min();
        }
        
        static int? TryGetVersionDoublet(string home, CancellationToken cancellation)
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? WindowsGetVersionDoublet(home, cancellation)
                : UnixGetVersionDoublet(home, cancellation);

        static readonly Regex FileNameVersionRegex = new Regex(@"[a-zA-Z]+(?<ver>\d\.\d)(\.exe)?");
        static PythonEnvironment TryDetectEnvironmentFromInterpreter(string potentialInterpreter, CancellationToken cancellation = default) {
            if (!File.Exists(potentialInterpreter)) return null;

            cancellation.ThrowIfCancellationRequested();

            var match = FileNameVersionRegex.Match(potentialInterpreter);
            if (!match.Success) return null;
            if (!Version.TryParse(match.Groups["ver"].Value, out var version)) return null;
            string home = null;
            string dll = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)){
                home = Path.GetDirectoryName(potentialInterpreter);
                dll = GetDll(home, version);
                if (dll != null && !File.Exists(dll))
                    dll = null;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)){
                dll = TryLocateSo(potentialInterpreter);
            }
            return home != null || dll != null
                ? new PythonEnvironment(interpreterPath: potentialInterpreter,
                    home: home, dll: dll,
                    languageVersion: version, architecture: null)
                : null;
        }

        static string TryLocateSo(string interpreterPath) {
            string getLibPathsScript = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? LinuxGetLibPathsScript
                : MacGetLibPathsScript;
            var getLibPathsStartInfo = new ProcessStartInfo(interpreterPath, $"-c \"{getLibPathsScript}\""){
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            try{
                var getLibPaths = Process.Start(getLibPathsStartInfo);
                if (!getLibPaths.WaitForExit(5000)){
                    getLibPaths.Kill();
                    return null;
                }
                string stdout = getLibPaths.StandardOutput.ReadToEnd();
                string[] paths = stdout
                    .Split(new []{Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries)
                    .ToArray();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    if (paths.Length == 1) return paths[0];
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                    return paths.Select(libPath => Path.ChangeExtension(libPath, ".dylib"))
                                .FirstOrDefault(File.Exists);
                }
                return null;
            } catch(PlatformNotSupportedException) {
                return null;
            } catch(System.ComponentModel.Win32Exception notFound) when (notFound.NativeErrorCode == 2){
                return null;
            }
        }

        const string LinuxGetLibPathsScript = @"from distutils import sysconfig; import os.path as op; v = sysconfig.get_config_vars(); fpaths = [op.join(v[pv], v['LDLIBRARY']) for pv in ('LIBDIR', 'LIBPL')]; print(list(filter(op.exists, fpaths))[0])";
        const string MacGetLibPathsScript = @"from distutils import sysconfig; import os.path as op; v = sysconfig.get_config_vars(); fpaths = [op.join(v[pv], v['LIBRARY']) for pv in ('LIBDIR', 'LIBPL')]; print(list(filter(op.exists, fpaths))[0])";
    }
}
