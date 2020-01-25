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
        public FileSystemInfo InterpreterPath { get; }
        /// <summary>
        /// The value of interpreter's PYTHONHOME
        /// </summary>
        public FileSystemInfo? Home { get; }
        /// <summary>
        /// Path to the Python dynamic library (useful for embedding). E.g. <c>python36.dll</c> on Windows.
        /// </summary>
        public FileSystemInfo? DynamicLibraryPath { get; }
        /// <summary>
        /// Python language version.
        /// </summary>
        public Version? LanguageVersion { get; }
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
        public PythonEnvironment(FileSystemInfo interpreterPath, FileSystemInfo? home, FileSystemInfo? dll, Version? languageVersion, Architecture? architecture) {
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
            var found = new SortedSet<string?>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                foreach (var environment in EnumerateRegistryEnvironments(found, cancellation))
                    yield return environment;
            }

            foreach (var environment in EnumeratePathEnvironments(found, cancellation))
                yield return environment;
        }

        /// <summary>
        /// Enumerate Conda environments of the current user.
        /// </summary>
        [Obsolete("Use CondaEnvironment.EnumerateCondaEnvironments")]
        public static IEnumerable<PythonEnvironment> EnumerateCondaEnvironments(CancellationToken cancellation = default)
            => CondaEnvironment.EnumerateCondaEnvironments(cancellation);

        /// <summary>
        /// Enumerate Python interpreters in the specified directories.
        /// Does not go into subdirectories.
        /// <para>SECURITY: this invokes any files matching the mask to determine if they are a Python interpreter.</para>
        /// </summary>
        /// <param name="paths">Directories to search for interpreters.</param>
        /// <param name="interpreterFileNameMask">What an interpreter file name is expected to look like.</param>
        /// <param name="cancellation">Optional cancellation token.</param>
        public static IEnumerable<PythonEnvironment> EnumeratePathEnvironments(
            IEnumerable<DirectoryInfo> paths,
            string interpreterFileNameMask = "python*",
            CancellationToken cancellation = default){
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (interpreterFileNameMask == null) throw new ArgumentNullException(nameof(interpreterFileNameMask));

            return Impl();

            IEnumerable<PythonEnvironment> Impl() {
                foreach (var directory in paths) {
                    if (!directory.Exists)
                        continue;
                    foreach (FileInfo potentialInterpreter in directory.EnumerateFiles(searchPattern: interpreterFileNameMask)) {
                        cancellation.ThrowIfCancellationRequested();

                        var env = TryDetectEnvironmentFromInterpreter(potentialInterpreter, cancellation);
                        if (env != null)
                            yield return env;
                    }
                }
            }
        }

        /// <summary>
        /// Enumerate Python environments, listed in the PATH environment variable.
        /// </summary>
        static IEnumerable<PythonEnvironment> EnumeratePathEnvironments(ISet<string?> enumerated, CancellationToken cancellation){
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            char pathSeparator = windows ? ';' : ':';
            DirectoryInfo[]? paths = Environment.GetEnvironmentVariable("PATH")
                ?.Split(new[]{pathSeparator}, StringSplitOptions.RemoveEmptyEntries)
                .Select(dirPath => new DirectoryInfo(dirPath))
                .ToArray();
            if (paths == null)
                yield break;

            string interpreterFileNameMask = windows ? "python.exe" : "python?.?";
            foreach (var env in EnumeratePathEnvironments(paths, interpreterFileNameMask, cancellation)) {
                if (enumerated.Add(env.DynamicLibraryPath?.FullName))
                    yield return env;
            }
        }

        static IEnumerable<PythonEnvironment> EnumerateRegistryEnvironments(ISet<string?> enumerated, CancellationToken cancellation) {
            foreach (var environment in EnumerateRegistryEnvironments(Registry.CurrentUser, enumerated, cancellation))
                yield return environment;
            foreach (var environment in EnumerateRegistryEnvironments(Registry.LocalMachine, enumerated, cancellation))
                yield return environment;
        }

        static IEnumerable<PythonEnvironment> EnumerateRegistryEnvironments(RegistryKey hive, ISet<string?> enumerated, CancellationToken cancellation) {
            using var registryEnvironments = hive.OpenSubKey(@"SOFTWARE\Python\PythonCore");
            if (registryEnvironments == null) yield break;

            var abi = RuntimeInformation.ProcessArchitecture;
            foreach (var environment in EnumerateRegistryEnvironments(enumerated, registryEnvironments, abi, cancellation))
                yield return environment;
        }

        static IEnumerable<PythonEnvironment> EnumerateRegistryEnvironments(ISet<string?> enumerated, RegistryKey registryKey, Architecture abi, CancellationToken cancellation) {
            foreach (string majorVersion in registryKey.GetSubKeyNames()) {
                cancellation.ThrowIfCancellationRequested();

                using var installPath = registryKey.OpenSubKey(majorVersion + @"\InstallPath");
                if (installPath == null)
                    continue;

                if (!(installPath.GetValue("") is string homePath)) continue;
                var home = new DirectoryInfo(homePath);
                string interpreterPath = Path.Combine(homePath, "python.exe");
                if (!File.Exists(interpreterPath)) continue;
                if (!enumerated.Add(homePath)) continue;
                bool hasVer = Version.TryParse(majorVersion, out var version);
                string? dllPath = hasVer ? GetDll(home, version) : null;
                var dll = dllPath == null || !File.Exists(dllPath)
                    ? null
                    : new FileInfo(dllPath);
                yield return new PythonEnvironment(
                    interpreterPath: new FileInfo(interpreterPath),
                    home: home,
                    dll: dll,
                    version, abi);
            }
        }

        static string WindowsGetDll(DirectoryInfo home, Version version)
            => Path.Combine(home.FullName, Invariant($"python{version.Major}{version.Minor}.dll"));
        static string UnixGetDll(DirectoryInfo home, Version version)
            => Path.Combine(home.FullName, "lib",
                Invariant($"libpython{version.Major}.{version.Minor}m{DynamicLibraryExtension}"));
        internal static string GetDll(DirectoryInfo home, Version version)
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? WindowsGetDll(home, version)
                : UnixGetDll(home, version);

        static readonly string DynamicLibraryExtension =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"
          : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib"
          : ".so";

        static int? WindowsGetVersionDoublet(DirectoryInfo home, CancellationToken cancellation) {
            return home.EnumerateFiles(searchPattern: "python??.dll")
                .Select(file => {
                    string name = Path.GetFileNameWithoutExtension(file.Name);
                    string verString = name.Substring(name.Length - 2);
                    return int.TryParse(verString, NumberStyles.None, CultureInfo.InvariantCulture, out int versionDigits) ? (int?)versionDigits : null;
                }).WithCancellation(cancellation).Min();
        }

        static int? UnixGetVersionDoublet(DirectoryInfo home, CancellationToken cancellation) {
            if (home is null) throw new ArgumentNullException(nameof(home));

            string lib = Path.Combine(home.FullName, "lib");
            string extension = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";
            return Directory.EnumerateFiles(lib, searchPattern: "libpython?.?m" + extension)
                .Select(name => {
                    name = Path.GetFileNameWithoutExtension(name);
                    string version = name.Substring(name.Length - 4, 3);
                    version = $"{version[0]}{version[2]}";
                    return int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out int versionDigits) ? (int?)versionDigits : null;
                }).WithCancellation(cancellation).Min();
        }

        internal static int? TryGetVersionDoublet(DirectoryInfo home, CancellationToken cancellation)
            => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? WindowsGetVersionDoublet(home, cancellation)
                : UnixGetVersionDoublet(home, cancellation);

        static readonly Regex FileNameVersionRegex = new Regex(@"[a-zA-Z]+(?<ver>\d\.\d)(\.exe)?");
        static PythonEnvironment? TryDetectEnvironmentFromInterpreter(FileInfo potentialInterpreter, CancellationToken cancellation = default) {
            if (potentialInterpreter is null) throw new ArgumentNullException(nameof(potentialInterpreter));
            if (!potentialInterpreter.Exists) return null;

            cancellation.ThrowIfCancellationRequested();

            var match = FileNameVersionRegex.Match(potentialInterpreter.Name);
            if (!match.Success) return null;
            if (!Version.TryParse(match.Groups["ver"].Value, out var version)) return null;
            DirectoryInfo? home = null;
            string? dllPath = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)){
                home = new DirectoryInfo(potentialInterpreter.DirectoryName);
                dllPath = GetDll(home, version);
                if (dllPath != null && !File.Exists(dllPath))
                    dllPath = null;
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)){
                dllPath = TryLocateSo(potentialInterpreter);
            }
            return home != null || dllPath != null
                ? new PythonEnvironment(interpreterPath: potentialInterpreter,
                    home: home,
                    dll: dllPath is null ? null : new FileInfo(dllPath),
                    languageVersion: version, architecture: null)
                : null;
        }

        static string? TryLocateSo(FileInfo interpreterPath) {
            if (interpreterPath is null) throw new ArgumentNullException(nameof(interpreterPath));

            string getLibPathsScript = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? LinuxGetLibPathsScript
                : MacGetLibPathsScript;
            var getLibPathsStartInfo = new ProcessStartInfo(interpreterPath.FullName, $"-c \"{getLibPathsScript}\""){
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

        const string LinuxGetLibPathsScript = "from distutils import sysconfig; import os.path as op; v = sysconfig.get_config_vars(); fpaths = [op.join(v[pv], v['LDLIBRARY']) for pv in ('LIBDIR', 'LIBPL')]; print(list(filter(op.exists, fpaths))[0])";
        const string MacGetLibPathsScript = "from distutils import sysconfig; import os.path as op; v = sysconfig.get_config_vars(); fpaths = [op.join(v[pv], v['LIBRARY']) for pv in ('LIBDIR', 'LIBPL')]; print(list(filter(op.exists, fpaths))[0])";
    }
}
