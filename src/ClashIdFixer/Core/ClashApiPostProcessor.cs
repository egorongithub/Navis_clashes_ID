using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using ClashIdFixer.Config;

namespace ClashIdFixer.Core
{
    /// <summary>
    /// Patches a Clash Detective XML report using the LIVE clash results of the
    /// open document instead of path/geometry matching.
    ///
    /// Every ClashResult in Autodesk.Navisworks.Api.Clash.DocumentClash.TestsData
    /// holds direct ModelItem references to both participants, so there is no
    /// "which of the identical walls" problem at all: a report entry is matched
    /// to its live result by GUID (falling back to test+result name), the item
    /// is taken from the result, and the true id ("Объект"/"Id") is read by
    /// climbing that item's parents - same climb as the path-based mode.
    ///
    /// Requires the clash tests (with results) to be present in the open file;
    /// a report for someone else's model cannot be fixed this way.
    /// </summary>
    public static class ClashApiPostProcessor
    {
        private const int MaxDiagnosedCases = 8;

        /// <summary>A live clash result together with the test it belongs to.</summary>
        private sealed class LiveResult
        {
            public ClashResult Result;
            public string TestName;
        }

        public static FixReportResult Fix(Document document, string inputXmlPath, string outputXmlPath,
            ClashIdFixerConfig config, Action<int, int> progress, StringBuilder diagnostics)
        {
            var result = new FixReportResult();

            XDocument xdoc;
            var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using (var reader = XmlReader.Create(inputXmlPath, readerSettings))
            {
                xdoc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            }

            // Index the live results of the open document.
            List<LiveResult> liveResults;
            int testCount;
            string clashError = TryCollectLiveResults(document, out liveResults, out testCount);
            if (clashError != null)
                throw new InvalidOperationException(clashError);

            var byGuid = new Dictionary<string, LiveResult>(StringComparer.OrdinalIgnoreCase);
            var byTestAndName = new Dictionary<string, LiveResult>(StringComparer.Ordinal);
            var byName = new Dictionary<string, List<LiveResult>>(StringComparer.Ordinal);
            foreach (var live in liveResults)
            {
                string guid = TryGetGuid(live.Result);
                if (guid != null && !byGuid.ContainsKey(guid)) byGuid[guid] = live;

                string name = live.Result.DisplayName ?? "";
                string testKey = (live.TestName ?? "") + "\n" + name;
                if (!byTestAndName.ContainsKey(testKey)) byTestAndName[testKey] = live;

                List<LiveResult> sameName;
                if (!byName.TryGetValue(name, out sameName)) byName[name] = sameName = new List<LiveResult>();
                sameName.Add(live);
            }

            result.UnitsSummary = string.Format(CultureInfo.InvariantCulture,
                "Режим: сопоставление через Clash Detective API (без путей и координат). " +
                "Тестов в документе: {0}, результатов: {1}.", testCount, liveResults.Count);

            var reportResults = xdoc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "clashresult", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int diagnosedNoId = 0;
            bool diagHeaderWritten = false;
            Action writeDiagHeader = () =>
            {
                if (diagHeaderWritten || diagnostics == null) return;
                diagHeaderWritten = true;
                diagnostics.AppendLine(result.UnitsSummary);
                diagnostics.AppendLine();
            };

            // True id per live ModelItem: participants repeat across results.
            var trueIdCache = new Dictionary<ModelItem, string>();

            int index = 0;
            foreach (var reportResult in reportResults)
            {
                index++;
                if (progress != null) progress(index, reportResults.Count);

                string clashName = AttributeValue(reportResult, "name") ?? "(без имени)";

                var clashObjects = reportResult.Descendants()
                    .Where(e => string.Equals(e.Name.LocalName, "clashobject", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                result.ClashObjectCount += clashObjects.Count;
                if (clashObjects.Count == 0) continue;

                LiveResult live = FindLiveResult(reportResult, clashName, byGuid, byTestAndName, byName);
                if (live == null)
                {
                    result.PathUnresolved += clashObjects.Count;
                    result.Errors.Add(string.Format(
                        "{0} | результат не найден в тестах Clash Detective открытого документа (по GUID и имени)",
                        clashName));
                    continue;
                }

                for (int side = 0; side < clashObjects.Count; side++)
                {
                    var clashObject = clashObjects[side];

                    var idValueElements = ReportPostProcessor.FindReportIdValues(clashObject, config.ReportIdAttributeNames);
                    if (!idValueElements.Any(e => e.Value.Trim().Length > 0))
                    {
                        result.NoIdAttribute++;
                        result.Errors.Add(string.Format(
                            "{0} | элемент {1} | в записи отчёта нет атрибута с ID (см. ReportIdAttributeNames в настройках)",
                            clashName, side + 1));
                        continue;
                    }

                    ModelItem item = GetSideItem(live.Result, side);
                    if (item == null)
                    {
                        result.PathUnresolved++;
                        result.Errors.Add(string.Format(
                            "{0} | элемент {1} | у результата Clash Detective нет ссылки на элемент модели (сторона {1})",
                            clashName, side + 1));
                        continue;
                    }

                    string trueId;
                    if (!trueIdCache.TryGetValue(item, out trueId))
                    {
                        trueId = ReportPostProcessor.FindTrueId(item, config);
                        trueIdCache[item] = trueId;
                    }

                    if (trueId == null)
                    {
                        result.PathResolved++;
                        result.TrueIdNotFound++;
                        result.Errors.Add(string.Format(
                            "{0} | элемент {1} | выше по дереву не найдено свойство \"Объект/Id\"",
                            clashName, side + 1));
                        if (diagnostics != null && diagnosedNoId < MaxDiagnosedCases)
                        {
                            diagnosedNoId++;
                            writeDiagHeader();
                            ReportPostProcessor.DescribeAncestors(item, diagnostics);
                        }
                        continue;
                    }

                    result.PathResolved++;
                    int replaced = 0;
                    foreach (var valueElement in idValueElements)
                    {
                        if (!string.Equals(valueElement.Value.Trim(), trueId, StringComparison.Ordinal))
                        {
                            valueElement.Value = trueId;
                            replaced++;
                        }
                    }
                    result.ValuesReplaced += replaced;
                    if (replaced > 0) result.IdReplaced++; else result.AlreadyCorrect++;
                }
            }

            xdoc.Save(outputXmlPath);
            result.OutputFile = outputXmlPath;
            return result;
        }

        /// <summary>
        /// Walks DocumentClash.TestsData and flattens all results (groups
        /// included). Returns an error message instead of throwing when the
        /// document simply has no usable clash data, so the caller can show it
        /// to the user verbatim.
        /// </summary>
        private static string TryCollectLiveResults(Document document, out List<LiveResult> results, out int testCount)
        {
            results = new List<LiveResult>();
            testCount = 0;

            DocumentClash clash;
            try
            {
                clash = document.GetClash();
            }
            catch (Exception ex)
            {
                return "Clash Detective API недоступен в этой установке Navisworks: " + ex.Message;
            }
            if (clash == null || clash.TestsData == null)
                return "Clash Detective API недоступен (нет данных тестов). Нужен Navisworks Manage.";

            foreach (SavedItem savedItem in clash.TestsData.Tests)
            {
                var test = savedItem as ClashTest;
                if (test == null) continue;
                testCount++;
                foreach (SavedItem child in test.Children)
                    Collect(child, test.DisplayName, results);
            }

            if (testCount == 0)
                return "В открытом документе нет тестов Clash Detective. " +
                       "Откройте файл, в котором сохранены проверки с результатами, или используйте кнопку с поиском по модели.";
            if (results.Count == 0)
                return "В тестах Clash Detective открытого документа нет результатов. " +
                       "Запустите проверку (или откройте файл с сохранёнными результатами) и повторите.";
            return null;
        }

        private static void Collect(SavedItem item, string testName, List<LiveResult> results)
        {
            var clashResult = item as ClashResult;
            if (clashResult != null)
            {
                results.Add(new LiveResult { Result = clashResult, TestName = testName });
                return;
            }

            var group = item as GroupItem;
            if (group != null)
            {
                foreach (SavedItem child in group.Children)
                    Collect(child, testName, results);
            }
        }

        /// <summary>
        /// Matches a report entry to a live result: by GUID when the report has
        /// one (globally unique), then by test name + result name (unique within
        /// a test), then by result name alone when it is unique in the document.
        /// </summary>
        private static LiveResult FindLiveResult(XElement reportResult, string clashName,
            Dictionary<string, LiveResult> byGuid,
            Dictionary<string, LiveResult> byTestAndName,
            Dictionary<string, List<LiveResult>> byName)
        {
            string guidRaw = AttributeValue(reportResult, "guid");
            Guid guid;
            if (guidRaw != null && Guid.TryParse(guidRaw.Trim(), out guid))
            {
                LiveResult byGuidHit;
                if (byGuid.TryGetValue(guid.ToString("D"), out byGuidHit)) return byGuidHit;
            }

            var testElement = reportResult.Ancestors()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, "clashtest", StringComparison.OrdinalIgnoreCase));
            if (testElement != null)
            {
                string testName = AttributeValue(testElement, "name");
                if (testName != null)
                {
                    LiveResult byTestHit;
                    if (byTestAndName.TryGetValue(testName + "\n" + clashName, out byTestHit)) return byTestHit;
                }
            }

            List<LiveResult> sameName;
            if (byName.TryGetValue(clashName, out sameName) && sameName.Count == 1) return sameName[0];
            return null;
        }

        /// <summary>
        /// The participant ModelItem of one side. Item1/Item2 hold the leaf
        /// geometry that clashed; CompositeItem1/2 (newer SDKs, read via
        /// reflection so 2022 still compiles) are tried as a fallback.
        /// </summary>
        private static ModelItem GetSideItem(ClashResult clashResult, int side)
        {
            ModelItem item = null;
            try { item = side == 0 ? clashResult.Item1 : clashResult.Item2; } catch { }
            if (item != null) return item;

            try
            {
                var property = clashResult.GetType().GetProperty(side == 0 ? "CompositeItem1" : "CompositeItem2");
                if (property != null) item = property.GetValue(clashResult, null) as ModelItem;
            }
            catch { }
            return item;
        }

        /// <summary>SavedItem.Guid via reflection: absent in some SDK versions.</summary>
        private static string TryGetGuid(SavedItem item)
        {
            try
            {
                var property = item.GetType().GetProperty("Guid");
                if (property != null && property.PropertyType == typeof(Guid))
                {
                    var value = (Guid)property.GetValue(item, null);
                    if (value != Guid.Empty) return value.ToString("D");
                }
            }
            catch { }
            return null;
        }

        private static string AttributeValue(XElement element, string name)
        {
            var attribute = element.Attribute(name);
            return attribute != null && attribute.Value.Length > 0 ? attribute.Value : null;
        }
    }
}
