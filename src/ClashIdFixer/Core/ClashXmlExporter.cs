using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using ClashIdFixer.Config;

namespace ClashIdFixer.Core
{
    public sealed class ExportSummary
    {
        public int TestCount;
        public int ClashCount;
        public int ResolvedCount;
        public int UnresolvedCount;
        public string OutputFile;
    }

    /// <summary>
    /// Walks every clash test/result in the document and writes one XML file where,
    /// for each of the two clashing items, both the original (leaf/sub-object) id
    /// and the resolved "real object" id are present - so whichever one the
    /// downstream matching script needs (to join against Signal's parameter export
    /// by file name + element id) is already there, without guessing.
    /// </summary>
    public static class ClashXmlExporter
    {
        public static ExportSummary Export(Document document, ClashIdFixerConfig config, string outputFilePath)
        {
            var summary = new ExportSummary();

            var documentClash = document.GetClash();
            var tests = documentClash.TestsData.Tests;

            var testElements = new List<XElement>();

            foreach (ClashTest test in tests.OfType<ClashTest>())
            {
                summary.TestCount++;
                var clashElements = new List<XElement>();
                CollectResults(test.Children, clashElements, config, summary);

                testElements.Add(new XElement("Test",
                    new XAttribute("name", test.DisplayName ?? ""),
                    clashElements));
            }

            var root = new XElement("ClashIdFixerExport",
                new XAttribute("generated", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)),
                new XAttribute("document", document.CurrentFileName ?? ""),
                new XAttribute("testCount", summary.TestCount),
                new XAttribute("clashCount", summary.ClashCount),
                new XAttribute("resolvedCount", summary.ResolvedCount),
                new XAttribute("unresolvedCount", summary.UnresolvedCount),
                testElements);

            new XDocument(root).Save(outputFilePath);
            summary.OutputFile = outputFilePath;
            return summary;
        }

        private static void CollectResults(IEnumerable<SavedItem> children, List<XElement> outClashElements,
            ClashIdFixerConfig config, ExportSummary summary)
        {
            foreach (var child in children)
            {
                var group = child as ClashResultGroup;
                if (group != null)
                {
                    CollectResults(group.Children, outClashElements, config, summary);
                    continue;
                }

                var result = child as ClashResult;
                if (result == null) continue;

                summary.ClashCount++;
                outClashElements.Add(BuildClashElement(result, config, summary));
            }
        }

        private static XElement BuildClashElement(ClashResult result, ClashIdFixerConfig config, ExportSummary summary)
        {
            string found = "";
            try
            {
                if (result.CreatedTime.HasValue)
                    found = result.CreatedTime.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch { }

            return new XElement("Clash",
                new XAttribute("name", result.DisplayName ?? ""),
                new XAttribute("status", SafeStatus(result)),
                new XAttribute("description", result.Description ?? ""),
                new XAttribute("approvedBy", result.ApprovedBy ?? ""),
                new XAttribute("found", found),
                BuildItemElement(1, result.Item1, config, summary),
                BuildItemElement(2, result.Item2, config, summary));
        }

        private static string SafeStatus(ClashResult result)
        {
            try { return result.Status.ToString(); }
            catch { return ""; }
        }

        private static XElement BuildItemElement(int side, ModelItem clashItem, ClashIdFixerConfig config, ExportSummary summary)
        {
            if (clashItem == null)
            {
                return new XElement("Item", new XAttribute("side", side), new XAttribute("missing", true));
            }

            var resolved = ObjectResolver.ResolveObject(clashItem, config.ObjectNodeTypes);
            if (resolved.WasResolved) summary.ResolvedCount++; else summary.UnresolvedCount++;

            string sourceFile = "";
            try
            {
                sourceFile = ReflectionHelpers.GetModelFileName(clashItem.Model);
            }
            catch { }

            var idElements = ObjectResolver.ExtractIdCandidates(resolved.ResolvedItem, config.IdCandidates)
                .Select(kv => new XElement("Id", new XAttribute("source", kv.Key), kv.Value));

            return new XElement("Item",
                new XAttribute("side", side),
                new XElement("SourceFile", sourceFile ?? ""),
                new XElement("OriginalItem",
                    new XAttribute("nodeType", ObjectResolver.GetNodeTypeName(clashItem) ?? ""),
                    GetPath(clashItem)),
                new XElement("ResolvedObject",
                    new XAttribute("resolved", resolved.WasResolved),
                    new XAttribute("nodeType", resolved.ResolvedNodeType ?? ""),
                    new XElement("Path", GetPath(resolved.ResolvedItem)),
                    new XElement("InstanceGuid", ObjectResolver.GetInstanceGuid(resolved.ResolvedItem) ?? ""),
                    idElements));
        }

        private static string GetPath(ModelItem item)
        {
            if (item == null) return "";
            try
            {
                return string.Join(" / ", item.AncestorsAndSelf.Select(m => m.DisplayName ?? "?"));
            }
            catch
            {
                return item.DisplayName ?? "";
            }
        }
    }
}
