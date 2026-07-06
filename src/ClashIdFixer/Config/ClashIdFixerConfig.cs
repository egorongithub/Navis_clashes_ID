using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ClashIdFixer.Config
{
    /// <summary>
    /// One candidate property to try when looking for the "real" element id
    /// (GUID, Element Id, Entity Handle, etc.) on the resolved object.
    /// </summary>
    public struct IdCandidate
    {
        public string Category;
        public string Property;

        public IdCandidate(string category, string property)
        {
            Category = category;
            Property = property;
        }

        public string Key
        {
            get { return Category + "::" + Property; }
        }
    }

    /// <summary>
    /// All settings for the exporter. Loaded from ClashIdFixer.config.xml next to the
    /// plugin DLL. A default file is written out the first time nothing is found, so
    /// the user always has something to edit instead of hunting through source code.
    /// </summary>
    public sealed class ClashIdFixerConfig
    {
        public string OutputFolder = "";

        // Navisworks internal node "icon" types (LcOaNode/LcOaNodeIcon) that are
        // considered "the real object" (the level that has the Item/"Объект" tab).
        // "Composite Object" is the normal case. "Insert Group" is included as a
        // fallback for files where the composite level is missing.
        public List<string> ObjectNodeTypes = new List<string> { "Composite Object", "Insert Group" };

        public List<IdCandidate> IdCandidates = new List<IdCandidate>
        {
            new IdCandidate("Item", "GUID"),
            new IdCandidate("Item", "Element ID"),
            new IdCandidate("Item", "Id"),
            new IdCandidate("Element", "GUID"),
            new IdCandidate("Element", "Element ID"),
            new IdCandidate("Element ID", "Value"),
            new IdCandidate("Element", "Id"),
            new IdCandidate("InstanceData", "Instance GUID"),
            new IdCandidate("IFC", "GlobalId"),
        };

        // Name of a saved Selection Set / Search Set (Sets window) whose items get
        // hidden before export (the "hide non-checked items" step). Leave empty to skip.
        public string HiddenSelectionSetName = "";

        public bool ShowAllBeforeExport = true;
        public bool HideExcludedBeforeExport = true;
        public bool UpdateTestsBeforeExport = true;

        public static string GetDefaultPath(string pluginDirectory)
        {
            return Path.Combine(pluginDirectory, "ClashIdFixer.config.xml");
        }

        public static ClashIdFixerConfig LoadOrCreateDefault(string path)
        {
            if (!File.Exists(path))
            {
                var def = new ClashIdFixerConfig();
                def.OutputFolder = Path.GetDirectoryName(path) ?? "";
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

            var nodeTypesEl = root.Element("ObjectNodeTypes");
            if (nodeTypesEl != null)
            {
                var list = nodeTypesEl.Elements("NodeType").Select(e => e.Value.Trim())
                    .Where(s => s.Length > 0).ToList();
                if (list.Count > 0) cfg.ObjectNodeTypes = list;
            }

            var idCandidatesEl = root.Element("IdCandidates");
            if (idCandidatesEl != null)
            {
                var list = idCandidatesEl.Elements("Candidate")
                    .Select(e => new IdCandidate((string)e.Attribute("Category"), (string)e.Attribute("Property")))
                    .Where(c => !string.IsNullOrEmpty(c.Category) && !string.IsNullOrEmpty(c.Property))
                    .ToList();
                if (list.Count > 0) cfg.IdCandidates = list;
            }

            cfg.HiddenSelectionSetName = (string)root.Element("HiddenSelectionSetName") ?? "";
            cfg.ShowAllBeforeExport = ParseBool(root.Element("ShowAllBeforeExport"), cfg.ShowAllBeforeExport);
            cfg.HideExcludedBeforeExport = ParseBool(root.Element("HideExcludedBeforeExport"), cfg.HideExcludedBeforeExport);
            cfg.UpdateTestsBeforeExport = ParseBool(root.Element("UpdateTestsBeforeExport"), cfg.UpdateTestsBeforeExport);

            return cfg;
        }

        private static bool ParseBool(XElement el, bool fallback)
        {
            if (el == null) return fallback;
            bool result;
            return bool.TryParse(el.Value, out result) ? result : fallback;
        }

        public void Save(string path)
        {
            var root = new XElement("ClashIdFixerConfig",
                new XElement("OutputFolder", OutputFolder),
                new XElement("ObjectNodeTypes", ObjectNodeTypes.Select(t => new XElement("NodeType", t))),
                new XElement("IdCandidates", IdCandidates.Select(c =>
                    new XElement("Candidate", new XAttribute("Category", c.Category), new XAttribute("Property", c.Property)))),
                new XElement("HiddenSelectionSetName", HiddenSelectionSetName),
                new XElement("ShowAllBeforeExport", ShowAllBeforeExport.ToString(CultureInfo.InvariantCulture)),
                new XElement("HideExcludedBeforeExport", HideExcludedBeforeExport.ToString(CultureInfo.InvariantCulture)),
                new XElement("UpdateTestsBeforeExport", UpdateTestsBeforeExport.ToString(CultureInfo.InvariantCulture))
            );

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            new XDocument(root).Save(path);
        }
    }
}
