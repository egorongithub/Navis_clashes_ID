using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;
using ClashIdFixer.Config;
using ClashIdFixer.Core;

namespace ClashIdFixer.Plugin
{
    /// <summary>
    /// Ribbon button (Add-ins tab): runs the full required sequence in one click -
    /// show all -> hide excluded -> update clash tests -> export XML with the
    /// sub-object ids replaced by the resolved "real object" ids.
    /// </summary>
    [PluginAttribute("ClashIdFixer.Export", "EGRN",
        ToolTip = "Показать все, скрыть непроверяемые, обновить проверки и выгрузить коллизии с исправленными Id",
        DisplayName = "Экспорт коллизий (исправленные Id)")]
    public class ExportClashesCommand : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            var log = new List<string>();
            try
            {
                Document document = Autodesk.Navisworks.Api.Application.ActiveDocument;
                if (document == null)
                {
                    MessageBox.Show("Нет открытого документа Navisworks.", "ClashIdFixer",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }

                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string configPath = ClashIdFixerConfig.GetDefaultPath(pluginDir);
                var config = ClashIdFixerConfig.LoadOrCreateDefault(configPath);

                if (config.ShowAllBeforeExport)
                    ModelPreparation.ShowAll(document, log);

                if (config.HideExcludedBeforeExport)
                    ModelPreparation.HideNamedSet(document, config.HiddenSelectionSetName, log);

                if (config.UpdateTestsBeforeExport)
                    ModelPreparation.UpdateAllTests(document, log);

                string outputFolder = string.IsNullOrWhiteSpace(config.OutputFolder) ? pluginDir : config.OutputFolder;
                if (!Directory.Exists(outputFolder)) Directory.CreateDirectory(outputFolder);

                string baseName = string.IsNullOrEmpty(document.CurrentFileName)
                    ? "clashes"
                    : Path.GetFileNameWithoutExtension(document.CurrentFileName);
                string outputFile = Path.Combine(outputFolder,
                    string.Format("{0}_clashes_{1:yyyyMMdd_HHmmss}.xml", baseName, DateTime.Now));

                var summary = ClashXmlExporter.Export(document, config, outputFile);

                var message = new StringBuilder();
                message.AppendLine(string.Join(Environment.NewLine, log));
                message.AppendLine();
                message.AppendLine(string.Format("Проверок: {0}", summary.TestCount));
                message.AppendLine(string.Format("Коллизий: {0}", summary.ClashCount));
                message.AppendLine(string.Format("Id заменены на объект (правильный выбор): {0}", summary.ResolvedCount));
                message.AppendLine(string.Format("Не удалось найти объект (оставлен исходный подобъект): {0}", summary.UnresolvedCount));
                message.AppendLine();
                message.AppendLine("Файл: " + summary.OutputFile);

                MessageBox.Show(message.ToString(), "ClashIdFixer - экспорт завершён",
                    MessageBoxButtons.OK,
                    summary.UnresolvedCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Join(Environment.NewLine, log) + Environment.NewLine + Environment.NewLine + ex,
                    "ClashIdFixer - ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
