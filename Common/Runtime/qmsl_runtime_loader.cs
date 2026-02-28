using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WackeEdl.Qualcomm.Common.Runtime
{
    internal static class QmslRuntimeLoader
    {
        internal const string QmslDllName = "QMSL_MSVC10R.dll";
        private const string EmbeddedResourceName = "WackeEdl.Qualcomm.Native.QMSL_MSVC10R.dll";

        private static readonly object SyncRoot = new object();
        private static IntPtr s_loadedModule = IntPtr.Zero;
        private static string s_loadedPath = string.Empty;

        internal static IntPtr LoadedModule
        {
            get
            {
                lock (SyncRoot)
                {
                    return s_loadedModule;
                }
            }
        }

        internal static string LoadedPath
        {
            get
            {
                lock (SyncRoot)
                {
                    return s_loadedPath;
                }
            }
        }

        internal static bool EnsureLoaded(Action<string> logDetail = null)
        {
            lock (SyncRoot)
            {
                if (s_loadedModule != IntPtr.Zero)
                    return true;

                Action<string> log = logDetail ?? delegate { };

                if (!OperatingSystem.IsWindows())
                {
                    log("[QMSL][Bootstrap] skip load: Windows only.");
                    return false;
                }

                if (TryLoadFromEmbedded(log))
                    return true;

                if (TryLoadByName(log, includeFailureLog: false))
                    return true;

                if (TryLoadFromExternalLocations(log))
                    return true;

                log("[QMSL][Bootstrap] load failed: QMSL runtime not found.");
                return false;
            }
        }

        private static bool TryLoadFromEmbedded(Action<string> log)
        {
            if (!TryReadEmbeddedDll(out byte[] payload, out string resourceName, log))
                return false;

            string hash = ComputeSha256Hex(payload);
            foreach (string baseDir in EnumerateExtractionRoots())
            {
                if (string.IsNullOrWhiteSpace(baseDir))
                    continue;

                string targetDir = Path.Combine(baseDir, hash.Substring(0, 16));
                string targetPath = Path.Combine(targetDir, QmslDllName);

                try
                {
                    Directory.CreateDirectory(targetDir);
                    if (!IsSameFile(targetPath, payload, hash))
                        WriteFileAtomic(targetPath, payload);
                }
                catch (Exception ex)
                {
                    log(string.Format("[QMSL][Bootstrap] extract failed: {0} ({1}: {2})",
                        targetPath, ex.GetType().Name, ex.Message));
                    continue;
                }

                if (TryLoadFromPath(targetPath, log))
                {
                    log(string.Format("[QMSL][Bootstrap] loaded from embedded resource '{0}'.", resourceName));
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateExtractionRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir))
                roots.Add(Path.Combine(baseDir, "runtime", "qmsl"));

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                roots.Add(Path.Combine(localAppData, "WackeEdl", "runtime", "qmsl"));

            return roots;
        }

        private static bool TryReadEmbeddedDll(out byte[] payload, out string resourceName, Action<string> log)
        {
            payload = null;
            resourceName = EmbeddedResourceName;

            Assembly asm = typeof(QmslRuntimeLoader).Assembly;
            Stream stream = asm.GetManifestResourceStream(EmbeddedResourceName);

            if (stream == null)
            {
                string fallback = asm
                    .GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(QmslDllName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    resourceName = fallback;
                    stream = asm.GetManifestResourceStream(fallback);
                }
            }

            if (stream == null)
            {
                log("[QMSL][Bootstrap] embedded resource not found.");
                return false;
            }

            using (stream)
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                payload = ms.ToArray();
            }

            if (payload.Length == 0)
            {
                log("[QMSL][Bootstrap] embedded resource is empty.");
                return false;
            }

            return true;
        }

        private static bool IsSameFile(string path, byte[] payload, string expectedHash)
        {
            if (!File.Exists(path))
                return false;

            var fi = new FileInfo(path);
            if (fi.Length != payload.Length)
                return false;

            try
            {
                using (var fs = File.OpenRead(path))
                using (SHA256 sha = SHA256.Create())
                {
                    string existingHash = Convert.ToHexString(sha.ComputeHash(fs));
                    return string.Equals(existingHash, expectedHash, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void WriteFileAtomic(string targetPath, byte[] payload)
        {
            string tempPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(tempPath, payload);

            try
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);

                File.Move(tempPath, targetPath);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }

                throw;
            }
        }

        private static bool TryLoadFromExternalLocations(Action<string> log)
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(baseDir, QmslDllName),
                Path.Combine(baseDir, "lib", QmslDllName),
                Path.Combine(baseDir, "Lib", QmslDllName)
            };

            foreach (string candidate in candidates)
            {
                if (TryLoadFromPath(candidate, log))
                    return true;
            }

            return false;
        }

        private static bool TryLoadByName(Action<string> log, bool includeFailureLog)
        {
            try
            {
                if (NativeLibrary.TryLoad(QmslDllName, out IntPtr module) && module != IntPtr.Zero)
                {
                    s_loadedModule = module;
                    s_loadedPath = QmslDllName;
                    log("[QMSL][Bootstrap] loaded via default DLL search.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (includeFailureLog)
                {
                    log(string.Format("[QMSL][Bootstrap] default load failed: {0}: {1}",
                        ex.GetType().Name, ex.Message));
                }
            }

            return false;
        }

        private static bool TryLoadFromPath(string candidatePath, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
                return false;

            try
            {
                if (NativeLibrary.TryLoad(candidatePath, out IntPtr module) && module != IntPtr.Zero)
                {
                    s_loadedModule = module;
                    s_loadedPath = candidatePath;
                    log(string.Format("[QMSL][Bootstrap] loaded: {0}", candidatePath));
                    return true;
                }
            }
            catch (Exception ex)
            {
                log(string.Format("[QMSL][Bootstrap] load failed: {0}: {1}", ex.GetType().Name, ex.Message));
            }

            return false;
        }

        private static string ComputeSha256Hex(byte[] payload)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Convert.ToHexString(sha.ComputeHash(payload));
            }
        }
    }
}
