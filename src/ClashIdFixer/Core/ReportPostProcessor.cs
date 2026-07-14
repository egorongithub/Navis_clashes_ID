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

        /// <summary>One line per skipped report entry (id left untouched).</summary>
        public List<string> Errors = new List<string>();

        /// <summary>How the report/model unit scale was determined.</summary>
        public string UnitsSummary;
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

            // Report coordinates are in the units declared on <exchange> (display
            // units, e.g. meters), while the API returns geometry in the units the
            // model was authored in (feet for Revit-sourced files - but NOT
            // guaranteed). The scale is therefore CALIBRATED: for the first clash
            // points, plausible unit hypotheses are tried and the one that puts
            // the points onto the candidate elements wins.
            string reportUnits = "";
            double reportUnitsToMeters = 1.0;
            if (xdoc.Root != null)
            {
                var unitsAttr = xdoc.Root.Attribute("units");
                if (unitsAttr != null)
                {
                    reportUnits = unitsAttr.Value;
                    reportUnitsToMeters = UnitsToMeters(reportUnits);
                }
            }

            // Path candidates are cached per unique path; decisions per (path, id, point).
            var candidatesCache = new Dictionary<string, List<ModelItem>>(StringComparer.Ordinal);
            var decisionCache = new Dictionary<string, Decision>(StringComparer.Ordinal);

            string unitsSummary;
            double metersPerUnit = CalibrateMetersPerUnit(document, clashObjects, candidatesCache,
                reportUnitsToMeters, out unitsSummary);
            result.UnitsSummary = string.Format(CultureInfo.InvariantCulture,
                "Единицы отчёта: \"{0}\" ({1} м). {2}", reportUnits, reportUnitsToMeters, unitsSummary);

            bool diagHeaderWritten = false;
            Action writeDiagHeader = () =>
            {
                if (diagHeaderWritten || diagnostics == null) return;
                diagHeaderWritten = true;
                diagnostics.AppendLine(result.UnitsSummary);
                diagnostics.AppendLine();
            };

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
                    AddError(result, clashObject, null, "в записи отчёта нет атрибута с ID (см. ReportIdAttributeNames в настройках)");
                    continue;
                }

                var pathNodes = ExtractPathNodes(clashObject);
                if (pathNodes.Count == 0)
                {
                    result.PathUnresolved++;
                    AddError(result, clashObject, reportedId, "в записи отчёта нет пути (pathlink)");
                    continue;
                }

                double px, py, pz;
                bool hasPoint = TryGetClashPoint(clashObject, reportUnitsToMeters / metersPerUnit, out px, out py, out pz);

                string pathKey = string.Join("\n", pathNodes);
                string decisionKey = pathKey + "\n#id=" + reportedId + (hasPoint
                    ? "\n#pt=" + px.ToString("F3", CultureInfo.InvariantCulture)
                        + ";" + py.ToString("F3", CultureInfo.InvariantCulture)
                        + ";" + pz.ToString("F3", CultureInfo.InvariantCulture)
                    : "");

                Decision decision;
                if (!decisionCache.TryGetValue(decisionKey, out decision))
                {
                    List<ModelItem> candidates;
                    if (!candidatesCache.TryGetValue(pathKey, out candidates))
                    {
                        candidates = ResolveByPath(document, pathNodes);
                        candidatesCache[pathKey] = candidates;
                    }

                    decision = Decide(candidates, reportedId, hasPoint, px, py, pz, metersPerUnit, config);
                    decisionCache[decisionKey] = decision;

                    if (diagnostics != null)
                    {
                        if (decision.Kind == DecisionKind.PathNotFound && diagnosedPaths < MaxDiagnosedCases)
                        {
                            diagnosedPaths++;
                            writeDiagHeader();
                            DiagnosePath(document, pathNodes, diagnostics);
                        }
                        else if (decision.Kind == DecisionKind.TrueIdMissing && diagnosedNoId < MaxDiagnosedCases)
                        {
                            diagnosedNoId++;
                            writeDiagHeader();
                            DescribeAncestors(candidates[0], diagnostics);
                        }
                        else if (decision.Kind == DecisionKind.Ambiguous && diagnosedAmbiguous < MaxDiagnosedCases)
                        {
                            diagnosedAmbiguous++;
                            writeDiagHeader();
                            DescribeAmbiguity(candidates, pathNodes, reportedId, hasPoint, px, py, pz, config, diagnostics);
                        }
                    }
                }

                switch (decision.Kind)
                {
                    case DecisionKind.PathNotFound:
                        result.PathUnresolved++;
                        AddError(result, clashObject, reportedId, "элемент не найден в открытой модели по пути из отчёта");
                        break;

                    case DecisionKind.AlreadyCorrect:
                        result.PathResolved++;
                        result.AlreadyCorrect++;
                        break;

                    case DecisionKind.TrueIdMissing:
                        result.PathResolved++;
                        result.TrueIdNotFound++;
                        AddError(result, clashObject, reportedId, "выше по дереву не найдено свойство \"Объект/Id\"");
                        break;

                    case DecisionKind.Ambiguous:
                        result.PathResolved++;
                        result.Ambiguous++;
                        AddError(result, clashObject, reportedId, "несколько одинаковых элементов, экземпляр не опознан (по id и точке коллизии)");
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
        /// One error line per skipped entry: clash name, element side, id, reason,
        /// path - enough to find the entry in the report and in the model.
        /// </summary>
        private static void AddError(FixReportResult result, XElement clashObject, string reportedId, string reason)
        {
            string clashName = "(без имени)";
            int side = 0;
            var clashResult = clashObject.Ancestors()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, "clashresult", StringComparison.OrdinalIgnoreCase));
            if (clashResult != null)
            {
                var nameAttr = clashResult.Attribute("name");
                if (nameAttr != null && nameAttr.Value.Length > 0) clashName = nameAttr.Value;

                var siblings = clashResult.Descendants()
                    .Where(e => string.Equals(e.Name.LocalName, "clashobject", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                side = siblings.IndexOf(clashObject) + 1;
            }

            var pathNodes = ExtractPathNodes(clashObject);
            result.Errors.Add(string.Format("{0} | элемент {1} | ID: {2} | {3} | путь: {4}",
                clashName,
                side > 0 ? side.ToString(CultureInfo.InvariantCulture) : "?",
                reportedId ?? "-",
                reason,
                string.Join(" > ", pathNodes)));
        }

        private sealed class ScaleHypothesis
        {
            public string Name;
            public double MetersPerUnit;
            public int Votes;
        }

        /// <summary>
        /// Determines what one API length unit is in meters by trying plausible
        /// hypotheses (document units from the API, feet, meters, mm, cm, inches)
        /// against the first clash points: the correct scale is the one under
        /// which points land on (distance ~0 to) their candidate elements. Revit
        /// models normally come out as feet, but this must not be assumed - other
        /// sources use other units, so it is measured per document instead.
        /// </summary>
        private static double CalibrateMetersPerUnit(Document document, List<XElement> clashObjects,
            Dictionary<string, List<ModelItem>> candidatesCache, double reportUnitsToMeters, out string summary)
        {
            string documentUnits;
            double apiMetersPerUnit = GetDocumentMetersPerUnit(document, out documentUnits);

            var hypotheses = new List<ScaleHypothesis>
            {
                new ScaleHypothesis { Name = "единицы документа (" + documentUnits + ")", MetersPerUnit = apiMetersPerUnit },
                new ScaleHypothesis { Name = "футы", MetersPerUnit = 0.3048 },
                new ScaleHypothesis { Name = "метры", MetersPerUnit = 1.0 },
                new ScaleHypothesis { Name = "миллиметры", MetersPerUnit = 0.001 },
                new ScaleHypothesis { Name = "сантиметры", MetersPerUnit = 0.01 },
                new ScaleHypothesis { Name = "дюймы", MetersPerUnit = 0.0254 },
            };
            // Drop duplicates of the API value so votes are not split between them.
            for (int i = hypotheses.Count - 1; i >= 1; i--)
            {
                if (Math.Abs(hypotheses[i].MetersPerUnit - apiMetersPerUnit) < 1e-9)
                    hypotheses.RemoveAt(i);
            }

            int observations = 0, scanned = 0;
            foreach (var clashObject in clashObjects)
            {
                if (observations >= 8 || scanned >= 300) break;
                scanned++;

                double mx, my, mz; // point in meters
                if (!TryGetClashPoint(clashObject, reportUnitsToMeters, out mx, out my, out mz)) continue;

                var pathNodes = ExtractPathNodes(clashObject);
                if (pathNodes.Count == 0) continue;

                string pathKey = string.Join("\n", pathNodes);
                List<ModelItem> candidates;
                if (!candidatesCache.TryGetValue(pathKey, out candidates))
                {
                    candidates = ResolveByPath(document, pathNodes);
                    candidatesCache[pathKey] = candidates;
                }
                if (candidates.Count == 0) continue;

                ScaleHypothesis best = null;
                double bestMeters = double.MaxValue;
                foreach (var hypothesis in hypotheses)
                {
                    if (hypothesis.MetersPerUnit <= 0) continue;
                    double min = double.MaxValue;
                    foreach (var candidate in candidates)
                    {
                        double d = DistanceToBoundingBox(candidate,
                            mx / hypothesis.MetersPerUnit,
                            my / hypothesis.MetersPerUnit,
                            mz / hypothesis.MetersPerUnit);
                        if (d < min) min = d;
                    }
                    if (min == double.MaxValue) continue;
                    double meters = min * hypothesis.MetersPerUnit;
                    if (meters < bestMeters)
                    {
                        bestMeters = meters;
                        best = hypothesis;
                    }
                }

                if (best != null && bestMeters <= 1.0)
                {
                    best.Votes++;
                    observations++;
                }
            }

            var winner = hypotheses.OrderByDescending(h => h.Votes).First();
            if (winner.Votes == 0)
            {
                summary = string.Format(CultureInfo.InvariantCulture,
                    "Единицы модели: калибровка по точкам не удалась, используются единицы документа из API: {0} ({1} м).",
                    documentUnits, apiMetersPerUnit);
                return apiMetersPerUnit > 0 ? apiMetersPerUnit : 1.0;
            }

            summary = string.Format(CultureInfo.InvariantCulture,
                "Единицы модели (по калибровке точек коллизий): {0} ({1} м), голосов {2} из {3}.",
                winner.Name, winner.MetersPerUnit, winner.Votes, observations);
            return winner.MetersPerUnit;
        }

        // A clash point must essentially touch the element it identifies; these
        // limits (meters) only reject nonsense matches when bounding boxes fail
        // or the point lands far from every candidate.
        private const double PointLimitConfirmed = 5.0;
        private const double PointLimitUnconfirmed = 0.5;

        /// <summary>
        /// Chooses what to do with one report entry given all model items whose
        /// tree path matches the report path.
        ///
        /// Same-type neighbours share both the tree path and (for nested families)
        /// the geometry-level id, so neither alone identifies the instance. The
        /// clash point does: the right element is the one whose geometry the point
        /// actually touches. Ids are only compared within a candidate's own
        /// segment (leaf up to its element node), never on shared ancestors.
        /// </summary>
        private static Decision Decide(List<ModelItem> candidates, string reportedId,
            bool hasPoint, double px, double py, double pz, double metersPerUnit, ClashIdFixerConfig config)
        {
            if (candidates == null || candidates.Count == 0)
                return new Decision { Kind = DecisionKind.PathNotFound };

            var items = new List<ModelItem>();
            var ids = new List<string>();
            foreach (var candidate in candidates)
            {
                string trueId = FindTrueId(candidate, config);

                // The reported id IS some candidate's true id - report is correct.
                if (trueId != null && string.Equals(trueId, reportedId, StringComparison.Ordinal))
                    return new Decision { Kind = DecisionKind.AlreadyCorrect };

                if (trueId != null)
                {
                    items.Add(candidate);
                    ids.Add(trueId);
                }
            }
            if (items.Count == 0)
                return new Decision { Kind = DecisionKind.TrueIdMissing };

            // Keep only candidates whose own segment carries the reported id, when
            // there are any - the id then confirms at least the right sub-family.
            var pool = new List<int>();
            for (int i = 0; i < items.Count; i++)
            {
                if (SegmentHasModelId(items[i], reportedId, config)) pool.Add(i);
            }
            bool idConfirmed = pool.Count > 0;
            if (!idConfirmed)
            {
                for (int i = 0; i < items.Count; i++) pool.Add(i);
            }

            var distinct = pool.Select(i => ids[i]).Distinct().ToList();
            if (distinct.Count == 1)
                return new Decision { Kind = DecisionKind.Replace, TrueId = distinct[0] };

            if (hasPoint)
            {
                int best = -1;
                double bestDistance = double.MaxValue;
                foreach (var i in pool)
                {
                    double distance = DistanceToBoundingBox(items[i], px, py, pz);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }

                // Limits are defined in meters; distances come out in document units.
                double limit = (idConfirmed ? PointLimitConfirmed : PointLimitUnconfirmed)
                    / (metersPerUnit > 0 ? metersPerUnit : 1.0);
                if (best >= 0 && bestDistance <= limit)
                    return new Decision { Kind = DecisionKind.Replace, TrueId = ids[best] };
            }

            return new Decision { Kind = DecisionKind.Ambiguous };
        }

        /// <summary>
        /// True when the reported id appears among the values of the geometry-id
        /// categories ("ID объекта" / LcRevitId) within the candidate's own
        /// segment: from the leaf up to and including the first node that has the
        /// true-id property (the element). Nodes above the element are shared with
        /// neighbouring same-type elements, so matching there would misidentify.
        /// </summary>
        private static bool SegmentHasModelId(ModelItem item, string reportedId, ClashIdFixerConfig config)
        {
            for (var current = item; current != null; current = current.Parent)
            {
                if (NodeHasModelId(current, reportedId, config)) return true;
                if (TrueIdOwn(current, config) != null) return false;
            }
            return false;
        }

        private static bool NodeHasModelId(ModelItem item, string reportedId, ClashIdFixerConfig config)
        {
            try
            {
                foreach (PropertyCategory category in item.PropertyCategories)
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
            return false;
        }

        private static double DistanceToBoundingBox(ModelItem item, double px, double py, double pz)
        {
            try
            {
                var box = item.BoundingBox();
                if (box == null) return double.MaxValue;

                double dx = Math.Max(0.0, Math.Max(box.Min.X - px, px - box.Max.X));
                double dy = Math.Max(0.0, Math.Max(box.Min.Y - py, py - box.Max.Y));
                double dz = Math.Max(0.0, Math.Max(box.Min.Z - pz, pz - box.Max.Z));
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private static bool TryGetClashPoint(XElement clashObject, double unitScale,
            out double px, out double py, out double pz)
        {
            px = py = pz = 0;

            var clashResult = clashObject.Ancestors()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, "clashresult", StringComparison.OrdinalIgnoreCase));
            if (clashResult == null) return false;

            var position = clashResult.Descendants().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, "pos3f", StringComparison.OrdinalIgnoreCase) &&
                e.Parent != null &&
                string.Equals(e.Parent.Name.LocalName, "clashpoint", StringComparison.OrdinalIgnoreCase));
            if (position == null) return false;

            if (!TryReadAttribute(position, "x", out px)) return false;
            if (!TryReadAttribute(position, "y", out py)) return false;
            if (!TryReadAttribute(position, "z", out pz)) return false;

            px *= unitScale;
            py *= unitScale;
            pz *= unitScale;
            return true;
        }

        private static bool TryReadAttribute(XElement element, string name, out double value)
        {
            value = 0;
            var attribute = element.Attribute(name);
            return attribute != null && double.TryParse(attribute.Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static double UnitsToMeters(string units)
        {
            if (string.IsNullOrWhiteSpace(units)) return 1.0;
            switch (units.Trim().ToLowerInvariant())
            {
                case "m": case "meter": case "meters": return 1.0;
                case "mm": case "millimeter": case "millimeters": return 0.001;
                case "cm": case "centimeter": case "centimeters": return 0.01;
                case "km": case "kilometer": case "kilometers": return 1000.0;
                case "ft": case "foot": case "feet": return 0.3048;
                case "in": case "inch": case "inches": return 0.0254;
                case "yd": case "yard": case "yards": return 0.9144;
                case "mi": case "mile": case "miles": return 1609.344;
                case "micrometer": case "micrometers": return 1e-6;
                case "mil": case "mils": return 2.54e-5;
                case "microinch": case "microinches": return 2.54e-8;
                default: return 1.0;
            }
        }

        /// <summary>
        /// The API returns geometry in the document's units (Document.Units), not
        /// in the report's display units - for Revit-sourced models that's feet,
        /// which made every point-to-box distance come out ~5 million meters.
        /// Read via reflection so an SDK where the property is named differently
        /// degrades to scale 1 instead of failing the build.
        /// </summary>
        private static double GetDocumentMetersPerUnit(Document document, out string unitsName)
        {
            unitsName = "(неизвестно)";
            try
            {
                var unitsProperty = typeof(Document).GetProperty("Units");
                if (unitsProperty != null)
                {
                    var value = unitsProperty.GetValue(document, null);
                    if (value != null)
                    {
                        unitsName = value.ToString();
                        return UnitsToMeters(unitsName);
                    }
                }
            }
            catch
            {
            }
            return 1.0;
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

        internal static List<XElement> FindReportIdValues(XElement clashObject, IList<string> idAttributeNames)
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
        internal static string FindTrueId(ModelItem item, ClashIdFixerConfig config)
        {
            for (var current = item; current != null; current = current.Parent)
            {
                string value = TrueIdOwn(current, config);
                if (value != null) return value;
            }
            return null;
        }

        /// <summary>The true-id property of THIS node only (no climbing).</summary>
        private static string TrueIdOwn(ModelItem item, ClashIdFixerConfig config)
        {
            foreach (var categoryName in config.TrueIdCategories)
            {
                foreach (var propertyName in config.TrueIdProperties)
                {
                    var property = FindProperty(item, categoryName, propertyName);
                    if (property == null) continue;

                    string value = VariantToString(property.Value);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
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
        internal static void DescribeAncestors(ModelItem item, StringBuilder diag)
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
            string reportedId, bool hasPoint, double px, double py, double pz,
            ClashIdFixerConfig config, StringBuilder diag)
        {
            diag.AppendLine("=== ЭЛЕМЕНТ НЕ ОПОЗНАН ОДНОЗНАЧНО ===");
            diag.AppendLine("Путь из отчёта: " + string.Join(" > ", pathNodes));
            diag.AppendLine("Id из отчёта: " + reportedId);
            diag.AppendLine(hasPoint
                ? string.Format(CultureInfo.InvariantCulture, "Точка коллизии (в единицах документа): {0:F3}; {1:F3}; {2:F3}", px, py, pz)
                : "Точка коллизии в отчёте отсутствует.");
            diag.AppendLine("Кандидатов по пути: " + candidates.Count);

            foreach (var candidate in candidates.Take(8))
            {
                string trueId = FindTrueId(candidate, config) ?? "(нет)";
                bool segment = SegmentHasModelId(candidate, reportedId, config);
                string distance = "(нет точки)";
                if (hasPoint)
                {
                    double d = DistanceToBoundingBox(candidate, px, py, pz);
                    distance = d == double.MaxValue
                        ? "(бокс недоступен)"
                        : d.ToString("F3", CultureInfo.InvariantCulture) + " ед. док.";
                }
                diag.AppendLine(string.Format("  Кандидат \"{0}\": Объект/Id = {1}; id в сегменте: {2}; расстояние до точки: {3}",
                    candidate.DisplayName ?? "(без имени)", trueId, segment ? "да" : "нет", distance));
            }
            diag.AppendLine();
        }

    }
}
