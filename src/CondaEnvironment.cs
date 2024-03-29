﻿namespace LostTech.WhichPython {
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    /// <summary>
    /// Represents a Python environment, managed by Anaconda
    /// </summary>
    public sealed class CondaEnvironment: PythonEnvironment {
        /// <inheritdoc cref="PythonEnvironment.Home"/> />
#pragma warning disable CS8600, CS8603 // Conda environments always have non-null homes
        public new DirectoryInfo Home => (DirectoryInfo)base.Home;
#pragma warning restore CS8600, CS8603 // Conda environments always have non-null homes
        /// <summary>
        /// Short name of the environment
        /// </summary>
        public string Name => this.Home.Name;
        /// <summary>
        /// Gets conda environment, that is currently active.
        /// </summary>
        public static CondaEnvironment? GetActive(CancellationToken cancellation = default) {
            string? activeEnvPath = Environment.GetEnvironmentVariable("CONDA_PREFIX");
            if (activeEnvPath is null) return null;
            DirectoryInfo home;
            try {
                home = new DirectoryInfo(activeEnvPath);
            }
            catch (ArgumentException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            catch (PathTooLongException) { return null; }

            return DetectCondaEnvironment(home, cancellation);
        }
        /// <summary>
        /// Checks specified directory
        /// </summary>
        public static CondaEnvironment? DetectCondaEnvironment(DirectoryInfo home, CancellationToken cancellation = default) {
            if (home is null) throw new ArgumentNullException(nameof(home));

            if (!home.Exists)
                return null;

            var version = TryGetVersion(home, cancellation);

            string? interpreterPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(home.FullName, "python.exe")
                : version == null ? null
                    : Path.Combine(home.FullName, "bin",
                        FormattableString.Invariant($"python{version.Major}.{version.Minor}"));

            if (interpreterPath is null || !File.Exists(interpreterPath)) return null;

            return new CondaEnvironment(
                interpreterPath: new FileInfo(interpreterPath),
                home: home,
                dll: version != null ? new FileInfo(GetDll(home, version)) : null,
                version,
                architecture: null);
        }

        public new static IEnumerable<CondaEnvironment> EnumerateCondaEnvironments(
            CancellationToken cancellation = default)
            => GetEnvironmentRoots(cancellation)
                .SelectMany(home => {
                    cancellation.ThrowIfCancellationRequested();
                    var environment = DetectCondaEnvironment(home, cancellation);
                    return environment is null
                        ? Array.Empty<CondaEnvironment>()
                        : new[] {environment};
                });

        static IEnumerable<DirectoryInfo> GetEnvironmentRoots(CancellationToken cancellation) {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string condaEnvironmentsFilePath = Path.Combine(userHome, ".conda", "environments.txt");
            if (File.Exists(condaEnvironmentsFilePath)) {
                foreach (var potentialRoot in ReadEnvironmentRootsFile(condaEnvironmentsFilePath))
                    yield return potentialRoot;
            }

            cancellation.ThrowIfCancellationRequested();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                foreach (var potentialRoot in GetSystemWideEnvironmentRoots(cancellation))
                    yield return potentialRoot;
            }
        }

        static IEnumerable<DirectoryInfo> ReadEnvironmentRootsFile(string filePath) {
            string[] pythonHomes;
            try {
                pythonHomes = File.ReadAllLines(filePath);
            }
            catch (FileNotFoundException) { yield break; }
            catch (DirectoryNotFoundException) { yield break; }

            foreach (string home in pythonHomes) {
                if (string.IsNullOrWhiteSpace(home))
                    continue;

                yield return new DirectoryInfo(home);
            }
        }

        static IEnumerable<DirectoryInfo> GetSystemWideEnvironmentRoots(CancellationToken cancellation) {
            string systemWideEnvironmentsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Anaconda3",
                "envs"
            );
            var systemWideEnvironmentsDir = new DirectoryInfo(systemWideEnvironmentsPath);
            return systemWideEnvironmentsDir.Exists
                ? systemWideEnvironmentsDir.EnumerateDirectories()
                : Array.Empty<DirectoryInfo>();
        }

        CondaEnvironment(FileInfo interpreterPath, DirectoryInfo home, FileInfo? dll, Version? languageVersion, Architecture? architecture) : base(interpreterPath, home, dll, languageVersion, architecture) { }
    }
}
