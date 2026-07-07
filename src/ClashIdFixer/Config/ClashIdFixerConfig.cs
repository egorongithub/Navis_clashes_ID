using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ClashIdFixer.Config
{
    /// <summary>
    /// Settings, loaded from ClashIdFixer.config.xml next to the plugin DLL.
    /// A default file is written out the first time nothing is found, so the user
    /// always has something to edit instead of hunting through source code.
    /// </summary>
    public sealed class ClashIdFixerConfig
    {
        /// <summary>Empty = the user's Downloads folder.</summary>
        public string OutputFolder = "";

        // objectattribute/smarttag names in the report whose value is the element
        // id to be checked and, when wrong, replaced. "ID объекта" is what the
        // standard Russian report writes; English variants included for safety.
        public List<string> ReportIdAttributeNames = new List<string>
        {
            "ID объекта",
            "ИД объекта",
            "Object ID",
            "Object Id",
        };

        // Where the TRUE element id lives: the "Объект" properties tab, "Id" row
        // (exists only at the real-object level). Both display and internal names
        // are tried for every category/property combination, so extra entries are
        // cheap and localization-proof. LcRevitData_Element/LcRevitPropertyElementId
        // are the INTERNAL names of that exact tab/row (confirmed by a live
        // diagnostics dump), so this works even if the UI language changes.
        public List<string> TrueIdCategories = new List<string>
        {
            "LcRevitData_Element",
            "Объект",
            "Item",
            "Element",
        };

        public List<string> TrueIdProperties = new List<string>
        {
            "LcRevitPropertyElementId",
            "Id",
            "ID",
            "ИД",
        };

        // Categories on MODEL items whose values correspond to the id written by
        // the standard report ("ID объекта" tab, internally LcRevitId). Used to
        // pick the right instance when several elements share the same tree path:
        // the one whose chain carries the reported id is the one the clash hit.
        public List<string> ModelIdCategories = new List<string>
        {
            "LcRevitId",
            "ID объекта",
            "ИД объекта",
            "Object ID",
        };

        public static string GetDefaultPath(string pluginDirectory)
        {
            return Path.Combine(pluginDirectory, "ClashIdFixer.config.xml");
        }

        public static ClashIdFixerConfig LoadOrCreateDefault(string path)
        {
            if (!File.Exists(path))
            {
                var def = new ClashIdFixerConfig();
                def.Save(path);
                return def;
            }

            try
            {
                return Load(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Не удалось прочитать файл настроек " + path + ": " + ex.Message, ex);
            }
        }

        public static ClashIdFixerConfig Load(string path)
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root == null) throw new InvalidOperationException("Пустой файл настроек.");

            var cfg = new ClashIdFixerConfig();

            cfg.OutputFolder = (string)root.Element("OutputFolder") ?? cfg.OutputFolder;
            ReadList(root, "ReportIdAttributeNames", "Name", cfg.ReportIdAttributeNames);
            ReadList(root, "TrueIdCategories", "Name", cfg.TrueIdCategories);
            ReadList(root, "TrueIdProperties", "Name", cfg.TrueIdProperties);
            ReadList(root, "ModelIdCategories", "Name", cfg.ModelIdCategories);

            return cfg;
        }

        private static void ReadList(XElement root, string listName, string itemName, List<string> target)
        {
            var listEl = root.Element(listName);
            if (listEl == null) return;

            var items = listEl.Elements(itemName).Select(e => e.Value.Trim())
                .Where(s => s.Length > 0).ToList();
            if (items.Count > 0)
            {
                target.Clear();
                target.AddRange(items);
            }
        }

        public void Save(string path)
        {
            var root = new XElement("ClashIdFixerConfig",
                new XElement("OutputFolder", OutputFolder),
                new XElement("ReportIdAttributeNames", ReportIdAttributeNames.Select(n => new XElement("Name", n))),
                new XElement("TrueIdCategories", TrueIdCategories.Select(n => new XElement("Name", n))),
                new XElement("TrueIdProperties", TrueIdProperties.Select(n => new XElement("Name", n))),
                new XElement("ModelIdCategories", ModelIdCategories.Select(n => new XElement("Name", n)))
            );

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            new XDocument(root).Save(path);
        }
    }
}
