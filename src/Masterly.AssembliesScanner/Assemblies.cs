using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Masterly.AssembliesScanner
{
    public static class Assemblies
    {
        /// <summary>
        /// Get all assemblies in the current App Domain according to assembly name patterns.
        /// </summary>
        /// <param name="assemblyPatterns">Name pattern to scan</param>
        /// <returns>Array of matched assemblies</returns>
        public static Assembly[] Get(params string[] assemblyPatterns)
        {
            if (!assemblyPatterns.Any())
                return Array.Empty<Assembly>();

            string[] assemblyExcludePatterns = GetAssemblyExcludePatterns(assemblyPatterns);

            IEnumerable<Assembly> allAssemblies = GetAllAssemblies();

            var referencedProjectAssemblies = new List<Assembly>();

            foreach (string pattern in assemblyPatterns)
            {
                if (string.IsNullOrEmpty(pattern.Trim()))
                    continue;

                List<Assembly> matchedAssemblies = GetMatchedAssemblies(allAssemblies, pattern);

                if (matchedAssemblies?.Count > 0)
                    referencedProjectAssemblies.AddRange(matchedAssemblies);
            }

            foreach (var pattern in assemblyExcludePatterns)
                referencedProjectAssemblies.RemoveAll(a => Regex.IsMatch(a.ManifestModule.Name.ToLower(), pattern.ToLower()));

            return referencedProjectAssemblies.ToArray();
        }

        /// <summary>
        /// Get all assemblies in the current App Domain according to assembly name patterns.
        /// </summary>
        /// <param name="assemblyPatterns">Name pattern to scan</param>
        /// <returns>Array of matched assemblies</returns>
        public static Assembly[] Get(IEnumerable<string> assemblyPatterns)
        {
            if (!assemblyPatterns.Any())
                return Array.Empty<Assembly>();

            return Get(assemblyPatterns.ToArray());
        }

        private static List<Assembly> GetMatchedAssemblies(IEnumerable<Assembly> allAssemblies, string pattern) 
            => allAssemblies.Where(a => Regex.IsMatch(a.ManifestModule.Name.ToLower(), pattern.ToLower())).ToList();

        private static List<Assembly> GetAllAssemblies() => Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.dll")
                                    .Select(x => Assembly.Load(AssemblyName.GetAssemblyName(x))).ToList();

        private static string[] GetAssemblyExcludePatterns(string[] assemblyPatterns) 
            => assemblyPatterns.Where(a => a.StartsWith("!")).Select(a => a.Replace("!", string.Empty).Trim()).ToArray();
    }
}