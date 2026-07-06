using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Autodesk.Navisworks.Api;

namespace ClashIdFixer.Core
{
    public sealed class FixReportResult
    {
        public int ClashObjectCount;
        public int PathResolved;
        public int PathUnresolved;
        public int AlreadyObject;
        public int PromotedToObject;
        public int NoObjectLevel;
        public int ValuesReplaced;
        public string OutputFile;
    }

    /// <summary>
    /// Takes a standard Clash Detective XML report (written by the user from the
    /// Clash Detective "Отчёт" tab, with whatever content settings they chose) and
    /// replaces, for every clash object, the values that belong to the geometry
    /// sub-object with the corresponding values of its composite-object parent -
    /// the "correct selection" level. The report structure, settings and every
    /// field the user configured stay exactly as Clash Detective wrote them; only
    /// the values are substituted.
    /// </summary>
    public static class ReportPostProcessor
    {
        private sealed class ItemMapping
        {
            public ModelItem SourceItem;          // what the report's path points at
            public ModelItem ObjectItem;          // its composite-object ancestor (may equal SourceItem, may be null)
            public Dictionary<string, string> Replacements; // old display value -> object-level display value
        }

        public static FixReportResult Fix(Document document, string inputXmlPath, string outputXmlPath,
            IList<string> fallbackNodeTypes, Action<int, int> progress)
        {
            var result = new FixReportResult();

            XDocument xdoc;
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using (var reader = XmlReader.Create(inputXmlPath, readerSettings))
            {
                xdoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }

            var clashObjects = xdoc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "clashobject", StringComparison.OrdinalIgnoreCase))
                .ToList();
            result.ClashObjectCount = clashObjects.Count;

            // The same sub-object appears in many clashes; resolve each unique path once.
            var mappingCache = new Dictionary<string, ItemMapping>(StringComparer.Ordinal);

            int index = 0;
            foreach (var clashObject in clashObjects)
            {
                index++;
                if (progress != null) progress(index, clashObjects.Count);

                var pathNodes = ExtractPathNodes(clashObject);
                if (pathNodes.Count == 0)
                {
                    result.PathUnresolved++;
                    continue;
                }

                string cacheKey = string.Join("\u0001", pathNodes);
                ItemMapping mapping;
                if (!mappingCache.TryGetValue(cacheKey, out mapping))
                {
                    mapping = BuildMapping(document, pathNodes, fallbackNodeTypes);
                    mappingCache[cacheKey] = mapping;
                }

                if (mapping.SourceItem == null)
                {
                    result.PathUnresolved++;
                    continue;
                }
                result.PathResolved++;

                if (mapping.ObjectItem == null)
                {
                    result.NoObjectLevel++;
                    continue;
                }
                if (ReferenceEquals(mapping.ObjectItem, mapping.SourceItem) || mapping.Replacements.Count == 0)
                {
                    result.AlreadyObject++;
                    continue;
                }

                result.PromotedToObject++;
                result.ValuesReplaced += SubstituteValues(clashObject, mapping.Replacements);
            }

            xdoc.Save(outputXmlPath);
            result.OutputFile = outputXmlPath;
            return result;
        }

        /// <summary>
        /// The standard report stores each clash object's location in the selection
        /// tree as &lt;pathlink&gt;...&lt;path&gt;&lt;node&gt;File.nwc&lt;/node&gt;&lt;node&gt;...&lt;/node&gt;...
        /// Nodes are collected by local name so namespaces/prefixes don't matter.
        /// </summary>
        private static List<string> ExtractPathNodes(XElement clashObject)
        {
            return clashObject.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "node", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value)
                .ToList();
        }

        private static ItemMapping BuildMapping(Document document, List<string> pathNodes, IList<string> fallbackNodeTypes)
        {
            var mapping = new ItemMapping { Replacements = new Dictionary<string, string>(StringComparer.Ordinal) };

            mapping.SourceItem = ResolveByPath(document, pathNodes);
            if (mapping.SourceItem == null) return mapping;

            mapping.ObjectItem = ObjectResolver.FindObjectLevel(mapping.SourceItem, fallbackNodeTypes);
            if (mapping.ObjectItem == null || ReferenceEquals(mapping.ObjectItem, mapping.SourceItem)) return mapping;

            mapping.Replacements = BuildReplacementMap(mapping.SourceItem, mapping.ObjectItem);
            return mapping;
        }

        /// <summary>
        /// Walks the selection tree by display names, level by level. Keeps every
        /// candidate at each level (duplicate names are common), so the walk only
        /// commits at the end. Returns null when the path cannot be followed -
        /// typically because a different model is open than the one the report was
        /// written from.
        /// </summary>
        private static ModelItem ResolveByPath(Document document, List<string> pathNodes)
        {
            var candidates = new List<ModelItem>();
            foreach (Model model in document.Models)
            {
                if (model.RootItem == null) continue;
                if (NamesMatch(model.RootItem.DisplayName, pathNodes[0]))
                    candidates.Add(model.RootItem);
            }

            // Single-model fallback: accept the only root even if the report spells
            // the file name differently (path prefix, changed extension, etc.).
            if (candidates.Count == 0)
            {
                var roots = document.Models.OfType<Model>()
                    .Where(m => m.RootItem != null).Select(m => m.RootItem).ToList();
                if (roots.Count == 1) candidates.Add(roots[0]);
            }

            for (int level = 1; level < pathNodes.Count && candidates.Count > 0; level++)
            {
                var next = new List<ModelItem>();
                foreach (var candidate in candidates)
                {
                    foreach (ModelItem child in candidate.Children)
                    {
                        if (NamesMatch(child.DisplayName, pathNodes[level]))
                            next.Add(child);
                    }
                }
                candidates = next;
            }

            return candidates.Count > 0 ? candidates[0] : null;
        }

        private static bool NamesMatch(string modelName, string reportName)
        {
            string a = (modelName ?? "").Trim();
            string b = (reportName ?? "").Trim();
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

            // Report sometimes carries the file name with a different extension /
            // without one; compare the stems too, but only when both look like names.
            if (a.Length > 0 && b.Length > 0)
            {
                string sa = StripExtension(a);
                string sb = StripExtension(b);
                if ((sa != a || sb != b) && string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string StripExtension(string name)
        {
            int dot = name.LastIndexOf('.');
            // Treat a short trailing chunk as an extension (".nwc", ".rvt", ".dwg"...).
            if (dot > 0 && name.Length - dot <= 5) return name.Substring(0, dot);
            return name;
        }

        /// <summary>
        /// Builds the value substitution table: for every property of the SOURCE
        /// sub-object whose display value is unique among that item's properties,
        /// look the same property up on the OBJECT-level item; if it exists there
        /// with a different non-empty value, map old -> new. The report is then
        /// patched purely by value matching, so it works for whatever columns and
        /// smart tags the user configured in Clash Detective - element ids, GUIDs,
        /// item names alike. Non-unique values (e.g. "0.000" shared by several
        /// numeric properties) are skipped as ambiguous rather than guessed at.
        /// </summary>
        private static Dictionary<string, string> BuildReplacementMap(ModelItem sourceItem, ModelItem objectItem)
        {
            var valueCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var valueToProp = new Dictionary<string, PropRef>(StringComparer.Ordinal);

            foreach (PropertyCategory category in sourceItem.PropertyCategories)
            {
                foreach (DataProperty property in category.Properties)
                {
                    string value = SafeDisplayString(property);
                    if (string.IsNullOrEmpty(value)) continue;

                    int count;
                    valueCount.TryGetValue(value, out count);
                    valueCount[value] = count + 1;
                    if (count == 0)
                    {
                        valueToProp[value] = new PropRef
                        {
                            CategoryName = category.Name,
                            CategoryDisplayName = category.DisplayName,
                            PropertyName = property.Name,
                            PropertyDisplayName = property.DisplayName
                        };
                    }
                }
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in valueToProp)
            {
                if (valueCount[pair.Key] != 1) continue; // ambiguous on the source item

                var newProperty = FindProperty(objectItem, pair.Value);
                if (newProperty == null) continue;

                string newValue = SafeDisplayString(newProperty);
                if (string.IsNullOrEmpty(newValue) || newValue == pair.Key) continue;

                map[pair.Key] = newValue;
            }
            return map;
        }

        private sealed class PropRef
        {
            public string CategoryName;
            public string CategoryDisplayName;
            public string PropertyName;
            public string PropertyDisplayName;
        }

        private static DataProperty FindProperty(ModelItem item, PropRef propRef)
        {
            try
            {
                var byName = item.PropertyCategories.FindPropertyByName(propRef.CategoryName, propRef.PropertyName);
                if (byName != null) return byName;
            }
            catch
            {
            }
            try
            {
                return item.PropertyCategories.FindPropertyByDisplayName(propRef.CategoryDisplayName, propRef.PropertyDisplayName);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeDisplayString(DataProperty property)
        {
            try { return property.Value.ToDisplayString(); }
            catch { return null; }
        }

        /// <summary>
        /// Rewrites the text of every &lt;value&gt; element inside the clash object
        /// (these carry objectattribute and smarttag values in the standard report)
        /// whose current content matches a source-item property value.
        /// </summary>
        private static int SubstituteValues(XElement clashObject, Dictionary<string, string> replacements)
        {
            int replaced = 0;
            var valueElements = clashObject.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "value", StringComparison.OrdinalIgnoreCase));

            foreach (var valueElement in valueElements)
            {
                string newValue;
                if (replacements.TryGetValue(valueElement.Value, out newValue))
                {
                    valueElement.Value = newValue;
                    replaced++;
                }
            }
            return replaced;
        }
    }
}
