using System;
using System.Linq;
using System.Reflection;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace ClashIdFixer.Core
{
    /// <summary>
    /// A handful of Navisworks API members whose exact name has drifted between SDK
    /// versions in the past (source file name on <see cref="Model"/>, the "run this
    /// clash test" method on <see cref="DocumentClashTests"/>). Rather than hard-code
    /// a guess and risk a build failure against a slightly different 2022 SDK build,
    /// these are looked up by name at runtime from a short candidate list. If your
    /// installed SDK uses a different name than all the candidates below, add it to
    /// the relevant array - Visual Studio's IntelliSense on the real types will tell
    /// you the exact name in seconds.
    /// </summary>
    internal static class ReflectionHelpers
    {
        private static readonly string[] ModelFileNameCandidates = { "SourceFileName", "FileName", "Filename" };
        private static readonly string[] RunSingleTestCandidates = { "TestsRunTest", "RunTest", "TestRunTest" };
        private static readonly string[] RunAllTestsCandidates = { "TestsRunAllTests", "RunAllTests", "TestsRunTests" };

        public static string GetModelFileName(Model model)
        {
            if (model == null) return null;

            foreach (var name in ModelFileNameCandidates)
            {
                var prop = typeof(Model).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null) continue;

                var value = prop.GetValue(model, null) as string;
                if (!string.IsNullOrEmpty(value)) return value;
            }

            var displayNameProp = typeof(Model).GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance);
            return displayNameProp != null ? displayNameProp.GetValue(model, null) as string : null;
        }

        /// <summary>
        /// Runs (recomputes) a single clash test. Returns false if no known method
        /// name matched, so the caller can fall back to running all tests, or to
        /// telling the user to hit "Update" in Clash Detective manually.
        /// </summary>
        public static bool TryRunTest(DocumentClashTests testsData, ClashTest test)
        {
            foreach (var name in RunSingleTestCandidates)
            {
                var method = typeof(DocumentClashTests).GetMethod(name, new[] { typeof(ClashTest) });
                if (method == null) continue;

                method.Invoke(testsData, new object[] { test });
                return true;
            }
            return false;
        }

        public static bool TryRunAllTests(DocumentClashTests testsData)
        {
            foreach (var name in RunAllTestsCandidates)
            {
                var method = typeof(DocumentClashTests).GetMethod(name, Type.EmptyTypes);
                if (method == null) continue;

                method.Invoke(testsData, null);
                return true;
            }
            return false;
        }
    }
}
