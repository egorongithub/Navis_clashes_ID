using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Fallback list only: the primary composite-object check is the
        // ModelItem.IsComposite API flag. These node-type names (compared against
        // the LcOaNode/LcOaNodeIcon constant) are consulted for files where that
        // flag is not set; both English and Russian spellings are accepted.
        public List<string> ObjectNodeTypes = new List<string>
        {
            "Composite Object",
            "Insert Group",
            "Составной объект",
            "Группа вставки",
        };

        // Name of a saved Selection Set / Search Set (Sets window) whose items get
        // hidden before recomputing tests (the "hide non-checked items" step).
        // Leave empty to skip.
        public string HiddenSelectionSetName = "";

        public bool ShowAllBeforeUpdate = true;
        public bool HideExcludedBeforeUpdate = true;

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

            var nodeTypesEl = root.Element("ObjectNodeTypes");
            if (nodeTypesEl != null)
            {
                var list = nodeTypesEl.Elements("NodeType").Select(e => e.Value.Trim())
                    .Where(s => s.Length > 0).ToList();
                if (list.Count > 0) cfg.ObjectNodeTypes = list;
            }

            cfg.HiddenSelectionSetName = (string)root.Element("HiddenSelectionSetName") ?? "";
            cfg.ShowAllBeforeUpdate = ParseBool(root.Element("ShowAllBeforeUpdate"), cfg.ShowAllBeforeUpdate);
            cfg.HideExcludedBeforeUpdate = ParseBool(root.Element("HideExcludedBeforeUpdate"), cfg.HideExcludedBeforeUpdate);

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
                new XElement("HiddenSelectionSetName", HiddenSelectionSetName),
                new XElement("ShowAllBeforeUpdate", ShowAllBeforeUpdate.ToString(CultureInfo.InvariantCulture)),
                new XElement("HideExcludedBeforeUpdate", HideExcludedBeforeUpdate.ToString(CultureInfo.InvariantCulture))
            );

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            new XDocument(root).Save(path);
        }
    }
}
