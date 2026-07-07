using System;
using System.Collections.Generic;
using System.Globalization;
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
        public int Ambiguous;
        public int NoIdAttribute;
        public int ValuesReplaced;
        public string OutputFile;
    }

    /// <summary>
    /// Patches a standard Clash Detective XML report.
    ///
    /// Key insight: the tree path in the report is NOT unique (a model has many
    /// identically named walls), so the reported id itself is used to identify
    /// the element among all path matches:
    ///  - if some candidate's true id ("Объект"/"Id") equals the reported id, the
    ///    report is already correct - nothing is touched;
    ///  - otherwise the candidate whose geometry-id chain ("ID объекта") contains
    ///    the reported id is the right instance, and ITS true id is written;
    ///  - when the element cannot be identified unambiguously, the value is left
    ///    alone rather than guessed at.
    /// </summary>
    public static class ReportPostProcessor
    {
        private const int MaxDiagnosedCases = 8;

        private enum DecisionKind
        {
            PathNotFound,
            AlreadyCorrect,
            Replace,
            TrueIdMissing,
            Ambiguous
        }

        private sealed class Decision
        {
            public DecisionKind Kind;
            public string TrueId;
        }

        public static FixReportResult Fix(Document document, string inputXmlPath, string outputXmlPath,
            ClashIdFixerConfig config, Action<int, int> progress, StringBuilder diagnostics)
        {
            var result = new FixReportResult();
            int diagnosedPaths = 0, diagnosedNoId = 0, diagnosedAmbiguous = 0;

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

            // Path candidates are cached per unique path; decisions per (path, id).
            var candidatesCache = new Dictionary<string, List<ModelItem>>(StringComparer.Ordinal);
            var decisionCache = new Dictionary<string, Decision>(StringComparer.Ordinal);

            int index = 0;
            foreach (var clashObject in clashObjects)
            {
                index++;
                if (progress != null) progress(index, clashObjects.Count);

                var idValueElements = FindReportIdValues(clashObject, config.ReportIdAttributeNames);
                string reportedId = idValueElements.Select(e => e.Value.Trim())
                    .FirstOrDefault(v => v.Length > 0);
                if (reportedId == null)
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

                string pathKey = string.Join("\n", pathNodes);
                string decisionKey = pathKey + "\n#id=" + reportedId;

                Decision decision;
                if (!decisionCache.TryGetValue(decisionKey, out decision))
                {
                    List<ModelItem> candidates;
                    if (!candidatesCache.TryGetValue(pathKey, out candidates))
                    {
                        candidates = ResolveByPath(document, pathNodes);
                        candidatesCache[pathKey] = candidates;
                    }

                    decision = Decide(candidates, reportedId, config);
                    decisionCache[decisionKey] = decision;

                    if (diagnostics != null)
                    {
                        if (decision.Kind == DecisionKind.PathNotFound && diagnosedPaths < MaxDiagnosedCases)
                        {
                            diagnosedPaths++;
                            DiagnosePath(document, pathNodes, diagnostics);
                        }
                        else if (decision.Kind == DecisionKind.TrueIdMissing && diagnosedNoId < MaxDiagnosedCases)
                        {
                            diagnosedNoId++;
                            DescribeAncestors(candidates[0], diagnostics);
                        }
                        else if (decision.Kind == DecisionKind.Ambiguous && diagnosedAmbiguous < MaxDiagnosedCases)
                        {
                            diagnosedAmbiguous++;
                            DescribeAmbiguity(candidates, pathNodes, reportedId, config, diagnostics);
                        }
                    }
                }

                switch (decision.Kind)
                {
                    case DecisionKind.PathNotFound:
                        result.PathUnresolved++;
                        break;

                    case DecisionKind.AlreadyCorrect:
                        result.PathResolved++;
                        result.AlreadyCorrect++;
                        break;

                    case DecisionKind.TrueIdMissing:
                        result.PathResolved++;
                        result.TrueIdNotFound++;
                        break;

                    case DecisionKind.Ambiguous:
                        result.PathResolved++;
                        result.Ambiguous++;
                        break;

                    case DecisionKind.Replace:
                        result.PathResolved++;
                        int replaced = 0;
                        foreach (var valueElement in idValueElements)
                        {
                            if (!string.Equals(valueElement.Value.Trim(), decision.TrueId, StringComparison.Ordinal))
                            {
                                valueElement.Value = decision.TrueId;
                                replaced++;
                            }
                        }
                        result.ValuesReplaced += replaced;
                        if (replaced > 0) result.IdReplaced++; else result.AlreadyCorrect++;
                        break;
                }
            }

            xdoc.Save(outputXmlPath);
            result.OutputFile = outputXmlPath;
            return result;
        }

        /// <summary>
        /// Chooses what to do with one report entry given all model items whose
        /// tree path matches the report path.
        /// </summary>
        private static Decision Decide(List<ModelItem> candidates, string reportedId, ClashIdFixerConfig config)
        {
            if (candidates == null || candidates.Count == 0)
                return new Decision { Kind = DecisionKind.PathNotFound };

            var trueIds = new List<string>(candidates.Count);
            foreach (var candidate in candidates)
            {
                string trueId = FindTrueId(candidate, config);
                trueIds.Add(trueId);

                // The reported id IS some candidate's true id - report is correct.
                if (trueId != null && string.Equals(trueId, reportedId, StringComparison.Ordinal))
                    return new Decision { Kind = DecisionKind.AlreadyCorrect };
            }

            // The reported id is a geometry-level id: find the instance that
            // carries it and take that instance's true id.
            for (int i = 0; i < candidates.Count; i++)
            {
                if (trueIds[i] == null) continue;
                if (ChainHasModelId(candidates[i], reportedId, config))
                    return new Decision { Kind = DecisionKind.Replace, TrueId = trueIds[i] };
            }

            // No candidate carries the reported id at all. Only safe when the path
            // pins down a single element anyway.
            var distinct = trueIds.Where(t => t != null).Distinct().ToList();
            if (distinct.Count == 1)
                return new Decision { Kind = DecisionKind.Replace, TrueId = distinct[0] };
            if (distinct.Count == 0)
                return new Decision { Kind = DecisionKind.TrueIdMissing };

            return new Decision { Kind = DecisionKind.Ambiguous };
        }

        /// <summary>
        /// True when the reported id appears among the values of the geometry-id
        /// categories ("ID объекта" / LcRevitId) on the item or any of its parents.
        /// </summary>
        private static bool ChainHasModelId(ModelItem item, string reportedId, ClashIdFixerConfig config)
        {
            for (var current = item; current != null; current = current.Parent)
            {
                try
                {
                    foreach (PropertyCategory category in current.PropertyCategories)
                    {
                        if (!MatchesAnyName(category, config.ModelIdCategories)) continue;

                        foreach (DataProperty property in category.Properties)
                        {
                            string value = VariantToString(property.Value);
                            if (value != null && string.Equals(value.Trim(), reportedId, StringComparison.Ordinal))
                                return true;
                        }
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        private static bool MatchesAnyName(PropertyCategory category, IList<string> names)
        {
            string display = null, internalName = null;
            try { display = category.DisplayName; } catch { }
            try { internalName = category.Name; } catch { }

            foreach (var name in names)
            {
                if (display != null && string.Equals(display.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
                if (internalName != null && string.Equals(internalName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

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
        /// Resolves a report path to ALL matching ModelItems. The report prefixes
        /// the path with a generic "Файл"/"File" node and includes the container
        /// NWD level, so the anchor point is searched at any offset; the rest of
        /// the path is walked down by display names.
        /// </summary>
        private static List<ModelItem> ResolveByPath(Document document, List<string> pathNodes)
        {
            var roots = document.Models.OfType<Model>()
                .Where(m => m.RootItem != null)
                .Select(m => m.RootItem)
                .ToList();
            if (roots.Count == 0) return new List<ModelItem>();

            for (int offset = 0; offset < pathNodes.Count; offset++)
            {
                var anchored = roots.Where(r => NamesMatch(r.DisplayName, pathNodes[offset])).Cast<ModelItem>().ToList();
                if (anchored.Count == 0) continue;

                var items = WalkDown(anchored, pathNodes, offset + 1);
                if (items.Count > 0) return items;
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

                var items = WalkDown(anchored, pathNodes, offset + 1);
                if (items.Count > 0) return items;
            }

            return new List<ModelItem>();
        }

        private static List<ModelItem> WalkDown(List<ModelItem> candidates, List<string> pathNodes, int startLevel)
        {
            for (int level = startLevel; level < pathNodes.Count; level++)
            {
                var next = MatchChildren(candidates, pathNodes[level]);
                if (next.Count == 0) return new List<ModelItem>();
                candidates = next;
            }
            return candidates;
        }

        /// <summary>
        /// Matches the next path node against the candidates' children. Unnamed
        /// nodes (typically leaf geometry) are shown in the Navisworks tree - and
        /// written to the report - with a type placeholder like "Твердое тело" /
        /// "Solid", while their real DisplayName is empty; so when nothing matches
        /// by name, unnamed children are accepted for that level.
        /// </summary>
        private static List<ModelItem> MatchChildren(List<ModelItem> candidates, string reportName)
        {
            var next = new List<ModelItem>();
            foreach (var candidate in candidates)
            {
                foreach (ModelItem child in candidate.Children)
                {
                    if (NamesMatch(child.DisplayName, reportName))
                        next.Add(child);
                }
            }
            if (next.Count > 0) return next;

            foreach (var candidate in candidates)
            {
                foreach (ModelItem child in candidate.Children)
                {
                    if (string.IsNullOrWhiteSpace(child.DisplayName))
                        next.Add(child);
                }
            }
            return next;
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
        /// The true element id: the "Id" row of the "Объект" properties tab
        /// (internally LcRevitData_Element / LcRevitPropertyElementId), which only
        /// exists at the real-object level. Climbs from the item up through its
        /// parents and returns the first such value found.
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

                        string value = VariantToString(property.Value);
                        if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// VariantData accessors are strictly typed: ToDisplayString() throws for
        /// a numeric value (which is exactly how the Revit element Id is stored,
        /// as Int32). Try the accessors in turn, then fall back to parsing
        /// VariantData.ToString() ("Type:Value").
        /// </summary>
        private static string VariantToString(VariantData value)
        {
            if (value == null) return null;

            try { return value.ToDisplayString(); } catch { }
            try { return value.ToInt32().ToString(CultureInfo.InvariantCulture); } catch { }
            try { return value.ToIdentifierString(); } catch { }
            try { return value.ToDouble().ToString(CultureInfo.InvariantCulture); } catch { }
            try
            {
                var constant = value.ToNamedConstant();
                if (constant != null) return constant.DisplayName;
            }
            catch { }
            try { return value.ToBoolean().ToString(CultureInfo.InvariantCulture); } catch { }

            try
            {
                string raw = value.ToString();
                if (raw == null) return null;
                int colon = raw.IndexOf(':');
                return colon >= 0 ? raw.Substring(colon + 1) : raw;
            }
            catch
            {
                return null;
            }
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
                var next = MatchChildren(candidates, pathNodes[level]);
                if (next.Count == 0)
                {
                    diag.AppendLine(string.Format("СТОП на узле[{0}] = \"{1}\": совпадений нет.", level, pathNodes[level]));
                    var childNames = candidates
                        .SelectMany(c => c.Children.Cast<ModelItem>())
                        .Select(c => string.IsNullOrEmpty(c.DisplayName) ? "(без имени)" : c.DisplayName)
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
        /// Written to the _diag.txt file when the item was found but no true-id
        /// property exists up the chain: dumps every category and property of the
        /// item and its parents so the correct names can be copied into the config.
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
                            string value = VariantToString(property.Value) ?? "(не читается)";
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

        /// <summary>
        /// Written to the _diag.txt file when several different elements match the
        /// path but none of them carries the reported id: lists each candidate's
        /// true id and the geometry ids found on its chain.
        /// </summary>
        private static void DescribeAmbiguity(List<ModelItem> candidates, List<string> pathNodes,
            string reportedId, ClashIdFixerConfig config, StringBuilder diag)
        {
            diag.AppendLine("=== ПУТЬ НЕОДНОЗНАЧЕН, ID ИЗ ОТЧЁТА НЕ НАЙДЕН НИ У ОДНОГО КАНДИДАТА ===");
            diag.AppendLine("Путь из отчёта: " + string.Join(" > ", pathNodes));
            diag.AppendLine("Id из отчёта: " + reportedId);
            diag.AppendLine("Кандидатов по пути: " + candidates.Count);

            foreach (var candidate in candidates.Take(6))
            {
                string trueId = FindTrueId(candidate, config) ?? "(нет)";
                var chainIds = CollectModelIds(candidate, config).Take(10).ToList();
                diag.AppendLine(string.Format("  Кандидат \"{0}\": Объект/Id = {1}; ID объекта по цепочке: {2}",
                    candidate.DisplayName ?? "(без имени)", trueId,
                    chainIds.Count > 0 ? string.Join(", ", chainIds) : "(нет)"));
            }
            diag.AppendLine();
        }

        private static IEnumerable<string> CollectModelIds(ModelItem item, ClashIdFixerConfig config)
        {
            for (var current = item; current != null; current = current.Parent)
            {
                List<string> values = null;
                try
                {
                    foreach (PropertyCategory category in current.PropertyCategories)
                    {
                        if (!MatchesAnyName(category, config.ModelIdCategories)) continue;
                        foreach (DataProperty property in category.Properties)
                        {
                            string value = VariantToString(property.Value);
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                if (values == null) values = new List<string>();
                                values.Add(value.Trim());
                            }
                        }
                    }
                }
                catch
                {
                }

                if (values != null)
                {
                    foreach (var value in values) yield return value;
                }
            }
        }
    }
}
