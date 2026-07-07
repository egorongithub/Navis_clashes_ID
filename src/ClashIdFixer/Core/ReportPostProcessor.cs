using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Autodesk.Navisworks.Api;
using ClashIdFixer.Config;

namespace ClashIdFixer.Core
{
    public sealed class FixReportResult
    {
        public int ClashObjectCount;
        public int PathResolved;
        public int PathUnresolved;
        public int AlreadyCorrect;
        public int IdReplaced;
        public int TrueIdNotFound;
        public int NoIdAttribute;
        public int ValuesReplaced;
        public string OutputFile;
    }

    /// <summary>
    /// Patches a standard Clash Detective XML report. For every clash object the
    /// report carries an id attribute (e.g. "ID объекта" / value) taken from the
    /// geometry sub-object. The true element id lives in the "Объект" properties
    /// tab, "Id" row, which only exists at the real-object level. So: resolve the
    /// report path to a ModelItem, walk up the tree to the first node that HAS
    /// the "Объект/Id" property, and if its value differs from what the report
    /// says - write the true value into the report. Ids that already match are
    /// left untouched. Nothing else in the report is modified.
    /// </summary>
    public static class ReportPostProcessor
    {
        private sealed class ItemMapping
        {
            public ModelItem SourceItem;
            public string TrueId;
        }

        // How many unique problem cases get a full dump in the diagnostics file -
        // enough to identify the real category/property/tree names without
        // producing a megabyte log on a large report.
        private const int MaxDiagnosedCases = 8;

        public static FixReportResult Fix(Document document, string inputXmlPath, string outputXmlPath,
            ClashIdFixerConfig config, Action<int, int> progress, StringBuilder diagnostics)
        {
            var result = new FixReportResult();
            int diagnosedPaths = 0, diagnosedNoId = 0;

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

                var idValueElements = FindReportIdValues(clashObject, config.ReportIdAttributeNames);
                if (idValueElements.Count == 0)
                {
                    result.NoIdAttribute++;
                    continue;
                }

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
                    mapping = new ItemMapping();
                    mapping.SourceItem = ResolveByPath(document, pathNodes);
                    if (mapping.SourceItem != null)
                        mapping.TrueId = FindTrueId(mapping.SourceItem, config);
                    mappingCache[cacheKey] = mapping;

                    if (diagnostics != null)
                    {
                        if (mapping.SourceItem == null && diagnosedPaths < MaxDiagnosedCases)
                        {
                            diagnosedPaths++;
                            DiagnosePath(document, pathNodes, diagnostics);
                        }
                        else if (mapping.SourceItem != null && string.IsNullOrEmpty(mapping.TrueId)
                                 && diagnosedNoId < MaxDiagnosedCases)
                        {
                            diagnosedNoId++;
                            DescribeAncestors(mapping.SourceItem, diagnostics);
                        }
                    }
                }

                if (mapping.SourceItem == null)
                {
                    result.PathUnresolved++;
                    continue;
                }
                result.PathResolved++;

                if (string.IsNullOrEmpty(mapping.TrueId))
                {
                    result.TrueIdNotFound++;
                    continue;
                }

                bool replacedAny = false;
                foreach (var valueElement in idValueElements)
                {
                    if (!string.Equals(valueElement.Value.Trim(), mapping.TrueId.Trim(), StringComparison.Ordinal))
                    {
                        valueElement.Value = mapping.TrueId;
                        result.ValuesReplaced++;
                        replacedAny = true;
                    }
                }

                if (replacedAny) result.IdReplaced++; else result.AlreadyCorrect++;
            }

            xdoc.Save(outputXmlPath);
            result.OutputFile = outputXmlPath;
            return result;
        }

        /// <summary>
        /// Collects the &lt;value&gt; elements of every objectattribute/smarttag of
        /// the clash object whose &lt;name&gt; is one of the configured id names
        /// ("ID объекта" in the standard Russian report).
        /// </summary>
        private static List<XElement> FindReportIdValues(XElement clashObject, IList<string> idAttributeNames)
        {
            var found = new List<XElement>();

            var entries = clashObject.Descendants().Where(e =>
                string.Equals(e.Name.LocalName, "objectattribute", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(e.Name.LocalName, "smarttag", StringComparison.OrdinalIgnoreCase));

            foreach (var entry in entries)
            {
                XElement nameElement = null, valueElement = null;
                foreach (var child in entry.Elements())
                {
                    if (string.Equals(child.Name.LocalName, "name", StringComparison.OrdinalIgnoreCase)) nameElement = child;
                    else if (string.Equals(child.Name.LocalName, "value", StringComparison.OrdinalIgnoreCase)) valueElement = child;
                }
                if (nameElement == null || valueElement == null) continue;

                string name = nameElement.Value.Trim();
                if (idAttributeNames.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                    found.Add(valueElement);
            }
            return found;
        }

        private static List<string> ExtractPathNodes(XElement clashObject)
        {
            return clashObject.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "node", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value)
                .ToList();
        }

        /// <summary>
        /// Resolves a report path to a ModelItem. The report prefixes the path
        /// with a generic "Файл"/"File" node and includes the container NWD level
        /// (e.g. Файл &gt; model.nwd &gt; part.nwc &gt; layer &gt; ...), while the
        /// open document's roots may sit at any of those levels - so the anchor
        /// point is searched: the first path node that matches a root wins, and
        /// the rest of the path is walked down by display names from there. As a
        /// last resort the walk is tried from the roots' children, for reports
        /// whose leading nodes don't name any root at all.
        /// </summary>
        private static ModelItem ResolveByPath(Document document, List<string> pathNodes)
        {
            var roots = document.Models.OfType<Model>()
                .Where(m => m.RootItem != null)
                .Select(m => m.RootItem)
                .ToList();
            if (roots.Count == 0) return null;

            for (int offset = 0; offset < pathNodes.Count; offset++)
            {
                var anchored = roots.Where(r => NamesMatch(r.DisplayName, pathNodes[offset])).Cast<ModelItem>().ToList();
                if (anchored.Count == 0) continue;

                var item = WalkDown(anchored, pathNodes, offset + 1);
                if (item != null) return item;
            }

            for (int offset = 0; offset < pathNodes.Count; offset++)
            {
                var anchored = new List<ModelItem>();
                foreach (var root in roots)
                {
                    foreach (ModelItem child in root.Children)
                    {
                        if (NamesMatch(child.DisplayName, pathNodes[offset]))
                            anchored.Add(child);
                    }
                }
                if (anchored.Count == 0) continue;

                var item = WalkDown(anchored, pathNodes, offset + 1);
                if (item != null) return item;
            }

            return null;
        }

        /// <summary>
        /// Walks the remaining path levels strictly; keeps all candidates at each
        /// level (duplicate names are common) and only succeeds if the whole path
        /// is consumed.
        /// </summary>
        private static ModelItem WalkDown(List<ModelItem> candidates, List<string> pathNodes, int startLevel)
        {
            for (int level = startLevel; level < pathNodes.Count; level++)
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
                if (next.Count == 0) return null;
                candidates = next;
            }
            return candidates.Count > 0 ? candidates[0] : null;
        }

        private static bool NamesMatch(string modelName, string reportName)
        {
            string a = (modelName ?? "").Trim();
            string b = (reportName ?? "").Trim();
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

            // The file name may be spelled with a different / missing extension.
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
        /// The true element id: the "Id" row of the "Объект" properties tab, which
        /// exists only at the real-object level. Climbs from the resolved item up
        /// through its parents and returns the first such value found. Category and
        /// property names are configurable (both display and internal names are
        /// tried) to survive localization differences.
        /// </summary>
        private static string FindTrueId(ModelItem item, ClashIdFixerConfig config)
        {
            for (var current = item; current != null; current = current.Parent)
            {
                foreach (var categoryName in config.TrueIdCategories)
                {
                    foreach (var propertyName in config.TrueIdProperties)
                    {
                        var property = FindProperty(current, categoryName, propertyName);
                        if (property == null) continue;

                        string value = null;
                        try { value = property.Value.ToDisplayString(); }
                        catch { }

                        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
                }
            }
            return null;
        }

        private static DataProperty FindProperty(ModelItem item, string categoryName, string propertyName)
        {
            try
            {
                var byDisplay = item.PropertyCategories.FindPropertyByDisplayName(categoryName, propertyName);
                if (byDisplay != null) return byDisplay;
            }
            catch
            {
            }
            try
            {
                return item.PropertyCategories.FindPropertyByName(categoryName, propertyName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Written to the _diag.txt file when a report path cannot be found in the
        /// open model: shows the report path, the document roots, where the walk
        /// stopped and what children were actually available at that level.
        /// </summary>
        private static void DiagnosePath(Document document, List<string> pathNodes, StringBuilder diag)
        {
            diag.AppendLine("=== ПУТЬ ИЗ ОТЧЁТА НЕ НАЙДЕН В МОДЕЛИ ===");
            diag.AppendLine("Путь из отчёта: " + string.Join(" > ", pathNodes));

            var roots = document.Models.OfType<Model>()
                .Where(m => m.RootItem != null)
                .Select(m => m.RootItem)
                .ToList();
            diag.AppendLine("Корни открытого документа: " +
                string.Join(" | ", roots.Select(r => "\"" + (r.DisplayName ?? "") + "\"")));

            int anchorOffset = -1;
            List<ModelItem> candidates = null;
            for (int offset = 0; offset < pathNodes.Count && anchorOffset < 0; offset++)
            {
                var anchored = roots.Where(r => NamesMatch(r.DisplayName, pathNodes[offset])).Cast<ModelItem>().ToList();
                if (anchored.Count > 0)
                {
                    anchorOffset = offset;
                    candidates = anchored;
                }
            }
            if (anchorOffset < 0)
            {
                for (int offset = 0; offset < pathNodes.Count && anchorOffset < 0; offset++)
                {
                    var anchored = new List<ModelItem>();
                    foreach (var root in roots)
                        foreach (ModelItem child in root.Children)
                            if (NamesMatch(child.DisplayName, pathNodes[offset]))
                                anchored.Add(child);
                    if (anchored.Count > 0)
                    {
                        anchorOffset = offset;
                        candidates = anchored;
                    }
                }
            }

            if (anchorOffset < 0)
            {
                diag.AppendLine("Ни один узел пути не совпал ни с корнями документа, ни с их детьми.");
                diag.AppendLine();
                return;
            }

            diag.AppendLine(string.Format("Привязка: узел[{0}] = \"{1}\", кандидатов: {2}",
                anchorOffset, pathNodes[anchorOffset], candidates.Count));

            for (int level = anchorOffset + 1; level < pathNodes.Count; level++)
            {
                var next = new List<ModelItem>();
                foreach (var candidate in candidates)
                    foreach (ModelItem child in candidate.Children)
                        if (NamesMatch(child.DisplayName, pathNodes[level]))
                            next.Add(child);

                if (next.Count == 0)
                {
                    diag.AppendLine(string.Format("СТОП на узле[{0}] = \"{1}\": совпадений нет.", level, pathNodes[level]));
                    var childNames = candidates
                        .SelectMany(c => c.Children.Cast<ModelItem>())
                        .Select(c => c.DisplayName ?? "(без имени)")
                        .Distinct()
                        .Take(25)
                        .ToList();
                    diag.AppendLine("Реальные дети на этом уровне: " + string.Join(" | ", childNames));
                    diag.AppendLine();
                    return;
                }
                candidates = next;
            }

            diag.AppendLine("(весь путь прошёл - элемент должен находиться)");
            diag.AppendLine();
        }

        /// <summary>
        /// Written to the _diag.txt file when the item was found but no
        /// "Объект/Id" property exists up the chain: dumps every category and
        /// property (display name, internal name, value) of the item and its
        /// parents, so the correct names can be copied straight into
        /// ClashIdFixer.config.xml.
        /// </summary>
        private static void DescribeAncestors(ModelItem item, StringBuilder diag)
        {
            diag.AppendLine("=== ЭЛЕМЕНТ НАЙДЕН, НО СВОЙСТВО С ИСТИННЫМ ID НЕ НАЙДЕНО ===");
            diag.AppendLine("Все категории/свойства элемента и его родителей (снизу вверх):");

            int depth = 0;
            for (var current = item; current != null && depth < 8; current = current.Parent, depth++)
            {
                diag.AppendLine(string.Format("[{0}] \"{1}\"", depth, current.DisplayName ?? "(без имени)"));
                try
                {
                    foreach (PropertyCategory category in current.PropertyCategories)
                    {
                        diag.AppendLine(string.Format("    Категория \"{0}\" [{1}]:",
                            category.DisplayName ?? "", category.Name ?? ""));
                        foreach (DataProperty property in category.Properties)
                        {
                            string value;
                            try { value = property.Value.ToDisplayString(); }
                            catch { value = "(не читается)"; }
                            diag.AppendLine(string.Format("        \"{0}\" [{1}] = {2}",
                                property.DisplayName ?? "", property.Name ?? "", value));
                        }
                    }
                }
                catch (Exception ex)
                {
                    diag.AppendLine("    (ошибка чтения свойств: " + ex.Message + ")");
                }
            }
            diag.AppendLine();
        }
    }
}
