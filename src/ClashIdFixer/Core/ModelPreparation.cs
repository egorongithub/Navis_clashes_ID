using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.DocumentParts;

namespace ClashIdFixer.Core
{
    /// <summary>
    /// The mandatory pre-report procedures: show everything, hide the items that
    /// must not be clash-checked, recompute the clash tests. Every step logs what
    /// it did so a failure is visible instead of silently producing a stale or
    /// incomplete report.
    /// </summary>
    public static class ModelPreparation
    {
        public static void ShowAll(Document document, List<string> log)
        {
            // One native call that un-hides the whole scene - much faster than
            // collecting hundreds of thousands of descendants into a collection.
            var resetAllHidden = typeof(DocumentModels).GetMethod("ResetAllHidden", Type.EmptyTypes);
            if (resetAllHidden != null)
            {
                resetAllHidden.Invoke(document.Models, null);
                log.Add("Показаны все элементы.");
                return;
            }

            var all = new ModelItemCollection();
            foreach (Model model in document.Models)
            {
                if (model.RootItem == null) continue;
                all.Add(model.RootItem);
                all.AddRange(model.RootItem.Descendants);
            }
            document.Models.SetHidden(all, false);
            log.Add(string.Format("Показаны все элементы ({0} шт.).", all.Count));
        }

        /// <summary>
        /// Hides every item belonging to the named Selection/Search set (Sets window),
        /// searched recursively through folders. Does nothing (and logs why) if the
        /// name is blank or the set cannot be found/resolved.
        /// </summary>
        public static void HideNamedSet(Document document, string setName, List<string> log)
        {
            if (string.IsNullOrWhiteSpace(setName))
            {
                log.Add("Скрытие непроверяемых элементов пропущено (имя набора не задано в ClashIdFixer.config.xml).");
                return;
            }

            var allSets = FindAllSelectionSets(document.SelectionSets.RootItem);
            var match = allSets.FirstOrDefault(s => string.Equals(s.DisplayName, setName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                log.Add(string.Format("ВНИМАНИЕ: набор \"{0}\" не найден - шаг скрытия пропущен.", setName));
                return;
            }

            ModelItemCollection items = ResolveSelectionSetItems(document, match);
            if (items == null || items.Count == 0)
            {
                log.Add(string.Format("ВНИМАНИЕ: набор \"{0}\" не вернул ни одного элемента.", setName));
                return;
            }

            document.Models.SetHidden(items, true);
            log.Add(string.Format("Скрыт набор \"{0}\" ({1} элементов).", setName, items.Count));
        }

        private static IEnumerable<SelectionSet> FindAllSelectionSets(FolderItem folder)
        {
            foreach (var child in folder.Children)
            {
                var set = child as SelectionSet;
                if (set != null)
                {
                    yield return set;
                    continue;
                }

                var subFolder = child as FolderItem;
                if (subFolder != null)
                {
                    foreach (var nested in FindAllSelectionSets(subFolder))
                        yield return nested;
                }
            }
        }

        /// <summary>
        /// SelectionSet exposes either an explicit list of items or a saved Search
        /// that needs to be re-run against the current model. The exact member names
        /// for this have moved around between SDK releases, so this is resolved by
        /// reflection instead of a hard-coded call that might not compile against
        /// your installed 2022 SDK.
        /// </summary>
        private static ModelItemCollection ResolveSelectionSetItems(Document document, SelectionSet set)
        {
            var type = set.GetType();

            var hasExplicitProp = type.GetProperty("HasExplicitModelItems");
            var explicitProp = type.GetProperty("ExplicitModelItems");
            if (hasExplicitProp != null && explicitProp != null)
            {
                var has = (bool)hasExplicitProp.GetValue(set, null);
                if (has)
                    return explicitProp.GetValue(set, null) as ModelItemCollection;
            }

            var hasSearchProp = type.GetProperty("HasSearch");
            var searchProp = type.GetProperty("Search");
            if (hasSearchProp != null && searchProp != null)
            {
                var has = (bool)hasSearchProp.GetValue(set, null);
                if (has)
                {
                    var search = searchProp.GetValue(set, null);
                    if (search != null)
                    {
                        var findAll = search.GetType().GetMethod("FindAll", new[] { typeof(Document), typeof(bool) })
                                      ?? search.GetType().GetMethod("FindAll", new[] { typeof(Document) });
                        if (findAll != null)
                        {
                            var parameters = findAll.GetParameters().Length == 2
                                ? new object[] { document, false }
                                : new object[] { document };
                            return findAll.Invoke(search, parameters) as ModelItemCollection;
                        }
                    }
                }
            }

            return null;
        }

        public static void UpdateAllTests(Document document, List<string> log)
        {
            var clash = document.GetClash();
            var testsData = clash.TestsData;

            if (ReflectionHelpers.TryRunAllTests(testsData))
            {
                log.Add("Все проверки на коллизии пересчитаны.");
                return;
            }

            int ok = 0, failed = 0;
            foreach (var test in testsData.Tests.OfType<ClashTest>())
            {
                if (ReflectionHelpers.TryRunTest(testsData, test))
                    ok++;
                else
                    failed++;
            }

            if (failed == 0)
                log.Add(string.Format("Пересчитано проверок: {0}.", ok));
            else
                log.Add(string.Format(
                    "Пересчитано проверок: {0}, не удалось пересчитать: {1} " +
                    "(нужный метод не найден в установленной версии API - см. ReflectionHelpers.cs). " +
                    "Обновите проверки вручную в Clash Detective.", ok, failed));
        }
    }
}
